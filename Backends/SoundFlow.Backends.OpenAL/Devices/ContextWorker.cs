using System.Collections.Concurrent;
using System.Runtime.ExceptionServices;

namespace SoundFlow.Backends.OpenAL.Devices;

/// <summary>Serializes all AL operations on one context-owning thread. Control calls propagate exceptions to their caller.</summary>
internal sealed class ContextWorker : IDisposable
{
    private readonly BlockingCollection<ICommand> _commands = new(256);
    private readonly Thread _thread;
    private readonly Action _cleanup;
    private volatile bool _disposed;
    private ExceptionDispatchInfo? _cleanupFailure;
    private ExceptionDispatchInfo? _failure;

    public ContextWorker(Action initialize, Action tick, Action cleanup, Action<Exception>? failed = null)
    {
        _cleanup = cleanup;
        using var ready = new ManualResetEventSlim();
        _thread = new Thread(() =>
        {
            try { initialize(); }
            catch (Exception ex)
            {
                _failure = ExceptionDispatchInfo.Capture(ex);
                try { _cleanup(); } catch (Exception cleanupError) { _cleanupFailure = ExceptionDispatchInfo.Capture(cleanupError); }
            }
            finally { ready.Set(); }
            if (_failure != null) return;
            try
            {
                while (!_commands.IsCompleted)
                {
                    if (_commands.TryTake(out var command, 2)) command.Run();
                    if (!_disposed)
                    {
                        try { tick(); }
                        catch (Exception ex) { failed?.Invoke(ex); }
                    }
                }
            }
            finally
            {
                try { _cleanup(); } catch (Exception error) { _cleanupFailure = ExceptionDispatchInfo.Capture(error); }
            }
        }) { IsBackground = true, Name = "SoundFlow OpenAL context" };
        _thread.Start();
        ready.Wait();
        _failure?.Throw();
    }

    internal bool IsCurrentThread => Thread.CurrentThread == _thread;

    public T Invoke<T>(Func<T> operation)
    {
        ProcessingScope.ThrowIfProcessing();
        ObjectDisposedException.ThrowIf(_disposed, this);
        _failure?.Throw();
        if (Thread.CurrentThread == _thread) return operation();
        using var command = new Command<T>(operation);
        try { _commands.Add(command); }
        catch (InvalidOperationException) { throw new ObjectDisposedException(nameof(ContextWorker)); }
        return command.Result();
    }
    public void Invoke(Action operation) => Invoke(() => { operation(); return 0; });
    public void Dispose()
    {
        ProcessingScope.ThrowIfProcessing();
        if (_disposed) return;
        _disposed = true;
        _commands.CompleteAdding();
        if (Thread.CurrentThread != _thread) { _thread.Join(); _cleanupFailure?.Throw(); }
    }

    private interface ICommand { void Run(); }
    private sealed class Command<T>(Func<T> operation) : ICommand, IDisposable
    {
        private readonly ManualResetEventSlim _completed = new();
        private T? _value;
        private ExceptionDispatchInfo? _error;
        public void Run()
        {
            try { _value = operation(); }
            catch (Exception ex) { _error = ExceptionDispatchInfo.Capture(ex); }
            finally { _completed.Set(); }
        }
        public T Result() { _completed.Wait(); _error?.Throw(); return _value!; }
        public void Dispose() => _completed.Dispose();
    }
}
