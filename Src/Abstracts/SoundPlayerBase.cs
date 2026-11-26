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
    private readonly DecodeSession _session;
    private readonly object _consumeGate = new();
    private DecodeSession.Block? _block;
    private int _offset;
    private int _position;
    private int _epoch;
    private volatile bool _changing;
    private volatile PlaybackState _state;
    /// <summary>Creates a player which owns its provider and one bounded producer session.</summary>
    protected SoundPlayerBase(Engine engine, AudioFormat format, IDataProvider dataProvider) : base(engine, format)
    {
        Transport = new Transport(format, dataProvider);
        try { _session = new DecodeSession(engine.Decoding, Transport); }
        catch { Transport.Dispose(); throw; }
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
    public float Time => (float)_position / Format.Channels / Format.SampleRate;
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
            var written = 0;
            while (written < buffer.Length)
            {
                if (_block == null && !_session.TryTake(false, out _block)) break;
                var count = Math.Min(buffer.Length - written, _block!.Count - _offset);
                _block.Samples.AsSpan(_offset, count).CopyTo(buffer[written..]);
                _offset += count; written += count;
                _position = _block.StartPosition + (int)(_offset * _block.Speed);
                if (_offset == _block.Count)
                {
                    _position = _block.EndPosition; _session.Release(_block); _block = null; _offset = 0;
                }
            }
            // Retain the existing internal test/diagnostic count as consumed PCM, not producer lookahead.
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
    /// <summary>Valid samples consumed during the latest software render call.</summary>
    public int LastConsumedSamples { get; private set; }
    /// <inheritdoc />
    protected override bool HandleProcessingFailure(Exception error)
    {
        _state = PlaybackState.Stopped; _session.Enable(false); Enabled = false;
        Engine.Decoding.Post(() => { if (!IsDisposed) PlaybackFailed?.Invoke(this, error); });
        return true;
    }
    private void Change(Action action)
    {
        ProcessingScope.ThrowIfProcessing();
        _changing = true; _session.Enable(false);
        // Never hold the consumer gate across provider I/O; a live callback skips only this brief state update.
        lock (_consumeGate) { _epoch++; ReleaseCurrent(); }
        try { _session.Change(() => { action(); if (_state == PlaybackState.Playing) Transport.Play(); }, true); }
        finally { _changing = false; if (_state == PlaybackState.Playing) _session.Enable(true); }
    }
    private void ReleaseCurrent() { if (_block != null) { _session.Release(_block); _block = null; _offset = 0; } }
    /// <inheritdoc />
    public void Play()
    {
        ObjectDisposedException.ThrowIf(IsDisposed, this);
        _epoch++;
        var restarting = _session.Done;
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
    public bool Seek(float time) => float.IsFinite(time) && Seek(checked((int)(time * Format.SampleRate * Format.Channels)));
    /// <inheritdoc />
    public bool Seek(int sampleOffset)
    {
        var result = false;
        Change(() => { result = Transport.Seek(sampleOffset); if (result) _position = Transport.SamplePosition; if (_state == PlaybackState.Playing) Transport.Play(); });
        return result;
    }
    /// <inheritdoc />
    public void SetLoopPoints(float startTime, float? endTime = -1f) => SetLoopPoints((int)(startTime * Format.SampleRate * Format.Channels),
        endTime is null or -1 ? -1 : (int)(endTime.Value * Format.SampleRate * Format.Channels));
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
            lock (_consumeGate) ReleaseCurrent();
            _session.Dispose();
        }
        base.Dispose(disposing);
    }
}
