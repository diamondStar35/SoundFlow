using SoundFlow.Structs;

namespace SoundFlow.Processing;

/// <summary>
/// Per-source F32 processing state. Ordinary processing preserves frame count and sample rate;
/// channel conversion is explicit through OutputFormat. Calls, reset, draining and disposal are serialized by the owner.
/// </summary>
public abstract class Processor : IDisposable
{
    internal int Lease;
    /// <summary>Exact format accepted by Process. A processor instance belongs to only one running chain.</summary>
    public AudioFormat InputFormat { get; }
    /// <summary>Exact format written by Process and Drain; must retain the input sample rate.</summary>
    public AudioFormat OutputFormat { get; }
    /// <summary>Constant algorithmic buffering delay, in frames. Excludes artistic delay and directional interaural differences.</summary>
    public virtual int LatencyFrames => 0;
    /// <summary>Hard upper bound on frames returned by Drain, including buffered input. Zero declares no tail.</summary>
    public virtual int MaximumTailFrames => LatencyFrames;
    /// <summary>Explicit opt-in used by sends selecting ProcessorOptIn compensation. False by default.</summary>
    public bool CompensateLatency { get; init; }
    /// <summary>Creates state with explicit input and output formats. Allocation belongs in construction, not Process.</summary>
    protected Processor(AudioFormat input, AudioFormat output) { InputFormat = input; OutputFormat = output; }
    /// <summary>Writes exactly frames complete output frames from the same number of input frames; do not retain either span.</summary>
    public abstract void Process(ReadOnlySpan<float> input, Span<float> output, int frames);
    /// <summary>Writes up to output.Length / OutputFormat.Channels tail frames. Zero ends the tail permanently until reset/new input.</summary>
    public virtual int Drain(Span<float> output) => 0;
    /// <summary>Clears history on seek, stop/restart or replacement. Pause/resume does not reset the processor.</summary>
    public virtual void Reset() { }
    /// <summary>Releases owned state after processing has quiesced. Shared immutable assets remain their owner's responsibility.</summary>
    public virtual void Dispose() { }
}

/// <summary>Send timing policy. No compensation is the default; every compensation path requires an explicit opt-in.</summary>
public enum LatencyCompensation
{
    /// <summary>Preserve independent path timing, even when processors declare buffering latency.</summary>
    None,
    /// <summary>Compensate the buffering latency of all processors after the capture point.</summary>
    Automatic,
    /// <summary>Compensate only later processors whose CompensateLatency flag is explicitly enabled.</summary>
    ProcessorOptIn
}
