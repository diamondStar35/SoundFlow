// Parameter values and ranges follow OpenAL Soft 1.25.2 include/AL/efx.h.
namespace SoundFlow.Effects;

/// <summary>Transposes the effected signal by semitones and cents without changing the source transport speed.</summary>
/// <remarks>Values form an immutable effect snapshot. Validate before native allocation; use a record with-expression to customize without mutating shared presets.</remarks>
public sealed record PitchShifterSettings : Settings
{
    /// <summary>Pitch offset in whole semitones. Range -12 through 12; default 12.</summary>
    public int CoarseTune { get; init; } = 12;
    /// <summary>Additional pitch offset in cents (one hundredth of a semitone). Range -50 through 50; default 0.</summary>
    public int FineTune { get; init; } = 0;
    /// <inheritdoc />
    public override void Validate()
    {
        Range(CoarseTune, -12, 12, nameof(CoarseTune));
        Range(FineTune, -50, 50, nameof(FineTune));
    }
}
