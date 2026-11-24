// Parameter values and ranges follow OpenAL Soft 1.25.2 include/AL/efx.h.
namespace SoundFlow.Effects;

/// <summary>Creates repeating stereo delay taps with feedback and high-frequency damping.</summary>
/// <remarks>Values form an immutable effect snapshot. Validate before native allocation; use a record with-expression to customize without mutating shared presets.</remarks>
public sealed record EchoSettings : Settings
{
    /// <summary>Base delay in seconds before the effected signal is heard. Range 0.0f through 0.207f; default 0.1f.</summary>
    public float Delay { get; init; } = 0.1f;
    /// <summary>Additional delay in seconds between the left and right echo taps. Range 0.0f through 0.404f; default 0.1f.</summary>
    public float LeftRightDelay { get; init; } = 0.1f;
    /// <summary>High-frequency damping applied on each pass through the echo feedback loop. Range 0.0f through 0.99f; default 0.5f.</summary>
    public float Damping { get; init; } = 0.5f;
    /// <summary>Fraction of each echo returned to the delay line; larger values produce longer repeats. Range 0.0f through 1.0f; default 0.5f.</summary>
    public float Feedback { get; init; } = 0.5f;
    /// <summary>Stereo placement of alternating echoes, from opposing channels through centered output. Range -1.0f through 1.0f; default -1.0f.</summary>
    public float Spread { get; init; } = -1.0f;
    /// <inheritdoc />
    public override void Validate()
    {
        Range(Delay, 0.0f, 0.207f, nameof(Delay));
        Range(LeftRightDelay, 0.0f, 0.404f, nameof(LeftRightDelay));
        Range(Damping, 0.0f, 0.99f, nameof(Damping));
        Range(Feedback, 0.0f, 1.0f, nameof(Feedback));
        Range(Spread, -1.0f, 1.0f, nameof(Spread));
    }
}
