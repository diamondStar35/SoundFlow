// Parameter values and ranges follow OpenAL Soft 1.25.2 include/AL/efx.h.
namespace SoundFlow.Effects;

/// <summary>Morphs between two phoneme/formant filter patterns under oscillator control.</summary>
/// <remarks>Values form an immutable effect snapshot. Validate before native allocation; use a record with-expression to customize without mutating shared presets.</remarks>
public sealed record VocalMorpherSettings : Settings
{
    /// <summary>First vocal formant pattern used by the morphing oscillator. Range 0 through 29; default 0.</summary>
    public VocalPhoneme PhonemeA { get; init; } = (VocalPhoneme)0;
    /// <summary>Pitch offset in semitones applied to the first formant pattern. Range -24 through 24; default 0.</summary>
    public int PhonemeACoarseTuning { get; init; } = 0;
    /// <summary>Second vocal formant pattern used by the morphing oscillator. Range 0 through 29; default 10.</summary>
    public VocalPhoneme PhonemeB { get; init; } = (VocalPhoneme)10;
    /// <summary>Pitch offset in semitones applied to the second formant pattern. Range -24 through 24; default 0.</summary>
    public int PhonemeBCoarseTuning { get; init; } = 0;
    /// <summary>Oscillator waveform used to vary the effect over time. Range 0 through 2; default 0. Values follow the documented EFX waveform enumeration.</summary>
    public VocalMorpherWaveform Waveform { get; init; } = (VocalMorpherWaveform)0;
    /// <summary>Modulation frequency in cycles per second (Hz). Range 0.0f through 10.0f; default 1.41f.</summary>
    public float Rate { get; init; } = 1.41f;
    /// <inheritdoc />
    public override void Validate()
    {
        Range((int)PhonemeA, 0, 29, nameof(PhonemeA));
        Range(PhonemeACoarseTuning, -24, 24, nameof(PhonemeACoarseTuning));
        Range((int)PhonemeB, 0, 29, nameof(PhonemeB));
        Range(PhonemeBCoarseTuning, -24, 24, nameof(PhonemeBCoarseTuning));
        Range((int)Waveform, 0, 2, nameof(Waveform));
        Range(Rate, 0.0f, 10.0f, nameof(Rate));
    }
}
