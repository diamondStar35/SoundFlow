using SoundFlow.Processing;
using SoundFlow.Features;
using SoundFlow.Providers;
using SoundFlow.Components;
using SoundFlow.Enums;
using SoundFlow.Interfaces;
using SoundFlow.Playback;
using SoundFlow.Structs;
namespace SoundFlow.Abstracts;

/// <summary>
/// Software graph player consuming bounded, predecoded PCM. Provider I/O and transport processing run
/// on the owning engine's fixed workers; GenerateAudio never waits for input. Call PrepareForRender only for offline work.
/// </summary>
public abstract class SoundPlayerBase : SoundComponent, IPlayer
{
    internal Transport Transport { get; }
    /// <summary>Transport input before software channel conversion. Seek/loop offsets use this interleaved-sample domain.</summary>
    public AudioFormat InputFormat => Transport.Format;
    /// <summary>Current factory definition; processor instances remain private to this player.</summary>
    public Chain ProcessingChain => _session.Processing.Definition;
    /// <summary>Final software packets, produced outside the physical audio callback. Spans are borrowed only for the handler.</summary>
    public event BufferCallback? SourceBufferProcessed;
    /// <inheritdoc />
    protected override bool ProcessesModifiersInternally => true;
    private readonly DecodeSession _session;
    private RenderSession? _render;
    private readonly int _renderFrames;
    private readonly int _endpointChannels;
    private readonly object? _renderDomain;
    internal bool RequiresRenderDomainAffinity => _render?.Processing.RequiresRenderDomainAffinity == true;
    private int _pendingRenderEnd;
    private int _pendingRenderEpoch;
    private Exception? _renderFailure;
    /// <summary>On-demand processor definition, or null for decode-ahead-only playback.</summary>
    public Chain? RenderProcessingChain => _render?.Processing.Definition;
    /// <summary>Copies bounded decode/render counters for on-demand playback.</summary>
    public RenderProcessingSnapshot? RenderProcessingState => _render?.GetSnapshot();
    private readonly object _consumeGate = new();
    private DecodeSession.Block? _block;
    private int _offset;
    private int _position;
    private int _epoch;
    private volatile bool _changing;
    private volatile PlaybackState _state;
    /// <summary>Creates a player which owns its provider and one bounded producer session.</summary>
    protected SoundPlayerBase(Engine engine, AudioFormat format, IDataProvider dataProvider, Chain? processing = null, int endpointChannels = 0, Chain? renderProcessing = null, int renderFrames = 256, object? renderDomain = null) : base(engine, format)
    {
        Transport = new Transport(format, dataProvider);
        try
        {
            var runner = (processing ?? Chain.Empty).CreateRunner(format with { Format = SampleFormat.F32 }, engine.PlaybackConfiguration.BufferFrames);
            _renderFrames = renderFrames;
            _endpointChannels = endpointChannels;
            _renderDomain = renderDomain;
            _session = new DecodeSession(engine.Decoding, Transport, process: PublishProcessed, processing: runner,
                endpointChannels: renderProcessing == null ? endpointChannels : 0, renderConsumer: renderProcessing != null);
            if (renderProcessing != null)
            {
                var late = renderProcessing.CreateRunner(_session.OutputFormat, renderFrames);
                try { _render = new(_session, late, renderFrames, endpointChannels, renderDomain); }
                catch { late.Dispose(); throw; }
                engine.Decoding.RegisterRenderPoll(PollRenderedNotifications);
            }
            var output = _render?.OutputFormat ?? _session.OutputFormat;
            ValidateEndpoint(output);
            SetProcessingFormat(output);
        }
        catch { _render?.Dispose(); if (_session != null) _session.Dispose(); else Transport.Dispose(); throw; }
    }
    private void PublishProcessed(float[] samples, int count) => SourceBufferProcessed?.Invoke(samples.AsSpan(0, count),
        new BufferInfo(_session.OutputFormat, ProcessingStage.SourceInput, Capability.Playback));
    private void RequireStopped()
    {
        ProcessingScope.ThrowIfProcessing(); ObjectDisposedException.ThrowIf(IsDisposed, this);
        if (State != PlaybackState.Stopped) throw new InvalidOperationException("Stop playback before changing processor structure.");
    }
    private void ValidateEndpoint(AudioFormat output)
    {
        if (_endpointChannels != 0 && output.Ambisonics != null)
            throw new NotSupportedException("Decode Ambisonics explicitly before device playback.");
    }
    /// <summary>Validates and installs new processor state while stopped, preserving the transport input domain.</summary>
    public void ConfigureProcessing(Chain? chain)
    {
        RequireStopped();
        var runner = (chain ?? Chain.Empty).CreateRunner(InputFormat with { Format = SampleFormat.F32 }, Engine.PlaybackConfiguration.BufferFrames);
        try
        {
            foreach (var modifier in Modifiers) if (modifier != null) runner.AddModifier(modifier);
            if (_render == null) ValidateEndpoint(runner.OutputFormat);
            if (_render == null) _session.ReplaceProcessing(runner);
            else _render.ReplaceProducer(runner);
        }
        catch { if (!ReferenceEquals(_session.Processing, runner)) runner.Dispose(); throw; }
        finally { SetProcessingFormat(_render?.OutputFormat ?? _session.OutputFormat); }
    }
    /// <summary>Replaces late processing while stopped, preserving the established endpoint format.</summary>
    public void ConfigureRenderProcessing(Chain? chain)
    {
        RequireStopped();
        if (_render == null) throw new NotSupportedException("Create the source with RenderProcessing to reserve its render handoff.");
        var runner = (chain ?? Chain.Empty).CreateRunner(_session.OutputFormat, _renderFrames);
        RenderSession replacement;
        try
        {
            ValidateEndpoint(runner.OutputFormat);
            if (runner.OutputFormat.WithEndpointChannels(Format.Channels) != Format)
                throw new ArgumentException("Recreate the source to change its endpoint format.");
            replacement = new(_session, runner, _renderFrames, Format.Channels, _renderDomain);
        }
        catch { runner.Dispose(); throw; }
        _render.Dispose();
        _render = replacement;
    }
    /// <inheritdoc />
    public override void AddModifier(SoundModifier modifier)
    {
        RequireStopped(); _session.Change(() => _session.Processing.AddModifier(modifier), true); base.AddModifier(modifier);
    }
    /// <inheritdoc />
    public override void RemoveModifier(SoundModifier modifier)
    {
        RequireStopped(); _session.Change(() => _session.Processing.RemoveModifier(modifier), true); base.RemoveModifier(modifier);
    }
    /// <inheritdoc />
    public float PlaybackSpeed
    {
        get => Transport.PlaybackSpeed;
        set
        {
            if (!float.IsFinite(value) || value == 0 || (value < 0 && !DataProvider.CanSeek)) throw new ArgumentOutOfRangeException(nameof(value));
            Change(() => { Transport.PlaybackSpeed = value; if (DataProvider.CanSeek) Transport.Seek(_position); });
        }
    }
    /// <inheritdoc />
    public PlaybackState State { get => _state; internal set => _state = value; }
    /// <inheritdoc />
    public IDataProvider DataProvider => Transport.DataProvider;
    /// <inheritdoc />
    public bool IsLooping
    {
        get => Transport.IsLooping;
        set
        {
            if (value && !DataProvider.CanSeek) throw new NotSupportedException("Looping requires seekable input.");
            Change(() => { Transport.IsLooping = value; if (DataProvider.CanSeek) Transport.Seek(_position); });
        }
    }
    /// <summary>Content consumed by the software mixer, excluding queued decoding and hardware output latency.</summary>
    public float Time => (float)_position / InputFormat.Channels / InputFormat.SampleRate;
    /// <inheritdoc />
    public float Duration => Transport.Duration;
    /// <inheritdoc />
    public int LoopStartSamples => Transport.LoopStartSamples;
    /// <inheritdoc />
    public int LoopEndSamples => Transport.LoopEndSamples;
    /// <inheritdoc />
    public float LoopStartSeconds => Transport.LoopStartSeconds;
    /// <inheritdoc />
    public float LoopEndSeconds => Transport.LoopEndSeconds;
    /// <inheritdoc />
    public WsolaConfig TimeStretchConfig { get => Transport.TimeStretchConfig; set => Change(() => Transport.TimeStretchConfig = value); }
    /// <inheritdoc />
    public event EventHandler<EventArgs>? PlaybackEnded;
    /// <summary>Terminal producer failure, delivered outside the rendering callback.</summary>
    public event EventHandler<Exception>? PlaybackFailed;
    /// <summary>Delivers natural completion after all prepared PCM has been consumed.</summary>
    protected virtual void OnPlaybackEnded() => PlaybackEnded?.Invoke(this, EventArgs.Empty);
    /// <summary>Waits for one input block for deterministic offline processing. Never call from an audio callback.</summary>
    public override void PrepareForRender()
    {
        base.PrepareForRender();
        if (_state == PlaybackState.Playing) _session.Prepare();
    }
    /// <inheritdoc />
    protected override void GenerateAudio(Span<float> buffer, int channels)
    {
        if (_changing || _state != PlaybackState.Playing || !Monitor.TryEnter(_consumeGate)) return;
        try
        {
            if (_render != null)
            {
                var frames = _render.Read(buffer);
                _position = _render.Position;
                LastConsumedSamples = frames * channels;
                if (_render.Completed) CompleteRenderedPlayback();
                return;
            }
            var written = 0;
            while (written < buffer.Length)
            {
                if (_block == null && !_session.TryTake(false, out _block)) break;
                var count = Math.Min(buffer.Length - written, _block!.Count - _offset);
                _block.Samples.AsSpan(_offset, count).CopyTo(buffer[written..]);
                _offset += count; written += count;
                _position = _block.Positions[_offset / Format.Channels];
                if (_offset == _block.Count)
                {
                    _position = _block.EndPosition; _session.Release(_block); _block = null; _offset = 0;
                }
            }
            // Count consumed PCM only; silence inserted for a missing producer block is not content progress.
            LastConsumedSamples = written;
            if (_session.Done && _session.ReadyCount == 0 && _block == null)
            {
                _state = PlaybackState.Stopped; _session.Enable(false); Enabled = false;
                var epoch = _epoch;
                Engine.Decoding.Post(() => { if (!IsDisposed && epoch == _epoch) OnPlaybackEnded(); });
            }
        }
        catch (Exception error)
        {
            HandleProcessingFailure(error);
        }
        finally { Monitor.Exit(_consumeGate); }
    }
    private void CompleteRenderedPlayback()
    {
        _state = PlaybackState.Stopped;
        Enabled = false;
        _pendingRenderEpoch = _epoch;
        Volatile.Write(ref _pendingRenderEnd, 1);
    }
    private void PollRenderedNotifications()
    {
        var error = Interlocked.Exchange(ref _renderFailure, null);
        if (error != null)
        {
            _session.Enable(false);
            Engine.Decoding.Post(() => { if (!IsDisposed) PlaybackFailed?.Invoke(this, error); });
        }
        if (Interlocked.Exchange(ref _pendingRenderEnd, 0) == 0) return;
        var epoch = _pendingRenderEpoch;
        if (epoch != _epoch || IsDisposed) return;
        _session.Enable(false);
        Engine.Decoding.Post(() => { if (!IsDisposed && epoch == _epoch) OnPlaybackEnded(); });
    }
    /// <summary>Valid samples consumed during the latest software render call.</summary>
    public int LastConsumedSamples { get; private set; }
    /// <inheritdoc />
    protected override bool HandleProcessingFailure(Exception error)
    {
        _state = PlaybackState.Stopped; Enabled = false;
        if (_render != null) { Volatile.Write(ref _renderFailure, error); return true; }
        _session.Enable(false);
        Engine.Decoding.Post(() => { if (!IsDisposed) PlaybackFailed?.Invoke(this, error); });
        return true;
    }
    private void Change(Action action)
    {
        ProcessingScope.ThrowIfProcessing();
        _changing = true; _session.Enable(false);
        // Never hold the consumer gate across provider I/O; a live callback skips only this brief state update.
        lock (_consumeGate) { _epoch++; ReleaseCurrent(); _render?.Suspend(); _render?.Reset(_position); }
        try { _session.Change(() => { action(); if (_state == PlaybackState.Playing) Transport.Play(); }, true); }
        finally { _render?.Resume(); _changing = false; if (_state == PlaybackState.Playing) _session.Enable(true); }
    }
    private void ReleaseCurrent() { if (_block != null) { _session.Release(_block); _block = null; _offset = 0; } }
    /// <inheritdoc />
    public void Play()
    {
        ObjectDisposedException.ThrowIf(IsDisposed, this);
        _epoch++;
        var restarting = _session.Done && _state != PlaybackState.Paused;
        if (_render != null && _state != PlaybackState.Paused) { _render.Suspend(); _render.Reset(restarting ? 0 : _position); _render.Resume(); }
        _session.Change(() => { if (restarting && DataProvider.CanSeek) Transport.Seek(0); Transport.Play(); }, restarting);
        _state = PlaybackState.Playing; Enabled = true; _session.Enable(true);
    }
    /// <inheritdoc />
    public void Pause() { _state = PlaybackState.Paused; Enabled = false; _session.Enable(false); }
    /// <inheritdoc />
    public void Stop()
    {
        _state = PlaybackState.Stopped; Enabled = false; _session.Enable(false);
        Change(() => { Transport.Stop(); _position = Transport.SamplePosition; });
    }
    /// <inheritdoc />
    public bool Seek(TimeSpan time, SeekOrigin seekOrigin = SeekOrigin.Begin) => Seek((float)time.TotalSeconds + (seekOrigin switch
    { SeekOrigin.Begin => 0, SeekOrigin.Current => Time, SeekOrigin.End => Duration, _ => throw new ArgumentOutOfRangeException(nameof(seekOrigin)) }));
    /// <inheritdoc />
    public bool Seek(float time) => float.IsFinite(time) && Seek(checked((int)(time * InputFormat.SampleRate * InputFormat.Channels)));
    /// <inheritdoc />
    public bool Seek(int sampleOffset)
    {
        var result = false;
        Change(() => { result = Transport.Seek(sampleOffset); if (result) _position = Transport.SamplePosition; if (_state == PlaybackState.Playing) Transport.Play(); });
        return result;
    }
    /// <inheritdoc />
    public void SetLoopPoints(float startTime, float? endTime = -1f) => SetLoopPoints((int)(startTime * InputFormat.SampleRate * InputFormat.Channels),
        endTime is null or -1 ? -1 : (int)(endTime.Value * InputFormat.SampleRate * InputFormat.Channels));
    /// <inheritdoc />
    public void SetLoopPoints(int startSample, int endSample = -1) => Change(() => { Transport.SetLoopPoints(startSample, endSample); if (DataProvider.CanSeek) Transport.Seek(_position); });
    /// <inheritdoc />
    public void SetLoopPoints(TimeSpan startTime, TimeSpan? endTime = null) => SetLoopPoints((float)startTime.TotalSeconds, (float?)endTime?.TotalSeconds);
    /// <inheritdoc />
    public void SetTimeStretchQuality(WsolaPerformancePreset preset) => TimeStretchConfig = WsolaConfig.FromPreset(preset);
    /// <inheritdoc />
    protected override void Dispose(bool disposing)
    {
        if (IsDisposed) return;
        ProcessingScope.ThrowIfProcessing();
        if (disposing)
        {
            Engine.Decoding.ValidateDisposal(); _epoch++; _state = PlaybackState.Stopped; _session.Enable(false);
            Engine.Decoding.RemoveRenderPoll(PollRenderedNotifications);
            lock (_consumeGate) { ReleaseCurrent(); _render?.Dispose(); }
            _session.Dispose();
        }
        base.Dispose(disposing);
    }
}
