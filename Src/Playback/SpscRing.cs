namespace SoundFlow.Playback;

/// <summary>
/// Fixed storage with one producer and one consumer. Publication uses release/acquire ordering so the
/// render thread never enters a producer lock. Clear is allowed only after both sides have quiesced.
/// </summary>
internal sealed class SpscRing<T> where T : class
{
    private readonly T?[] _items;
    private long _read;
    private long _write;

    internal SpscRing(int capacity) => _items = new T[capacity];
    internal int Count => (int)(Volatile.Read(ref _write) - Volatile.Read(ref _read));

    internal bool TryWrite(T value)
    {
        var write = _write;
        if (write - Volatile.Read(ref _read) == _items.Length) return false;
        _items[(int)(write % _items.Length)] = value;
        Volatile.Write(ref _write, write + 1);
        return true;
    }

    internal bool TryRead(out T? value)
    {
        var read = _read;
        if (read == Volatile.Read(ref _write)) { value = null; return false; }
        var index = (int)(read % _items.Length);
        value = _items[index];
        _items[index] = null;
        Volatile.Write(ref _read, read + 1);
        return true;
    }
}
