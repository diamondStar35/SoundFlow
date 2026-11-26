using SoundFlow.Playback;
using SoundFlow.Effects;
using SoundFlow.Features;
using Xunit;

namespace SoundFlow.Tests;

public sealed class PreparedClipTests
{
    [Fact]
    public void PreparedSourcesCanBeScheduledAndRestartedAsAGroup()
    {
        using var engine = new SoundFlow.Backends.OpenAL.Engine();
        using var device = engine.CreateRenderDevice(OpenAlPlaybackTests.Stereo);
        Assert.True(device.TryGetFeature<IPreparedClips>(out var clips));
        Assert.True(device.TryGetFeature<ISourceGroups>(out var groups));
        Assert.True(device.TryGetFeature<IScheduledPlayback>(out var scheduler));
        Assert.True(device.TryGetFeature<IPlaybackTiming>(out var timing));
        using var prepared = clips.Prepare(Tone(1024));
        using var first = prepared.CreateSource(); using var second = prepared.CreateSource();
        using var group = groups.CreateGroup(first, second);
        var completed = 0; first.PlaybackEnded += (_, _) => Interlocked.Increment(ref completed);
        scheduler.PlayAt(group, timing.GetClock().Nanoseconds + 100_000_000);
        var silent = new float[2048]; device.Render(silent);
        Assert.All(silent, value => Assert.Equal(0, value));
        device.Render(new float[16384]);
        Assert.True(SpinWait.SpinUntil(() => completed == 1, TimeSpan.FromSeconds(2)));
        group.Stop(); group.Play(); device.Render(new float[8192]);
        Assert.True(SpinWait.SpinUntil(() => completed == 2, TimeSpan.FromSeconds(2)));
    }
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
