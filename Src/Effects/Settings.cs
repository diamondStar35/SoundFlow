using SoundFlow.Abstracts.Devices;

namespace SoundFlow.Effects;

/// <summary>Immutable effect parameters. Backends may support additional derived records without changing the core interface.</summary>
public abstract record Settings
{
    /// <summary>Validates every parameter before a backend changes native state.</summary>
    public abstract void Validate();
    /// <summary>Rejects nonfinite or out-of-range parameters, including integer enumeration values.</summary>
    protected static void Range(float value, float min, float max, string name)
    {
        if (!float.IsFinite(value) || value < min || value > max)
            throw new ArgumentOutOfRangeException(name, value, $"Expected a finite value in [{min}, {max}].");
    }
}
