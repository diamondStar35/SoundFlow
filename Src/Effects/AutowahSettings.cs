// Parameter values and ranges follow OpenAL Soft 1.25.2 include/AL/efx.h.
namespace SoundFlow.Effects;

/// <summary>Moves a resonant filter in response to the input amplitude envelope.</summary>
/// <remarks>Values form an immutable effect snapshot. Validate before native allocation; use a record with-expression to customize without mutating shared presets.</remarks>
public sealed record AutowahSettings : Settings
{
    /// <summary>Envelope attack time in seconds; smaller values respond faster to rising input. Range 0.0001f through 1.0f; default 0.06f.</summary>
    public float AttackTime { get; init; } = 0.06f;
    /// <summary>Envelope release time in seconds; larger values retain the filter movement longer. Range 0.0001f through 1.0f; default 0.06f.</summary>
    public float ReleaseTime { get; init; } = 0.06f;
    /// <summary>Resonant emphasis of the envelope-controlled wah filter. Range 2.0f through 1000.0f; default 1000.0f.</summary>
    public float Resonance { get; init; } = 1000.0f;
    /// <summary>Peak output gain of the wah filter as a linear multiplier. Range 0.00003f through 31621.0f; default 11.22f.</summary>
    public float PeakGain { get; init; } = 11.22f;
    /// <inheritdoc />
    public override void Validate()
    {
        Range(AttackTime, 0.0001f, 1.0f, nameof(AttackTime));
        Range(ReleaseTime, 0.0001f, 1.0f, nameof(ReleaseTime));
        Range(Resonance, 2.0f, 1000.0f, nameof(Resonance));
        Range(PeakGain, 0.00003f, 31621.0f, nameof(PeakGain));
    }
}
