using SoundFlow.Backends.MiniAudio.Enums;
using SoundFlow.Enums;
using SoundFlow.Interfaces;
using SoundFlow.Structs;

namespace SoundFlow.Backends.MiniAudio;

/// <summary>
/// Implements the <see cref="ICodecFactory"/> for the formats natively supported by MiniAudio.
/// This factory is registered by the <see cref="SoundFlow.Backends.MiniAudio.Engine"/> at a low priority to act as a default/fallback.
/// </summary>
public sealed class CodecFactory : ICodecFactory
{
    /// <inheritdoc />
    public string FactoryId => "SoundFlow.MiniAudio.Default";

    /// <inheritdoc />
    public IReadOnlyCollection<string> SupportedFormatIds { get; } = ["wav", "mp3", "flac"];

    /// <inheritdoc />
    public int Priority => 0; // Low priority, intended as a fallback.

    /// <inheritdoc />
    public ISoundDecoder? CreateDecoder(Stream stream, string formatId, AudioFormat format)
    {
        return SupportedFormatIds.Contains(formatId) ? new Decoder(stream, format.Format, format.Channels, format.SampleRate) : null;
    }

    /// <inheritdoc />
    public ISoundDecoder TryCreateDecoder(Stream stream, out AudioFormat detectedFormat, AudioFormat? hintFormat = null)
    {
        // MiniAudio does not support probing, so we just return a default format.
        detectedFormat = hintFormat ?? AudioFormat.DvdHq;
        return new Decoder(stream, detectedFormat.Format, detectedFormat.Channels, detectedFormat.SampleRate);
    }

    /// <inheritdoc />
    public ISoundEncoder? CreateEncoder(Stream stream, string formatId, AudioFormat format)
    {
        // MiniAudio's encoder only supports WAV.
        return formatId == "wav" ?
            new Encoder(stream, format.Format, format.Channels, format.SampleRate) : null;
    }
}
