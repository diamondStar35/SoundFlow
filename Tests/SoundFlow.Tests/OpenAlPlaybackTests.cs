using BackendEngine = SoundFlow.Backends.OpenAL.Engine;
using SoundFlow.Backends.OpenAL;
using SoundFlow.Enums;
using SoundFlow.Features;
using SoundFlow.Playback;
using SoundFlow.Structs;
using Xunit;

namespace SoundFlow.Tests;

public sealed class OpenAlPlaybackTests
{
    internal static readonly AudioFormat Stereo = new() { Channels = 2, SampleRate = 48000, Format = SampleFormat.F32, Layout = ChannelLayout.Stereo };

    [Fact]
    public void OfflineDeviceDecodesAndRendersWithoutMiniAudioOrPhysicalHardware()
    {
        using var engine = new SoundFlow.Backends.OpenAL.Engine();
        Assert.Equal("SoundFlow.Codecs.FFMpeg", Assert.Single(engine.GetRegisteredCodecs("wav")).FactoryId);
        Assert.True(engine.TryGetFeature<IOfflineRendering>(out var offline));
        using var device = offline.CreateRenderDevice(Stereo);
        using var source = device.CreateSource(new SoundFlow.Playback.Clip(Enumerable.Repeat(.25f, 4096).ToArray(), TransportTests.Mono));
        int completions = 0;
        source.PlaybackEnded += (_, _) => completions++;
        source.Play();
        var output = new float[8192];
        device.Render(output);
        Assert.Contains(output, x => Math.Abs(x) > .01f);
        device.Render(new float[2048]);
        Assert.Equal(PlaybackState.Stopped, source.State);
        Assert.True(SpinWait.SpinUntil(() => Volatile.Read(ref completions) == 1, TimeSpan.FromSeconds(2)));
        Assert.Null(typeof(BackendEngine).Assembly.GetType("SoundFlow.Backends.MiniAudio.Engine"));
    }

    [Fact]
    public void DifferentContextsKeepTheirSourcesIsolated()
    {
        using var engine = new SoundFlow.Backends.OpenAL.Engine();
        Assert.True(engine.TryGetFeature<IOfflineRendering>(out var offline));
        using var first = offline.CreateRenderDevice(Stereo);
        using var second = offline.CreateRenderDevice(Stereo);
        using var source = first.CreateSource(new SoundFlow.Playback.Clip(Enumerable.Repeat(.2f, 8192).ToArray(), TransportTests.Mono));
        source.Play();
        var silent = new float[1024]; var audible = new float[1024];
        second.Render(silent); first.Render(audible);
        Assert.All(silent, x => Assert.Equal(0, x));
        Assert.Contains(audible, x => Math.Abs(x) > .01f);
    }
}
