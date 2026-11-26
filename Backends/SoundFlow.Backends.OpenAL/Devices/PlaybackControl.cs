using SoundFlow.Backends.OpenAL.Playback;
using SoundFlow.Features;
using SoundFlow.Playback;
using CoreSource = SoundFlow.Playback.Source;

namespace SoundFlow.Backends.OpenAL.Devices;

internal sealed unsafe partial class Context : IPlaybackTiming, ISourceGroups, IScheduledPlayback, IUpdateBatch
{
    public ClockSnapshot GetClock() => Worker.Invoke(() =>
    {
        if (!HasTiming) throw new NotSupportedException("Native timing queries are unavailable.");
        long* pair = stackalloc long[2];
        Al.alcGetInteger64vSOFT(Device, 0x1602, 2, pair);
        Al.CheckAlc(Device, "Query device clock and latency");
        return new ClockSnapshot(pair[0], pair[1]);
    });

    public SourceTiming GetTiming(CoreSource source) => Worker.Invoke(() =>
    {
        var voice = GetVoice(source);
        if (!HasTiming) throw new NotSupportedException("Native timing queries are unavailable.");
        long* pair = stackalloc long[2];
        Al.alGetSourcei64vSOFT(voice.Id, 0x1200, pair);
        Al.CheckAl("Query source offset and latency");
        return new SourceTiming(pair[0] / 4294967296.0, pair[1], voice.Time);
    });

    internal void RequireRunningForNativePlayback()
    {
        if (!Offline && !_running) throw new InvalidOperationException("Start physical playback before using native group or scheduled controls.");
    }

    internal Voice GetVoice(CoreSource source)
    {
        ArgumentNullException.ThrowIfNull(source);
        ObjectDisposedException.ThrowIf(source.IsDisposed, source);
        if (source.Device != Owner || source is not INativeSource native || !Sources.Contains(native.Voice))
            throw new ArgumentException("The source must belong to this native context.", nameof(source));
        return native.Voice;
    }

    public SourceGroup CreateGroup(params CoreSource[] sources) => Worker.Invoke<SourceGroup>(() => new Group(this, sources));

    public void Apply(Action updates)
    {
        ArgumentNullException.ThrowIfNull(updates);
        Worker.Invoke(() =>
        {
            if (!HasDeferredUpdates) throw new NotSupportedException("Native deferred updates are unavailable.");
            ApplyNativeUpdates(updates);
            Al.CheckAl("Apply deferred controls");
        });
    }

    public void PlayAt(CoreSource source, long nanoseconds)
    {
        var voice = Worker.Invoke(() =>
        {
            ValidateTimestamp(nanoseconds);
            var member = GetVoice(source);
            if (member.State == Enums.PlaybackState.Playing) throw new InvalidOperationException("Pause or stop the source before scheduling it.");
            return member;
        });
        voice.PrepareStart();
        Worker.Invoke(() =>
        {
            GetVoice(source);
            Al.alSourcePlayAtTimeSOFT(voice.Id, nanoseconds); Al.CheckAl("Schedule source playback");
            voice.CompleteStart(nanoseconds);
        });
    }
    public void PlayAt(SourceGroup group, long nanoseconds)
    {
        Worker.Invoke(() => ValidateTimestamp(nanoseconds));
        if (group is not Group native || native.Context != this) throw new ArgumentException("The group must belong to this context.", nameof(group));
        native.Start(nanoseconds);
    }

    private void ValidateTimestamp(long nanoseconds)
    {
        RequireRunningForNativePlayback();
        if (nanoseconds < 0) throw new ArgumentOutOfRangeException(nameof(nanoseconds));
        if (!HasScheduling) throw new NotSupportedException("Native clock-scheduled playback is unavailable.");
    }
}
