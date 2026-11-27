using SoundFlow.Enums;
using SoundFlow.Playback;
using SoundFlow.Processing;
using SoundFlow.Spatial;
using SoundFlow.Structs;
using Xunit;

namespace SoundFlow.Tests;

public sealed class RenderProcessingTests
{
    [Fact]
    public void ShortEffectTailRemainsAvailableAcrossSmallCallbackRequests()
    {
        using var scheduler = new DecodeScheduler(new());
        using var decode = CreateDecode(scheduler, 1);
        var builder = new Builder().Add(format => new Tail(format));
        using var render = new RenderSession(decode, builder.Build().CreateRunner(decode.OutputFormat, 256), 256);
        Start(decode);
        Assert.True(SpinWait.SpinUntil(() => decode.Done, 3000));
        render.Read(new float[256]);
        var first = new float[1];
        Assert.Equal(1, render.Read(first));
        Assert.Equal(.3f, first[0]);
        Assert.False(render.Completed);
        var rest = new float[36];
        Assert.Equal(36, render.Read(rest));
        Assert.All(rest, value => Assert.Equal(.3f, value));
        Assert.True(render.Completed);
        Assert.Equal(0, render.Read(first));
        Assert.Equal(0, render.RetainedFrames);
    }

    [Theory]
    [InlineData(AttenuationModel.None, 500f, 1f, 1f)]
    [InlineData(AttenuationModel.Inverse, 2f, 1f, .5f)]
    [InlineData(AttenuationModel.Inverse, 3f, 2f, .2f)]
    [InlineData(AttenuationModel.Inverse, 400f, 1f, .0025f)]
    [InlineData(AttenuationModel.Exponential, 3f, 2f, .11111111f)]
    [InlineData(AttenuationModel.Exponential, 2f, 0f, .9993071f)]
    [InlineData(AttenuationModel.Linear, 100.5f, 1f, .5f)]
    [InlineData(AttenuationModel.Linear, 100.5f, 2f, 0f)]
    [InlineData(AttenuationModel.LinearDb, 100.5f, 2f, .02238721f)]
    public void DistanceCurvesPreserveReferenceGains(AttenuationModel model, float distance, float rolloff, float expected)
    {
        Assert.Equal(expected, new DistanceAttenuation { Model = model, Rolloff = rolloff }.Evaluate(distance), 6);
    }

    [Fact]
    public void PoseChangesOnlyWaitForTheAlreadyRenderedQuantumRemainder()
    {
        using var scheduler = new DecodeScheduler(new());
        using var decode = CreateDecode(scheduler, 8192);
        var gain = .25f;
        var builder = new Builder().Add(format => new Gain(format, () => gain));
        using var render = new RenderSession(decode, builder.Build().CreateRunner(decode.OutputFormat, 256), 256);
        Start(decode);
        render.Read(new float[100]);
        gain = .75f;
        var following = new float[257];
        render.Read(following);
        Assert.All(following[..156], sample => Assert.Equal(.25f, sample));
        Assert.All(following[156..], sample => Assert.Equal(.75f, sample));
    }

    [Fact]
    public async Task ABlockedControlSideOperationDoesNotBlockRenderConsumption()
    {
        using var scheduler = new DecodeScheduler(new());
        using var decode = CreateDecode(scheduler, 8192);
        using var render = new RenderSession(decode, Chain.Empty.CreateRunner(decode.OutputFormat, 256), 256);
        Start(decode);
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var change = Task.Run(() => decode.Change(() => { entered.Set(); release.Wait(); }, false));
        Assert.True(entered.Wait(TimeSpan.FromSeconds(3)));
        try
        {
            var read = Task.Run(() =>
            {
                var samples = new float[256];
                Assert.Equal(256, render.Read(samples));
                Assert.All(samples, value => Assert.Equal(0, value));
            });
            Assert.Same(read, await Task.WhenAny(read, Task.Delay(1000)));
            await read;
        }
        finally { release.Set(); await change; }
    }

    [Fact]
    public void PrefilledInputUsesTheNewestControlsWithoutAStartupBlock()
    {
        using var scheduler = new DecodeScheduler(new());
        using var decode = CreateDecode(scheduler, 8192);
        var gain = .25f;
        var builder = new Builder().Add(format => new Gain(format, () => gain));
        using var render = new RenderSession(decode, builder.Build().CreateRunner(decode.OutputFormat, 256), 256);
        Start(decode);
        Assert.True(SpinWait.SpinUntil(() => decode.ReadyCount == 4, 3000));
        gain = .75f;
        var output = new float[256];
        Assert.Equal(256, render.Read(output));
        Assert.All(output, value => Assert.Equal(.75f, value));
        gain = .5f;
        Assert.Equal(256, render.Read(output));
        Assert.All(output, value => Assert.Equal(.5f, value));
    }

    [Fact]
    public void ArbitraryCallbackSizesAndReversedWetReadsProcessEachQuantumOnce()
    {
        using var scheduler = new DecodeScheduler(new());
        using var decode = CreateDecode(scheduler, 8192);
        Gain? effect = null;
        var builder = new Builder();
        var point = builder.Capture("dry-input");
        builder.Add(format => effect = new Gain(format, () => .3f));
        using var render = new RenderSession(decode, builder.Build().CreateRunner(decode.OutputFormat, 256), 256);
        var wet = render.AddEndpoint();
        Start(decode);
        var captured = new float[511];
        Assert.Equal(511, render.Read(captured, wet, point));
        Assert.All(captured, sample => Assert.Equal(1, sample));
        var output = new float[511];
        Assert.Equal(511, render.Read(output));
        Assert.All(output, sample => Assert.Equal(.3f, sample));
        Assert.Equal(2, effect!.Calls);
        Assert.Equal(1, render.RetainedFrames);
    }

    [Fact]
    public void FinalPartialQuantumIsPaddedOnceAndDoesNotDiscardInput()
    {
        using var scheduler = new DecodeScheduler(new());
        using var decode = CreateDecode(scheduler, 513);
        var chain = new Builder().Build();
        using var render = new RenderSession(decode, chain.CreateRunner(decode.OutputFormat, 256), 256);
        Start(decode);
        Assert.True(SpinWait.SpinUntil(() => decode.Done, 3000));
        var output = new float[1024];
        Assert.Equal(768, render.Read(output));
        Assert.All(output[..513], value => Assert.Equal(1, value));
        Assert.All(output[513..], value => Assert.Equal(0, value));
        Assert.True(render.Completed);
        Assert.Equal(513, render.Position);
    }

    [Fact]
    public void WarmRenderConsumptionDoesNotAllocateOnTheCallingThread()
    {
        using var scheduler = new DecodeScheduler(new());
        using var decode = CreateDecode(scheduler, 65536);
        var builder = new Builder().Add(format => new Gain(format, () => 1));
        using var render = new RenderSession(decode, builder.Build().CreateRunner(decode.OutputFormat, 256), 256);
        Start(decode);
        var output = new float[256];
        render.Read(output);
        var before = GC.GetAllocatedBytesForCurrentThread();
        for (var i = 0; i < 64; i++) render.Read(output);
        Assert.Equal(before, GC.GetAllocatedBytesForCurrentThread());
    }

    [Theory]
    [InlineData(.01f, 1f)]
    [InlineData(1f, 1f)]
    [InlineData(100.5f, .14962357f)]
    [InlineData(200f, 0f)]
    public void LinearDbMatchesReferenceValues(float distance, float expected)
    {
        Assert.Equal(expected, new DistanceAttenuation().Evaluate(distance), 6);
    }

    [Fact]
    public void CurveScalingPreservesTheOrderOfBothDistanceClamps()
    {
        var curve = new DistanceAttenuation { MinimumDistance = 10, MaximumDistance = 5, CurveScaler = 2 };
        Assert.Equal(1, curve.Evaluate(20));
        Assert.Equal(MathF.Pow(10, -16.5f / 20), curve.Evaluate(30));
        Assert.Equal(0, curve.Evaluate(40));
        Assert.Equal(new DistanceAttenuation().Evaluate(40), new DistanceAttenuation { CurveScaler = 0 }.Evaluate(40));
    }

    private static DecodeSession CreateDecode(DecodeScheduler scheduler, int samples)
    {
        var transport = new Transport(TransportTests.Mono, new Clip(Enumerable.Repeat(1f, samples).ToArray(), TransportTests.Mono).Open());
        transport.Play();
        return new(scheduler, transport, renderConsumer: true);
    }

    private static void Start(DecodeSession decode)
    {
        decode.Enable(true);
        decode.Prepare();
    }

    private sealed class Gain(AudioFormat format, Func<float> gain) : Processor(format, format)
    {
        internal int Calls;
        public override void Process(ReadOnlySpan<float> input, Span<float> output, int frames)
        {
            Calls++;
            var value = gain();
            for (var i = 0; i < frames; i++) output[i] = input[i] * value;
        }
    }

    private sealed class Tail(AudioFormat format) : Processor(format, format)
    {
        private bool _drained;
        public override int MaximumTailFrames => 37;
        public override void Process(ReadOnlySpan<float> input, Span<float> output, int frames) => input.CopyTo(output);
        public override int Drain(Span<float> output)
        {
            if (_drained) return 0;
            _drained = true;
            output[..37].Fill(.3f);
            return 37;
        }
    }
}
