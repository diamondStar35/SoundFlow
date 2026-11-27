using SoundFlow.Backends.OpenAL;
using SoundFlow.Effects;
using SoundFlow.Enums;
using SoundFlow.Features;
using SoundFlow.Playback;
using SoundFlow.Processing;
using SoundFlow.Structs;
using Xunit;

namespace SoundFlow.Tests;

public sealed class OnDemandOpenAlTests
{
    [Fact]
    public void PausedCallbackDoesNotAdvanceTheSharedTimeline()
    {
        using var engine = new SoundFlow.Backends.OpenAL.Engine();
        using var device = engine.CreateRenderDevice(OpenAlPlaybackTests.Stereo);
        using var source = device.CreateSource(new Clip(Enumerable.Repeat(.2f, 48000).ToArray(), TransportTests.Mono),
            new SourceOptions { RenderProcessing = new Builder().Add(format => new Stereo(format)).Build() });
        var output = new float[2048];
        source.Play();
        device.Render(output);
        source.Pause();
        var time = source.Time;
        device.Render(output);
        Assert.Equal(time, source.Time);
        Assert.Equal(PlaybackState.Paused, source.State);
        source.Play();
        device.Render(output);
        Assert.True(source.Time > time);
        Assert.Contains(output, sample => Math.Abs(sample) > .01f);
    }

    [Fact]
    public void MonoRenderChainsRemainAudibleOnStereoOutputs()
    {
        using var engine = new SoundFlow.Backends.OpenAL.Engine();
        using var device = engine.CreateRenderDevice(OpenAlPlaybackTests.Stereo);
        using var source = device.CreateSource(new Clip(Enumerable.Repeat(.3f, 4096).ToArray(), TransportTests.Mono),
            new SourceOptions { RenderProcessing = Chain.Empty });
        source.Play();
        var output = new float[2048]; device.Render(output);
        Assert.Contains(output, sample => Math.Abs(sample) > .01f);
    }

    [Fact]
    public void StoppedProducerReplacementRebuildsCaptureStorageWithoutReplacingLateDsp()
    {
        using var engine = new SoundFlow.Backends.OpenAL.Engine();
        using var device = engine.CreateRenderDevice(OpenAlPlaybackTests.Stereo);
        using var source = device.CreateSource(new Clip(Enumerable.Repeat(.25f, 8192).ToArray(), TransportTests.Mono),
            new SourceOptions { RenderProcessing = new Builder().Add(format => new Stereo(format)).Build() });
        source.Play(); device.Render(new float[1024]); source.Stop();
        var producer = new Builder();
        var point = producer.Capture("producer");
        source.ConfigureProcessing(producer.Build());
        Assert.True(device.TryGetFeature<IEffects>(out var effects));
        using var room = effects.CreateSlot(new EaxReverbSettings());
        Assert.True(source.TryGetFeature<IProcessingSends>(out var sends));
        sends.SetSend(0, point, room);
        source.Play();
        var output = new float[2048]; device.Render(output);
        Assert.Contains(output, value => Math.Abs(value) > .01f);
    }

    [Fact]
    public void CallbackGroupsAndPooledLeasesKeepIndependentState()
    {
        using var engine = new SoundFlow.Backends.OpenAL.Engine();
        using var device = engine.CreateRenderDevice(OpenAlPlaybackTests.Stereo);
        var clip = new Clip(Enumerable.Repeat(.2f, 8192).ToArray(), TransportTests.Mono);
        var chain = new Builder().Add(format => new Stereo(format)).Build();
        using var pool = device.CreatePool(new() { Capacity = 2 });
        using var first = pool.Play(clip, new() { Mode = PlaybackMode.Processed, RenderProcessingFactory = () => chain });
        using var second = pool.Play(clip, new() { Mode = PlaybackMode.Processed, RenderProcessingFactory = () => chain });
        Assert.True(device.TryGetFeature<ISourceGroups>(out var groups));
        first.Source.Pause(); second.Source.Pause();
        using var group = groups.CreateGroup(new[] { first.Source, second.Source });
        group.Play();
        var output = new float[2048];
        device.Render(output);
        Assert.Contains(output, value => Math.Abs(value) > .1f);
        group.Pause(); group.Play();
        device.Render(output);
        Assert.Contains(output, value => Math.Abs(value) > .1f);
        Assert.True(device.TryGetFeature<IDiagnostics>(out var diagnostics));
        Assert.Equal(2, diagnostics.GetSnapshot().OnDemandSources);
    }

    [Fact]
    public void CallbackBuffersRenderAndCompleteWithoutNativeStreamingQueues()
    {
        using var engine = new SoundFlow.Backends.OpenAL.Engine();
        using var device = engine.CreateRenderDevice(OpenAlPlaybackTests.Stereo);
        using var source = device.CreateSource(new Clip(Enumerable.Repeat(.3f, 4096).ToArray(), TransportTests.Mono),
            new SourceOptions { RenderProcessing = new Builder().Add(format => new Stereo(format)).Build() });
        source.Play();
        var output = new float[16384];
        device.Render(output);
        Assert.Contains(output, value => Math.Abs(value) > .05f);
        device.Render(new float[4096]);
        Assert.Equal(PlaybackState.Stopped, source.State);
        source.Stop();
        source.Play();
        device.Render(output);
        Assert.Contains(output, value => Math.Abs(value) > .05f);
    }

    [Fact]
    public void EarlierCallbackCapturesFeedEaxAndCanBeDisposedAfterRendering()
    {
        using var engine = new SoundFlow.Backends.OpenAL.Engine();
        using var device = engine.CreateRenderDevice(OpenAlPlaybackTests.Stereo);
        Assert.True(device.TryGetFeature<IEffects>(out var effects));
        using var room = effects.CreateSlot(new EaxReverbSettings());
        var builder = new Builder();
        var point = builder.Capture("room");
        builder.Add(format => new Stereo(format));
        using var source = device.CreateSource(new Clip(Enumerable.Repeat(.25f, 8192).ToArray(), TransportTests.Mono),
            new SourceOptions { RenderProcessing = builder.Build() });
        Assert.True(source.TryGetFeature<IProcessingSends>(out var sends));
        sends.SetSend(0, point, room);
        source.Play();
        var output = new float[8192];
        device.Render(output);
        Assert.Contains(output, value => Math.Abs(value) > .01f);
        source.Stop();
        sends.SetSend(0, point, null);
        source.Dispose();
        device.Render(new float[2048]);
    }

    private sealed class Stereo(AudioFormat format) : Processor(format,
        format with { Channels = 2, Layout = AudioFormat.GetLayoutFromChannels(2) })
    {
        public override void Process(ReadOnlySpan<float> input, Span<float> output, int frames)
        {
            for (var i = 0; i < frames; i++) output[i * 2] = output[i * 2 + 1] = input[i];
        }
    }
}
