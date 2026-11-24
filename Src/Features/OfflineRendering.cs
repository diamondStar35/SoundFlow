using DeviceConfiguration = SoundFlow.Devices.DeviceConfiguration;
using RenderDevice = SoundFlow.Devices.RenderDevice;
using SoundFlow.Structs;

namespace SoundFlow.Features;

/// <summary>Creates manually advanced devices rendering their own mix without opening physical audio hardware.</summary>
public interface IOfflineRendering
{
    /// <summary>Creates an owned render device with interleaved F32 output.</summary>
    RenderDevice CreateRenderDevice(AudioFormat format, DeviceConfiguration? config = null);
}
