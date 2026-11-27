namespace SoundFlow.SteamAudio;

/// <summary>
/// Selects the direct-sound components applied by one processor. Split these across processors to
/// capture distance-attenuated sound before dry-only absorption, directivity, or occlusion.
/// </summary>
[Flags]
public enum DirectEffects
{
    /// <summary>Copy the input without direct-sound filtering.</summary>
    None = 0,
    /// <summary>Apply the source's software distance curve.</summary>
    Distance = 1,
    /// <summary>Apply automatic or explicit three-band air absorption.</summary>
    AirAbsorption = 2,
    /// <summary>Apply automatic or explicit source directivity.</summary>
    Directivity = 4,
    /// <summary>Apply the unoccluded direct-sound fraction.</summary>
    Occlusion = 8,
    /// <summary>Apply transmission through occluding material; also requires Occlusion.</summary>
    Transmission = 16,
    /// <summary>Apply every configured component, preserving the default processor behavior.</summary>
    All = Distance | AirAbsorption | Directivity | Occlusion | Transmission
}
