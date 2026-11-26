namespace SoundFlow.Playback;

/// <summary>Why a source's current playback stopped. Shared auxiliary effects can retain tails afterward.</summary>
public enum PlaybackEndReason
{
    /// <summary>All source PCM was consumed.</summary>
    Completed,
    /// <summary>The caller explicitly stopped playback or returned its lease.</summary>
    Stopped,
    /// <summary>A higher-priority pooled playback reused the occupied slot.</summary>
    Stolen,
    /// <summary>Input, processing or native playback failed.</summary>
    Failed,
    /// <summary>The source was disposed.</summary>
    Disposed,
    /// <summary>The owning playback pool was disposed.</summary>
    PoolDisposed
}

/// <summary>Copied terminal outcome, safe to retain after the source or lease has been released.</summary>
public sealed class PlaybackTermination : EventArgs
{
    /// <summary>Terminal reason for this playback generation.</summary>
    public PlaybackEndReason Reason { get; }
    /// <summary>Underlying error for failed playback, otherwise null.</summary>
    public Exception? Error { get; }
    /// <summary>Creates a terminal outcome.</summary>
    public PlaybackTermination(PlaybackEndReason reason, Exception? error = null) { Reason = reason; Error = error; }
}
