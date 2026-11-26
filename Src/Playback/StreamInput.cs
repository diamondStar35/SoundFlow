using System.Buffers;
using System.Runtime.ExceptionServices;

namespace SoundFlow.Playback;

/// <summary>
/// Bridges serialized native I/O callbacks to a managed Stream. A reusable managed buffer permits cancellable
/// ReadAsync without exposing native pointers beyond the callback. Errors are rethrown only after native code returns.
/// </summary>
public sealed class StreamInput : IDisposable
{
    private readonly Stream _stream;
    private byte[]? _scratch;
    private ExceptionDispatchInfo? _failure;
    private CancellationToken _token;
    /// <summary>Wraps a caller-owned readable stream; disposing the bridge does not close it.</summary>
    public StreamInput(Stream stream) => _stream = stream;
    /// <summary>Starts a serialized codec operation; the caller must pair it with ThrowIfFailed after returning from native code.</summary>
    public void Begin(CancellationToken cancellationToken = default) { _failure = null; _token = cancellationToken; cancellationToken.ThrowIfCancellationRequested(); }
    /// <summary>Copies bytes into native storage. Zero signals native EOF; captured failures distinguish errors from real EOF.</summary>
    public int Read(Span<byte> destination)
    {
        try
        {
            if (!_token.CanBeCanceled) return _stream.Read(destination);
            if (_scratch == null || _scratch.Length < destination.Length)
            {
                if (_scratch != null) ArrayPool<byte>.Shared.Return(_scratch);
                _scratch = ArrayPool<byte>.Shared.Rent(destination.Length);
            }
            var count = _stream.ReadAsync(_scratch.AsMemory(0, destination.Length), _token).AsTask().GetAwaiter().GetResult();
            _scratch.AsSpan(0, count).CopyTo(destination);
            return count;
        }
        catch (Exception error) { _failure ??= ExceptionDispatchInfo.Capture(error); return 0; }
    }
    /// <summary>Throws a captured I/O error or cancellation on the managed side of the codec call.</summary>
    public void ThrowIfFailed() { _failure?.Throw(); _token.ThrowIfCancellationRequested(); }
    /// <summary>Returns scratch storage after the native codec has stopped making callbacks.</summary>
    public void Dispose() { if (_scratch != null) { ArrayPool<byte>.Shared.Return(_scratch); _scratch = null; } }
}
