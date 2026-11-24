// Parameter values and ranges follow OpenAL Soft 1.25.2 include/AL/efx.h.
namespace SoundFlow.Effects;

/// <summary>Shapes the wet signal with low/high shelves and two parametric middle bands.</summary>
/// <remarks>Values form an immutable effect snapshot. Validate before native allocation; use a record with-expression to customize without mutating shared presets.</remarks>
public sealed record EqualizerSettings : Settings
{
    /// <summary>Linear gain of the low-frequency shelving band. Range 0.126f through 7.943f; default 1.0f.</summary>
    public float LowGain { get; init; } = 1.0f;
    /// <summary>Low-frequency shelf cutoff in Hz. Range 50.0f through 800.0f; default 200.0f.</summary>
    public float LowCutoff { get; init; } = 200.0f;
    /// <summary>Linear gain of the first parametric middle band. Range 0.126f through 7.943f; default 1.0f.</summary>
    public float Mid1Gain { get; init; } = 1.0f;
    /// <summary>Center frequency in Hz of the first parametric band. Range 200.0f through 3000.0f; default 500.0f.</summary>
    public float Mid1Center { get; init; } = 500.0f;
    /// <summary>Width in octaves of the first parametric band. Range 0.01f through 1.0f; default 1.0f.</summary>
    public float Mid1Width { get; init; } = 1.0f;
    /// <summary>Linear gain of the second parametric middle band. Range 0.126f through 7.943f; default 1.0f.</summary>
    public float Mid2Gain { get; init; } = 1.0f;
    /// <summary>Center frequency in Hz of the second parametric band. Range 1000.0f through 8000.0f; default 3000.0f.</summary>
    public float Mid2Center { get; init; } = 3000.0f;
    /// <summary>Width in octaves of the second parametric band. Range 0.01f through 1.0f; default 1.0f.</summary>
    public float Mid2Width { get; init; } = 1.0f;
    /// <summary>Linear gain of the high-frequency shelving band. Range 0.126f through 7.943f; default 1.0f.</summary>
    public float HighGain { get; init; } = 1.0f;
    /// <summary>High-frequency shelf cutoff in Hz. Range 4000.0f through 16000.0f; default 6000.0f.</summary>
    public float HighCutoff { get; init; } = 6000.0f;
    /// <inheritdoc />
    public override void Validate()
    {
        Range(LowGain, 0.126f, 7.943f, nameof(LowGain));
        Range(LowCutoff, 50.0f, 800.0f, nameof(LowCutoff));
        Range(Mid1Gain, 0.126f, 7.943f, nameof(Mid1Gain));
        Range(Mid1Center, 200.0f, 3000.0f, nameof(Mid1Center));
        Range(Mid1Width, 0.01f, 1.0f, nameof(Mid1Width));
        Range(Mid2Gain, 0.126f, 7.943f, nameof(Mid2Gain));
        Range(Mid2Center, 1000.0f, 8000.0f, nameof(Mid2Center));
        Range(Mid2Width, 0.01f, 1.0f, nameof(Mid2Width));
        Range(HighGain, 0.126f, 7.943f, nameof(HighGain));
        Range(HighCutoff, 4000.0f, 16000.0f, nameof(HighCutoff));
    }
}
