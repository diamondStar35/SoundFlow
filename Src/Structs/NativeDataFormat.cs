using SoundFlow.Enums;

namespace SoundFlow.Structs;

/// <summary>
/// Represents a native data format with audio format, channels, sample rate, and flags.
/// </summary>
public record struct NativeDataFormat
{
    /// <summary>
    /// The audio sample format.
    /// </summary>
    public SampleFormat Format;

    /// <summary>
    /// The number of audio channels.
    /// </summary>
    public uint Channels;

    /// <summary>
    /// The sample rate of the audio in Hz.
    /// </summary>
    public uint SampleRate;

}
