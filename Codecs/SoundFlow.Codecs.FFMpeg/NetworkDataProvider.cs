using SoundFlow.Enums;
using SoundFlow.Interfaces;
using SoundFlow.Metadata.Models;
using SoundFlow.Playback;
using SoundFlow.Providers;
using SoundFlow.Structs;

namespace SoundFlow.Codecs.FFMpeg;

/// <summary>
/// Incremental URL audio input consumed by the engine's shared decoder workers. It does not retain
/// the complete download or decoded file. Live streams are nonseekable and may have unknown duration.
/// </summary>
public sealed class NetworkDataProvider : ICancellableDataProvider
{
    private readonly ISoundDecoder _decoder;
    private NetworkDataProvider(ISoundDecoder decoder)
    {
        _decoder = decoder;
        _decoder.EndOfStreamReached += OnEnd;
        FormatInfo = new SoundFormatInfo
        {
            FormatName = "FFmpeg network audio",
            FormatIdentifier = "network",
            ChannelCount = decoder.Channels,
            SampleRate = decoder.SampleRate,
            Duration = decoder.Length > 0 ? TimeSpan.FromSeconds((double)decoder.Length / decoder.Channels / decoder.SampleRate) : TimeSpan.Zero
        };
    }

    /// <summary>
    /// Opens HTTP(S), RTP/RTSP, RTMP(S), TCP, or UDP audio, including HTTP HLS playlists and radio streams.
    /// Opening/probing runs on the thread pool; later reads run on shared decoder workers. Cancellation
    /// interrupts startup. Read cancellation and disposal interrupt subsequent native I/O.
    /// </summary>
    public static Task<NetworkDataProvider> OpenAsync(Uri uri, AudioFormat format,
        NetworkSettings? settings = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(uri);
        if (!uri.IsAbsoluteUri || uri.Scheme is not ("http" or "https" or "rtsp" or "rtmp" or "rtmps" or "rtp" or "tcp" or "udp"))
            throw new ArgumentException("Use an absolute supported network audio URL.", nameof(uri));
        if (format.Channels is < 1 or > 8 || format.SampleRate <= 0 || format.Format != SampleFormat.F32)
            throw new ArgumentException("Network playback requires an F32 format with a positive rate and 1 through 8 channels.", nameof(format));
        settings ??= new NetworkSettings();
        settings.Validate();
        var snapshot = settings with { Headers = new Dictionary<string, string>(settings.Headers) };
        return Task.Run(() =>
        {
            ISoundDecoder decoder = new FFmpegDecoder(uri, snapshot, cancellationToken);
            try
            {
                if (decoder.Channels != format.Channels || decoder.SampleRate != format.SampleRate)
                    decoder = new PcmFormatConvertingDecoder(decoder, format.Channels, format.SampleRate);
                return new NetworkDataProvider(decoder);
            }
            catch
            {
                decoder.Dispose();
                throw;
            }
        }, cancellationToken);
    }

    /// <inheritdoc />
    public int Position { get; private set; }
    /// <inheritdoc />
    public int Length => _decoder.Length;
    /// <inheritdoc />
    public bool CanSeek => false;
    /// <inheritdoc />
    public SampleFormat SampleFormat => SampleFormat.F32;
    /// <inheritdoc />
    public int SampleRate => _decoder.SampleRate;
    /// <summary>Number of interleaved output channels.</summary>
    public int Channels => _decoder.Channels;
    /// <inheritdoc />
    public bool IsDisposed => _decoder.IsDisposed;
    /// <inheritdoc />
    public SoundFormatInfo FormatInfo { get; }
    /// <inheritdoc />
    public event EventHandler<EventArgs>? EndOfStreamReached;
    /// <inheritdoc />
    public event EventHandler<PositionChangedEventArgs>? PositionChanged;
    private void OnEnd(object? sender, EventArgs args) => EndOfStreamReached?.Invoke(this, args);
    /// <inheritdoc />
    public int ReadSamples(Span<float> buffer) => ReadSamples(buffer, default);
    /// <inheritdoc />
    public int ReadSamples(Span<float> buffer, CancellationToken cancellationToken)
    {
        var count = _decoder is ICancellableDecoder cancellable ? cancellable.Decode(buffer, cancellationToken) : _decoder.Decode(buffer);
        Position = (int)Math.Min(int.MaxValue, (long)Position + count);
        PositionChanged?.Invoke(this, new(Position));
        return count;
    }
    /// <summary>Network inputs do not promise byte-range seeking; reopen to reconnect.</summary>
    public void Seek(int offset) => throw new NotSupportedException("Network audio does not support seeking.");
    /// <inheritdoc />
    public void Dispose()
    {
        _decoder.EndOfStreamReached -= OnEnd;
        _decoder.Dispose();
    }
}
