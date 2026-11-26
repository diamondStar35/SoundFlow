using System.Numerics;
using SoundFlow.Playback;

namespace SoundFlow.Effects;

/// <summary>Convolves the wet input with an immutable measured or authored impulse response.</summary>
/// <remarks>
/// OpenAL Soft 1.25.2 exposes this experimentally as AL_SOFTX_convolution_effect. Responses are uploaded in full,
/// retained while attached, and never normalized automatically. Use send/slot gain to control potentially large output levels.
/// </remarks>
public sealed record ConvolutionSettings : Settings
{
    /// <summary>Nonempty mono or stereo F32 response. The response sample rate is retained; native rendering performs any required resampling.</summary>
    public required Clip ImpulseResponse { get; init; }
    /// <summary>Response forward orientation in right-handed world coordinates. Default looks along -Z.</summary>
    public Vector3 Forward { get; init; } = -Vector3.UnitZ;
    /// <summary>Response up orientation. Must be finite, nonzero and independent of Forward.</summary>
    public Vector3 Up { get; init; } = Vector3.UnitY;
    /// <inheritdoc />
    public override void Validate()
    {
        ArgumentNullException.ThrowIfNull(ImpulseResponse);
        if (ImpulseResponse.Frames == 0 || ImpulseResponse.Format.Channels is not (1 or 2))
            throw new ArgumentException("Convolution requires a nonempty mono or stereo impulse response.", nameof(ImpulseResponse));
        foreach (var vector in new[] { Forward, Up })
            if (!float.IsFinite(vector.X) || !float.IsFinite(vector.Y) || !float.IsFinite(vector.Z) || vector.LengthSquared() < 1e-12f)
                throw new ArgumentException("Response orientation must contain finite, nonzero vectors.");
        if (Vector3.Cross(Vector3.Normalize(Forward), Vector3.Normalize(Up)).LengthSquared() < 1e-12f)
            throw new ArgumentException("Response forward and up vectors must be independent.");
    }
}
