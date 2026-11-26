using SoundFlow.Processing;

namespace SoundFlow.Playback;

/// <summary>Software processing selected before source playback. The chain contains factories, not shared live DSP state.</summary>
public sealed record SourceOptions
{
    /// <summary>Ordered processing definition; null selects the identity chain. Input sample rate is retained through all processors.</summary>
    public Chain? Processing { get; init; }
}
