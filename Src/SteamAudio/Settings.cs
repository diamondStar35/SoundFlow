using System.Numerics;
using SoundFlow.Spatial;

namespace SoundFlow.SteamAudio;

/// <summary>Immutable native processing configuration, selected before creating voices.</summary>
public sealed record RendererSettings
{
    /// <summary>Fixed DSP frames per call: 64, 128, 256 (default), or 512.</summary>
    public int BlockFrames { get; init; } = 256;
    /// <summary>Optional SOFA file. Null selects Steam Audio's built-in HRTF.</summary>
    public string? SofaPath { get; init; }
    /// <summary>World-unit length in metres, used only for physical air absorption.</summary>
    public float MetresPerUnit { get; init; } = 1;
    /// <summary>Default helper output mode. Explicit processor factories remain independently selectable.</summary>
    public RenderingMode Mode { get; init; }
    /// <summary>Speaker layout used by speaker rendering. Stereo is the default.</summary>
    public SpeakerLayout Speakers { get; init; } = SpeakerLayout.Stereo;
}

/// <summary>Three-band amplitude coefficients used by Steam Audio's direct path.</summary>
public readonly record struct Bands(float Low, float Mid, float High)
{
    /// <summary>Unfiltered response in all three frequency bands.</summary>
    public static Bands Unity => new(1, 1, 1);
    internal void Validate()
    {
        if (!float.IsFinite(Low) || !float.IsFinite(Mid) || !float.IsFinite(High) ||
            Low is < 0 or > 1 || Mid is < 0 or > 1 || High is < 0 or > 1)
            throw new ArgumentOutOfRangeException(nameof(Bands), "Band coefficients must be between zero and one.");
    }
}

/// <summary>
/// One complete spatial snapshot. Distance attenuation is enabled by default; additional direct effects
/// are selected by supplying their coefficients. These controls do not run acoustic simulation.
/// </summary>
public sealed record SpatializationSettings
{
    /// <summary>Source position in a right-handed world: +X right, +Y up, -Z forward.</summary>
    public Vector3 Position { get; init; }
    /// <summary>Interpret Position in the listener's local coordinate system.</summary>
    public bool Relative { get; init; }
    /// <summary>Distance curve applied before binaural rendering. None explicitly disables attenuation.</summary>
    public DistanceAttenuation Distance { get; init; } = new();
    /// <summary>
    /// Spatialization from zero (ordinary mono/stereo) to one (fully positional). Headphones use Steam
    /// Audio's HRTF blend; speaker panning uses a linear mix with a fixed-front unpositioned image.
    /// Distance and direct-path filters still apply at zero.
    /// </summary>
    public float SpatialBlend { get; init; } = 1;
    /// <summary>HRTF interpolation used for the next newly rendered quantum.</summary>
    public HrtfInterpolation Interpolation { get; init; } = HrtfInterpolation.Bilinear;
    /// <summary>Stereo balance from zero (left) to one (right); 0.5 preserves both channel gains.</summary>
    public float Pan { get; init; } = .5f;
    /// <summary>Mid/side width: zero is mono, one is unchanged, and two doubles the side signal.</summary>
    public float Width { get; init; } = 1;
    /// <summary>Source forward direction for automatic directivity, in world or listener-relative coordinates.</summary>
    public Vector3 Forward { get; init; } = -Vector3.UnitZ;
    /// <summary>Source up direction, nonparallel to Forward.</summary>
    public Vector3 Up { get; init; } = Vector3.UnitY;
    /// <summary>Optional distance-based atmosphere model. Manual AirAbsorption gains override this model.</summary>
    public AirAbsorptionSettings? AirAbsorptionModel { get; init; }
    /// <summary>Optional oriented dipole model. Manual Directivity overrides this model.</summary>
    public DirectivitySettings? DirectivityModel { get; init; }
    /// <summary>Optional low/mid/high amplitude gains in [0,1], overriding AirAbsorptionModel.</summary>
    public Bands? AirAbsorption { get; init; }
    /// <summary>Optional source directivity gain.</summary>
    public float? Directivity { get; init; }
    /// <summary>Optional fraction of unoccluded direct sound.</summary>
    public float? Occlusion { get; init; }
    /// <summary>Optional frequency-dependent transmission, used with occlusion.</summary>
    public Bands? Transmission { get; init; }

    internal void Validate()
    {
        if (!float.IsFinite(Position.LengthSquared()) || !float.IsFinite(SpatialBlend) || SpatialBlend is < 0 or > 1)
            throw new ArgumentException("Spatial position must be finite and spatial blend must be between zero and one.");
        ArgumentNullException.ThrowIfNull(Distance);
        Distance.Validate();
        if (!Enum.IsDefined(Interpolation) || !float.IsFinite(Pan) || Pan is < 0 or > 1 ||
            !float.IsFinite(Width) || Width is < 0 or > 2 || !float.IsFinite(Forward.LengthSquared()) ||
            !float.IsFinite(Up.LengthSquared()) || Forward.LengthSquared() < 1e-12f || Vector3.Cross(Forward, Up).LengthSquared() < 1e-12f)
            throw new ArgumentException("Invalid stereo shaping or source orientation.");
        AirAbsorptionModel?.Validate();
        DirectivityModel?.Validate();
        AirAbsorption?.Validate();
        Transmission?.Validate();
        if (Directivity.HasValue && (!float.IsFinite(Directivity.Value) || Directivity is < 0 or > 1) ||
            Occlusion.HasValue && (!float.IsFinite(Occlusion.Value) || Occlusion is < 0 or > 1))
            throw new ArgumentOutOfRangeException(nameof(Directivity));
        if (Transmission.HasValue && !Occlusion.HasValue)
            throw new ArgumentException("Transmission requires an explicit occlusion coefficient.");
    }
}
