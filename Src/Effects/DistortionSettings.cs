// Parameter values and ranges follow OpenAL Soft 1.25.2 include/AL/efx.h.
namespace SoundFlow.Effects;

/// <summary>Shapes the signal nonlinearly and filters the result with low-pass and equalizer controls.</summary>
/// <remarks>Values form an immutable effect snapshot. Validate before native allocation; use a record with-expression to customize without mutating shared presets.</remarks>
public sealed record DistortionSettings : Settings
{
    /// <summary>Strength of the distortion waveform shaping. Range 0.0f through 1.0f; default 0.2f.</summary>
    public float Edge { get; init; } = 0.2f;
    /// <summary>Linear gain applied to the distorted output. Range 0.01f through 1.0f; default 0.05f.</summary>
    public float Gain { get; init; } = 0.05f;
    /// <summary>Low-pass cutoff in Hz after the distortion stage. Range 80.0f through 24000.0f; default 8000.0f.</summary>
    public float LowPassCutoff { get; init; } = 8000.0f;
    /// <summary>Center frequency in Hz of the post-distortion equalizer. Range 80.0f through 24000.0f; default 3600.0f.</summary>
    public float EqCenter { get; init; } = 3600.0f;
    /// <summary>Bandwidth in Hz of the post-distortion equalizer. Range 80.0f through 24000.0f; default 3600.0f.</summary>
    public float EqBandwidth { get; init; } = 3600.0f;
    /// <inheritdoc />
    public override void Validate()
    {
        Range(Edge, 0.0f, 1.0f, nameof(Edge));
        Range(Gain, 0.01f, 1.0f, nameof(Gain));
        Range(LowPassCutoff, 80.0f, 24000.0f, nameof(LowPassCutoff));
        Range(EqCenter, 80.0f, 24000.0f, nameof(EqCenter));
        Range(EqBandwidth, 80.0f, 24000.0f, nameof(EqBandwidth));
    }
}
