namespace SoundFlow.Backends.OpenAL;

/// <summary>An OpenAL/ALC operation failed. Includes the operation, native error and loaded library for diagnostics.</summary>
public sealed class BackendException : InvalidOperationException
{
    /// <summary>Native AL or ALC error code.</summary>
    public int NativeError { get; }
    /// <summary>Creates a native failure preserving its context.</summary>
    public BackendException(string operation, int error, string library)
        : base($"OpenAL Soft: {operation} failed with 0x{error:X} ({library}).") => NativeError = error;
}
