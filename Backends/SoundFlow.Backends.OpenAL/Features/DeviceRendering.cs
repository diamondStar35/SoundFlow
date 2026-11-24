namespace SoundFlow.Backends.OpenAL.Features;

/// <summary>Preferred physical output topology. The implementation reports the mode it actually negotiated.</summary>
public enum OutputMode
{
    /// <summary>Respect the native implementation and user configuration.</summary>
    Automatic,
    /// <summary>Mono output.</summary>
    Mono,
    /// <summary>Ordinary stereo without requesting HRTF.</summary>
    Stereo,
    /// <summary>Stereo with HRTF requested.</summary>
    StereoHrtf,
    /// <summary>Four-speaker output.</summary>
    Quad,
    /// <summary>5.1 output.</summary>
    Surround51,
    /// <summary>6.1 output.</summary>
    Surround61,
    /// <summary>7.1 output.</summary>
    Surround71
}

/// <summary>Actual native output processing; null denotes a query unsupported by this device.</summary>
public readonly record struct DeviceRenderingSnapshot(OutputMode? OutputMode, bool? OutputLimiter);

/// <summary>OpenAL output-mode and limiter diagnostics. Configure their initial requests before opening the device.</summary>
public interface IDeviceRendering
{
    /// <summary>Returns the actual negotiated rendering state rather than echoing the requested configuration.</summary>
    DeviceRenderingSnapshot GetRenderingState();
}
