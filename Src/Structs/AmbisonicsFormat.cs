namespace SoundFlow.Structs;

/// <summary>Channel ordering and spherical-harmonic normalization for Ambisonics PCM.</summary>
public enum AmbisonicsConvention
{
    /// <summary>ACN ordering with orthonormal normalization; native Steam Audio processing format.</summary>
    N3D,
    /// <summary>ACN ordering with semi-normalized harmonics, commonly called AmbiX.</summary>
    SN3D,
    /// <summary>Furse-Malham ordering and normalization.</summary>
    FuMa
}

/// <summary>Explicit sound-field format. Orders zero through three are supported by this integration.</summary>
public readonly record struct AmbisonicsFormat(int Order, AmbisonicsConvention Convention = AmbisonicsConvention.N3D)
{
    /// <summary>Number of spherical-harmonic channels.</summary>
    public int Channels => checked((Order + 1) * (Order + 1));
    /// <summary>Rejects unsupported orders and unknown conventions before native processing.</summary>
    public void Validate()
    {
        if (Order is < 0 or > 3 || !Enum.IsDefined(Convention))
            throw new ArgumentOutOfRangeException(nameof(Order), "Ambisonics requires order zero through three and a known convention.");
    }
}
