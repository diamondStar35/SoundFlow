using SoundFlow.Providers;
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
internal sealed unsafe partial class Voice : IDisposable, ISourcePitch, IEffectSends
{
    private readonly Action<float[], int> _publish;
    private readonly Action _ended;
    internal AudioFormat Format { get; }
    internal PlaybackDevice Device => _owner.Owner;
    internal bool IsDisposed { get; private set; }
    private readonly Context _owner;
    private Functions Al => _owner.Al;
    internal uint Id;
    private readonly uint[] _buffers;
    private readonly Queue<uint> _free;
    private readonly Queue<(uint Buffer, int Start, int End, int Frames, float Speed)> _queued;
    private readonly Transport _transport;
    private readonly DecodeSession _pump;
    private SoundModifier[] _modifiers = [];
    private volatile PlaybackState _state;
    private float _gain = 1;
    private int _playedPosition;
    private bool _hasStarted;
    private bool _failed;
    private readonly object _disposeGate = new();
    internal bool IsGraphFeed;
    internal long Underruns;
    private readonly int _nativeFormat;

    internal Voice(Context owner, IDataProvider provider, AudioFormat format, bool leaveOpen, bool spatial, Action<float[], int> publish, Action ended)
    {
        _owner = owner; Format = format; _publish = publish; _ended = ended;
        _buffers = new uint[owner.Config.QueueBuffers];
        _free = new Queue<uint>(_buffers.Length);
        _queued = new Queue<(uint, int, int, int, float)>(_buffers.Length);
        try
        {
            if (provider.SampleRate != format.SampleRate) throw new ArgumentException("The source format must match its provider's sample rate.", nameof(format));
            _nativeFormat = format.Channels switch
            {
                1 => 0x10010, 2 => 0x10011, 4 => 0x1206, 6 => 0x120C, 7 => 0x120F, 8 => 0x1212,
                _ => throw new NotSupportedException($"Unsupported source channel count {format.Channels}.")
            };
            uint id; Al.alGenSources(1, &id); Id = id;
            Al.CheckAl("Allocate source");
            fixed (uint* pointers = _buffers) Al.alGenBuffers(_buffers.Length, pointers);
            Al.CheckAl("Allocate queue buffers");
            foreach (var buffer in _buffers) _free.Enqueue(buffer);
            _transport = new Transport(format, provider, leaveOpen);
            _pump = new DecodeSession(owner.Owner.Engine.Decoding, _transport, owner.Config.BufferFrames, _buffers.Length, ProcessPcm, provider is GraphProvider, owner.Offline && provider is GraphProvider graph ? graph.PrepareForRender : null);
            ApplySpatial(new SourceSettings { Spatialize = spatial });
            ApplySceneDistance(owner.DistanceModel);
        }
        catch
        {
            _pump?.Dispose();
            if (_buffers.Any(b => b != 0)) fixed (uint* pointers = _buffers) Al.alDeleteBuffers(_buffers.Length, pointers);
            if (Id != 0) { var id = Id; Al.alDeleteSources(1, &id); }
            throw;
        }
    }
    private void ProcessPcm(float[] samples, int count)
    {
        using var processing = Devices.ProcessingScope.Enter();
        var span = samples.AsSpan(0, count);
        foreach (var modifier in Volatile.Read(ref _modifiers))
            if (modifier.Enabled) modifier.Process(span, Format.Channels);
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
        set => Control(() => { _pump.Change(() => _transport.IsLooping = value, false); });
    }
    public float PlaybackSpeed
    {
        get => _transport.PlaybackSpeed;
        set => Control(() =>
        {
            if (!float.IsFinite(value) || value == 0) throw new ArgumentOutOfRangeException(nameof(value));
            if (value < 0 && !DataProvider.CanSeek) throw new NotSupportedException("Reverse playback requires a seekable provider.");
            var position = AudiblePosition();
            _pump.Change(() => { _transport.PlaybackSpeed = value; if (DataProvider.CanSeek) _transport.Seek(position); }, DataProvider.CanSeek);
            if (DataProvider.CanSeek) FlushQueue();
        });
    }
    public float Volume
    {
        get => _gain;
        set => Control(() =>
        {
            if (!float.IsFinite(value) || value < 0) throw new ArgumentOutOfRangeException(nameof(value));
            // OpenAL Soft ramps native gain during mixing, independent of decode-ahead latency.
            Al.alSourcef(Id, 0x100A, value); Al.CheckAl("Set source gain"); _gain = value;
        });
    }
    public float Time => IsDisposed ? (float)_playedPosition / Format.Channels / Format.SampleRate
        : _owner.Worker.Invoke(() => (float)AudiblePosition() / Format.Channels / Format.SampleRate);
    public float Duration => _transport.Duration;
    public int LoopStartSamples => _transport.LoopStartSamples;
    public int LoopEndSamples => _transport.LoopEndSamples;
    public void Play() => Control(() =>
    {
        if (_failed) throw new InvalidOperationException("This voice failed and must be recreated. See backend notifications or diagnostics.");
        _pump.ThrowIfFailed();
        if (_state == PlaybackState.Playing) return;
        var restarting = _pump.Done;
        _pump.Change(() =>
        {
            if (restarting)
            {
                if (!DataProvider.CanSeek) throw new NotSupportedException("An exhausted nonseekable source cannot restart.");
                _transport.Seek(_transport.PlaybackSpeed < 0 && DataProvider.Length > 0 ? DataProvider.Length : 0);
                _playedPosition = _transport.SamplePosition;
            }
            _transport.Play();
        }, restarting);
        _state = PlaybackState.Playing;
        _pump.Enable(true);
        if (!_preparing) ResumeNative();
    });
    public void Pause() => Control(() =>
    {
        if (_state != PlaybackState.Playing) return;
        PauseNative(); _state = PlaybackState.Paused; _pump.Enable(false);
        _pump.Change(_transport.Pause, false);
    });
    public void Stop() => Control(() =>
    {
        ScheduledAt = null;
        _pump.Enable(false);
        _pump.Change(_transport.Stop, true);
        FlushQueue(); _playedPosition = DataProvider.CanSeek ? 0 : _transport.SamplePosition; _state = PlaybackState.Stopped;
    });
    public bool Seek(int sampleOffset)
    {
        var result = false;
        Control(() =>
        {
            if (!DataProvider.CanSeek) return;
            _pump.Change(() => { result = _transport.Seek(sampleOffset); if (_state == PlaybackState.Playing) _transport.Play(); }, true);
            if (result) { FlushQueue(); _playedPosition = _transport.SamplePosition; }
        });
        return result;
    }
    public void SetLoopPoints(int startSample, int endSample = -1) => Control(() =>
    {
        var position = AudiblePosition();
        _pump.Change(() => { _transport.SetLoopPoints(startSample, endSample); if (DataProvider.CanSeek) _transport.Seek(position); }, DataProvider.CanSeek);
        if (DataProvider.CanSeek) FlushQueue();
    });
    public void AddModifier(SoundModifier modifier)
    {
        ArgumentNullException.ThrowIfNull(modifier);
        Control(() => { if (!_modifiers.Contains(modifier)) Volatile.Write(ref _modifiers, [.. _modifiers, modifier]); });
    }
    public void RemoveModifier(SoundModifier modifier) => Control(() => Volatile.Write(ref _modifiers, _modifiers.Where(m => m != modifier).ToArray()));

    internal void ResumeNative()
    {
        if (_state == PlaybackState.Playing && _queued.Count > 0 && Al.SourceInteger(Id, 0x1010) != 0x1012) { Al.alSourcePlay(Id); Al.CheckAl("Resume source"); }
    }
    internal void PauseNative()
    {
        if (_queued.Count > 0) { Al.alSourcePause(Id); Al.CheckAl("Pause source"); }
    }
    internal void FillQueue(bool wait, bool running)
    {
        if (IsDisposed || _failed) return;
        var processed = Al.SourceInteger(Id, 0x1016);
        while (processed-- > 0)
        {
            uint buffer; Al.alSourceUnqueueBuffers(Id, 1, &buffer); Al.CheckAl("Reclaim processed buffer");
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
                fixed (float* samples = block!.Samples) Al.alBufferData(buffer, _nativeFormat, samples, block.Count * sizeof(float), Format.SampleRate);
                Al.CheckAl("Upload source PCM");
                Al.alSourceQueueBuffers(Id, 1, &buffer); Al.CheckAl("Queue source PCM");
                _queued.Enqueue((buffer, block.StartPosition, block.EndPosition, block.Count / Format.Channels, block.Speed));
            }
            finally { _pump.Release(block!); }
            // Offline only waits for one block at a time; decode-ahead remains bounded and starvation does not stall live output.
            wait = false;
        }
        if (_queued.Count > 0 && running && Al.SourceInteger(Id, 0x1010) != 0x1012)
        {
            if (_hasStarted && !_pump.Done) Underruns++;
            Al.alSourcePlay(Id); Al.CheckAl("Start/recover source"); _hasStarted = true;
        }
        if (_pump.Done && _queued.Count == 0 && _pump.ReadyCount == 0)
        {
            _state = PlaybackState.Stopped; _pump.Enable(false); _owner.PostCompletion(() => { if (!IsDisposed) _ended(); });
        }
    }
    private int AudiblePosition()
    {
        if (_queued.Count == 0) return _playedPosition;
        var offset = Al.SourceInteger(Id, 0x1025);
        foreach (var record in _queued)
        {
            if (offset >= record.Frames) { offset -= record.Frames; continue; }
            var position = record.Start + (int)(offset * record.Speed * Format.Channels);
            var end = IsLooping && LoopEndSamples >= 0 ? LoopEndSamples : DataProvider.Length;
            if (IsLooping && end > LoopStartSamples)
            {
                var width = end - LoopStartSamples;
                if (position >= end) position = LoopStartSamples + (position - end) % width;
                else if (position < LoopStartSamples && record.Speed < 0) position = end - (LoopStartSamples - position) % width;
            }
            return Math.Max(0, position);
        }
        return _queued.Last().End;
    }
    private void FlushQueue()
    {
        Al.alSourceStop(Id);
        while (_queued.Count > 0)
        {
            uint buffer; Al.alSourceUnqueueBuffers(Id, 1, &buffer); _queued.Dequeue(); _free.Enqueue(buffer);
        }
        Al.CheckAl("Flush stale source queue");
        _hasStarted = false;
    }
    internal void Fail()
    {
        _failed = true;
        _state = PlaybackState.Stopped;
        _pump.Enable(false);
        try { FlushQueue(); } catch { /* The context may have lost its physical endpoint; native destruction still happens on disposal. */ }
    }

    internal void ValidateDisposal() { Devices.ProcessingScope.ThrowIfProcessing(); _owner.EnsureOutsideBatch(); }

    public void Dispose()
    {
        ValidateDisposal();
        lock (_disposeGate)
        {
            if (IsDisposed) return;
            try
            {
                _owner.Worker.Invoke(() =>
                {
                    FlushQueue();
                    DisposeFilters();
                    var id = Id; Al.alDeleteSources(1, &id);
                    fixed (uint* buffers = _buffers) Al.alDeleteBuffers(_buffers.Length, buffers);
                    Al.CheckAl("Release source");
                    _owner.Sources.Remove(this);
                });
            }
            finally { IsDisposed = true; _pump.Dispose(); }
        }
    }
}
