using SoundFlow.Providers;
using SourceSettings = SoundFlow.Spatial.SourceSettings;
using SoundFlow.Features;
using SoundFlow.Interfaces;
using SoundFlow.Structs;

namespace SoundFlow.Playback;

/// <summary>A positional voice rendered by its backend. Input channels are preserved rather than implicitly downmixed.</summary>
/// <remarks>Obtain this type through a playback device's CreateSpatialSource method. Software graph children share its position.</remarks>
public abstract class SpatialSource : Source
{
    /// <summary>Reserves the input provider for a positional source owned by the specified device.</summary>
    protected SpatialSource(Devices.PlaybackDevice device, IDataProvider provider, AudioFormat format) : base(device, provider, format) { }

    /// <summary>Replaces the complete spatial snapshot. Positions use right-handed coordinates and cone angles use degrees.</summary>
    public abstract SourceSettings Settings { get; set; }
}
