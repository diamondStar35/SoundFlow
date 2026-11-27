using SoundFlow.Codecs.FFMpeg.Enums;
using SoundFlow.Codecs.FFMpeg.Exceptions;
using SoundFlow.Enums;
using SoundFlow.Interfaces;
using SoundFlow.Structs;
using SoundFlow.Codecs.FFMpeg.Native;
using SoundFlow.Utils;

namespace SoundFlow.Codecs.FFMpeg;

/// <summary>
/// An <see cref="ISoundDecoder"/> implementation that uses a native FFmpeg wrapper
/// to decode various audio formats.
/// </summary>
internal sealed class FFmpegDecoder : ICancellableDecoder
{
    private readonly SafeDecoderHandle _handle;
    private readonly Stream? _stream;
    private readonly SoundFlow.Playback.StreamInput? _input;
    private readonly FFmpeg.ReadCallback? _readCallback;
    private readonly FFmpeg.SeekCallback? _seekCallback;
    private readonly FFmpeg.InterruptCallback? _interrupt;
    private CancellationToken _operationToken;
    private int _disposeRequested;
    private bool _ended;
    private bool _atEnd;

    /// <summary>
    /// Initializes a new instance of the <see cref="FFmpegDecoder"/> class.
    /// </summary>
    /// <param name="stream">The input stream containing the audio data to decode.</param>
    /// <param name="targetFormat">The desired output format for the decoded PCM data.</param>
    public FFmpegDecoder(Stream stream, AudioFormat targetFormat)
    {
        _stream = stream;
        _input = new(stream);

        _readCallback = OnRead;
        _seekCallback = stream.CanSeek ? OnSeek : null;

        _handle = FFmpeg.CreateDecoder();
        if (_handle.IsInvalid)
            throw new InvalidOperationException("Failed to create FFmpeg decoder handle.");

        var result = FFmpeg.InitializeDecoder(_handle, _readCallback, _seekCallback, IntPtr.Zero,
            SampleFormat.F32, out var nativeFormat, out var channels, out var sampleRate);

        if (result != FFmpegResult.Success)
        {
            var logMessage = $"Failed to initialize FFmpeg decoder. Result: {result}";
            Log.Error(logMessage);
            _handle.Dispose();
            throw new FFmpegException(result, logMessage);
        }

        InitializeFormat(nativeFormat, channels, sampleRate);
    }

    internal FFmpegDecoder(Uri uri, NetworkSettings settings, CancellationToken cancellationToken)
    {
        _handle = FFmpeg.CreateDecoder();
        if (_handle.IsInvalid)
            throw new InvalidOperationException("Could not allocate the network decoder.");
        _operationToken = cancellationToken;
        _interrupt = _ => Volatile.Read(ref _disposeRequested) != 0 || _operationToken.IsCancellationRequested ? 1 : 0;
        var strings = new List<nint>();
        nint Utf8(string? text)
        {
            if (text == null)
                return 0;
            var pointer = System.Runtime.InteropServices.Marshal.StringToCoTaskMemUTF8(text);
            strings.Add(pointer);
            return pointer;
        }
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            var options = new FFmpeg.NetworkOptions
            {
                TimeoutMicroseconds = checked((long)(settings.ReadTimeout.TotalMilliseconds * 1000)),
                ProbeBytes = settings.ProbeBytes,
                AnalyzeMicroseconds = checked((long)(settings.AnalyzeDuration.TotalMilliseconds * 1000)),
                UserAgent = Utf8(settings.UserAgent),
                Headers = Utf8(settings.HeaderText()),
                CertificateAuthorities = Utf8(settings.CertificateAuthorityFile),
                RtspTransport = Utf8(settings.RtspOverTcp ? "tcp" : "udp")
            };
            var result = FFmpeg.InitializeNetworkDecoder(_handle, uri.AbsoluteUri, options, _interrupt, 0,
                SampleFormat.F32, out var nativeFormat, out var channels, out var sampleRate);
            cancellationToken.ThrowIfCancellationRequested();
            if (result != FFmpegResult.Success)
                throw new FFmpegException(result, "Could not open network audio. Check the endpoint, timeout, and certificate trust.");
            InitializeFormat(nativeFormat, channels, sampleRate);
        }
        catch
        {
            _handle.Dispose();
            throw;
        }
        finally
        {
            foreach (var pointer in strings)
                System.Runtime.InteropServices.Marshal.FreeCoTaskMem(pointer);
            GC.KeepAlive(_interrupt);
        }
    }

    private void InitializeFormat(SampleFormat nativeFormat, uint channels, uint sampleRate)
    {
        SourceSampleFormat = nativeFormat;
        SourceChannels = Channels = checked((int)channels);
        SourceSampleRate = SampleRate = checked((int)sampleRate);
        SampleFormat = SampleFormat.F32;
        var frames = FFmpeg.GetLengthInPcmFrames(_handle);
        Length = frames > 0 && channels > 0 && frames <= int.MaxValue / channels ? (int)(frames * channels) : 0;
    }

    /// <inheritdoc />
    public bool IsDisposed => _handle.IsClosed;

    /// <inheritdoc />
    public int Length { get; private set; }

    /// <inheritdoc />
    public SampleFormat SampleFormat { get; private set; }
    public SampleFormat SourceSampleFormat { get; private set; }

    /// <inheritdoc />
    public int Channels { get; private set; }
    public int SourceChannels { get; private set; }

    /// <inheritdoc />
    public int SampleRate { get; private set; }
    public int SourceSampleRate { get; private set; }

    /// <inheritdoc />
    public event EventHandler<EventArgs>? EndOfStreamReached;

    /// <inheritdoc />
    public int Decode(Span<float> samples) => Decode(samples, default);

    /// <inheritdoc />
    public unsafe int Decode(Span<float> samples, CancellationToken cancellationToken)
    {
        _operationToken = cancellationToken;
        cancellationToken.ThrowIfCancellationRequested();
        _input?.Begin(cancellationToken);
        if (IsDisposed || samples.IsEmpty)
            return 0;
        if (_atEnd)
        {
            if (!_ended)
            {
                _ended = true;
                EndOfStreamReached?.Invoke(this, EventArgs.Empty);
            }
            return 0;
        }

        var framesToRead = samples.Length / Channels;
        if (framesToRead == 0)
            return 0;
        long framesRead;

        fixed (float* pSamples = samples)
        {
            var result = FFmpeg.ReadPcmFrames(_handle, (IntPtr)pSamples, framesToRead, out framesRead);
            _input?.ThrowIfFailed();
            GC.KeepAlive(_interrupt);
            cancellationToken.ThrowIfCancellationRequested();
            if (result != FFmpegResult.Success)
            {
                throw new FFmpegException(result, $"An unrecoverable error occurred during decoding. Result: {result}");
            }
        }

        var samplesRead = (int)framesRead * Channels;
        if (samplesRead == 0 && !_ended)
        {
            _ended = true;
            EndOfStreamReached?.Invoke(this, EventArgs.Empty);
        }

        return samplesRead;
    }

    /// <inheritdoc />
    public bool Seek(int sampleOffset)
    {
        if (IsDisposed || _stream?.CanSeek != true)
            return false;

        if (sampleOffset < 0 || (Length > 0 && sampleOffset > Length))
            return false;
        if (Length > 0 && sampleOffset == Length)
        {
            _atEnd = true;
            _ended = false;
            return true;
        }
        _atEnd = false;
        var frameIndex = sampleOffset / Channels;
        var result = FFmpeg.SeekToPcmFrame(_handle, frameIndex);
        if (result == FFmpegResult.Success)
            _ended = false;
        return result == FFmpegResult.Success;
    }

    private unsafe nuint OnRead(IntPtr pUserData, IntPtr pBuffer, nuint bytesToRead)
    {
        var buffer = new Span<byte>((void*)pBuffer, checked((int)bytesToRead));
        return (nuint)_input!.Read(buffer);
    }

    private long OnSeek(IntPtr pUserData, long offset, SeekWhence whence)
    {
        try
        {
            if (whence == SeekWhence.Size)
                return _stream!.CanSeek ? _stream.Length : -1;

            if (!_stream!.CanSeek)
                return -1;

            var seekOrigin = (SeekOrigin)((int)whence & 0xFFFF);
            return _stream.Seek(offset, seekOrigin);
        }
        catch
        {
            Log.Critical("Failed to seek stream.");
            return -1;
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (IsDisposed)
            return;
        Interlocked.Exchange(ref _disposeRequested, 1);
        _handle.Dispose();
        _input?.Dispose();

        // Ensure the delegates are not collected while the native code might still be using them.
        GC.KeepAlive(_readCallback);
        GC.KeepAlive(_seekCallback);
        GC.KeepAlive(_interrupt);
    }
}
