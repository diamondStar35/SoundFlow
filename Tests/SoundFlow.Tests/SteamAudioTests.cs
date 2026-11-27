using System.Numerics;
using SoundFlow.Playback;
using SoundFlow.Processing;
using SoundFlow.Spatial;
using SoundFlow.SteamAudio;
using Xunit;
using Native = global::SteamAudio;

namespace SoundFlow.Tests;

public sealed class SteamAudioTests
{
    [Fact]
    public void SpatialControlsCannotBindToTwoLiveChains()
    {
        using var renderer = new Renderer();
        var state = new Spatialization(new() { Position = Vector3.UnitX });
        var chain = new Builder().Add(format => renderer.CreateBinauralProcessor(format, state)).Build();
        using var first = chain.CreateRunner(TransportTests.Mono, 256);
        Assert.Throws<InvalidOperationException>(() => chain.CreateRunner(TransportTests.Mono, 256));
        var input = new float[256]; input[0] = 1;
        var output = new float[512];
        first.Process(input, output, 256, first.CreateCaptures());
        Assert.Contains(output, sample => Math.Abs(sample) > .001f);
    }

    [Fact]
    public void IntegratedBinauralOutputMatchesTheDirectNativeWrapper()
    {
        using var renderer = new Renderer();
        var state = new Spatialization(new() { Position = Vector3.UnitX, Distance = new() { Model = AttenuationModel.None } });
        var format = TransportTests.Mono;
        using var processor = renderer.CreateBinauralProcessor(format, state);
        using var context = new Native.Context();
        using var hrtf = new Native.Hrtf(context, new(48000, 256));
        using var effect = new Native.Effects.Binaural(context, hrtf);
        using var input = new Native.AudioBuffer(context, 1, 256);
        using var output = new Native.AudioBuffer(context, 2, 256);
        var source = new float[256];
        source[0] = 1;
        var actual = new float[512];
        var expected = new float[512];
        input.Deinterleave(source);
        effect.Process(Vector3.UnitX, input, output);
        output.Interleave(expected);
        processor.Process(source, actual, 256);
        Assert.Contains(actual, sample => Math.Abs(sample) > .001f);
        for (var i = 0; i < actual.Length; i++) Assert.Equal(expected[i], actual[i], 6);
        Assert.True(processor.MaximumTailFrames > 0);
        var nativeTail = new float[512];
        var drained = 0;
        while (effect.TailSize > 0)
        {
            effect.GetTail(output);
            output.Interleave(nativeTail);
            Assert.Equal(256, processor.Drain(actual));
            for (var i = 0; i < actual.Length; i++) Assert.Equal(nativeTail[i], actual[i], 6);
            drained += 256;
        }
        Assert.InRange(drained, 1, processor.MaximumTailFrames);
        Assert.Equal(0, processor.Drain(actual));
    }

    [Fact]
    public void ListenerRotationIsAppliedAfterTheDecodedQueueHasFilled()
    {
        using var scheduler = new DecodeScheduler(new());
        var clip = new Clip(Enumerable.Range(0, 8192).Select(i => MathF.Sin(i * .17f)).ToArray(), TransportTests.Mono);
        using var transport = new Transport(TransportTests.Mono, clip.Open());
        transport.Play();
        using var decode = new DecodeSession(scheduler, transport, renderConsumer: true);
        using var renderer = new Renderer();
        var state = new Spatialization(new() { Position = Vector3.UnitX });
        var builder = new Builder().Add(format => renderer.CreateBinauralProcessor(format, state));
        using var render = new RenderSession(decode, builder.Build().CreateRunner(decode.OutputFormat, 256), 256);
        decode.Enable(true);
        Assert.True(SpinWait.SpinUntil(() => decode.ReadyCount == 4, 3000));
        renderer.Listener = new() { Forward = Vector3.UnitX };
        state.Settings = state.Settings with { Position = Vector3.UnitZ };
        var output = new float[512];
        render.Read(output);
        Assert.Equal(1, state.AppliedGeneration);
        Assert.Equal(1, state.AppliedListenerGeneration);
        Assert.Contains(output, sample => Math.Abs(sample) > .001f);
        var before = GC.GetAllocatedBytesForCurrentThread();
        for (var i = 0; i < 10; i++) render.Read(output);
        Assert.Equal(before, GC.GetAllocatedBytesForCurrentThread());
    }

    [Fact]
    public void DefaultSourceRendersDistanceAndHrtfThroughOpenAlCallbackBuffers()
    {
        using var engine = new SoundFlow.Backends.OpenAL.Engine();
        using var device = engine.CreateRenderDevice(OpenAlPlaybackTests.Stereo);
        using var renderer = new Renderer();
        using var source = renderer.CreateSource(device,
            new Clip(Enumerable.Range(0, 16384).Select(i => .3f * MathF.Sin(i * .1f)).ToArray(), TransportTests.Mono).Open(),
            TransportTests.Mono, new() { Position = Vector3.UnitX * 5 });
        Assert.True(source.TryGetFeature<ISpatialization>(out var controls));
        source.Play();
        var output = new float[4096];
        device.Render(output);
        Assert.Contains(output, sample => Math.Abs(sample) > .001f);
        controls.Settings = controls.Settings with { Position = Vector3.UnitX * -5 };
        device.Render(output);
        Assert.Equal(1, controls.AppliedGeneration);
        source.Stop();
        source.Play();
        device.Render(output);
        Assert.Contains(output, sample => Math.Abs(sample) > .001f);
    }
}
