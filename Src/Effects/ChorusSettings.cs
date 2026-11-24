// Parameter values and ranges follow OpenAL Soft 1.25.2 include/AL/efx.h.
namespace SoundFlow.Effects;

/// <summary>Blends modulated delayed copies to produce a thicker, ensemble-like sound.</summary>
/// <remarks>Values form an immutable effect snapshot. Validate before native allocation; use a record with-expression to customize without mutating shared presets.</remarks>
public sealed record ChorusSettings : Settings
{
    /// <summary>Oscillator waveform used to vary the effect over time. Range 0 through 1; default 1. 0 = sinusoid; 1 = triangle.</summary>
    public ModulationWaveform Waveform { get; init; } = (ModulationWaveform)1;
    /// <summary>Left/right modulation phase difference in degrees. Range -180 through 180; default 90.</summary>
    public int Phase { get; init; } = 90;
    /// <summary>Modulation frequency in cycles per second (Hz). Range 0.0f through 10.0f; default 1.1f.</summary>
    public float Rate { get; init; } = 1.1f;
    /// <summary>Fractional modulation depth; zero keeps the delay constant. Range 0.0f through 1.0f; default 0.1f.</summary>
    public float Depth { get; init; } = 0.1f;
    /// <summary>Amount of output fed back into the effect; negative values invert feedback polarity. Range -1.0f through 1.0f; default 0.25f.</summary>
    public float Feedback { get; init; } = 0.25f;
    /// <summary>Base delay in seconds before the effected signal is heard. Range 0.0f through 0.016f; default 0.016f.</summary>
    public float Delay { get; init; } = 0.016f;
    /// <inheritdoc />
    public override void Validate()
    {
        Range((int)Waveform, 0, 1, nameof(Waveform));
        Range(Phase, -180, 180, nameof(Phase));
        Range(Rate, 0.0f, 10.0f, nameof(Rate));
        Range(Depth, 0.0f, 1.0f, nameof(Depth));
        Range(Feedback, -1.0f, 1.0f, nameof(Feedback));
        Range(Delay, 0.0f, 0.016f, nameof(Delay));
    }
}
