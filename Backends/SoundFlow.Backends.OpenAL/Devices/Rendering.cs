using SoundFlow.Backends.OpenAL.Features;

namespace SoundFlow.Backends.OpenAL.Devices;

internal sealed unsafe partial class Context : IDeviceRendering
{
    internal static int OutputModeToken(OutputMode mode) => mode switch
    {
        OutputMode.Automatic => 0x19AD, OutputMode.Mono => 0x1500, OutputMode.Stereo => 0x19AE,
        OutputMode.StereoHrtf => 0x19B2, OutputMode.Quad => 0x1503, OutputMode.Surround51 => 0x1504,
        OutputMode.Surround61 => 0x1505, OutputMode.Surround71 => 0x1506,
        _ => throw new ArgumentOutOfRangeException(nameof(mode))
    };

    public DeviceRenderingSnapshot GetRenderingState() => Worker.Invoke(() =>
    {
        OutputMode? mode = null;
        if (!Offline && Al.HasAlcExtension(Device, "ALC_SOFT_output_mode"))
            mode = Al.DeviceInteger(Device, 0x19AC) switch
            {
                0x1500 => OutputMode.Mono, 0x1501 or 0x19AE => OutputMode.Stereo, 0x19B2 => OutputMode.StereoHrtf,
                0x1503 => OutputMode.Quad, 0x1504 => OutputMode.Surround51, 0x1505 => OutputMode.Surround61,
                0x1506 => OutputMode.Surround71, _ => null
            };
        bool? limiter = Al.HasAlcExtension(Device, "ALC_SOFT_output_limiter") ? Al.DeviceInteger(Device, 0x199A) != 0 : null;
        return new DeviceRenderingSnapshot(mode, limiter);
    });
}
