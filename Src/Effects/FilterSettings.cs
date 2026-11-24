using SoundFlow.Abstracts.Devices;

namespace SoundFlow.Effects;

/// <summary>Filter shape for a native source route.</summary>
public enum FilterKind
{
    /// <summary>Attenuates high frequencies.</summary>
    LowPass,
    /// <summary>Attenuates low frequencies.</summary>
    HighPass,
    /// <summary>Independent attenuation of low and high frequencies.</summary>
    BandPass
}

/// <summary>Immutable route-filter gains. All gains range from 0 through 1 and multiply any send gain.</summary>
public sealed record FilterSettings
{
    /// <summary>Filter shape.</summary>
    public FilterKind Kind { get; init; }
    /// <summary>Overall route gain.</summary>
    public float Gain { get; init; } = 1;
    /// <summary>Low-frequency gain, used by high-pass and band-pass shapes.</summary>
    public float GainLF { get; init; } = 1;
    /// <summary>High-frequency gain, used by low-pass and band-pass shapes.</summary>
    public float GainHF { get; init; } = 1;
    /// <summary>Rejects invalid kinds and gains before native state is changed.</summary>
    public void Validate()
    {
        if (!Enum.IsDefined(Kind)) throw new ArgumentOutOfRangeException(nameof(Kind));
        foreach (var value in new[] { Gain, GainLF, GainHF })
            if (!float.IsFinite(value) || value < 0 || value > 1) throw new ArgumentOutOfRangeException(nameof(Gain));
    }
}
