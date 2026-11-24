namespace SoundFlow.Playback;

/// <summary>Outcome of one transport read. Starvation keeps a live source playable; completion ends its input.</summary>
public enum ReadStatus
{
    /// <summary>At least one interleaved sample was produced; input may provide more later.</summary>
    Data,
    /// <summary>No samples are currently available, but the input has not ended.</summary>
    Starved,
    /// <summary>The input ended. Samples in this result must still be rendered before audible completion.</summary>
    Completed
}

/// <summary>Number of valid interleaved float samples and the state of their input after reading.</summary>
public readonly record struct ReadResult(int Samples, ReadStatus Status);
