using PlaybackDevice = SoundFlow.Devices.PlaybackDevice;
using SoundFlow.Devices;

namespace SoundFlow.Effects;

/// <summary>A shared, device-owned wet-effect bus. Native handles never escape this interface.</summary>
public abstract class Slot : IDisposable
{
    /// <summary>Owning device; sends and targets must have this same owner.</summary>
    public abstract PlaybackDevice Device { get; }
    /// <summary>Whether this slot has been disposed.</summary>
    public abstract bool IsDisposed { get; }
    /// <summary>Current immutable effect parameters.</summary>
    public abstract Settings Settings { get; }
    /// <summary>Wet output gain, from 0 to 1.</summary>
    public abstract float Gain { get; set; }
    /// <summary>Current target; null mixes the wet output into the main output.</summary>
    public abstract Slot? Target { get; }
    /// <summary>Replaces and reapplies the complete effect snapshot. Validation failure retains the previous effect.</summary>
    public abstract void Update(Settings settings);
    /// <summary>Targets another slot or the main output. Cross-device targets and routing cycles are rejected.</summary>
    public abstract void SetTarget(Slot? target);
    /// <summary>Detaches incoming source sends and slot links, then releases the effect and slot.</summary>
    public abstract void Dispose();
}
