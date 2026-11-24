using SoundFlow.Devices;

namespace SoundFlow.Playback;

/// <summary>A fixed collection of voices sharing one device and one native playback command.</summary>
/// <remarks>The group borrows its members. Disposing it never stops or disposes the sources. Membership is validated again before every operation.</remarks>
public abstract class SourceGroup : IDisposable
{
    /// <summary>The device whose clock and context govern every member.</summary>
    public abstract PlaybackDevice Device { get; }
    /// <summary>The immutable membership snapshot; duplicate members are rejected at creation.</summary>
    public abstract IReadOnlyList<Source> Sources { get; }
    /// <summary>Whether this group has released its membership.</summary>
    public abstract bool IsDisposed { get; }
    /// <summary>Prepares all members and starts or resumes them together.</summary>
    public abstract void Play();
    /// <summary>Pauses all members at one native mixing boundary.</summary>
    public abstract void Pause();
    /// <summary>Stops all members and discards queued PCM.</summary>
    public abstract void Stop();
    /// <summary>Stops and seeks every member to the beginning. Nonseekable members are rejected before modifying the group.</summary>
    public abstract void Rewind();
    /// <summary>Releases this group while leaving its sources alive.</summary>
    public abstract void Dispose();
}
