using Engine = SoundFlow.Engine;
using SoundFlow.Structs;

namespace SoundFlow.Devices;

/// <summary>
/// Represents a capture (input) audio device.
/// </summary>
public abstract class CaptureDevice : Device
{
    /// <summary>
    /// Initializes a new instance of the <see cref="SoundFlow.Devices.CaptureDevice"/> class.
    /// </summary>
    /// <param name="engine">The parent audio engine.</param>
    /// <param name="format">The desired audio format.</param>
    /// <param name="config">The device configuration.</param>
    protected CaptureDevice(Engine engine, AudioFormat format, DeviceConfiguration config) : base(engine, format, config) { }
}
