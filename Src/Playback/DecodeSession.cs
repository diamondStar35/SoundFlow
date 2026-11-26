using SoundFlow.Processing;
using SoundFlow.Structs;
using System.Runtime.ExceptionServices;
using SoundFlow.Enums;

namespace SoundFlow.Playback;

/// <summary>
/// Exclusive PCM producer with bounded reusable storage. Consumers never wait unless they explicitly request offline preparation.
/// Control operations serialize with reads; call them from control threads, never an audio callback.
/// </summary>
public sealed class DecodeSession : IDisposable
{
    /// <summary>A borrowed PCM block. Return exactly once with Release after consuming its samples.</summary>
    public sealed class Block
    {
        internal readonly float[] Input;
        internal readonly float[] Processed;
        internal readonly int Version;
        internal Block(int frames, Runner runner, AudioFormat output, int version)
        {
            Version = version; Input = new float[checked(frames * runner.InputFormat.Channels)];
            Processed = new float[checked(frames * runner.OutputFormat.Channels)];
            Samples = output == runner.OutputFormat ? Processed : new float[checked(frames * output.Channels)];
            Captures = runner.CreateCaptures(); Positions = new int[frames + 1];
        }
        /// <summary>Independent point storage for this packet; only Count / output-channel-count frames are valid.</summary>
        public IReadOnlyDictionary<Point, float[]> Captures { get; }
        /// <summary>Content positions in input interleaved samples at each output frame boundary, including buffering delay.</summary>
        public int[] Positions { get; }
        /// <summary>Reusable interleaved F32 storage; only Count samples are valid.</summary>
        public float[] Samples { get; }
        /// <summary>Number of valid interleaved samples in Samples.</summary>
        public int Count { get; internal set; }
        /// <summary>Content position before this block, in interleaved samples.</summary>
        public int StartPosition { get; internal set; }
        /// <summary>Content position after this block, including loop wraps.</summary>
        public int EndPosition { get; internal set; }
        /// <summary>Signed transport speed used to map partial consumption back to content time.</summary>
        public float Speed { get; internal set; }
    }
    private readonly object _gate = new();
    private readonly object _transportGate = new();
    private readonly Queue<Block> _free;
    private readonly int _frames;
    private readonly int _blocks;
    private int _version;
    private bool _inputEnded;
    private int[] _clock = [];
    private int _clockCursor;
    private int _mappedPosition;
    private readonly int _endpointChannels;
    /// <summary>Owned, serialized DSP state. Backends edit it only through Change while stopped.</summary>
    public Runner Processing { get; private set; }
    /// <summary>Final packet format, including optional endpoint channel conversion.</summary>
    public AudioFormat OutputFormat => Processing.OutputFormat with
    {
        Channels = _endpointChannels == 0 ? Processing.OutputFormat.Channels : _endpointChannels,
        Layout = AudioFormat.GetLayoutFromChannels(_endpointChannels == 0 ? Processing.OutputFormat.Channels : _endpointChannels)
    };
    private readonly Queue<Block> _ready;
    private readonly DecodeScheduler _scheduler;
    private readonly Transport _transport;
    private readonly Action<float[], int>? _process;
    private readonly CancellationTokenSource _cancel = new();
    private readonly bool _inline;
    private readonly Action? _prepare;
    private bool _scheduled;
    private bool _enabled;
    private bool _disposed;
    private bool _done;
    private bool _starved;
    private int _generation;
    private ExceptionDispatchInfo? _failure;
    private long _starvations;
    private long _failures;
    /// <summary>Whether the decoder reached the end. Ready blocks may still need consumption.</summary>
    public bool Done { get { lock (_gate) return _done; } }
    /// <summary>Completed blocks awaiting consumption; excludes blocks checked out by the consumer.</summary>
    public int ReadyCount { get { lock (_gate) return _ready.Count; } }
    /// <summary>Ready PCM frames, excluding downstream native buffers.</summary>
    public long BufferedFrames { get { lock (_gate) return _ready.Sum(b => (long)b.Count / OutputFormat.Channels); } }
    /// <summary>Producer turns yielding no samples before end of input.</summary>
    public long Starvations => Interlocked.Read(ref _starvations);
    /// <summary>Terminal producer failures captured by this session.</summary>
    public long Failures => Interlocked.Read(ref _failures);

    /// <summary>
    /// Owns a transport and its provider. Inline mode is reserved for nonblocking graph generation;
    /// file and custom providers must use scheduled mode.
    /// </summary>
    public DecodeSession(DecodeScheduler scheduler, Transport transport, int frames = 0, int blocks = 0,
        Action<float[], int>? process = null, bool inline = false, Action? prepare = null, Runner? processing = null, int endpointChannels = 0)
    {
        _scheduler = scheduler; _transport = transport; _process = process; _inline = inline; _prepare = prepare;
        frames = frames == 0 ? scheduler.Configuration.BufferFrames : frames;
        blocks = blocks == 0 ? scheduler.Configuration.QueueBuffers : blocks;
        if (frames <= 0 || blocks < 2) throw new ArgumentOutOfRangeException(nameof(frames));
        if (endpointChannels is < 0 or > 32) throw new ArgumentOutOfRangeException(nameof(endpointChannels));
        if (processing != null && (processing.InputFormat != (transport.Format with { Format = SampleFormat.F32 }) || processing.MaximumFrames != frames))
            throw new ArgumentException("Runner input format and packet bound must match the transport.", nameof(processing));
        _frames = frames; _blocks = blocks; _endpointChannels = endpointChannels;
        Processing = processing ?? Chain.Empty.CreateRunner(transport.Format with { Format = Enums.SampleFormat.F32 }, frames);
        _free = new Queue<Block>(blocks); _ready = new Queue<Block>(blocks);
        try { RebuildPackets(); ResetProcessing(); scheduler.Register(this); } catch { Processing.Dispose(); throw; }
    }
    private void RebuildPackets()
    {
        _version++; _free.Clear(); _ready.Clear();
        for (var i = 0; i < _blocks; i++) _free.Enqueue(new Block(_frames, Processing, OutputFormat, _version));
    }
    private void ResetProcessing()
    {
        Processing.Reset(); _inputEnded = false;
        _clock = new int[Processing.LatencyFrames]; _clockCursor = 0; _mappedPosition = _transport.SamplePosition;
        Array.Fill(_clock, _mappedPosition);
    }
    /// <summary>Installs prevalidated state while stopped. Packet storage is rebuilt before the old state is released.</summary>
    public void ReplaceProcessing(Runner replacement)
    {
        ArgumentNullException.ThrowIfNull(replacement);
        if (ReferenceEquals(Processing, replacement)) throw new ArgumentException("Replacement state must be a different runner.", nameof(replacement));
        if (replacement.InputFormat != (_transport.Format with { Format = Enums.SampleFormat.F32 }) || replacement.MaximumFrames != _frames)
            throw new ArgumentException("Replacement input format and packet bound must match the transport.");
        Runner? old = null;
        try
        {
            Change(() =>
            {
                // Finish allocation and user Reset before publication. A failed replacement leaves the
                // previous runner and its packets intact; no user code runs inside the commit below.
                var output = replacement.OutputFormat with
                {
                    Channels = _endpointChannels == 0 ? replacement.OutputFormat.Channels : _endpointChannels,
                    Layout = AudioFormat.GetLayoutFromChannels(_endpointChannels == 0 ? replacement.OutputFormat.Channels : _endpointChannels)
                };
                var packets = Enumerable.Range(0, _blocks).Select(_ => new Block(_frames, replacement, output, _version + 1)).ToArray();
                var clock = new int[replacement.LatencyFrames];
                var position = _transport.SamplePosition;
                Array.Fill(clock, position);
                replacement.Reset();
                lock (_gate)
                {
                    old = Processing;
                    Processing = replacement;
                    _version++;
                    _generation++;
                    _free.Clear();
                    _ready.Clear();
                    foreach (var packet in packets) _free.Enqueue(packet);
                    _clock = clock;
                    _clockCursor = 0;
                    _mappedPosition = position;
                    _inputEnded = false;
                    _done = false;
                    _starved = false;
                }
            }, false);
        }
        finally { old?.Dispose(); }
    }

    private void MapPositions(Block block, int frames, int inputFrames, int start, int end)
    {
        block.Positions[0] = _mappedPosition;
        for (var frame = 0; frame < frames; frame++)
        {
            var position = inputFrames == 0 ? end : frame + 1 == inputFrames ? end : start + (int)((frame + 1) * block.Speed * _transport.Format.Channels);
            if (_transport.IsLooping)
            {
                var last = _transport.LoopEndSamples >= 0 ? _transport.LoopEndSamples : _transport.DataProvider.Length;
                var first = _transport.LoopStartSamples; var width = last - first;
                if (width > 0 && (position >= last || position < first)) position = first + ((position - first) % width + width) % width;
            }
            if (_clock.Length > 0) { var delayed = _clock[_clockCursor]; _clock[_clockCursor] = position; position = delayed; _clockCursor = (_clockCursor + 1) % _clock.Length; }
            _mappedPosition = Math.Max(0, position); block.Positions[frame + 1] = _mappedPosition;
        }
        block.StartPosition = block.Positions[0]; block.EndPosition = block.Positions[frames];
    }
    private void ConvertEndpoint(Block block, int frames)
    {
        var source = Processing.OutputFormat.Channels; var target = OutputFormat.Channels;
        if (ReferenceEquals(block.Processed, block.Samples)) return;
        for (var frame = 0; frame < frames; frame++)
            for (var channel = 0; channel < target; channel++)
            {
                float sample;
                if (target == 1) { sample = 0; for (var i = 0; i < source; i++) sample += block.Processed[frame * source + i] / source; }
                else sample = block.Processed[frame * source + Math.Min(channel, source - 1)];
                block.Samples[frame * target + channel] = sample;
            }
    }

    /// <summary>Re-throws a captured producer failure on the caller's control thread.</summary>
    public void ThrowIfFailed() { lock (_gate) _failure?.Throw(); }
    /// <summary>Enables or suspends future refill work without discarding already prepared blocks.</summary>
    public void Enable(bool enabled) { lock (_gate) { _enabled = enabled; Monitor.PulseAll(_gate); } if (enabled) Request(); }
    /// <summary>Coalesces refill demand; no allocation, I/O or waiting for the producer.</summary>
    public void Request()
    {
        lock (_gate)
        {
            if (_inline || _scheduled || !_enabled || _disposed || _done || _free.Count == 0 || _cancel.IsCancellationRequested) return;
            _scheduled = true;
            _scheduler.Enqueue(this, _ready.Count == 0);
        }
    }
    /// <summary>Discards decode-ahead before serializing a control operation with the transport.</summary>
    public void Change(Action action, bool invalidate)
    {
        ProcessingScope.ThrowIfProcessing();
        DecodeScheduler.ValidateControlThread();
        ThrowIfFailed();
        lock (_transportGate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            action();
            lock (_gate)
            {
                if (invalidate)
                {
                    _generation++; _done = false; _starved = false; ResetProcessing();
                    while (_ready.TryDequeue(out var block)) _free.Enqueue(block);
                }
            }
        }
        Request();
    }
    /// <summary>Consumes a ready block. Offline callers may wait; live callers receive false immediately on starvation.</summary>
    public bool TryTake(bool wait, out Block? block)
    {
        if (wait) DecodeScheduler.ValidateControlThread();
        if (_inline) RunOne();
        Request();
        lock (_gate)
        {
            var deadline = Environment.TickCount64 + 15000;
            while (wait && _ready.Count == 0 && !_done && !_starved && _enabled && !_disposed && !_cancel.IsCancellationRequested)
            {
                _failure?.Throw();
                var remaining = deadline - Environment.TickCount64;
                if (remaining <= 0) throw new TimeoutException("The offline PCM provider did not supply a block within 15 seconds.");
                Monitor.Wait(_gate, (int)remaining);
            }
            _failure?.Throw();
            return _ready.TryDequeue(out block);
        }
    }
    /// <summary>Waits for producer readiness without consuming audio. Intended for offline graph preparation.</summary>
    public void Prepare()
    {
        ProcessingScope.ThrowIfProcessing();
        DecodeScheduler.ValidateControlThread();
        Request();
        lock (_gate)
        {
            var deadline = Environment.TickCount64 + 15000;
            while (_ready.Count == 0 && !_done && !_starved && _enabled && !_disposed && !_cancel.IsCancellationRequested)
            {
                _failure?.Throw();
                var remaining = deadline - Environment.TickCount64;
                if (remaining <= 0) throw new TimeoutException("Offline graph preparation timed out.");
                Monitor.Wait(_gate, (int)remaining);
            }
            _failure?.Throw();
        }
    }
    /// <summary>Returns a checked-out block and requests another bounded producer turn.</summary>
    public void Release(Block block) { lock (_gate) { if (!_disposed && block.Version == _version) _free.Enqueue(block); } Request(); }
    internal void RunOne()
    {
        Block? block; int generation;
        lock (_gate)
        {
            if (_disposed || !_enabled || _done || _cancel.IsCancellationRequested || !_free.TryDequeue(out block)) { _scheduled = false; return; }
            generation = _generation;
        }
        var produced = false;
        try
        {
            lock (_transportGate)
            {
                lock (_gate) if (_disposed || generation != _generation) return;
                _cancel.Token.ThrowIfCancellationRequested();
                _prepare?.Invoke();
                block.Speed = _transport.PlaybackSpeed; block.StartPosition = _transport.SamplePosition;
                using var timeout = _scheduler.Configuration.ReadTimeout.HasValue ? CancellationTokenSource.CreateLinkedTokenSource(_cancel.Token) : null;
                if (timeout != null) timeout.CancelAfter(_scheduler.Configuration.ReadTimeout!.Value);
                var inputFrames = 0; var start = _transport.SamplePosition;
                if (!_inputEnded)
                {
                    _transport.Render(block.Input, _transport.Format.Channels, timeout?.Token ?? _cancel.Token);
                    inputFrames = _transport.LastReadSamples / _transport.Format.Channels;
                    _inputEnded = _transport.State == PlaybackState.Stopped;
                }
                var frames = inputFrames;
                var captures = (Dictionary<Point, float[]>)block.Captures;
                if (frames > 0) Processing.Process(block.Input.AsSpan(0, frames * _transport.Format.Channels), block.Processed, frames, captures);
                else if (_inputEnded) frames = Processing.Drain(block.Processed, captures);
                ConvertEndpoint(block, frames);
                block.Count = frames * OutputFormat.Channels;
                MapPositions(block, frames, inputFrames, start, _transport.SamplePosition);
                if (block.Count > 0)
                {
                    using var scope = ProcessingScope.Enter(); _process?.Invoke(block.Samples, block.Count);
                }
                lock (_gate)
                {
                    if (generation == _generation && !_disposed)
                    {
                        _done = _inputEnded && frames == 0 && Processing.Completed;
                        _starved = block.Count == 0 && !_done;
                        if (_starved) Interlocked.Increment(ref _starvations);
                        if (block.Count > 0) { _ready.Enqueue(block); produced = true; }
                    }
                }
            }
        }
        catch (OperationCanceledException) when (_cancel.IsCancellationRequested) { }
        catch (Exception error)
        {
            lock (_gate) { _failure = ExceptionDispatchInfo.Capture(error); _done = true; Interlocked.Increment(ref _failures); }
        }
        finally
        {
            lock (_gate)
            {
                if (!produced && block.Version == _version) _free.Enqueue(block);
                _scheduled = false; Monitor.PulseAll(_gate);
            }
        }
        if (produced) Request();
    }
    /// <summary>Requests terminal cancellation. A canceled session cannot restart.</summary>
    public void Cancel()
    {
        try { _cancel.Cancel(); }
        catch (Exception error) { lock (_gate) { _failure = ExceptionDispatchInfo.Capture(error); _done = true; } }
        finally { lock (_gate) Monitor.PulseAll(_gate); }
    }
    /// <summary>Cancels and drains the producer before disposing its transport and input.</summary>
    public void Dispose()
    {
        ProcessingScope.ThrowIfProcessing();
        _scheduler.ValidateDisposal();
        lock (_gate) { if (_disposed) return; _disposed = true; _enabled = false; _generation++; Monitor.PulseAll(_gate); }
        Cancel();
        try { lock (_transportGate) { try { _transport.Dispose(); } finally { Processing.Dispose(); } } }
        finally { _scheduler.Remove(this); }
        // CancellationTokenSource remains alive until outstanding queued scheduler turns have observed disposal.
    }
}
