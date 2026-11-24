using System.Numerics;
using SoundFlow.Spatial;

namespace SoundFlow.Backends.OpenAL.Features;

/// <summary>Native channel mapping performed after software modifiers.</summary>
public enum ChannelMapping
{
    /// <summary>Use the normal spatial/nonspatial renderer selected by source creation.</summary>
    Normal,
    /// <summary>Map channels directly to matching output speakers; omit unmatched channels.</summary>
    Direct,
    /// <summary>Map channels directly and remix unmatched channels into the available outputs.</summary>
    RemixUnmatched
}

/// <summary>Optional controls implemented by the current native context. Check flags before exposing related UI.</summary>
[Flags]
public enum RenderingCapabilities
{
    /// <summary>Only core gain bounds are available.</summary>
    None = 0,
    /// <summary>Per-source distance model overrides.</summary>
    DistanceModel = 1,
    /// <summary>Finite source radius.</summary>
    Radius = 2,
    /// <summary>Independent stereo-channel directions.</summary>
    StereoAngles = 4,
    /// <summary>Selectable native resampler.</summary>
    Resampler = 8,
    /// <summary>Direct channel-to-speaker mapping.</summary>
    DirectChannels = 16,
    /// <summary>Direct mapping with unmatched-channel remixing.</summary>
    ChannelRemix = 32,
    /// <summary>EFX automatic direct/send gain adjustments.</summary>
    AutomaticFilterGain = 64
}

/// <summary>Immutable overrides for native voice rendering. Unspecified nullable values inherit device/native defaults.</summary>
/// <remarks>Gain bounds apply to the attenuated dry voice before the listener gain. Radius uses world distance units; stereo angles use radians.</remarks>
public sealed record RenderingSettings
{
    /// <summary>Minimum attenuated source gain, 0 through 1. Default 0.</summary>
    public float MinimumGain { get; init; }
    /// <summary>Maximum attenuated source gain, 0 through 1 and at least MinimumGain. Default 1.</summary>
    public float MaximumGain { get; init; } = 1;
    /// <summary>Distance model override; null follows the current scene model.</summary>
    public DistanceModel? DistanceModel { get; init; }
    /// <summary>Nonnegative source radius; zero represents a point source.</summary>
    public float Radius { get; init; }
    /// <summary>Left/right channel directions in radians; null uses OpenAL's default angles.</summary>
    public Vector2? StereoAngles { get; init; }
    /// <summary>Index into AvailableResamplers; null selects the native default.</summary>
    public int? Resampler { get; init; }
    /// <summary>Channel-to-speaker mapping policy. Normal preserves spatial source behavior.</summary>
    public ChannelMapping ChannelMapping { get; init; }
    /// <summary>Automatically scale direct high-frequency gain with source/listener distance. Default true.</summary>
    public bool DirectGainHFAutomatic { get; init; } = true;
    /// <summary>Automatically scale auxiliary send gain with distance. Default true.</summary>
    public bool SendGainAutomatic { get; init; } = true;
    /// <summary>Automatically scale auxiliary high-frequency gain with distance. Default true.</summary>
    public bool SendGainHFAutomatic { get; init; } = true;
}

/// <summary>OpenAL-specific voice rendering controls, separate from portable positional settings.</summary>
public interface ISourceRendering
{
    /// <summary>Controls supported by this source's context.</summary>
    RenderingCapabilities Capabilities { get; }
    /// <summary>Native resampler names in native index order; empty when selection is unavailable.</summary>
    IReadOnlyList<string> AvailableResamplers { get; }
    /// <summary>Complete rendering overrides. Validation and capability failures leave the previous snapshot intact.</summary>
    RenderingSettings Settings { get; set; }
}
