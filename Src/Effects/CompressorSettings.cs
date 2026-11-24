// Parameter values and ranges follow OpenAL Soft 1.25.2 include/AL/efx.h.
namespace SoundFlow.Effects;

/// <summary>Controls the native compressor stage for reducing input dynamic range.</summary>
/// <remarks>Values form an immutable effect snapshot. Validate before native allocation; use a record with-expression to customize without mutating shared presets.</remarks>
public sealed record CompressorSettings : Settings
{
    /// <summary>Enables the native dynamics compressor; false bypasses its compression stage. Range 0 through 1; default true.</summary>
    public bool Enabled { get; init; } = true;
    /// <inheritdoc />
    public override void Validate()
    {
    }
}
