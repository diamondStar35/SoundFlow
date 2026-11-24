using System.Numerics;

namespace SoundFlow.Spatial;

/// <summary>Distance attenuation models. Clamped models constrain distance to the source's reference/maximum range.</summary>
public enum DistanceModel
{
    /// <summary>No distance attenuation.</summary>
    None,
    /// <summary>Inverse distance.</summary>
    Inverse,
    /// <summary>Clamped inverse distance.</summary>
    InverseClamped,
    /// <summary>Linear distance.</summary>
    Linear,
    /// <summary>Clamped linear distance.</summary>
    LinearClamped,
    /// <summary>Exponential distance.</summary>
    Exponential,
    /// <summary>Clamped exponential distance.</summary>
    ExponentialClamped
}
