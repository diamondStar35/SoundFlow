using System.Numerics;

namespace SoundFlow.Spatial;

/// <summary>Listener pose in a right-handed world: +X right, +Y up, -Z forward; metres and metres/second.</summary>
public sealed record Listener
{
    /// <summary>World-space position in metres.</summary>
    public Vector3 Position { get; init; }
    /// <summary>World-space velocity in metres/second.</summary>
    public Vector3 Velocity { get; init; }
    /// <summary>Nonzero forward direction, nonparallel to Up. The backend normalizes directions.</summary>
    public Vector3 Forward { get; init; } = -Vector3.UnitZ;
    /// <summary>Nonzero up direction, nonparallel to Forward.</summary>
    public Vector3 Up { get; init; } = Vector3.UnitY;
}
