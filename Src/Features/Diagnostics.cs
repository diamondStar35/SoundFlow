using SoundFlow.Structs;

namespace SoundFlow.Features;

/// <summary>A snapshot of an initialized backend's negotiated state.</summary>
public sealed record DiagnosticsSnapshot
{
    /// <summary>Backend implementation.</summary>
    public required string Backend { get; init; }
    /// <summary>Loaded native version and library path.</summary>
    public required string Library { get; init; }
    /// <summary>Actual device name.</summary>
    public required string Device { get; init; }
    /// <summary>Negotiated output format.</summary>
    public AudioFormat Format { get; init; }
    /// <summary>Available extension names.</summary>
    public IReadOnlyList<string> Extensions { get; init; } = [];
    /// <summary>Maximum auxiliary sends per source, distinct from the effect-slot count.</summary>
    public int AuxiliarySends { get; init; }
    /// <summary>Current source count.</summary>
    public int Sources { get; init; }
    /// <summary>Current effect-slot count.</summary>
    public int EffectSlots { get; init; }
    /// <summary>Number of queue starvations while playback was requested.</summary>
    public long Underruns { get; init; }
    /// <summary>Whether the hardware remains connected.</summary>
    public bool Connected { get; init; }
    /// <summary>Actual HRTF status.</summary>
    public string HrtfStatus { get; init; } = "Unavailable";
    /// <summary>Most recent processing failure. A failed voice does not prevent other voices or cleanup from proceeding.</summary>
    public string? LastFailure { get; init; }
    /// <summary>Number of diagnostic notifications omitted because their consumer fell behind.</summary>
    public long DroppedNotifications { get; init; }
}

/// <summary>Optional device diagnostics. Snapshot collection is a control operation, not a mixing callback operation.</summary>
public interface IDiagnostics
{
    /// <summary>Returns current native and streaming state.</summary>
    DiagnosticsSnapshot GetSnapshot();
}
