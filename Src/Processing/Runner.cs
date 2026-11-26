using SoundFlow.Abstracts;
using SoundFlow.Enums;
using SoundFlow.Structs;

namespace SoundFlow.Processing;

/// <summary>
/// Exclusively owned chain state and bounded scratch storage. A backend serializes all calls with its transport;
/// captures are copied into caller-owned packet storage before later processors can overwrite scratch buffers.
/// </summary>
public sealed class Runner : IDisposable
{
    private readonly List<Processor> _processors = [];
    private readonly List<float[]> _scratch = [];
    private readonly Dictionary<SoundModifier, ModifierProcessor> _legacy = new(ReferenceEqualityComparer.Instance);
    private int _drainIndex;
    private int _tailUsed;
    private bool _disposed;
    /// <summary>Immutable definition, reused safely across independently created runners.</summary>
    public Chain Definition { get; }
    /// <summary>Provider/transport format, before channel-changing processors.</summary>
    public AudioFormat InputFormat { get; }
    /// <summary>Software chain output, before endpoint rate/channel conversion or native processing.</summary>
    public AudioFormat OutputFormat => _processors.Count == 0 ? InputFormat : _processors[^1].OutputFormat;
    /// <summary>Maximum complete frames in a producer packet.</summary>
    public int MaximumFrames { get; }
    /// <summary>Total declared buffering delay; artistic delays are excluded.</summary>
    public int LatencyFrames => _processors.Sum(processor => processor.LatencyFrames);
    /// <summary>Whether all processor tails have drained.</summary>
    public bool Completed => _drainIndex >= _processors.Count;

    internal Runner(Chain definition, AudioFormat input, int maximumFrames)
    {
        ValidateFormat(input); if (maximumFrames < 1) throw new ArgumentOutOfRangeException(nameof(maximumFrames));
        Definition = definition; InputFormat = input; MaximumFrames = maximumFrames;
        _scratch.Add(new float[checked(maximumFrames * input.Channels)]);
        try { foreach (var factory in definition.Factories) Add(factory(OutputFormat)); }
        catch { Dispose(); throw; }
    }
    private static void ValidateFormat(AudioFormat format)
    {
        if (format.Format != SampleFormat.F32 || format.Channels is < 1 or > 32 || format.SampleRate <= 0)
            throw new ArgumentException("Processors require interleaved F32, 1 through 32 channels and a positive sample rate.");
    }
    private void Add(Processor processor)
    {
        ArgumentNullException.ThrowIfNull(processor);
        if (Interlocked.CompareExchange(ref processor.Lease, 1, 0) != 0) throw new InvalidOperationException("A processor instance is already bound. Supply a factory creating fresh state.");
        try
        {
            ValidateFormat(processor.OutputFormat);
            if (processor.InputFormat != OutputFormat || processor.OutputFormat.SampleRate != InputFormat.SampleRate)
                throw new ArgumentException("Processor formats must match the preceding stage and retain its sample rate.");
            if (processor.LatencyFrames < 0 || processor.MaximumTailFrames < processor.LatencyFrames)
                throw new ArgumentException("A processor must drain its nonnegative declared buffering latency.");
            _ = checked(LatencyFrames + processor.LatencyFrames);
            _scratch.Add(new float[checked(MaximumFrames * processor.OutputFormat.Channels)]);
            _processors.Add(processor);
        }
        catch { try { processor.Dispose(); } finally { processor.Lease = 0; } throw; }
    }
    /// <summary>Adds a borrowed format-preserving modifier while the owning source is stopped.</summary>
    public void AddModifier(SoundModifier modifier)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_legacy.ContainsKey(modifier)) return;
        var adapter = new ModifierProcessor(OutputFormat, modifier); Add(adapter); _legacy.Add(modifier, adapter);
        Reset();
    }
    /// <summary>Removes a borrowed modifier after the owner has stopped consumption and quiesced its producer.</summary>
    public void RemoveModifier(SoundModifier modifier)
    {
        if (!_legacy.Remove(modifier, out var adapter)) return;
        var index = _processors.IndexOf(adapter); _processors.RemoveAt(index); _scratch.RemoveAt(index + 1);
        adapter.Dispose(); adapter.Lease = 0; Reset();
    }
    /// <summary>Returns the exact captured format; foreign definition points are rejected before any routing changes.</summary>
    public AudioFormat GetFormat(Point point)
    {
        ValidatePoint(point); return point.Index == 0 ? InputFormat : _processors[point.Index - 1].OutputFormat;
    }
    private void ValidatePoint(Point point)
    {
        ArgumentNullException.ThrowIfNull(point);
        if (!ReferenceEquals(point.Owner, Definition.Owner) || !Definition.Points.Contains(point)) throw new ArgumentException("The point belongs to another processing chain.", nameof(point));
    }
    /// <summary>Computes explicitly requested compensation. None always returns zero, including for opted-in processors.</summary>
    public int GetCompensationFrames(Point point, LatencyCompensation policy)
    {
        ValidatePoint(point); if (!Enum.IsDefined(policy)) throw new ArgumentOutOfRangeException(nameof(policy));
        return policy == LatencyCompensation.None ? 0 : _processors.Skip(point.Index)
            .Where(processor => policy == LatencyCompensation.Automatic || processor.CompensateLatency).Sum(processor => processor.LatencyFrames);
    }
    /// <summary>Allocates one bounded capture set for a reusable producer packet; no allocations occur during Process or Drain.</summary>
    public Dictionary<Point, float[]> CreateCaptures() => Definition.Points.ToDictionary(point => point,
        point => new float[checked(MaximumFrames * GetFormat(point).Channels)]);
    private void Capture(int position, int frames, Dictionary<Point, float[]> captures)
    {
        foreach (var point in Definition.Points)
            if (point.Index == position) _scratch[position].AsSpan(0, frames * GetFormat(point).Channels).CopyTo(captures[point]);
    }
    /// <summary>Processes one exact-size packet and its independent captures. Publication belongs to the caller after this call succeeds.</summary>
    public void Process(ReadOnlySpan<float> input, Span<float> output, int frames, Dictionary<Point, float[]> captures)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (frames < 0 || frames > MaximumFrames || input.Length != frames * InputFormat.Channels || output.Length < frames * OutputFormat.Channels)
            throw new ArgumentException("Expected complete frames within the configured packet bound.");
        using var scope = Playback.ProcessingScope.Enter();
        _drainIndex = 0; _tailUsed = 0;
        input.CopyTo(_scratch[0]); Capture(0, frames, captures);
        for (var i = 0; i < _processors.Count; i++)
        {
            var processor = _processors[i];
            var destination = _scratch[i + 1].AsSpan(0, frames * processor.OutputFormat.Channels); destination.Clear();
            processor.Process(_scratch[i].AsSpan(0, frames * processor.InputFormat.Channels), destination, frames);
            Capture(i + 1, frames, captures);
        }
        _scratch[^1].AsSpan(0, frames * OutputFormat.Channels).CopyTo(output);
    }
    /// <summary>Drains upstream state through downstream processors. Captures before the active tail receive silence, not downstream tail audio.</summary>
    public int Drain(Span<float> output, Dictionary<Point, float[]> captures)
    {
        using var scope = Playback.ProcessingScope.Enter();
        while (_drainIndex < _processors.Count)
        {
            foreach (var scratch in _scratch) Array.Clear(scratch);
            var processor = _processors[_drainIndex];
            var bound = Math.Min(MaximumFrames, Math.Max(0, processor.MaximumTailFrames - _tailUsed));
            var frames = bound == 0 ? 0 : processor.Drain(_scratch[_drainIndex + 1].AsSpan(0, bound * processor.OutputFormat.Channels));
            if (frames < 0 || frames > bound) throw new InvalidOperationException("A processor exceeded its declared tail/frame bound.");
            if (frames == 0) { _drainIndex++; _tailUsed = 0; continue; }
            _tailUsed += frames;
            for (var i = 0; i <= _drainIndex + 1; i++) Capture(i, frames, captures);
            for (var i = _drainIndex + 1; i < _processors.Count; i++)
            {
                var next = _processors[i];
                next.Process(_scratch[i].AsSpan(0, frames * next.InputFormat.Channels), _scratch[i + 1].AsSpan(0, frames * next.OutputFormat.Channels), frames);
                Capture(i + 1, frames, captures);
            }
            _scratch[^1].AsSpan(0, frames * OutputFormat.Channels).CopyTo(output); return frames;
        }
        return 0;
    }
    /// <summary>Clears processor history after transport discontinuities; paused state is retained by not calling Reset.</summary>
    public void Reset()
    {
        foreach (var processor in _processors) processor.Reset();
        foreach (var scratch in _scratch) Array.Clear(scratch);
        _drainIndex = 0; _tailUsed = 0;
    }
    /// <summary>Attempts every owned processor cleanup even if one implementation throws.</summary>
    public void Dispose()
    {
        if (_disposed) return; _disposed = true;
        List<Exception>? errors = null;
        foreach (var processor in _processors.AsEnumerable().Reverse())
        {
            try { processor.Dispose(); } catch (Exception error) { (errors ??= []).Add(error); }
            finally { processor.Lease = 0; }
        }
        if (errors != null) throw new AggregateException(errors);
    }
}
