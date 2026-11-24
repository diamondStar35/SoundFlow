using SoundFlow.Devices;

namespace SoundFlow.Features;

/// <summary>Managed notifications produced by a backend. Physical device categories depend on platform support.</summary>
public enum NotificationKind
{
    /// <summary>A physical endpoint disconnected.</summary>
    Disconnected,
    /// <summary>The operating system reported a new endpoint.</summary>
    DeviceAdded,
    /// <summary>The operating system removed an endpoint.</summary>
    DeviceRemoved,
    /// <summary>The operating system selected a different default endpoint.</summary>
    DefaultDeviceChanged,
    /// <summary>The native source state changed.</summary>
    SourceStateChanged,
    /// <summary>A PCM provider, processor, or native operation failed.</summary>
    ProcessingFailure
}

/// <summary>A copied, managed notification. No borrowed native strings or AL identifiers escape the callback.</summary>
public sealed class BackendNotification : EventArgs
{
    /// <summary>The category of change.</summary>
    public required NotificationKind Kind { get; init; }
    /// <summary>A diagnostic message or operating-system endpoint name.</summary>
    public required string Message { get; init; }
    /// <summary>The owning initialized device, when this event concerns a context.</summary>
    public Device? Device { get; init; }
}

/// <summary>Optional managed backend notifications delivered outside native callbacks and PCM processing locks.</summary>
/// <remarks>Handlers may issue normal controls, but should return promptly. Diagnostic overflow is counted; notifications are not a replacement for querying current state.</remarks>
public interface INotifications
{
    /// <summary>Receives state changes and failures on the backend's notification dispatcher.</summary>
    event EventHandler<BackendNotification>? Notification;
    /// <summary>Number of diagnostic notifications dropped because their consumer fell behind.</summary>
    long DroppedNotifications { get; }
}
