using Device = SoundFlow.Devices.Device;
using SoundFlow.Devices;

namespace SoundFlow.Structs.Events;

/// <summary>
/// Provides data for device-related events.
/// </summary>
public class DeviceEventArgs(Device device) : EventArgs
{
    /// <summary>
    /// Gets the audio device that the event is about.
    /// </summary>
    public Device Device { get; } = device;
}
