using Transport = SoundFlow.Playback.Transport;
using System.Runtime.ExceptionServices;
using SoundFlow.Playback;

namespace SoundFlow.Backends.OpenAL.Playback;

/// <summary>Bounded reusable PCM blocks. Blocking provider reads run on a decode thread, never the AL context/mixer thread.</summary>
internal sealed class DecodePump : IDisposable
{
    internal sealed class Block(int size)
    {
        public readonly float[] Samples = new float[size];
        public int Count;
        public int StartPosition;
        public int EndPosition;
        public float Speed;
    }
    private readonly Queue<Block> _free;
    private readonly Queue<Block> _ready;
    private readonly object _queueGate = new();
    internal readonly object TransportGate = new();
    private readonly AutoResetEvent _wake = new(false);
    private readonly ManualResetEventSlim _available = new(false);
    private readonly Thread _thread;
    private readonly Transport _transport;
    private readonly Action<float[], int> _process;
    private volatile bool _disposed;
    private volatile bool _enabled;
    private volatile bool _done;
    private volatile bool _starved;
    private int _generation;
    private ExceptionDispatchInfo? _failure;

    public DecodePump(Transport transport, int samples, int blocks, Action<float[], int> process)
    {
        _transport = transport; _process = process;
        _free = new Queue<Block>(blocks); _ready = new Queue<Block>(blocks);
        for (var i = 0; i < blocks; i++) _free.Enqueue(new Block(samples));
        _thread = new Thread(Run) { IsBackground = true, Name = "SoundFlow PCM decode" };
        _thread.Start();
    }
    internal void ThrowIfFailed() => _failure?.Throw();
    public bool Done => _done;
    public int ReadyCount { get { lock (_queueGate) return _ready.Count; } }
    public void Enable(bool enabled) { _enabled = enabled; if (enabled) _wake.Set(); }
    public void Request() { if (_enabled && !_done) { _starved = false; _wake.Set(); } }

    public void Change(Action action, bool invalidate)
    {
        lock (TransportGate)
        {
            if (invalidate)
            {
                lock (_queueGate)
                {
                    _generation++;
                    while (_ready.TryDequeue(out var block)) _free.Enqueue(block);
                    _available.Reset();
                }
                _done = false;
                _starved = false;
            }
            action();
        }
        Request();
    }
    public bool TryTake(bool wait, out Block? block)
    {
        _failure?.Throw();
        if (wait && ReadyCount == 0 && !_done && _enabled)
        {
            Request();
            var deadline = Environment.TickCount64 + 15000;
            while (true)
            {
                lock (_queueGate)
                {
                    if (_ready.Count > 0 || _done || _starved || !_enabled) break;
                    _available.Reset();
                }
                var remaining = deadline - Environment.TickCount64;
                if (remaining <= 0 || !_available.Wait(TimeSpan.FromMilliseconds(remaining)))
                    throw new TimeoutException("The offline PCM provider did not supply a block within 15 seconds.");
                _failure?.Throw();
            }
        }
        _failure?.Throw();
        lock (_queueGate)
        {
            var result = _ready.TryDequeue(out block);
            if (_ready.Count == 0) _available.Reset();
            return result;
        }
    }
    public void Release(Block block)
    {
        lock (_queueGate) _free.Enqueue(block);
        Request();
    }
    private void Run()
    {
        while (!_disposed)
        {
            _wake.WaitOne();
            if (_disposed) break;
            while (_enabled && !_disposed && !_done)
            {
                Block? block;
                int generation;
                lock (_queueGate)
                {
                    if (!_free.TryDequeue(out block)) break;
                    generation = _generation;
                }
                try
                {
                    lock (TransportGate)
                    {
                        if (generation == _generation)
                        {
                            block.Speed = _transport.PlaybackSpeed;
                            block.StartPosition = _transport.SamplePosition;
                            _transport.Render(block.Samples, _transport.Format.Channels);
                            block.Count = _transport.LastReadSamples;
                            block.EndPosition = _transport.SamplePosition;
                            if (block.Count > 0) _process(block.Samples, block.Count);
                            if (_transport.State == Enums.PlaybackState.Stopped) _done = true;
                        }
                        else block.Count = 0;
                    }
                    lock (_queueGate)
                    {
                        if (generation == _generation && block.Count > 0) _ready.Enqueue(block);
                        else _free.Enqueue(block);
                        if (generation == _generation && block.Count == 0 && !_done) _starved = true;
                        _available.Set();
                    }
                    // Zero samples while the transport is still playing means starvation, not EOF.
                    if (block.Count == 0) break;
                }
                catch (Exception ex)
                {
                    _failure = ExceptionDispatchInfo.Capture(ex);
                    _done = true;
                    _available.Set();
                    break;
                }
            }
        }
    }
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _wake.Set();
        _available.Set();
        if (Thread.CurrentThread != _thread) _thread.Join();
        _wake.Dispose(); _available.Dispose();
        _transport.Dispose();
    }
}
