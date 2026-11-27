using SoundFlow.Abstracts;
using SoundFlow.Playback;
using SoundFlow.Enums;
using SoundFlow.Interfaces;
using SoundFlow.Structs;

namespace SoundFlow.Codecs.FFMpeg;

/// <summary>
/// An <see cref="ICodecFactory"/> that uses a native FFmpeg wrapper to provide decoding and encoding
/// for a wide range of audio formats. Register an instance of this factory with your
/// <see cref="SoundFlow.Engine"/> to enable support for additional codecs.
/// </summary>
public sealed class FFmpegCodecFactory : ICodecFactory
{
    /// <inheritdoc />
    public string FactoryId => "SoundFlow.Codecs.FFMpeg";

    /// <inheritdoc />
    /// <remarks>
    /// This list is derived from the specific FFmpeg build configuration, including enabled demuxers,
    /// decoders, and encoders. It maps common file extensions to the codecs available in the native library.
    /// </remarks>
    public IReadOnlyCollection<string> SupportedFormatIds { get; } =
    [
        // Lossless Formats
        "wav",
        "aiff", "aif", "aifc", "caf",
        "flac",
        "alac",
        "ape",
        "wv",
        "tta",
        "shn",

        // Lossy Formats
        "mp3",
        "mp2",
        "ogg", "oga", "weba", "webm",
        "opus",
        "aac",
        "m4a", "m4b", "mp4",
        "wma",
        "ac3", "eac3", "dts", "truehd", "amr", "awb", "sbc",

        // Container Formats
        "mka",
        "mpc",
        "tak",
        "ra",
        "dsf",
        "au",
        "gsm"
    ];

    /// <inheritdoc />
    public int Priority => 100;

    /// <inheritdoc />
    public ISoundDecoder? CreateDecoder(Stream stream, string formatId, AudioFormat format)
    {
        if (!SupportedFormatIds.Contains(formatId.ToLowerInvariant()))
            return null;

        ISoundDecoder decoder = new FFmpegDecoder(stream, format);
        if (format.Channels > 0 && format.SampleRate > 0
            && (decoder.Channels != format.Channels || decoder.SampleRate != format.SampleRate))
        {
            return new PcmFormatConvertingDecoder(decoder, format.Channels, format.SampleRate);
        }

        return decoder;
    }

    /// <inheritdoc />
    public ISoundDecoder? TryCreateDecoder(Stream stream, out AudioFormat detectedFormat, AudioFormat? hintFormat = null)
    {
        detectedFormat = default;
        try
        {

            // use hint or a dummy format to initialize the decoder, but the actual format is determined by the underlying FFmpeg wrapper.
            ISoundDecoder decoder = new FFmpegDecoder(stream, hintFormat ?? AudioFormat.DvdHq);

            // If initialization succeeds, the decoder has determined the actual source format.
            detectedFormat = new AudioFormat
            {
                Format = decoder is FFmpegDecoder native ? native.SourceSampleFormat : decoder.SampleFormat,
                Channels = decoder.Channels,
                SampleRate = decoder.SampleRate,
                Layout = AudioFormat.GetLayoutFromChannels(decoder.Channels)
            };
            if (hintFormat.HasValue
                && hintFormat.Value.Channels > 0
                && hintFormat.Value.SampleRate > 0
                && (decoder.Channels != hintFormat.Value.Channels || decoder.SampleRate != hintFormat.Value.SampleRate))
            {
                return new PcmFormatConvertingDecoder(decoder, hintFormat.Value.Channels, hintFormat.Value.SampleRate);
            }

            return decoder;
        }
        catch (Exception)
        {
            if (stream.CanSeek)
                stream.Position = 0;
            return null;
        }
    }

    /// <inheritdoc />
    public ISoundEncoder? CreateEncoder(Stream stream, string formatId, AudioFormat format)
    {
        // The native wrapper handles resampling from the source format to what the encoder needs.
        var encoderName = formatId.ToLowerInvariant() switch
        {
            "aif" or "aifc" => "aiff",
            "oga" => "ogg",
            "mka" => "matroska",
            "weba" => "webm",
            "m4b" or "mp4" => "m4a",
            var name => name
        };

        if (encoderName == "m4a")
        {
            encoderName = format.Format is SampleFormat.S24 or SampleFormat.S32
                ? "alac"
                : "m4a";
        }

        return SupportedFormatIds.Contains(formatId.ToLowerInvariant())
            ? new FFmpegEncoder(stream, encoderName, format)
            : null;
    }
}
