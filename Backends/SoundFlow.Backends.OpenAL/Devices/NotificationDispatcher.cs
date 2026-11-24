using System.Collections.Concurrent;

namespace SoundFlow.Backends.OpenAL.Devices;

/// <summary>Moves user handlers off context/decode/native threads. Diagnostic pressure cannot grow the diagnostic queue indefinitely.</summary>
internal sealed class NotificationDispatcher : IDisposable
{
    private readonly ConcurrentQueue<(Action Callback, bool Important)> _pending = new();
    private readonly AutoResetEvent _wake = new(false);
    private readonly Thread _thread;
    private readonly object _gate = new();
    private volatile bool _closed;
    private int _diagnostics;
    private long _dropped;
    internal long Dropped => Interlocked.Read(ref _dropped);
    internal string? LastHandlerFailure { get; private set; }

    internal NotificationDispatcher()
    {
        _thread = new Thread(Run) { IsBackground = true, Name = "SoundFlow notifications" };
        _thread.Start();
    }

    internal void Post(Action callback, bool important = false)
    {
        lock (_gate)
        {
            if (_closed) return;
            if (!important && _diagnostics >= 256) { Interlocked.Increment(ref _dropped); return; }
            if (!important) _diagnostics++;
            _pending.Enqueue((callback, important));
            _wake.Set();
        }
    }

    private void Run()
    {
        try
        {
            while (!_closed)
            {
                _wake.WaitOne();
                while (!_closed && _pending.TryDequeue(out var item))
                {
                    if (!item.Important) lock (_gate) _diagnostics--;
                    try { item.Callback(); }
                    catch (Exception error) { LastHandlerFailure = error.ToString(); }
                }
            }
        }
        finally { _wake.Dispose(); }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_closed) return;
            _closed = true;
            while (_pending.TryDequeue(out _)) { }
            _wake.Set();
        }
        if (Thread.CurrentThread != _thread) _thread.Join();
    }
}
