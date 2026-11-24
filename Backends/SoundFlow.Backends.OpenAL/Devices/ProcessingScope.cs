namespace SoundFlow.Backends.OpenAL.Devices;

/// <summary>Marks borrowed PCM callbacks so synchronous native controls can reject cycles before entering the worker queue.</summary>
internal readonly struct ProcessingScope : IDisposable
{
    [ThreadStatic] private static int _depth;
    internal static ProcessingScope Enter() { _depth++; return new ProcessingScope(); }
    internal static void ThrowIfProcessing()
    {
        if (_depth != 0) throw new InvalidOperationException("Native controls cannot run inside a PCM processing callback. Queue the control on your application thread.");
    }
    public void Dispose() => _depth--;
}
