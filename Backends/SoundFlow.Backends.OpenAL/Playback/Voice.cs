using SoundFlow.Providers;
using SoundFlow.Processing;
using SoundFlow.Backends.OpenAL.Devices;
using Context = SoundFlow.Backends.OpenAL.Devices.Context;
using Functions = SoundFlow.Backends.OpenAL.Interop.Functions;
using IEffectSends = SoundFlow.Effects.IEffectSends;
using PlaybackDevice = SoundFlow.Devices.PlaybackDevice;
using SourceSettings = SoundFlow.Spatial.SourceSettings;
using Transport = SoundFlow.Playback.Transport;
using SoundFlow.Abstracts;
using SoundFlow.Effects;
using SoundFlow.Enums;
using SoundFlow.Features;
using SoundFlow.Interfaces;
using SoundFlow.Playback;
using SoundFlow.Structs;

namespace SoundFlow.Backends.OpenAL.Playback;

/// <summary>One native streaming source with bounded decoded blocks and independently tracked audible/decoded cursors.</summary>
internal sealed unsafe partial class Voice : IDisposable, ISourcePitch, IEffectSends, IProcessingSends
{
    private readonly Action<float[], int> _publish;
    private readonly Action _ended;
    internal AudioFormat Format { get; }
    internal AudioFormat RenderedFormat => IsPrepared ? Format : _pump.OutputFormat;
    internal Chain? ProcessingChain => IsPrepared ? null : _pump.Processing.Definition;
    private readonly Dictionary<uint, int[]> _positions = new();
    internal PlaybackDevice Device => _owner.Owner;
    internal bool IsDisposed { get; private set; }
    private readonly Context _owner;
    private Functions Al => _owner.Al;
    internal uint Id;
    private readonly uint[] _buffers;
    private readonly Queue<uint> _free;
    private readonly Queue<(uint Buffer, int End, int Frames)> _queued;
    private readonly Transport _transport;
    private readonly DecodeSession _pump = null!;
    private readonly PreparedBuffer? _prepared;
    private readonly Action<Exception>? _onFailure;
    private readonly Action? _onBeginning;
    internal bool IsPrepared => _prepared != null;
    internal static int FormatToken(int channels) => channels switch
    {
        1 => 0x10010,
        2 => 0x10011,
        4 => 0x1206,
        6 => 0x120C,
        7 => 0x120F,
        8 => 0x1212,
        _ => throw new NotSupportedException($"Unsupported source channel count {channels}.")
    };
    private SoundModifier[] _modifiers = [];
    private volatile PlaybackState _state;
    private float _gain = 1;
    private int _playedPosition;
    private bool _hasStarted;
    private int _playbackGeneration;
    private bool _failed;
    private readonly object _disposeGate = new();
    internal bool IsGraphFeed;
    internal long Underruns;
    private int _nativeFormat;

    internal Voice(Context owner, IDataProvider provider, AudioFormat format, bool leaveOpen, bool spatial, Action<float[], int> publish, Action ended, PreparedBuffer? prepared = null, Action<Exception>? failed = null, Action? beginning = null, SourceOptions? options = null)
    {
        _owner = owner; Format = format; _publish = publish; _ended = ended; _prepared = prepared; _onFailure = failed; _onBeginning = beginning;
        _buffers = new uint[prepared == null ? owner.Config.QueueBuffers : 0];
        _free = new Queue<uint>(_buffers.Length);
        _queued = new Queue<(uint, int, int)>(_buffers.Length);
        try
        {
            if (provider.SampleRate != format.SampleRate) throw new ArgumentException("The source format must match its provider's sample rate.", nameof(format));
            if (prepared != null && options?.Processing != null) throw new NotSupportedException("Prepared clips bypass software processing.");
            _nativeFormat = FormatToken(format.Channels);
            if (prepared != null) Id = owner.RentPreparedSource();
            else { uint id; Al.alGenSources(1, &id); Id = id; Al.CheckAl("Allocate source"); owner.SourceAllocations++; }
            RefreshNativeIds();
            _transport = new Transport(format, provider, leaveOpen);
            if (prepared == null)
            {
                fixed (uint* pointers = _buffers) Al.alGenBuffers(_buffers.Length, pointers);
                Al.CheckAl("Allocate queue buffers"); owner.BufferAllocations += _buffers.Length;
                foreach (var buffer in _buffers) { _free.Enqueue(buffer); _positions.Add(buffer, new int[owner.Config.BufferFrames + 1]); }
                var runner = (options?.Processing ?? Chain.Empty).CreateRunner(format with { Format = SampleFormat.F32 }, owner.Config.BufferFrames);
                try { _nativeFormat = FormatToken(runner.OutputFormat.Channels); }
                catch { runner.Dispose(); throw; }
                _pump = new DecodeSession(owner.Owner.Engine.Decoding, _transport, owner.Config.BufferFrames, _buffers.Length, ProcessPcm,
                    provider is GraphProvider, owner.Offline && provider is GraphProvider graph ? graph.PrepareForRender : null, runner);
            }
            else
            {
                Al.alSourcei(Id, 0x1009, (int)prepared.Id);
                Al.alSourcei(Id, 0x1007, 0); Al.alSourcef(Id, 0x1003, 1); Al.alSourcef(Id, 0x100A, 1);
                Al.CheckAl("Attach shared prepared clip");
                ApplyRendering(new());
            }
            ApplySpatial(new SourceSettings { Spatialize = spatial });
            ApplySceneDistance(owner.DistanceModel);
            if (prepared != null) owner.RetainClip(prepared);
        }
        catch
        {
            _pump?.Dispose();
            if (_pump == null) _transport?.Dispose();
            if (_buffers.Any(b => b != 0)) fixed (uint* pointers = _buffers) Al.alDeleteBuffers(_buffers.Length, pointers);
            if (Id != 0) { var id = Id; Al.alDeleteSources(1, &id); }
            throw;
        }
    }
    private void ProcessPcm(float[] samples, int count)
    {
        using var processing = Devices.ProcessingScope.Enter();
        _publish(samples, count);
        if (IsGraphFeed) _owner.GraphTap?.Invoke(samples, count);
    }
    private void Control(Action action)
    {
        ObjectDisposedException.ThrowIf(IsDisposed, this);
        _owner.Worker.Invoke(action);
    }
    public PlaybackState State => _state;
    public IDataProvider DataProvider => _transport.DataProvider;
    public bool IsLooping
    {
        get => _transport.IsLooping;
        set
        {
            ValidateTransportControl();
            if (IsPrepared) Control(() => { _transport.IsLooping = value; Al.alSourcei(Id, 0x1007, value ? 1 : 0); Al.CheckAl("Set prepared clip looping"); });
            else
            {
                if (value && !DataProvider.CanSeek) throw new NotSupportedException("Looping requires seekable input.");
                var position = _owner.Worker.Invoke(AudiblePosition); _pump.Enable(false);
                try { _pump.Change(() => { _transport.IsLooping = value; if (DataProvider.CanSeek) _transport.Seek(position); if (_state == PlaybackState.Playing) _transport.Play(); }, true); }
                finally { Control(() => { if (DataProvider.CanSeek) FlushQueue(); if (_state == PlaybackState.Playing) _pump.Enable(true); }); }
            }
        }
    }
    public float PlaybackSpeed
    {
        get => _transport.PlaybackSpeed;
        set
        {
            ValidateTransportControl();
            if (IsPrepared) { if (value != 1) throw new NotSupportedException("Prepared clips support native pitch, not software transport speed changes."); return; }
            if (!float.IsFinite(value) || value == 0) throw new ArgumentOutOfRangeException(nameof(value));
            if (value < 0 && !DataProvider.CanSeek) throw new NotSupportedException("Reverse playback requires a seekable provider.");
            var position = _owner.Worker.Invoke(AudiblePosition);
            _pump.Enable(false);
            _pump.Change(() => { _transport.PlaybackSpeed = value; if (DataProvider.CanSeek) _transport.Seek(position); if (_state == PlaybackState.Playing) _transport.Play(); }, DataProvider.CanSeek);
            Control(() => { if (DataProvider.CanSeek) FlushQueue(); if (_state == PlaybackState.Playing) _pump.Enable(true); });
        }
    }
    public float Volume
    {
        get => _gain;
        set => Control(() =>
        {
            if (!float.IsFinite(value) || value < 0) throw new ArgumentOutOfRangeException(nameof(value));
            // OpenAL Soft ramps native gain during mixing, independent of decode-ahead latency.
            foreach (var relay in _relays) Al.alSourcef(relay.Id, 0x100A, value);
            Al.alSourcef(Id, 0x100A, value); Al.CheckAl("Set source gain"); _gain = value;
        });
    }
    public float Time => IsDisposed ? (float)_playedPosition / Format.Channels / Format.SampleRate
        : _owner.Worker.Invoke(() => (float)AudiblePosition() / Format.Channels / Format.SampleRate);
    public float Duration => _transport.Duration;
    public int LoopStartSamples => _transport.LoopStartSamples;
    public int LoopEndSamples => _transport.LoopEndSamples;
    private void ValidateTransportControl()
    {
        Devices.ProcessingScope.ThrowIfProcessing(); _owner.EnsureOutsideBatch();
        ObjectDisposedException.ThrowIf(IsDisposed, this);
    }
    public void Play()
    {
        ValidateTransportControl();
        if (_failed) throw new InvalidOperationException("This voice failed and must be recreated. See backend notifications or diagnostics.");
        if (_state == PlaybackState.Playing) return;
        _onBeginning?.Invoke(); _playbackGeneration++;
        if (!IsPrepared)
        {
            _pump.ThrowIfFailed();
            var restarting = _pump.Done;
            _pump.Change(() =>
            {
                if (restarting)
                {
                    if (!DataProvider.CanSeek) throw new NotSupportedException("An exhausted nonseekable source cannot restart.");
                    _transport.Seek(_transport.PlaybackSpeed < 0 && DataProvider.Length > 0 ? DataProvider.Length : 0);
                }
                _transport.Play();
            }, restarting);
        }
        Control(() =>
        {
            _state = PlaybackState.Playing; _pump?.Enable(true);
            if (!_preparing && _owner.CanStartPlayback) ResumeNative();
        });
    }
    public void Pause()
    {
        ValidateTransportControl();
        Control(() => { if (_state != PlaybackState.Playing) return; PauseNative(); _state = PlaybackState.Paused; _pump?.Enable(false); });
    }
    public void Stop()
    {
        ValidateTransportControl();
        Control(() =>
        {
            _playbackGeneration++; _preparing = false; ScheduledAt = null; _pump?.Enable(false); FlushQueue();
            _playedPosition = DataProvider.CanSeek ? 0 : _playedPosition; _state = PlaybackState.Stopped; _hasStarted = false;
            if (IsPrepared) { Al.alSourcei(Id, 0x1025, 0); Al.CheckAl("Rewind prepared source"); }
        });
        // Serialized provider seeking may wait, but the context worker is already free and audible playback has stopped.
        if (!IsPrepared) _pump.Change(_transport.Stop, true);
    }
    public bool Seek(int sampleOffset)
    {
        ValidateTransportControl();
        if (IsPrepared)
        {
            if (sampleOffset < 0 || sampleOffset >= DataProvider.Length) return false;
            Control(() => { sampleOffset -= sampleOffset % Format.Channels; Al.alSourcei(Id, 0x1025, sampleOffset / Format.Channels); Al.CheckAl("Seek prepared source"); _playedPosition = sampleOffset; });
            return true;
        }
        if (!DataProvider.CanSeek) return false;
        _pump.Enable(false);
        var result = false;
        _pump.Change(() => { result = _transport.Seek(sampleOffset); if (_state == PlaybackState.Playing) _transport.Play(); }, true);
        Control(() => { if (result) { FlushQueue(); _playedPosition = _transport.SamplePosition; } if (_state == PlaybackState.Playing) _pump.Enable(true); });
        return result;
    }
    public void SetLoopPoints(int startSample, int endSample = -1)
    {
        ValidateTransportControl();
        if (IsPrepared)
        {
            if (startSample != 0 || (endSample != -1 && endSample != DataProvider.Length))
                throw new NotSupportedException("Prepared sources share a native buffer and support only whole-clip loops. Use an ordinary source for authored loop ranges.");
            return;
        }
        var position = _owner.Worker.Invoke(AudiblePosition);
        _pump.Enable(false);
        _pump.Change(() => { _transport.SetLoopPoints(startSample, endSample); if (DataProvider.CanSeek) _transport.Seek(position); if (_state == PlaybackState.Playing) _transport.Play(); }, DataProvider.CanSeek);
        Control(() => { if (DataProvider.CanSeek) FlushQueue(); if (_state == PlaybackState.Playing) _pump.Enable(true); });
    }
    private void RequireStoppedProcessing()
    {
        ValidateTransportControl();
        if (IsPrepared) throw new NotSupportedException("Prepared clips bypass software processing. Use an ordinary or processed pooled source.");
        if (_state != PlaybackState.Stopped) throw new InvalidOperationException("Stop playback before changing processor structure.");
    }
    internal void ConfigureProcessing(Chain? chain)
    {
        RequireStoppedProcessing();
        var runner = (chain ?? Chain.Empty).CreateRunner(Format with { Format = SampleFormat.F32 }, _owner.Config.BufferFrames);
        try
        {
            _ = FormatToken(runner.OutputFormat.Channels);
            foreach (var modifier in _modifiers) runner.AddModifier(modifier);
            Control(() => { FlushQueue(); ClearProcessingRoutes(); });
            try { _pump.ReplaceProcessing(runner); }
            finally { Control(() => _nativeFormat = FormatToken(_pump.OutputFormat.Channels)); }
        }
        catch { if (!ReferenceEquals(_pump.Processing, runner)) runner.Dispose(); throw; }
    }
    public void AddModifier(SoundModifier modifier)
    {
        ArgumentNullException.ThrowIfNull(modifier); RequireStoppedProcessing();
        if (_modifiers.Contains(modifier)) return;
        Control(() => { FlushQueue(); ClearProcessingRoutes(); });
        _pump.Change(() => _pump.Processing.AddModifier(modifier), true);
        _modifiers = [.. _modifiers, modifier];
    }
    public void RemoveModifier(SoundModifier modifier)
    {
        RequireStoppedProcessing(); Control(() => { FlushQueue(); ClearProcessingRoutes(); });
        _pump.Change(() => _pump.Processing.RemoveModifier(modifier), true);
        _modifiers = _modifiers.Where(m => m != modifier).ToArray();
    }

    internal void ResumeNative()
    {
        if (_preparing) return;
        if (IsPrepared)
        {
            if (_state == PlaybackState.Playing && (!ScheduledAt.HasValue) && Al.SourceInteger(Id, 0x1010) != 0x1012)
            { Al.alSourcePlay(Id); Al.CheckAl("Start prepared source"); _hasStarted = true; }
            return;
        }
        if (_state == PlaybackState.Playing && _queued.Count > 0 && Al.SourceInteger(Id, 0x1010) != 0x1012) { PlayNativeEndpoints(); Al.CheckAl("Resume source"); }
    }
    internal void PauseNative()
    {
        if (IsPrepared || _queued.Count > 0) { fixed (uint* ids = NativeIds) Al.alSourcePausev(NativeIds.Length, ids); Al.CheckAl("Pause source"); }
    }
    internal void FillQueue(bool wait, bool running)
    {
        if (IsDisposed || _failed) return;
        running &= !_preparing;
        if (IsPrepared) { FillPrepared(running); return; }
        var processed = Al.SourceInteger(Id, 0x1016);
        foreach (var relay in _relays) processed = Math.Min(processed, Al.SourceInteger(relay.Id, 0x1016));
        while (processed-- > 0)
        {
            uint buffer; Al.alSourceUnqueueBuffers(Id, 1, &buffer); Al.CheckAl("Reclaim processed buffer");
            foreach (var relay in _relays) relay.Reclaim();
            var record = _queued.Dequeue(); _playedPosition = record.End; _free.Enqueue(buffer);
        }
        if (_state != PlaybackState.Playing) return;
        if (wait && DataProvider is GraphProvider graph) graph.PrepareForRender();
        _pump.Request();
        while (_free.Count > 0 && _pump.TryTake(wait, out var block))
        {
            try
            {
                var buffer = _free.Dequeue();
                fixed (float* samples = block!.Samples) Al.alBufferData(buffer, _nativeFormat, samples, block.Count * sizeof(float), RenderedFormat.SampleRate);
                Al.CheckAl("Upload source PCM");
                var frames = block.Count / RenderedFormat.Channels;
                foreach (var relay in _relays) relay.Upload(buffer, block.Captures[relay.Point].AsSpan(0, frames * relay.Format.Channels));
                block.Positions.AsSpan(0, frames + 1).CopyTo(_positions[buffer]);
                Al.alSourceQueueBuffers(Id, 1, &buffer); Al.CheckAl("Queue source PCM");
                _queued.Enqueue((buffer, block.EndPosition, frames));
                foreach (var relay in _relays) relay.Queue(buffer);
            }
            finally { _pump.Release(block!); }
            // Offline only waits for one block at a time; decode-ahead remains bounded and starvation does not stall live output.
            wait = false;
        }
        if (_queued.Count > 0 && running && Al.SourceInteger(Id, 0x1010) != 0x1012)
        {
            if (_hasStarted && !_pump.Done) Underruns++;
            PlayNativeEndpoints(); Al.CheckAl("Start/recover source"); _hasStarted = true;
        }
        if (_pump.Done && _queued.Count == 0 && _pump.ReadyCount == 0)
        {
            _state = PlaybackState.Stopped; _pump.Enable(false); PostNaturalCompletion();
        }
    }
    private void PostNaturalCompletion()
    {
        var generation = _playbackGeneration;
        _owner.PostCompletion(() => { if (!IsDisposed && generation == _playbackGeneration) _ended(); });
    }

    private void FillPrepared(bool running)
    {
        if (_state != PlaybackState.Playing) return;
        var state = Al.SourceInteger(Id, 0x1010);
        if (!_hasStarted)
        {
            if (state == 0x1012 || ScheduledAt.HasValue) _hasStarted = true;
            else if (running) ResumeNative();
        }
        else if (state == 0x1014)
        {
            _playedPosition = DataProvider.Length; _state = PlaybackState.Stopped;
            PostNaturalCompletion();
        }
    }

    private int AudiblePosition()
    {
        if (IsPrepared) return _state == PlaybackState.Stopped ? _playedPosition : Al.SourceInteger(Id, 0x1025) * Format.Channels;
        if (_queued.Count == 0) return _playedPosition;
        var offset = Al.SourceInteger(Id, 0x1025);
        foreach (var record in _queued)
        {
            if (offset >= record.Frames) { offset -= record.Frames; continue; }
            return _positions[record.Buffer][offset];
        }
        return _queued.Last().End;
    }
    private void FlushQueue()
    {
        fixed (uint* ids = NativeIds) Al.alSourceStopv(NativeIds.Length, ids);
        foreach (var relay in _relays) relay.Flush();
        if (IsPrepared) return;
        while (_queued.Count > 0)
        {
            uint buffer; Al.alSourceUnqueueBuffers(Id, 1, &buffer); _queued.Dequeue(); _free.Enqueue(buffer);
        }
        Al.CheckAl("Flush stale source queue");
        _hasStarted = false;
    }
    internal void Fail(Exception? error = null)
    {
        _failed = true;
        _state = PlaybackState.Stopped;
        _pump?.Enable(false);
        if (error != null) _owner.PostCompletion(() => { if (!IsDisposed) _onFailure?.Invoke(error); });
        try { FlushQueue(); } catch { /* The context may have lost its physical endpoint; native destruction still happens on disposal. */ }
    }

    internal void ValidateDisposal() { Devices.ProcessingScope.ThrowIfProcessing(); _owner.EnsureOutsideBatch(); }

    public void Dispose()
    {
        ValidateDisposal();
        lock (_disposeGate)
        {
            if (IsDisposed) return;
            List<Exception>? errors = null;
            void Attempt(Action action) { try { action(); } catch (Exception error) { (errors ??= []).Add(error); } }
            try
            {
                Attempt(() => _pump?.Dispose());
                Attempt(() => _owner.Worker.Invoke(() =>
                {
                    Attempt(FlushQueue); Attempt(ClearProcessingRoutes); Attempt(DisposeFilters);
                    try
                    {
                        if (_prepared != null)
                        {
                            Al.alSourcei(Id, 0x1009, 0); Al.alSourcei(Id, 0x1007, 0); Al.CheckAl("Detach prepared source");
                            if (errors == null) _owner.ReturnPreparedSource(Id);
                            else { var id = Id; Al.alDeleteSources(1, &id); }
                        }
                        else { var id = Id; Al.alDeleteSources(1, &id); }
                        if (_buffers.Length > 0) fixed (uint* buffers = _buffers) Al.alDeleteBuffers(_buffers.Length, buffers);
                        Al.CheckAl("Release source");
                    }
                    finally { if (_prepared != null) _owner.ReleaseClip(_prepared); _owner.Sources.Remove(this); }
                }));
            }
            finally { IsDisposed = true; if (_pump == null) Attempt(_transport.Dispose); }
            if (errors != null) throw new AggregateException("Source cleanup encountered errors.", errors);
        }
    }
}
