namespace SoundFlow.Playback;

/// <summary>Marks synchronous graph processing so control operations fail before waiting on an input or destroying resources.</summary>
public static class ProcessingScope
{
    [ThreadStatic] private static int _depth;
    /// <summary>Whether the calling thread is currently generating or processing graph PCM.</summary>
    public static bool IsProcessing => _depth != 0;
    /// <summary>Begins a nestable processing scope. Dispose on the same thread before returning borrowed PCM.</summary>
    public static Scope Enter() { _depth++; return new Scope(); }
    /// <summary>Rejects operations that can wait for I/O or invalidate an active processing graph.</summary>
    public static void ThrowIfProcessing()
    {
        if (IsProcessing) throw new InvalidOperationException("Issue playback controls and disposal outside synchronous PCM processing callbacks.");
    }
    /// <summary>A stack-only same-thread processing marker.</summary>
    public readonly ref struct Scope
    {
        /// <summary>Ends one nested scope.</summary>
        public void Dispose() => _depth--;
    }
}
