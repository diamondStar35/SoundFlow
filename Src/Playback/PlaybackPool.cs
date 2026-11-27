using SoundFlow.Devices;
using SoundFlow.Features;
using SoundFlow.Spatial;

namespace SoundFlow.Playback;

/// <summary>Capacity for one explicit pool. Ordinary device sources are never eligible for stealing.</summary>
public sealed record PlaybackPoolOptions
{
    /// <summary>Occupied playbacks, including paused ones. Native voices are allocated lazily.</summary>
    public int Capacity { get; init; } = 32;
}

/// <summary>Chooses shared immutable native clip storage or an independent software processor path.</summary>
public enum PlaybackMode
{
    /// <summary>Use shared prepared native clips when available; software fallback on other backends.</summary>
    Prepared,
    /// <summary>Create an ordinary source with fresh processor state for each admitted lease.</summary>
    Processed
}

/// <summary>Initial native playback state and admission priority for a transient clip.</summary>
public sealed record PlaybackOptions
{
    /// <summary>Prepared remains the default; select Processed for managed effects and captured-point sends.</summary>
    public PlaybackMode Mode { get; init; }
    /// <summary>Invoked for each processed lease. Reusable definitions are safe because binding creates fresh state.</summary>
    public Func<Processing.Chain>? ProcessingFactory { get; init; }
    /// <summary>Creates a fresh on-demand chain for each processed lease, outside the render callback.</summary>
    public Func<Processing.Chain>? RenderProcessingFactory { get; init; }
    /// <summary>Fixed quantum used by the on-demand factory; 256 frames by default.</summary>
    public int RenderBlockFrames { get; init; } = 256;
    /// <summary>Runs after initial controls but before Play, allowing initial filters and sends without a dry-only first packet.</summary>
    public Action<Source>? Configure { get; init; }
    /// <summary>Higher numbers win; equal priorities never steal one another.</summary>
    public int Priority { get; init; }
    /// <summary>Protected playbacks retain their slot until completion, stop or disposal.</summary>
    public bool Protected { get; init; }
    /// <summary>Initial nonnegative linear gain.</summary>
    public float Volume { get; init; } = 1;
    /// <summary>Initial native pitch/rate multiplier; requires ISourcePitch when different from one.</summary>
    public float Pitch { get; init; } = 1;
    /// <summary>Repeats the entire clip until stopped.</summary>
    public bool Looping { get; init; }
    /// <summary>Null creates a nonspatial voice; otherwise creates a spatial source with this initial snapshot.</summary>
    public SourceSettings? Spatial { get; init; }
}

/// <summary>Copied admission and reuse statistics for one explicit pool.</summary>
public readonly record struct PlaybackPoolSnapshot(int Capacity, int Occupied, long Played, long Stolen, long Rejected);

/// <summary>
/// Device-owned clip admission and lifetime manager. Uses prepared native buffers where available and the
/// ordinary software clip path otherwise. Each lease receives a fresh logical source; only backend resources are reused.
/// </summary>
public sealed class PlaybackPool : IDisposable
{
    private readonly object _gate = new();
    private readonly List<PlaybackLease> _active = [];
    private PlaybackDevice _device;
    private readonly int _capacity;
    private bool _disposed;
    private long _played;
    private long _stolen;
    private long _rejected;
    internal PlaybackPool(PlaybackDevice device, PlaybackPoolOptions options)
    {
        if (options.Capacity is < 1 or > 4096) throw new ArgumentOutOfRangeException(nameof(options.Capacity));
        _device = device; _capacity = options.Capacity;
    }
    /// <summary>Moves a stopped software pool together with all its sources during a validated device transfer.</summary>
    internal void RebindDevice(PlaybackDevice target) => Volatile.Write(ref _device, target);
    /// <summary>Returns an atomic copy of occupancy and lifetime admission totals.</summary>
    public PlaybackPoolSnapshot GetSnapshot() { lock (_gate) return new(_capacity, _active.Count, _played, _stolen, _rejected); }

    /// <summary>Starts a clip or throws if admission fails. Use TryPlay to handle capacity exhaustion without throwing.</summary>
    public PlaybackLease Play(Clip clip, PlaybackOptions? options = null)
        => TryPlay(clip, out var lease, options) ? lease! : throw new InvalidOperationException("The playback pool cannot admit this clip; capacity or native clip storage is exhausted.");

    /// <summary>
    /// Attempts admission, stealing only a strictly lower-priority, unprotected playback from this pool.
    /// Invalid arguments and unsupported features throw. Capacity/native resource exhaustion returns false.
    /// </summary>
    public bool TryPlay(Clip clip, out PlaybackLease? lease, PlaybackOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(clip); options ??= new(); lease = null;
        if (!float.IsFinite(options.Volume) || options.Volume < 0) throw new ArgumentOutOfRangeException(nameof(options.Volume));
        if (!float.IsFinite(options.Pitch) || options.Pitch <= 0) throw new ArgumentOutOfRangeException(nameof(options.Pitch));
        options.Spatial?.Validate();
        if (!Enum.IsDefined(options.Mode)) throw new ArgumentOutOfRangeException(nameof(options.Mode));
        if (options.Mode != PlaybackMode.Processed && (options.ProcessingFactory != null || options.RenderProcessingFactory != null))
            throw new ArgumentException("ProcessingFactory requires Processed mode.", nameof(options));
        if (clip.Frames == 0) throw new ArgumentException("A pooled clip must contain audio.", nameof(clip));
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed || _device.IsDisposed, this);
            var victim = _active.Count < _capacity ? null : _active.Where(p => !p.Options.Protected && p.Options.Priority < options.Priority)
                .OrderBy(p => p.Options.Priority).ThenBy(p => p.Sequence).FirstOrDefault();
            if (_active.Count >= _capacity && victim == null) { _rejected++; return false; }
            PreparedClip? prepared = null; Source? source = null; var userCallback = false;
            try
            {
                if (options.Mode == PlaybackMode.Prepared && _device.TryGetFeature<IPreparedClips>(out var clips)) prepared = clips.Prepare(clip);
                userCallback = true;
                var processing = options.Mode == PlaybackMode.Processed ? new SourceOptions { Processing = options.ProcessingFactory?.Invoke(), RenderProcessing = options.RenderProcessingFactory?.Invoke(), RenderBlockFrames = options.RenderBlockFrames } : null;
                userCallback = false;
                // Prepare the incoming upload before interrupting the old sound. Once stolen, a playback never resumes.
                if (victim != null) { Finish(victim, PlaybackEndReason.Stolen); _stolen++; }
                source = prepared != null
                    ? options.Spatial != null ? prepared.CreateSpatialSource() : prepared.CreateSource()
                    : processing != null ? options.Spatial != null ? _device.CreateSpatialSource(clip, processing) : _device.CreateSource(clip, processing)
                    : options.Spatial != null ? _device.CreateSpatialSource(clip) : _device.CreateSource(clip);
                source.Volume = options.Volume; source.IsLooping = options.Looping;
                if (options.Spatial != null) ((SpatialSource)source).Settings = options.Spatial;
                if (options.Pitch != 1)
                {
                    if (!source.TryGetFeature<ISourcePitch>(out var pitch)) throw new NotSupportedException("This backend does not provide native source pitch.");
                    pitch.Pitch = options.Pitch;
                }
                userCallback = true; options.Configure?.Invoke(source); userCallback = false;
                lease = new PlaybackLease(this, source, prepared, options, ++_played);
                _active.Add(lease); source.PlaybackTerminated += lease.SourceTerminated;
                source.Play(); return true;
            }
            catch (InvalidOperationException) when (!userCallback)
            {
                if (lease != null) Finish(lease, PlaybackEndReason.Failed);
                else { source?.Dispose(); prepared?.Dispose(); }
                lease = null; _rejected++; return false;
            }
            catch
            {
                if (lease != null) Finish(lease, PlaybackEndReason.Failed);
                else { source?.Dispose(); prepared?.Dispose(); }
                lease = null; throw;
            }
        }
    }

    internal void Finish(PlaybackLease lease, PlaybackEndReason reason, Exception? error = null)
    {
        lock (_gate)
        {
            if (!lease.TryFinish(new(reason, error))) return;
            _active.Remove(lease);
            lease.Source.PlaybackTerminated -= lease.SourceTerminated;
            try { lease.Source.Dispose(); }
            finally
            {
                lease.Prepared?.Dispose();
                _device.Engine.Decoding.Post(lease.NotifyEnded);
            }
        }
    }
    /// <summary>Stops every occupied lease, releases its resources and publishes PoolDisposed outcomes.</summary>
    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            List<Exception>? errors = null;
            foreach (var lease in _active.ToArray())
                try { Finish(lease, PlaybackEndReason.PoolDisposed); } catch (Exception error) { (errors ??= []).Add(error); }
            _device.ReleasePool(this);
            if (errors != null) throw new AggregateException(errors);
        }
    }
}

/// <summary>
/// One playback generation. Source is valid until Outcome is set; never retain its native features for later playbacks.
/// Subscribe to Ended for a copied result, or inspect Outcome when attaching a handler after a very short sound completes.
/// </summary>
public sealed class PlaybackLease : IDisposable
{
    private readonly PlaybackPool _pool;
    private PlaybackTermination? _outcome;
    internal PlaybackOptions Options { get; }
    internal long Sequence { get; }
    internal PreparedClip? Prepared { get; }
    /// <summary>Fresh source for this lease only. Returning a lease disposes this source, even when a native voice is reused.</summary>
    public Source Source { get; }
    /// <summary>Null while occupied; immutable terminal outcome afterward.</summary>
    public PlaybackTermination? Outcome => Volatile.Read(ref _outcome);
    /// <summary>Delivered once outside rendering and pool locks, after the source has been released.</summary>
    public event EventHandler<PlaybackTermination>? Ended;
    internal PlaybackLease(PlaybackPool pool, Source source, PreparedClip? prepared, PlaybackOptions options, long sequence)
    { _pool = pool; Source = source; Prepared = prepared; Options = options; Sequence = sequence; }
    internal bool TryFinish(PlaybackTermination outcome) => Interlocked.CompareExchange(ref _outcome, outcome, null) == null;
    internal void SourceTerminated(object? sender, PlaybackTermination outcome) => _pool.Finish(this, outcome.Reason, outcome.Error);
    internal void NotifyEnded() => Ended?.Invoke(this, Outcome!);
    /// <summary>Immediately returns this slot with a Stopped outcome. Repeated calls are harmless.</summary>
    public void Stop() => _pool.Finish(this, PlaybackEndReason.Stopped);
    /// <summary>Returns this playback's slot; disposing a stale lease cannot affect a newer playback.</summary>
    public void Dispose() => Stop();
}
