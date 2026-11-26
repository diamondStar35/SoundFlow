using SoundFlow.Effects;
using SoundFlow.Features;
using SoundFlow.Playback;
using SoundFlow.Processing;
using Xunit;

namespace SoundFlow.Tests;

public sealed class OpenAlProcessingTests
{
    [Fact]
    public void ScheduledStartsIncludeCapturedEndpoints()
    {
        using var engine = new SoundFlow.Backends.OpenAL.Engine();
        using var device = engine.CreateRenderDevice(OpenAlPlaybackTests.Stereo);
        Assert.True(device.TryGetFeature<IEffects>(out var effects));
        Assert.True(device.TryGetFeature<IScheduledPlayback>(out var scheduler));
        Assert.True(device.TryGetFeature<IPlaybackTiming>(out var timing));
        using var slot = effects.CreateSlot(new EchoSettings { Delay = .005f, LeftRightDelay = 0, Feedback = 0 });
        var builder = new Builder(); var point = builder.Capture("input"); builder.Add(f => new ProcessingTests.Gain(f, 0));
        using var source = device.CreateSource(PreparedClipTests.Tone(), new SourceOptions { Processing = builder.Build() });
        Assert.True(source.TryGetFeature<IProcessingSends>(out var sends)); sends.SetSend(0, point, slot);
        scheduler.PlayAt(source, timing.GetClock().Nanoseconds + 100_000_000);
        var before = new float[4800]; device.Render(before); Assert.All(before, n => Assert.Equal(0, n));
        var after = new float[12000]; device.Render(after); Assert.Contains(after, n => Math.Abs(n) > .001);
    }

    [Fact]
    public void SharedPointsReuseWetEndpointsAndDoNotCreateDecoderSessions()
    {
        using var engine = new SoundFlow.Backends.OpenAL.Engine();
        using var device = engine.CreateRenderDevice(OpenAlPlaybackTests.Stereo);
        Assert.True(device.TryGetFeature<IEffects>(out var effects));
        Assert.True(device.TryGetFeature<IDiagnostics>(out var diagnostics));
        using var slot = effects.CreateSlot(new ReverbSettings());
        var builder = new Builder(); var point = builder.Capture("input");
        using var source = device.CreateSource(PreparedClipTests.Tone(), new SourceOptions { Processing = builder.Build() });
        Assert.True(source.TryGetFeature<IProcessingSends>(out var sends));
        var sessions = engine.Decoding.GetSnapshot().Sessions;
        sends.SetSend(0, point, slot); sends.SetSend(1, point, slot);
        Assert.Equal(1, diagnostics.GetSnapshot().ProcessingEndpoints);
        Assert.Equal(sessions, engine.Decoding.GetSnapshot().Sessions);
        sends.SetSend(0, point, null); Assert.Equal(1, diagnostics.GetSnapshot().ProcessingEndpoints);
        sends.SetSend(1, point, null); Assert.Equal(0, diagnostics.GetSnapshot().ProcessingEndpoints);
    }
    [Fact]
    public void AFailedProcessorPublishesNeitherDryPcmNorAnEarlierWetCapture()
    {
        using var engine = new SoundFlow.Backends.OpenAL.Engine();
        using var device = engine.CreateRenderDevice(OpenAlPlaybackTests.Stereo);
        Assert.True(device.TryGetFeature<IEffects>(out var effects));
        using var slot = effects.CreateSlot(new ReverbSettings());
        var builder = new Builder(); var point = builder.Capture("input"); builder.Add(f => new Failure(f));
        using var source = device.CreateSource(PreparedClipTests.Tone(), new SourceOptions { Processing = builder.Build() });
        Assert.True(source.TryGetFeature<IProcessingSends>(out var sends)); sends.SetSend(0, point, slot);
        source.Play(); var output = new float[16384]; device.Render(output);
        Assert.All(output, sample => Assert.Equal(0, sample));
        Assert.Equal(SoundFlow.Enums.PlaybackState.Stopped, source.State);
        Assert.True(device.TryGetFeature<IDiagnostics>(out var diagnostics)); Assert.NotNull(diagnostics.GetSnapshot().LastFailure);
    }
    private sealed class Failure(SoundFlow.Structs.AudioFormat format) : Processor(format, format)
    {
        public override void Process(ReadOnlySpan<float> input, Span<float> output, int frames)
            => throw new InvalidOperationException("processor failure");
    }

    [Fact]
    public void EarlierMonoPointFeedsNativeReverbWhenLaterStereoProcessingMutesEverything()
    {
        using var engine = new SoundFlow.Backends.OpenAL.Engine();
        using var device = engine.CreateRenderDevice(OpenAlPlaybackTests.Stereo);
        Assert.True(device.TryGetFeature<IEffects>(out var effects));
        using var slot = effects.CreateSlot(EfxReverbPresets.All["CAVE"]);
        var builder = new Builder(); var point = builder.Capture("environment");
        builder.Add(f => new ProcessingTests.StereoBlock(f, 256)).Add(f => new ProcessingTests.Gain(f, 0));
        var input = new float[2048]; input[128] = 1;
        using var source = device.CreateSource(new Clip(input, TransportTests.Mono), new SourceOptions { Processing = builder.Build() });
        Assert.Equal(1, source.Format.Channels); Assert.Equal(2, source.RenderedFormat.Channels);
        Assert.True(source.TryGetFeature<IProcessingSends>(out var sends));
        sends.SetSend(0, point, slot);
        source.Play();
        var output = new float[16384]; device.Render(output);
        Assert.True(output.Sum(x => Math.Abs(x)) > .001);
        Assert.Equal(SoundFlow.Enums.PlaybackState.Stopped, source.State);
        Assert.Equal(2048f / 48000, source.Time, 5);
    }

    [Fact]
    public void CompensationIsOptInAndShiftsOnlyTheWetInput()
    {
        static float[] Render(LatencyCompensation compensation)
        {
            using var engine = new SoundFlow.Backends.OpenAL.Engine();
            using var device = engine.CreateRenderDevice(OpenAlPlaybackTests.Stereo);
            Assert.True(device.TryGetFeature<IEffects>(out var effects));
            using var slot = effects.CreateSlot(new EchoSettings { Delay = .01f, LeftRightDelay = 0, Damping = 0, Feedback = 0 });
            var builder = new Builder(); var point = builder.Capture("early");
            builder.Add(f => new ProcessingTests.StereoBlock(f, 256) { CompensateLatency = true })
                .Add(f => new ProcessingTests.Gain(f, 0));
            var input = new float[2048]; input[128] = .8f;
            using var source = device.CreateSource(new Clip(input, TransportTests.Mono), new SourceOptions { Processing = builder.Build() });
            Assert.True(source.TryGetFeature<IProcessingSends>(out var sends));
            sends.SetSend(0, point, slot, new() { Compensation = compensation });
            source.Play(); var result = new float[8192]; device.Render(result); return result;
        }
        static int First(float[] samples) => Array.FindIndex(samples, x => Math.Abs(x) > .00001) / 2;
        var immediate = Render(LatencyCompensation.None); var aligned = Render(LatencyCompensation.Automatic);
        Assert.Contains(immediate, x => Math.Abs(x) > .00001);
        Assert.InRange(First(aligned) - First(immediate), 255, 257);
    }

    [Fact]
    public void RoutesShareSendIndicesAndRejectStructuralChangesDuringPlayback()
    {
        using var engine = new SoundFlow.Backends.OpenAL.Engine();
        using var device = engine.CreateRenderDevice(OpenAlPlaybackTests.Stereo);
        Assert.True(device.TryGetFeature<IEffects>(out var effects));
        using var slot = effects.CreateSlot(new EaxReverbSettings());
        var builder = new Builder(); var before = builder.Capture("before");
        builder.Add(f => new ProcessingTests.Gain(f, 1)); var after = builder.Capture("after");
        using var source = device.CreateSource(PreparedClipTests.Tone(), new SourceOptions { Processing = builder.Build() });
        Assert.True(source.TryGetFeature<IProcessingSends>(out var sends));
        Assert.True(source.TryGetFeature<IEffectSends>(out var legacy));
        sends.SetSend(0, before, slot); source.IsLooping = true; source.Play();
        device.Render(new float[1024]);
        sends.SetSend(0, before, slot, new() { Gain = .2f });
        Assert.Throws<InvalidOperationException>(() => sends.SetSend(0, after, slot));
        Assert.Throws<InvalidOperationException>(() => source.ConfigureProcessing(null));
        Assert.Throws<ArgumentOutOfRangeException>(() => sends.SetSend(effects.MaximumSends, before, slot));
        legacy.SetSend(0, slot); // Replaces, rather than doubles, the earlier captured route.
        slot.Dispose(); device.Render(new float[1024]);
        source.Stop(); source.ConfigureProcessing(null);
        Assert.Equal(source.Format.Channels, source.RenderedFormat.Channels);
    }

    [Fact]
    public void GroupsCreatedBeforeRoutingIncludeLaterWetEndpointsAndSeekClearsTheirHistory()
    {
        using var engine = new SoundFlow.Backends.OpenAL.Engine();
        using var device = engine.CreateRenderDevice(OpenAlPlaybackTests.Stereo);
        Assert.True(device.TryGetFeature<IEffects>(out var effects));
        Assert.True(device.TryGetFeature<ISourceGroups>(out var groups));
        using var slot = effects.CreateSlot(new ReverbSettings());
        var builder = new Builder(); var point = builder.Capture("input");
        using var source = device.CreateSource(PreparedClipTests.Tone(), new SourceOptions { Processing = builder.Build() });
        using var group = groups.CreateGroup(source);
        Assert.True(source.TryGetFeature<IProcessingSends>(out var sends)); sends.SetSend(0, point, slot);
        group.Play(); device.Render(new float[1024]); group.Pause();
        Assert.True(source.Seek(0)); group.Play(); device.Render(new float[1024]); group.Stop();
        Assert.Equal(SoundFlow.Enums.PlaybackState.Stopped, source.State);
    }

    [Fact]
    public void ProcessedPoolCreatesFreshStateAndConfiguresRoutesBeforePlay()
    {
        using var engine = new SoundFlow.Backends.OpenAL.Engine();
        using var device = engine.CreateRenderDevice(OpenAlPlaybackTests.Stereo);
        using var pool = device.CreatePool(new() { Capacity = 1 });
        var created = 0; var configured = 0;
        var builder = new Builder().Add(f => { created++; return new ProcessingTests.StereoBlock(f); });
        var definition = builder.Build();
        var options = new PlaybackOptions
        {
            Mode = PlaybackMode.Processed,
            ProcessingFactory = () => definition,
            Configure = source => { Assert.Equal(SoundFlow.Enums.PlaybackState.Stopped, source.State); configured++; }
        };
        var first = pool.Play(PreparedClipTests.Tone(), options); first.Stop();
        using var next = pool.Play(PreparedClipTests.Tone(), options);
        Assert.Equal(2, created); Assert.Equal(2, configured);
        first.Dispose(); Assert.Null(next.Outcome);
        Assert.True(next.Source.SupportsSoftwareProcessing);
    }
}
