namespace SoundFlow.Playback;

/// <summary>Engine-wide decoding limits. Set on the engine before creating players or devices.</summary>
public sealed record PlaybackConfiguration
{
    /// <summary>Fixed decoding threads per engine, independent of source count. At least one is required.</summary>
    public int WorkerCount { get; init; } = Math.Clamp(Environment.ProcessorCount - 1, 2, 4);
    /// <summary>Complete frames in each reusable decoded block.</summary>
    public int BufferFrames { get; init; } = 512;
    /// <summary>Maximum decoded blocks retained by one session, including checked-out blocks.</summary>
    public int QueueBuffers { get; init; } = 4;
    /// <summary>Optional limit for one cooperative read. Null leaves the input's own timeout policy in charge.</summary>
    public TimeSpan? ReadTimeout { get; init; }

    internal void Validate()
    {
        if (WorkerCount is < 1 or > 64) throw new ArgumentOutOfRangeException(nameof(WorkerCount));
        if (BufferFrames is < 64 or > 16384) throw new ArgumentOutOfRangeException(nameof(BufferFrames));
        if (QueueBuffers is < 2 or > 16) throw new ArgumentOutOfRangeException(nameof(QueueBuffers));
        if (ReadTimeout is { } timeout && (timeout <= TimeSpan.Zero || timeout.TotalMilliseconds > int.MaxValue))
            throw new ArgumentOutOfRangeException(nameof(ReadTimeout));
    }
}

/// <summary>A copied scheduler snapshot. Counts concern this engine, not the CLR thread pool or native codec internals.</summary>
public readonly record struct DecodingSnapshot(int Workers, int Sessions, int Pending, int Active,
    long BufferedFrames, long Starvations, long Failures);
