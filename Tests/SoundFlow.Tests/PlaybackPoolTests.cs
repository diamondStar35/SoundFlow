using SoundFlow.Playback;
using SoundFlow.Effects;
using Xunit;

namespace SoundFlow.Tests;

public sealed class PlaybackPoolTests
{
    [Fact]
    public void PoolStealsOnlyLowerPrioritiesAndStaleLeasesCannotControlReusedVoices()
    {
        using var engine = new SoundFlow.Backends.OpenAL.Engine();
        using var device = engine.CreateRenderDevice(OpenAlPlaybackTests.Stereo);
        using var pool = device.CreatePool(new() { Capacity = 2 });
        var clip = PreparedClipTests.Tone();
        using var protectedVoice = pool.Play(clip, new() { Priority = -100, Protected = true, Looping = true });
        using var old = pool.Play(clip, new() { Priority = 1, Looping = true });
        Assert.False(pool.TryPlay(clip, out _, new() { Priority = 1 }));
        using var replacement = pool.Play(clip, new() { Priority = 2, Looping = true });
        Assert.Equal(PlaybackEndReason.Stolen, old.Outcome?.Reason);
        old.Dispose(); Assert.Null(replacement.Outcome);
        Assert.Throws<ObjectDisposedException>(() => old.Source.Play());
        Assert.Null(protectedVoice.Outcome);
        Assert.Equal(1, pool.GetSnapshot().Stolen);
        Assert.Equal(2, pool.GetSnapshot().Occupied);
    }

    [Fact]
    public void ReturnedPreparedVoiceDoesNotRetainThePreviousDirectFilter()
    {
        using var engine = new SoundFlow.Backends.OpenAL.Engine();
        using var device = engine.CreateRenderDevice(OpenAlPlaybackTests.Stereo);
        using var pool = device.CreatePool(new() { Capacity = 1 });
        var clip = PreparedClipTests.Tone();
        var old = pool.Play(clip);
        Assert.True(old.Source.TryGetFeature<IEffectSends>(out var routing));
        routing.SetDirectFilter(new FilterSettings { Gain = 0 });
        old.Stop();
        using var next = pool.Play(clip);
        var output = new float[2048]; device.Render(output);
        Assert.Contains(output.Skip(256), x => Math.Abs(x) > .01);
        Assert.Equal(PlaybackEndReason.Stopped, old.Outcome?.Reason);
    }

    [Fact]
    public void PoolReportsNaturalCompletionOnceAndDeviceDisposalTerminatesOccupiedLeases()
    {
        using var engine = new SoundFlow.Backends.OpenAL.Engine();
        var device = engine.CreateRenderDevice(OpenAlPlaybackTests.Stereo);
        var pool = device.CreatePool();
        var lease = pool.Play(PreparedClipTests.Tone(256));
        var calls = 0; lease.Ended += (_, _) => Interlocked.Increment(ref calls);
        device.Render(new float[8192]);
        Assert.True(SpinWait.SpinUntil(() => calls == 1, TimeSpan.FromSeconds(2)));
        Assert.Equal(PlaybackEndReason.Completed, lease.Outcome?.Reason);
        var looping = pool.Play(PreparedClipTests.Tone(), new() { Looping = true });
        device.Dispose();
        Assert.Equal(PlaybackEndReason.PoolDisposed, looping.Outcome?.Reason);
        lease.Dispose(); Assert.Equal(1, calls);
    }

}
