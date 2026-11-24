using SoundFlow.Structs;

namespace SoundFlow.Devices;

/// <summary>
/// Represents device information including ID, name, default status, and data formats.
/// </summary>
public record struct DeviceInfo
{
    /// <summary>
    /// Opaque backend-qualified identifier. Valid for the backend enumeration session; never a native pointer.
    /// </summary>
    public string Id { get; init; }

    /// <summary>
    /// The name of the device.
    /// </summary>
    public string Name { get; init; }

    /// <summary>
    /// Indicates whether the device is set as default.
    /// </summary>
    public bool IsDefault { get; init; }

    /// <summary>
    /// Gets the supported native data formats for the device.
    /// </summary>
    public NativeDataFormat[] SupportedDataFormats { get; init; }
}
