namespace SoundFlow.Playback;

/// <summary>Serializes completion delivery independently of provider reads and rendering.</summary>
internal sealed class PlaybackDispatcher : IDisposable
{
    private readonly object _gate = new();
    private readonly Queue<Action> _pending = new();
    private Thread? _thread;
    private bool _stopping;
    internal bool IsCurrentThread => Thread.CurrentThread == _thread;
    internal void Post(Action action)
    {
        lock (_gate)
        {
            if (_stopping) return;
            _pending.Enqueue(action);
            if (_thread == null) { _thread = new Thread(Run) { IsBackground = true, Name = "SoundFlow playback events" }; _thread.Start(); }
            Monitor.Pulse(_gate);
        }
    }
    private void Run()
    {
        while (true)
        {
            Action action;
            lock (_gate)
            {
                while (!_stopping && _pending.Count == 0) Monitor.Wait(_gate);
                if (_pending.Count == 0) return;
                action = _pending.Dequeue();
            }
            try { action(); } catch (Exception error) { Utils.Log.Error($"Playback notification failed: {error.Message}"); }
        }
    }
    public void Dispose()
    {
        lock (_gate) { _stopping = true; Monitor.PulseAll(_gate); }
        if (!IsCurrentThread) _thread?.Join();
    }
}
