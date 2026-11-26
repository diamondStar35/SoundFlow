using SoundFlow.Playback;
using SoundFlow.Effects;
using Xunit;

namespace SoundFlow.Tests;

public sealed class PreparedClipTests
{
    [Fact]
    public void SharedUploadCreatesIndependentSourcesWithoutAddingDecodeSessions()
    {
        using var engine = new SoundFlow.Backends.OpenAL.Engine();
        using var device = engine.CreateRenderDevice(OpenAlPlaybackTests.Stereo);
        Assert.True(device.TryGetFeature<IPreparedClips>(out var clips));
        using var prepared = clips.Prepare(Tone());
        var sessions = engine.Decoding.GetSnapshot().Sessions;
        using var first = prepared.CreateSource(); using var second = prepared.CreateSource();
        Assert.Equal(sessions, engine.Decoding.GetSnapshot().Sessions);
        Assert.False(first.SupportsSoftwareProcessing);
        first.Play(); second.Play(); second.Pause();
        var output = new float[2048]; device.Render(output);
        Assert.True(output.Max(Math.Abs) > .01);
        Assert.True(first.Time > 0); Assert.Equal(0, second.Time);
        Assert.Throws<NotSupportedException>(() => first.PlaybackSpeed = 2);
        Assert.Throws<NotSupportedException>(() => first.AudioBufferProcessed += static (_, _) => { });
    }

    [Fact]
    public void ActiveSourcePinsItsBufferAfterItsPreparationHandleIsDisposed()
    {
        using var engine = new SoundFlow.Backends.OpenAL.Engine();
        using var device = engine.CreateRenderDevice(OpenAlPlaybackTests.Stereo,
            new SoundFlow.Backends.OpenAL.DeviceConfiguration { PreparedClipBytes = 4096 });
        Assert.True(device.TryGetFeature<IPreparedClips>(out var clips));
        var prepared = clips.Prepare(Tone(1024));
        var source = prepared.CreateSource(); prepared.Dispose();
        Assert.Throws<InvalidOperationException>(() => clips.Prepare(Tone(1024)));
        source.Play(); var output = new float[1024]; device.Render(output);
        Assert.Contains(output, x => Math.Abs(x) > .01);
        source.Dispose();
        using var replacement = clips.Prepare(Tone(1024));
    }

    internal static Clip Tone(int frames = 4096) => new(Enumerable.Range(0, frames).Select(i => .2f * MathF.Sin(i * .1f)).ToArray(), TransportTests.Mono);
}
