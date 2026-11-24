using SoundFlow.Playback;

namespace SoundFlow.Features;

/// <summary>A monotonic device-clock reading and its estimated physical output latency, in nanoseconds.</summary>
/// <remarks>The timestamp belongs to one device, not wall-clock time. Offline clocks advance only while rendering.</remarks>
public readonly record struct ClockSnapshot(long Nanoseconds, long LatencyNanoseconds);

/// <summary>Native sample offset, estimated output latency, and the source's mapped content time in seconds.</summary>
/// <remarks>OffsetFrames is measured in the PCM uploaded to the native queue. ContentSeconds additionally accounts for transport speed and loops.</remarks>
public readonly record struct SourceTiming(double OffsetFrames, long LatencyNanoseconds, double ContentSeconds);

/// <summary>Optional device-clock and source-latency queries. These snapshots are intended for synchronization, not a sample callback.</summary>
public interface IPlaybackTiming
{
    /// <summary>Returns a native device-clock/latency pair from a single query.</summary>
    ClockSnapshot GetClock();
    /// <summary>Queries one live source owned by this device.</summary>
    SourceTiming GetTiming(Source source);
}

/// <summary>Optional grouped playback using a backend's synchronized native operations.</summary>
public interface ISourceGroups
{
    /// <summary>Creates a non-owning group. Empty, duplicate, disposed, and foreign-device members are rejected.</summary>
    SourceGroup CreateGroup(params Source[] sources);
}

/// <summary>Optional playback scheduled against the owning device's monotonic clock.</summary>
/// <remarks>Negative times are invalid; past times start immediately. Stop cancels pending playback. Scheduling never uses system time.</remarks>
public interface IScheduledPlayback
{
    /// <summary>Queues and schedules a stopped or paused source at the specified device-clock nanosecond timestamp.</summary>
    void PlayAt(Source source, long nanoseconds);
    /// <summary>Prepares and schedules all members with one native vector operation.</summary>
    void PlayAt(SourceGroup group, long nanoseconds);
}

/// <summary>Optional coordinated changes to native scene state.</summary>
public interface IUpdateBatch
{
    /// <summary>Executes controls on the device worker while deferring native updates until the delegate finishes.</summary>
    /// <remarks>Use synchronous property setters only; do not render, dispose, switch devices, or await work here. Applied setters are not rolled back if a later setter throws.</remarks>
    void Apply(Action updates);
}
