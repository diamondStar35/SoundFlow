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
        internal Block(int size) => Samples = new float[size];
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
    public long BufferedFrames { get { lock (_gate) return _ready.Sum(b => (long)b.Count / _transport.Format.Channels); } }
    /// <summary>Producer turns yielding no samples before end of input.</summary>
    public long Starvations => Interlocked.Read(ref _starvations);
    /// <summary>Terminal producer failures captured by this session.</summary>
    public long Failures => Interlocked.Read(ref _failures);

    /// <summary>
    /// Owns a transport and its provider. Inline mode is reserved for nonblocking graph generation;
    /// file and custom providers must use scheduled mode.
    /// </summary>
    public DecodeSession(DecodeScheduler scheduler, Transport transport, int frames = 0, int blocks = 0,
        Action<float[], int>? process = null, bool inline = false, Action? prepare = null)
    {
        _scheduler = scheduler; _transport = transport; _process = process; _inline = inline; _prepare = prepare;
        frames = frames == 0 ? scheduler.Configuration.BufferFrames : frames;
        blocks = blocks == 0 ? scheduler.Configuration.QueueBuffers : blocks;
        if (frames <= 0 || blocks < 2) throw new ArgumentOutOfRangeException(nameof(frames));
        _free = new Queue<Block>(blocks); _ready = new Queue<Block>(blocks);
        for (var i = 0; i < blocks; i++) _free.Enqueue(new Block(checked(frames * transport.Format.Channels)));
        scheduler.Register(this);
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
        ThrowIfFailed();
        lock (_transportGate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            action();
            lock (_gate)
            {
                if (invalidate)
                {
                    _generation++; _done = false; _starved = false;
                    while (_ready.TryDequeue(out var block)) _free.Enqueue(block);
                }
            }
        }
        Request();
    }
    /// <summary>Consumes a ready block. Offline callers may wait; live callers receive false immediately on starvation.</summary>
    public bool TryTake(bool wait, out Block? block)
    {
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
    public void Release(Block block) { lock (_gate) { if (!_disposed) _free.Enqueue(block); } Request(); }
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
                _transport.Render(block.Samples, _transport.Format.Channels, timeout?.Token ?? _cancel.Token);
                block.Count = _transport.LastReadSamples; block.EndPosition = _transport.SamplePosition;
                if (block.Count > 0) _process?.Invoke(block.Samples, block.Count);
                lock (_gate)
                {
                    if (generation == _generation && !_disposed)
                    {
                        _done = _transport.State == PlaybackState.Stopped;
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
                if (!produced) _free.Enqueue(block);
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
        try { lock (_transportGate) _transport.Dispose(); }
        finally { _scheduler.Remove(this); }
        // CancellationTokenSource remains alive until outstanding queued scheduler turns have observed disposal.
    }
}
