using SoundFlow.Providers;
using FeatureHost = SoundFlow.Features.FeatureHost;
using PlaybackDevice = SoundFlow.Devices.PlaybackDevice;
using SoundFlow.Abstracts;
using SoundFlow.Devices;
using SoundFlow.Enums;
using SoundFlow.Features;
using SoundFlow.Interfaces;
using SoundFlow.Structs;

namespace SoundFlow.Playback;

/// <summary>A device-owned playback source. Software modifiers run before native spatialization, filters and effect sends.</summary>
/// <remarks>Dispose the source before its device. A mutable provider may be consumed by only one source at a time.</remarks>
public abstract class Source : FeatureHost, IPlayer
{
    /// <summary>Owning playback device.</summary>
    public PlaybackDevice Device { get; private set; }
    /// <summary>PCM input format; mono is retained for 3D sources.</summary>
    public AudioFormat Format { get; }
    /// <summary>Whether the source has been disposed.</summary>
    public bool IsDisposed { get; protected set; }
    /// <summary>Creates an owned source and reserves exclusive consumption of its provider.</summary>
    protected Source(PlaybackDevice device, IDataProvider provider, AudioFormat format)
    {
        Device = device;
        Format = format;
        device.RegisterSource(this, provider);
    }
    /// <summary>Reports whether a stopped replacement device can own this implementation without changing its native context.</summary>
    protected internal virtual bool CanTransferTo(PlaybackDevice device) => false;
    /// <summary>Moves implementation state after the device has validated every source in a transfer.</summary>
    protected internal virtual void RebindDevice(PlaybackDevice device) => throw new NotSupportedException("This source cannot leave its native context.");
    /// <summary>Updates the owner after an implementation has moved its processing state. Called only from RebindDevice.</summary>
    protected void SetDeviceOwner(PlaybackDevice device) => Device = device;
    /// <inheritdoc />
    public abstract PlaybackState State { get; }
    /// <inheritdoc />
    public abstract IDataProvider DataProvider { get; }
    /// <inheritdoc />
    public abstract bool IsLooping { get; set; }
    /// <inheritdoc />
    public abstract float PlaybackSpeed { get; set; }
    /// <inheritdoc />
    public abstract float Volume { get; set; }
    /// <inheritdoc />
    public abstract float Time { get; }
    /// <inheritdoc />
    public abstract float Duration { get; }
    /// <inheritdoc />
    public abstract int LoopStartSamples { get; }
    /// <inheritdoc />
    public abstract int LoopEndSamples { get; }
    /// <inheritdoc />
    public float LoopStartSeconds => (float)LoopStartSamples / Format.Channels / Format.SampleRate;
    /// <inheritdoc />
    public float LoopEndSeconds => LoopEndSamples < 0 ? -1 : (float)LoopEndSamples / Format.Channels / Format.SampleRate;
    /// <summary>Raised once after audible queued PCM drains, independently of any remaining effect tail.</summary>
    public event EventHandler? PlaybackEnded;
    /// <summary>Terminal outcome for the current playback, including explicit stop and failure. Delivered once per Play generation.</summary>
    public event EventHandler<PlaybackTermination>? PlaybackTerminated;
    private int _terminated;
    /// <summary>Whether this source can run managed modifiers and publish per-source PCM during playback.</summary>
    public virtual bool SupportsSoftwareProcessing => true;
    private BufferCallback? _bufferProcessed;
    /// <summary>Borrowed source PCM after software modifiers and before native effects, raised on its processing worker.</summary>
    public event BufferCallback? AudioBufferProcessed
    {
        add { if (!SupportsSoftwareProcessing) throw new NotSupportedException("Prepared clips bypass managed PCM processing. Use an ordinary clip source for PCM callbacks."); _bufferProcessed += value; }
        remove { _bufferProcessed -= value; }
    }
    /// <summary>Publishes source-input PCM with its source format.</summary>
    protected void PublishBuffer(Span<float> samples, AudioFormat? format = null) => _bufferProcessed?.Invoke(samples,
        new BufferInfo((format ?? Format) with { Format = SampleFormat.F32 }, ProcessingStage.SourceInput, Capability.Playback));
    /// <summary>Raises completion. Native adapters must defer this until their queues have drained.</summary>
    protected void RaisePlaybackEnded()
    {
        if (Interlocked.Exchange(ref _terminated, 1) != 0) return;
        try { PlaybackEnded?.Invoke(this, EventArgs.Empty); }
        finally { PlaybackTerminated?.Invoke(this, new PlaybackTermination(PlaybackEndReason.Completed)); }
    }
    /// <summary>Starts a fresh lifecycle notification generation before enabling playback.</summary>
    protected void BeginPlayback() => Interlocked.Exchange(ref _terminated, 0);
    /// <summary>Publishes an explicit stop or terminal error without raising natural completion.</summary>
    protected void RaisePlaybackTerminated(PlaybackEndReason reason, Exception? error = null)
    {
        if (Interlocked.Exchange(ref _terminated, 1) == 0) PlaybackTerminated?.Invoke(this, new PlaybackTermination(reason, error));
    }
    /// <inheritdoc />
    public abstract void Play();
    /// <inheritdoc />
    public abstract void Pause();
    /// <inheritdoc />
    public abstract void Stop();
    /// <inheritdoc />
    public abstract bool Seek(int sampleOffset);
    /// <inheritdoc />
    public bool Seek(float time) => float.IsFinite(time) && Seek(checked((int)(time * Format.SampleRate * Format.Channels)));
    /// <inheritdoc />
    public bool Seek(TimeSpan time, SeekOrigin seekOrigin = SeekOrigin.Begin) => Seek((float)time.TotalSeconds + (seekOrigin switch
    {
        SeekOrigin.Begin => 0, SeekOrigin.Current => Time, SeekOrigin.End when Duration > 0 => Duration,
        _ => throw new ArgumentOutOfRangeException(nameof(seekOrigin))
    }));
    /// <inheritdoc />
    public abstract void SetLoopPoints(int startSample, int endSample = -1);
    /// <inheritdoc />
    public void SetLoopPoints(float startTime, float? endTime = -1f) => SetLoopPoints(
        checked((int)(startTime * Format.SampleRate * Format.Channels)),
        endTime is null or -1 ? -1 : checked((int)(endTime.Value * Format.SampleRate * Format.Channels)));
    /// <inheritdoc />
    public void SetLoopPoints(TimeSpan startTime, TimeSpan? endTime = null) => SetLoopPoints((float)startTime.TotalSeconds, (float?)endTime?.TotalSeconds);
    /// <summary>Adds a software modifier before native effects. Call outside a processing callback.</summary>
    public abstract void AddModifier(SoundModifier modifier);
    /// <summary>Removes a software modifier.</summary>
    public abstract void RemoveModifier(SoundModifier modifier);
    /// <inheritdoc />
    private int _disposeStarted;
    private readonly object _disposeGate = new();

    /// <summary>Releases backend resources and always relinquishes the provider lease, including when cleanup fails.</summary>
    public void Dispose()
    {
        if (IsDisposed) return;
        ProcessingScope.ThrowIfProcessing();
        Device.Engine.Decoding.ValidateDisposal();
        ValidateDisposal();
        lock (_disposeGate)
        {
            if (Interlocked.Exchange(ref _disposeStarted, 1) != 0) return;
            try { DisposeSource(); }
            finally { IsDisposed = true; Device.ReleaseSource(this); RaisePlaybackTerminated(PlaybackEndReason.Disposed); }
            GC.SuppressFinalize(this);
        }
    }

    /// <summary>Checks implementation threading constraints before disposal changes ownership. Throwing leaves the source alive.</summary>
    protected virtual void ValidateDisposal() { }

    /// <summary>Releases implementation-owned resources. Provider leasing is handled by the base class.</summary>
    protected abstract void DisposeSource();

    /// <summary>Releases a construction-time lease when an implementation constructor cannot finish.</summary>
    protected void ReleaseProviderOwnership() => Device.ReleaseSource(this);
}
