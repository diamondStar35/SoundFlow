using SoundFlow.Providers;
using Clip = SoundFlow.Playback.Clip;
using Engine = SoundFlow.Engine;
using SoftwareSource = SoundFlow.Playback.SoftwareSource;
using Source = SoundFlow.Playback.Source;
using SpatialSource = SoundFlow.Playback.SpatialSource;
using SoundFlow.Abstracts;
using SoundFlow.Playback;
using SoundFlow.Interfaces;
using System.Runtime.CompilerServices;
using SoundFlow.Components;
using SoundFlow.Structs;
using SoundFlow.Structs.Events;

namespace SoundFlow.Devices;

/// <summary>
/// Represents a playback (output) audio device.
/// </summary>
public abstract class PlaybackDevice : Device
{
    private static readonly ConditionalWeakTable<IDataProvider, Source> ProviderOwners = new();
    private readonly Dictionary<Source, IDataProvider> _sources = new();
    private readonly object _sourceLock = new();
    private readonly List<PlaybackPool> _pools = [];
    /// <summary>Creates an explicitly budgeted pool for transient clips. Device disposal releases all of its pools.</summary>
    public PlaybackPool CreatePool(PlaybackPoolOptions? options = null)
    {
        lock (_sourceLock)
        {
            ObjectDisposedException.ThrowIf(IsDisposed, this);
            var pool = new PlaybackPool(this, options ?? new()); _pools.Add(pool); return pool;
        }
    }
    internal void ReleasePool(PlaybackPool pool) { lock (_sourceLock) _pools.Remove(pool); }

    /// <summary>Creates a source consuming the provider at its source format. The source owns the provider unless leaveOpen is true.</summary>
    public virtual Source CreateSource(IDataProvider provider, AudioFormat format, bool leaveOpen = false)
        => new SoftwareSource(this, provider, format, leaveOpen);

    /// <summary>Creates an ordinary source with explicit channel-aware software processing.</summary>
    public virtual Source CreateSource(IDataProvider provider, AudioFormat format, SourceOptions options, bool leaveOpen = false)
        => new SoftwareSource(this, provider, format, leaveOpen, options);

    /// <summary>Creates an independent source over an immutable decoded clip.</summary>
    public Source CreateSource(Clip clip) => CreateSource(clip.Open(), clip.Format);
    /// <summary>Creates an independently processed clip source; the native prepared-clip fast path is not used.</summary>
    public Source CreateSource(Clip clip, SourceOptions options) => CreateSource(clip.Open(), clip.Format, options);

    /// <summary>Creates one source from a software graph. The graph must not also be attached to another rendering path.</summary>
    public Source CreateSource(SoundComponent graph, bool leaveOpen = false)
        => CreateSource(new GraphProvider(graph, leaveOpen), graph.Format);

    /// <summary>Creates a native spatial voice without implicitly converting its channel layout. Unsupported backends throw.</summary>
    public virtual SpatialSource CreateSpatialSource(IDataProvider provider, AudioFormat format, bool leaveOpen = false)
        => throw new NotSupportedException("This backend does not implement native spatial sources.");

    /// <summary>Creates a native spatial source with explicit software processing; unsupported backends reject the request.</summary>
    public virtual SpatialSource CreateSpatialSource(IDataProvider provider, AudioFormat format, SourceOptions options, bool leaveOpen = false)
        => throw new NotSupportedException("This backend does not implement processed native spatial sources.");

    /// <summary>Creates an independent positional voice over an immutable clip.</summary>
    public SpatialSource CreateSpatialSource(Clip clip) => CreateSpatialSource(clip.Open(), clip.Format);
    /// <summary>Creates a positional clip source with independent software processor state.</summary>
    public SpatialSource CreateSpatialSource(Clip clip, SourceOptions options) => CreateSpatialSource(clip.Open(), clip.Format, options);

    /// <summary>Feeds a software graph into one positional native voice. Its children share this spatial location.</summary>
    public SpatialSource CreateSpatialSource(SoundComponent graph, bool leaveOpen = false)
        => CreateSpatialSource(new GraphProvider(graph, leaveOpen), graph.Format);

    /// <summary>Publishes the number of complete frames produced by one backend rendering operation.</summary>
    protected void NotifyFramesRendered(int frames)
    {
        CachedRenderEventArgs.FrameCount = frames;
        Engine.RaiseAudioFramesRendered(CachedRenderEventArgs);
    }

    /// <summary>Device-wide gain, applied to every source and graph feed. Must be finite and nonnegative.</summary>
    public virtual float MasterGain
    {
        get => MasterMixer.Volume;
        set => MasterMixer.Volume = value;
    }

    internal void RegisterSource(Source source, IDataProvider provider)
    {
        ArgumentNullException.ThrowIfNull(provider);
        if (source.Format.Channels <= 0 || source.Format.SampleRate <= 0)
            throw new ArgumentException("A source requires a positive channel count and sample rate.");
        lock (ProviderOwners)
        {
            ObjectDisposedException.ThrowIf(IsDisposed, this);
            if (ProviderOwners.TryGetValue(provider, out _))
                throw new InvalidOperationException("The provider is already consumed by another source. Create a separate provider or use SoundFlow.Playback.Clip.");
            ProviderOwners.Add(provider, source);
            lock (_sourceLock) _sources.Add(source, provider);
        }
    }

    internal void ReleaseSource(Source source)
    {
        lock (ProviderOwners)
        {
            lock (_sourceLock)
                if (_sources.Remove(source, out var provider)) ProviderOwners.Remove(provider);
        }
    }

    /// <summary>Moves transferable software sources to a replacement device with the same engine and format.</summary>
    /// <remarks>Stop both devices first. Native-context sources reject transfer; native backends should reopen their endpoint instead.</remarks>
    public void TransferSourcesTo(PlaybackDevice target)
    {
        ArgumentNullException.ThrowIfNull(target);
        ObjectDisposedException.ThrowIf(IsDisposed, this);
        ObjectDisposedException.ThrowIf(target.IsDisposed, target);
        if (ReferenceEquals(target, this)) return;
        lock (ProviderOwners)
        lock (_sourceLock)
        lock (target._sourceLock)
        {
            if (_sources.Keys.Any(source => !source.CanTransferTo(target))) throw new NotSupportedException("Every source must support this transfer before ownership can move.");
            foreach (var (source, provider) in _sources)
            {
                source.RebindDevice(target);
                target._sources.Add(source, provider);
            }
            _sources.Clear();
            foreach (var pool in _pools) { pool.RebindDevice(target); target._pools.Add(pool); }
            _pools.Clear();
        }
    }

    /// <summary>Disposes all sources before releasing a backend device and its context.</summary>
    protected void DisposeSources()
    {
        PlaybackPool[] pools;
        lock (_sourceLock) { pools = _pools.ToArray(); _pools.Clear(); }
        List<Exception>? failures = null;
        foreach (var pool in pools)
            try { pool.Dispose(); } catch (Exception error) { (failures ??= []).Add(error); }
        Source[] sources;
        lock (_sourceLock) sources = _sources.Keys.ToArray();
        foreach (var source in sources)
            try { source.Dispose(); } catch (Exception error) { (failures ??= []).Add(error); }
        if (failures != null) throw new AggregateException("One or more sources failed to dispose.", failures);
    }

    /// <summary>
    /// Gets the master mixer for this device. Software graph audio is routed here; native sources may be rendered alongside this graph.
    /// </summary>
    public Mixer MasterMixer { get; }

    /// <summary>
    /// Cached event args object to prevent GC allocations every frame.
    /// </summary>
    protected readonly AudioFramesRenderedEventArgs CachedRenderEventArgs;

    /// <summary>
    /// Initializes a new instance of the <see cref="SoundFlow.Devices.PlaybackDevice"/> class.
    /// </summary>
    /// <param name="engine">The parent audio engine.</param>
    /// <param name="format">The desired audio format.</param>
    /// <param name="config">The device configuration.</param>
    protected PlaybackDevice(Engine engine, AudioFormat format, DeviceConfiguration config) : base(engine, format, config)
    {
        MasterMixer = new Mixer(engine, Format, isMasterMixer: true) { ParentDevice = this };
        CachedRenderEventArgs = new AudioFramesRenderedEventArgs(this, 0);
    }
}
