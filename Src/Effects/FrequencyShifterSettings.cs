// Parameter values and ranges follow OpenAL Soft 1.25.2 include/AL/efx.h.
namespace SoundFlow.Effects;

/// <summary>Adds or subtracts a fixed frequency from each channel, unlike proportional pitch shifting.</summary>
/// <remarks>Values form an immutable effect snapshot. Validate before native allocation; use a record with-expression to customize without mutating shared presets.</remarks>
public sealed record FrequencyShifterSettings : Settings
{
    /// <summary>Fixed frequency offset in Hz, with direction selected independently for each channel. Range 0.0f through 24000.0f; default 0.0f.</summary>
    public float Frequency { get; init; } = 0.0f;
    /// <summary>Whether the left channel is shifted up, down, or left unchanged. Range 0 through 2; default 0. 0 = down, 1 = up, 2 = off.</summary>
    public FrequencyShiftDirection LeftDirection { get; init; } = (FrequencyShiftDirection)0;
    /// <summary>Whether the right channel is shifted up, down, or left unchanged. Range 0 through 2; default 0. 0 = down, 1 = up, 2 = off.</summary>
    public FrequencyShiftDirection RightDirection { get; init; } = (FrequencyShiftDirection)0;
    /// <inheritdoc />
    public override void Validate()
    {
        Range(Frequency, 0.0f, 24000.0f, nameof(Frequency));
        Range((int)LeftDirection, 0, 2, nameof(LeftDirection));
        Range((int)RightDirection, 0, 2, nameof(RightDirection));
    }
}
