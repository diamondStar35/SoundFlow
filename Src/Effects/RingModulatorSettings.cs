// Parameter values and ranges follow OpenAL Soft 1.25.2 include/AL/efx.h.
namespace SoundFlow.Effects;

/// <summary>Multiplies the input by an oscillator, creating sum/difference sidebands.</summary>
/// <remarks>Values form an immutable effect snapshot. Validate before native allocation; use a record with-expression to customize without mutating shared presets.</remarks>
public sealed record RingModulatorSettings : Settings
{
    /// <summary>Frequency in Hz of the ring-modulation oscillator. Range 0.0f through 8000.0f; default 440.0f.</summary>
    public float Frequency { get; init; } = 440.0f;
    /// <summary>High-pass cutoff frequency in Hz applied to the ring-modulated output. Range 0.0f through 24000.0f; default 800.0f.</summary>
    public float HighPassCutoff { get; init; } = 800.0f;
    /// <summary>Oscillator waveform used to vary the effect over time. Range 0 through 2; default 0. Values follow the documented EFX waveform enumeration.</summary>
    public RingModulatorWaveform Waveform { get; init; } = (RingModulatorWaveform)0;
    /// <inheritdoc />
    public override void Validate()
    {
        Range(Frequency, 0.0f, 8000.0f, nameof(Frequency));
        Range(HighPassCutoff, 0.0f, 24000.0f, nameof(HighPassCutoff));
        Range((int)Waveform, 0, 2, nameof(Waveform));
    }
}
