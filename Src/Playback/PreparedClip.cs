using SoundFlow.Devices;

namespace SoundFlow.Playback;

/// <summary>Optional native clip preparation. Buffers belong to one device and are shared by independently controlled sources.</summary>
public interface IPreparedClips
{
    /// <summary>Retains an immutable native upload. May reject unsupported layouts or an exhausted clip-memory budget.</summary>
    PreparedClip Prepare(Clip clip);
}

/// <summary>
/// A retained device upload. Created sources hold their own reference and may outlive this handle.
/// Native clip sources support native processing, but reject software modifiers, PCM callbacks and transport time stretching.
/// </summary>
public abstract class PreparedClip : IDisposable
{
    /// <summary>Device whose context owns this upload.</summary>
    public abstract PlaybackDevice Device { get; }
    /// <summary>Original immutable PCM clip.</summary>
    public abstract Clip Clip { get; }
    /// <summary>Creates an independent nonspatial native source without decoding jobs.</summary>
    public abstract Source CreateSource();
    /// <summary>Creates an independent positional native source without decoding jobs.</summary>
    public abstract SpatialSource CreateSpatialSource();
    /// <summary>Releases this handle's reference. Active sources remain valid.</summary>
    public abstract void Dispose();
}
