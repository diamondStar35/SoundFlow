using SoundFlow.Providers;
using SoundFlow.Enums;
using SoundFlow.Metadata.Models;

namespace SoundFlow.Providers;

/// <summary>
///     Synchronous source of interleaved F32 sample values. A read returning zero may mean starvation until completion is signalled.
/// </summary>
public interface IDataProvider : IDisposable
{
    /// <summary>
    ///     Gets the current playback position in samples.
    /// </summary>
    int Position { get; }

    /// <summary>
    ///     Gets the total length of the audio data in samples.
    ///     May be 0 or -1 if the length is unknown (e.g., for streaming audio).
    /// </summary>
    int Length { get; }

    /// <summary>
    ///     Gets a value indicating whether the data source supports seeking.
    /// </summary>
    bool CanSeek { get; }

    /// <summary>
    ///     Gets the format of the audio samples.
    /// </summary>
    SampleFormat SampleFormat { get; }

    /// <summary>
    ///     Gets the rate of one channel in frames per second.
    /// </summary>
    int SampleRate { get; }

    /// <summary>
    ///     Gets a value indicating whether the data source has been disposed.
    /// </summary>
    bool IsDisposed { get; }

    /// <summary>
    /// Gets the detailed format and tag information read from the source file.
    /// This will be null if the provider is not sourced from a file with metadata (e.g., raw data, microphone).
    /// </summary>
    SoundFormatInfo? FormatInfo { get; }

    /// <summary>
    ///     Reads up to buffer.Length interleaved float samples synchronously. Complete frames must be preserved.
    /// </summary>
    /// <param name="buffer">Borrowed destination for decoded float samples.</param>
    /// <returns>
    ///     The number of interleaved samples actually
    ///     written. Zero is not by itself an end-of-stream signal for live providers.
    /// </returns>
    int ReadSamples(Span<float> buffer);

    /// <summary>
    ///     Sets the playback position to the specified sample offset.
    /// </summary>
    /// <param name="offset">The sample offset to seek to.</param>
    void Seek(int offset);

    /// <summary>
    ///     Raised when the end of the audio stream is reached.
    /// </summary>
    event EventHandler<EventArgs> EndOfStreamReached;

    /// <summary>
    ///     Raised when the playback position changes.
    /// </summary>
    event EventHandler<PositionChangedEventArgs> PositionChanged;
}

/// <summary>
///     Event arguments for the PositionChanged event.
/// </summary>
public class PositionChangedEventArgs : EventArgs
{
    /// <summary>
    ///     Initializes a new instance of the <see cref="PositionChangedEventArgs" /> class.
    /// </summary>
    /// <param name="newPosition">The new playback position in samples.</param>
    public PositionChangedEventArgs(int newPosition)
    {
        NewPosition = newPosition;
    }

    /// <summary>
    ///     Gets the new playback position in samples.
    /// </summary>
    public int NewPosition { get; }
}
