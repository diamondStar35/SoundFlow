using System.Numerics;

namespace SoundFlow.Spatial;

/// <summary>Spatial source properties. Distances use metres, velocities metres/second, and cone angles degrees.</summary>
public sealed record SourceSettings
{
    /// <summary>Validates finite coordinates and authored distance/cone/environmental ranges before allocating or stealing a voice.</summary>
    public void Validate()
    {
        static void Number(float value, float min, float max, string name)
        { if (!float.IsFinite(value) || value < min || value > max) throw new ArgumentOutOfRangeException(name); }
        foreach (var vector in new[] { Position, Velocity, Direction })
            if (!float.IsFinite(vector.X) || !float.IsFinite(vector.Y) || !float.IsFinite(vector.Z)) throw new ArgumentException("Spatial vectors must be finite.");
        Number(ReferenceDistance, float.Epsilon, float.MaxValue, nameof(ReferenceDistance));
        Number(MaximumDistance, ReferenceDistance, float.MaxValue, nameof(MaximumDistance));
        Number(RolloffFactor, 0, float.MaxValue, nameof(RolloffFactor));
        Number(ConeInnerAngle, 0, 360, nameof(ConeInnerAngle)); Number(ConeOuterAngle, ConeInnerAngle, 360, nameof(ConeOuterAngle));
        Number(ConeOuterGain, 0, 1, nameof(ConeOuterGain)); Number(ConeOuterGainHF, 0, 1, nameof(ConeOuterGainHF));
        Number(AirAbsorptionFactor, 0, 10, nameof(AirAbsorptionFactor)); Number(RoomRolloffFactor, 0, 10, nameof(RoomRolloffFactor));
    }
    /// <summary>Enables spatialization. Use mono PCM for positional sources; multichannel inputs default to nonspatial.</summary>
    public bool Spatialize { get; init; } = true;
    /// <summary>Interprets coordinates relative to the listener.</summary>
    public bool Relative { get; init; }
    /// <summary>Source position.</summary>
    public Vector3 Position { get; init; }
    /// <summary>Source velocity.</summary>
    public Vector3 Velocity { get; init; }
    /// <summary>Cone direction; zero disables directional attenuation.</summary>
    public Vector3 Direction { get; init; }
    /// <summary>Distance at which gain is unattenuated; greater than zero.</summary>
    public float ReferenceDistance { get; init; } = 1;
    /// <summary>Maximum attenuation distance; at least ReferenceDistance.</summary>
    public float MaximumDistance { get; init; } = float.MaxValue;
    /// <summary>Nonnegative distance rolloff multiplier.</summary>
    public float RolloffFactor { get; init; } = 1;
    /// <summary>Inner cone angle, 0 through 360 degrees.</summary>
    public float ConeInnerAngle { get; init; } = 360;
    /// <summary>Outer cone angle, at least the inner angle and at most 360 degrees.</summary>
    public float ConeOuterAngle { get; init; } = 360;
    /// <summary>Outer cone gain, 0 through 1.</summary>
    public float ConeOuterGain { get; init; }
    /// <summary>High-frequency outer cone gain, 0 through 1.</summary>
    public float ConeOuterGainHF { get; init; } = 1;
    /// <summary>Air absorption multiplier, 0 through 10.</summary>
    public float AirAbsorptionFactor { get; init; }
    /// <summary>Additional reverb distance rolloff, 0 through 10.</summary>
    public float RoomRolloffFactor { get; init; }
}
