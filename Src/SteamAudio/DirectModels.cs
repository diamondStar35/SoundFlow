namespace SoundFlow.SteamAudio;

/// <summary>Distance-based air absorption. Null on a source keeps the effect disabled.</summary>
public sealed record AirAbsorptionSettings
{
    /// <summary>Optional exponential coefficients per metre. Null uses Steam Audio's default atmosphere.</summary>
    public AbsorptionCoefficients? Coefficients { get; init; }
    /// <summary>Nonnegative multiplier on physical distance. Zero produces unity response.</summary>
    public float Strength { get; init; } = 1;
    internal void Validate()
    {
        if (!float.IsFinite(Strength) || Strength < 0)
            throw new ArgumentOutOfRangeException(nameof(Strength));
        Coefficients?.Validate();
    }
}

/// <summary>Exponential attenuation rates per metre; these are not amplitude gains and may exceed one.</summary>
public readonly record struct AbsorptionCoefficients(float Low, float Mid, float High)
{
    internal void Validate()
    {
        if (!float.IsFinite(Low) || !float.IsFinite(Mid) || !float.IsFinite(High) || Low < 0 || Mid < 0 || High < 0)
            throw new ArgumentOutOfRangeException(nameof(AbsorptionCoefficients));
    }
}

/// <summary>Orientation-dependent blend between omnidirectional and dipole source radiation.</summary>
public sealed record DirectivitySettings
{
    /// <summary>Zero is omnidirectional, one is a dipole, and 0.5 is cardioid.</summary>
    public float DipoleWeight { get; init; } = .5f;
    /// <summary>Nonnegative sharpness of the radiation pattern.</summary>
    public float DipolePower { get; init; } = 1;
    internal void Validate()
    {
        if (!float.IsFinite(DipoleWeight) || DipoleWeight is < 0 or > 1 || !float.IsFinite(DipolePower) || DipolePower < 0)
            throw new ArgumentOutOfRangeException(nameof(DipoleWeight));
    }
}

/// <summary>Strategy for selecting or interpolating measured HRTFs.</summary>
public enum HrtfInterpolation
{
    /// <summary>Choose the closest measured direction.</summary>
    Nearest,
    /// <summary>Blend surrounding measurements for moving sources; the default.</summary>
    Bilinear
}

/// <summary>Point-source output rendering selected before source creation.</summary>
public enum RenderingMode
{
    /// <summary>Stereo binaural rendering with HRTF.</summary>
    Headphones,
    /// <summary>Positional panning into a configured speaker layout.</summary>
    Speakers
}
