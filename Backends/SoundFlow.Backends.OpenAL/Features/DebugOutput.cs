using SoundFlow.Playback;
using SoundFlow.Effects;

namespace SoundFlow.Backends.OpenAL.Features;

/// <summary>Native debug severity, ordered from the most important messages to routine notifications.</summary>
public enum DebugSeverity
{
    /// <summary>Errors that can prevent correct rendering.</summary>
    High,
    /// <summary>Significant warnings and potentially incorrect usage.</summary>
    Medium,
    /// <summary>Lower-impact portability and performance notices.</summary>
    Low,
    /// <summary>Routine markers and informational notices.</summary>
    Notification
}

/// <summary>A copied AL_EXT_debug message. Native identifiers are diagnostic values, never callable handles.</summary>
public sealed class DebugMessage : EventArgs
{
    /// <summary>Native message source category.</summary>
    public required int Source { get; init; }
    /// <summary>Native message type category.</summary>
    public required int Type { get; init; }
    /// <summary>Implementation-defined diagnostic message identifier.</summary>
    public required uint Id { get; init; }
    /// <summary>Severity assigned by the implementation or application.</summary>
    public required DebugSeverity Severity { get; init; }
    /// <summary>Managed UTF-8 message copy.</summary>
    public required string Text { get; init; }
}

/// <summary>Optional native debugging. Request DebugMessages in device configuration before creating its context.</summary>
public interface IDebugOutput
{
    /// <summary>Whether this context was created with native debug delivery enabled.</summary>
    bool Enabled { get; }
    /// <summary>Least important severity delivered; Medium includes High and Medium. Changes are context-worker controls.</summary>
    DebugSeverity MinimumSeverity { get; set; }
    /// <summary>Bounded diagnostics delivery outside native/PCM callbacks. Dropped messages are included in IDiagnostics totals.</summary>
    event EventHandler<DebugMessage>? Message;
    /// <summary>Inserts a managed application marker through the native debug interface.</summary>
    void Write(string message, DebugSeverity severity = DebugSeverity.Notification);
    /// <summary>Labels a live source owned by this device for native diagnostic reports.</summary>
    void SetLabel(Source source, string label);
    /// <summary>Labels a live effect slot owned by this device for native diagnostic reports.</summary>
    void SetLabel(Slot slot, string label);
}
