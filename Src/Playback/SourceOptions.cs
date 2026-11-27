using SoundFlow.Processing;

namespace SoundFlow.Playback;

/// <summary>Software processing selected before source playback. The chain contains factories, not shared live DSP state.</summary>
public sealed record SourceOptions
{
    /// <summary>Ordered processing definition; null selects the identity chain. Input sample rate is retained through all processors.</summary>
    public Chain? Processing { get; init; }
    /// <summary>
    /// Optional processing performed on demand after decoded PCM is queued. Factories must construct
    /// processors safe for rendering: no blocking, allocation, I/O, or playback controls in Process/Drain.
    /// OpenAL requires AL_SOFT_callback_buffer for this path.
    /// </summary>
    public Chain? RenderProcessing { get; init; }
    /// <summary>Frames gathered before each render-chain call. The default is 256; fixed effects must match.</summary>
    public int RenderBlockFrames { get; init; } = 256;
}
