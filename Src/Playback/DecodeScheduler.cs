namespace SoundFlow.Playback;

/// <summary>
/// Fixed workers shared by an engine's PCM producers. A session receives at most one queued turn;
/// a turn produces one block, so a busy decoder cannot monopolize a worker indefinitely between reads.
/// </summary>
public sealed class DecodeScheduler : IDisposable
{
    private readonly object _gate = new();
    private readonly Queue<DecodeSession> _urgent = new();
    private readonly Queue<DecodeSession> _normal = new();
    private readonly HashSet<DecodeSession> _sessions = [];
    private readonly Thread[] _workers;
    private readonly Timer _retry;
    private readonly PlaybackDispatcher _notifications = new();
    private bool _stopping;
    private bool _accepting = true;
    private DecodeSession[] _snapshot = [];
    private long _retiredStarvations;
    private long _retiredFailures;
    private int _active;
    private int _urgentTurns;
    [ThreadStatic] private static DecodeScheduler? _current;
    internal PlaybackConfiguration Configuration { get; }

    /// <summary>Creates fixed background workers. Normally obtained through Engine.Decoding.</summary>
    public DecodeScheduler(PlaybackConfiguration configuration)
    {
        configuration.Validate(); Configuration = configuration;
        _workers = Enumerable.Range(0, configuration.WorkerCount).Select(i => new Thread(Run)
            { IsBackground = true, Name = $"SoundFlow decode {i + 1}" }).ToArray();
        foreach (var worker in _workers) worker.Start();
        _retry = new Timer(_ => Retry(), null, 5, 5);
    }

    internal void Register(DecodeSession session)
    {
        lock (_gate) { ObjectDisposedException.ThrowIf(_stopping || !_accepting, this); _sessions.Add(session); Volatile.Write(ref _snapshot, _sessions.ToArray()); }
    }
    internal void Remove(DecodeSession session)
    {
        lock (_gate) if (_sessions.Remove(session))
        {
            static void Prune(Queue<DecodeSession> queue, DecodeSession removed)
            {
                var count = queue.Count;
                while (count-- > 0) { var item = queue.Dequeue(); if (!ReferenceEquals(item, removed)) queue.Enqueue(item); }
            }
            Prune(_urgent, session); Prune(_normal, session);
            _retiredStarvations += session.Starvations; _retiredFailures += session.Failures;
            Volatile.Write(ref _snapshot, _sessions.ToArray());
        }
    }
    internal void Enqueue(DecodeSession session, bool empty)
    {
        lock (_gate)
        {
            if (_stopping) return;
            (empty ? _urgent : _normal).Enqueue(session);
            Monitor.Pulse(_gate);
        }
    }
    private void Run()
    {
        _current = this;
        try
        {
            while (true)
            {
                DecodeSession session;
                lock (_gate)
                {
                    while (!_stopping && _urgent.Count + _normal.Count == 0) Monitor.Wait(_gate);
                    if (_stopping) return;
                    // Periodically service a partly buffered source even under continuous new admissions.
                    var urgent = _urgent.Count > 0 && (_normal.Count == 0 || _urgentTurns < 8);
                    session = (urgent ? _urgent : _normal).Dequeue();
                    _urgentTurns = urgent ? _urgentTurns + 1 : 0;
                    _active++;
                }
                try { session.RunOne(); }
                finally { lock (_gate) _active--; }
            }
        }
        finally { _current = null; }
    }
    private void Retry()
    {
        foreach (var session in Volatile.Read(ref _snapshot)) session.Request();
    }

    /// <summary>Posts a lifecycle notification outside decoding and mixing locks. Handler exceptions do not stop dispatch.</summary>
    public void Post(Action notification) => _notifications.Post(notification);

    /// <summary>Captures scheduler and bounded PCM queue usage without touching a provider.</summary>
    public DecodingSnapshot GetSnapshot()
    {
        DecodeSession[] sessions; int pending; int active;
        lock (_gate) { sessions = _sessions.ToArray(); pending = _urgent.Count + _normal.Count; active = _active; }
        return new(_workers.Length, sessions.Length, pending, active,
            sessions.Sum(s => s.BufferedFrames), _retiredStarvations + sessions.Sum(s => s.Starvations), _retiredFailures + sessions.Sum(s => s.Failures));
    }

    /// <summary>Stops new session registration and cancels every session before waiting for any one of them. Does not free native resources.</summary>
    public void CancelAll()
    {
        DecodeSession[] sessions;
        lock (_gate) { _accepting = false; sessions = _sessions.ToArray(); }
        foreach (var session in sessions) session.Cancel();
    }
    /// <summary>Rejects lifecycle operations from a decoding callback before any state changes.</summary>
    public void ValidateDisposal()
    {
        if (_current != null)
            throw new InvalidOperationException("Dispose the engine/session outside its decoding callback.");
    }
    internal static void ValidateControlThread()
    {
        if (_current != null) throw new InvalidOperationException("Transport controls and offline waits cannot run inside a decoding worker's provider callback.");
    }
    /// <summary>Cancels and drains owned sessions, then joins the fixed workers. Noncooperative custom reads must return.</summary>
    public void Dispose()
    {
        ValidateDisposal();
        DecodeSession[] sessions;
        lock (_gate)
        {
            if (_stopping) return;
            _stopping = true; sessions = _sessions.ToArray(); Monitor.PulseAll(_gate);
        }
        _retry.Dispose();
        foreach (var session in sessions) session.Cancel();
        foreach (var worker in _workers) worker.Join();
        List<Exception>? errors = null;
        foreach (var session in sessions)
            try { session.Dispose(); } catch (Exception error) { (errors ??= []).Add(error); }
        _notifications.Dispose();
        if (errors != null) throw new AggregateException(errors);
    }
}
