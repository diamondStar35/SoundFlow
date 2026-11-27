using SoundFlow.Structs;
using SoundFlow.Playback;

namespace SoundFlow.Features;

/// <summary>A snapshot of an initialized backend's negotiated state.</summary>
public sealed record DiagnosticsSnapshot
{
    /// <summary>SoundFlow interfaces actually registered on the device, distinct from all native extension names.</summary>
    public IReadOnlyList<string> ImplementedFeatures { get; init; } = [];
    /// <summary>Engine-wide managed PCM producer counts and ready storage.</summary>
    public DecodingSnapshot Decoding { get; init; }
    /// <summary>Lifetime native source allocations, including graph feeds; reused identifiers do not increment this.</summary>
    public long SourceAllocations { get; init; }
    /// <summary>Lifetime native buffer allocations, including streaming queues and prepared PCM.</summary>
    public long BufferAllocations { get; init; }
    /// <summary>Prepared-source identifiers successfully reused by fresh logical sources.</summary>
    public long ReusedSources { get; init; }
    /// <summary>Current native prepared PCM bytes, including referenced impulse responses and unused cached clips.</summary>
    public long PreparedClipBytes { get; init; }
    /// <summary>Current cached native clip uploads.</summary>
    public int PreparedClips { get; init; }
    /// <summary>Idle native sources retained for subsequent prepared playback.</summary>
    public int IdleSources { get; init; }
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
    /// <summary>Current native wet-only endpoints consuming software captures, additional to logical sources.</summary>
    public int ProcessingEndpoints { get; init; }
    /// <summary>Logical sources rendered on demand rather than into native streaming queues.</summary>
    public int OnDemandSources { get; init; }
    /// <summary>Rendered frames retained by on-demand sources; excludes decoded input and device buffers.</summary>
    public long RetainedRenderFrames { get; init; }
    /// <summary>Missing-input quanta observed by render consumers.</summary>
    public long RenderUnderruns { get; init; }
    /// <summary>Current logical source count; excludes graph feeds and internal processing endpoints.</summary>
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
