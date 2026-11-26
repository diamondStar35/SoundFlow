using Native = SoundFlow.Backends.MiniAudio.Interop.Functions;
using Result = SoundFlow.Backends.MiniAudio.Enums.Result;
using System.Buffers;
using System.Runtime.InteropServices;
using SoundFlow.Backends.MiniAudio.Enums;
using SoundFlow.Enums;
using SoundFlow.Interfaces;
using SoundFlow.Utils;

namespace SoundFlow.Backends.MiniAudio;

/// <summary>
///     An object to assist with converting audio formats into raw PCM frames.
/// </summary>
internal sealed unsafe class Decoder : ICancellableDecoder
{
    private readonly nint _decoder;
    private readonly Stream _stream;
    private readonly SoundFlow.Playback.StreamInput _input;

    // Keep references to delegates to prevent GC collection while native code uses them
    private readonly Native.BufferProcessingCallback _readCallback;
    private readonly Native.SeekCallback? _seekCallbackCallback;

    private bool _endOfStreamReached;
    private byte[]? _rentedReadBuffer;
    private readonly object _syncLock = new();

    /// <summary>
    ///     Constructs a new decoder from the given stream in one of the supported formats.
    /// </summary>
    /// <param name="stream">A stream to a file or streaming audio source in one of the supported formats.</param>
    /// <param name="sampleFormat">The format of the audio samples.</param>
    /// <param name="channels">The number of audio channels.</param>
    /// <param name="sampleRate">The sample rate of the audio.</param>
    /// <param name="seekPointCount">Controls how many seek points the decoder maintains internally</param>
    public Decoder(Stream stream, SampleFormat sampleFormat, int channels, int sampleRate, uint seekPointCount = 100)    // 0 = disable, use binary search only)
    {
        _stream = stream ?? throw new ArgumentNullException(nameof(stream));
        _input = new(stream);
        SampleFormat = sampleFormat;
        Channels = channels;
        SampleRate = sampleRate;

        var configPtr = Native.AllocateDecoderConfig(SampleFormat, (uint)Channels, (uint)SampleRate, seekPointCount);

        _decoder = Native.AllocateDecoder();

        // Store delegates in fields to prevent GC collection
        _readCallback = ReadCallback;

        // Only assign and use the seek callback if the underlying stream supports seeking.
        if (_stream.CanSeek)
        {
            _seekCallbackCallback = SeekCallback;
        }

        var result = Native.DecoderInit(_readCallback, _seekCallbackCallback, nint.Zero, configPtr, _decoder);
        Native.Free(configPtr);

        if (result != Result.Success)
            throw new BackendException("MiniAudio", result, "Unable to initialize decoder.");

        // Attempt to get the length. For some formats (like VBR MP3s without a Xing header), this may fail.
        result = Native.DecoderGetLengthInPcmFrames(_decoder, out var length);
        Length = result switch
        {
            Result.Success => (int)length * Channels,
            _ => 0 // Length is unknown (e.g., VBR without Xing header or live stream)
        };

        _endOfStreamReached = false;
    }

    /// <inheritdoc />
    public int Channels { get; }

    /// <inheritdoc />
    public int SampleRate { get; }

    /// <inheritdoc />
    public bool IsDisposed { get; private set; }

    /// <inheritdoc />
    public int Length { get; private set; }

    /// <inheritdoc />
    public SampleFormat SampleFormat { get; }

    public event EventHandler<EventArgs>? EndOfStreamReached;

    /// <summary>
    ///     Decodes the next several samples.
    /// </summary>
    public int Decode(Span<float> samples) => Decode(samples, default);

    /// <inheritdoc />
    public int Decode(Span<float> samples, CancellationToken cancellationToken)
    {
        lock (_syncLock)
        {
            _input.Begin(cancellationToken);
            if (IsDisposed || _endOfStreamReached)
                return 0;

            var framesToRead = (uint)(samples.Length / Channels);
            if (framesToRead == 0)
                return 0;

            var buffer = GetBufferIfNeeded(samples.Length);
            var span = buffer ?? MemoryMarshal.AsBytes(samples);

            try
            {
                fixed (byte* nativeBuffer = span)
                {
                    var result = Native.DecoderReadPcmFrames(_decoder, (nint)nativeBuffer, framesToRead, out var framesRead);
                    _input.ThrowIfFailed();
                    if (result == Result.AtEnd) _endOfStreamReached = true;
                    else if (result != Result.Success) throw new BackendException("MiniAudio", result, "Failed to decode PCM frames.");
                    if (framesRead == 0 && _endOfStreamReached) EndOfStreamReached?.Invoke(this, EventArgs.Empty);
                    if (SampleFormat is not SampleFormat.F32 && framesRead > 0) ConvertToFloat(samples, framesRead, span);
                    return checked((int)framesRead * Channels);
                }
            }
            finally { if (buffer != null) ArrayPool<byte>.Shared.Return(buffer); }
        }
    }

    private byte[]? GetBufferIfNeeded(int sampleLength)
    {
        // U32 can be done in-place with the passed in float span
        if (SampleFormat is SampleFormat.F32 or SampleFormat.S32)
        {
            return null;
        }
        var byteSize = SampleFormat switch
        {
            SampleFormat.S16 => sampleLength * 2,
            SampleFormat.S24 => sampleLength * 3,
            SampleFormat.U8 => sampleLength,
            _ => throw new NotSupportedException($"Sample format {SampleFormat} is not supported.")
        };
        return ArrayPool<byte>.Shared.Rent(byteSize);
    }

    private void ConvertToFloat(Span<float> samples, ulong framesRead, Span<byte> nativeBuffer)
    {
        var sampleCount = checked((int)framesRead * Channels);
        switch (SampleFormat)
        {
            case SampleFormat.S16:
                var shortSpan = MemoryMarshal.Cast<byte, short>(nativeBuffer);
                for (var i = 0; i < sampleCount; i++)
                    samples[i] = shortSpan[i] / (float)short.MaxValue;
                break;
            case SampleFormat.S24:
                for (var i = 0; i < sampleCount; i++)
                {
                    // Read 3 bytes as a 24-bit integer
                    var sample24 = (nativeBuffer[i * 3] << 0) | (nativeBuffer[i * 3 + 1] << 8) | (nativeBuffer[i * 3 + 2] << 16);
                    if ((sample24 & 0x800000) != 0) // Sign extension for negative values
                        sample24 |= unchecked((int)0xFF000000);
                    samples[i] = sample24 / 8388608f;
                }
                break;
            case SampleFormat.S32:
                var int32Span = MemoryMarshal.Cast<byte, int>(nativeBuffer);
                for (var i = 0; i < sampleCount; i++)
                    samples[i] = int32Span[i] / (float)int.MaxValue;
                break;
            case SampleFormat.U8:
                for (var i = 0; i < sampleCount; i++)
                    samples[i] = (nativeBuffer[i] - 128) / 128f; // Scale U8 to -1.0 to 1.0
                break;
        }
    }

    /// <summary>
    ///     Seek to start decoding at the given offset.
    /// </summary>
    public bool Seek(int offset)
    {
        lock (_syncLock)
        {
            // We don't strictly need the total length to seek if the stream is seekable.
            // MiniAudio's internal decoder can binary search or scan.
            _endOfStreamReached = false;
            var miniAudioResult = Native.DecoderSeekToPcmFrame(_decoder, (ulong)(offset / Channels));
            return miniAudioResult == Result.Success;
        }
    }

    /// <summary>
    /// Disposes of the decoder resources.
    /// </summary>
    public void Dispose()
    {
        Dispose(true);
        GC.SuppressFinalize(this);
    }

    ~Decoder()
    {
        Dispose(false);
    }

    private Result ReadCallback(nint pDecoder, nint pBufferOut, ulong bytesToRead, out ulong pBytesRead)
    {
        try
        {
            lock (_syncLock)
            {
                if (!_stream.CanRead)
                {
                    pBytesRead = 0;
                    return Result.NoDataAvailable;
                }

                var size = (int)bytesToRead;
                if (size <= 0)
                {
                     pBytesRead = 0;
                     return Result.Success;
                }

                // Use ArrayPool to avoid allocating a new buffer on every read
                if (_rentedReadBuffer == null || _rentedReadBuffer.Length < size)
                {
                    if (_rentedReadBuffer != null)
                        ArrayPool<byte>.Shared.Return(_rentedReadBuffer);

                    _rentedReadBuffer = ArrayPool<byte>.Shared.Rent(size);
                }

                var read = _input.Read(_rentedReadBuffer.AsSpan(0, size));

                if (read > 0)
                {
                    fixed (byte* pReadBuffer = _rentedReadBuffer)
                    {
                        Buffer.MemoryCopy(pReadBuffer, (void*)pBufferOut, size, read);
                    }
                }

                pBytesRead = (ulong)read;
                return Result.Success;
            }
        }
        catch (Exception)
        {
            // Swallow exception to prevent runtime crash, signal I/O error to miniaudio
            pBytesRead = 0;
            Log.Critical("Failed to read PCM frames from decoder.");
            return Result.IoError;
        }
    }

    private Result SeekCallback(nint _, long byteOffset, SeekPoint point)
    {
        try
        {
            lock (_syncLock)
            {
                if (!_stream.CanSeek)
                {
                    return Result.InvalidOperation;
                }

                long streamLength = _stream.Length;
                long targetOffset;

                // MiniAudio's binary search uses relative offsets to seek backward.
                // We must calculate the absolute target position first, clamp it, and then execute the seek.
                if (point == SeekPoint.FromCurrent)
                {
                    long currentPos = _stream.Position;
                    targetOffset = currentPos + byteOffset;
                }
                else
                {
                    targetOffset = byteOffset;
                }

                // Clamp targetOffset to stream bounds to prevent exceptions
                if (targetOffset < 0)
                {
                    targetOffset = 0;
                }

                if (streamLength > 0 && targetOffset > streamLength)
                {
                    // Allow seeking to the very end of the stream (streamLength) for EOF checks
                    targetOffset = streamLength;
                }

                // Always use SeekOrigin.Begin with the absolute targetOffset to avoid relative offset no-op bugs
                _stream.Seek(targetOffset, SeekOrigin.Begin);
                return Result.Success;
            }
        }
        catch (NotSupportedException)
        {
            // Some streams claim CanSeek but throw on Length or Position
            Log.Critical("Stream does not support seeking.");
            return Result.InvalidOperation;
        }
        catch (Exception)
        {
            Log.Critical("Failed to seek stream.");
            return Result.IoError;
        }
    }

    private void Dispose(bool _)
    {
        lock (_syncLock)
        {
            if (IsDisposed) return;

            // Return rented buffer if it exists
            if (_rentedReadBuffer != null)
            {
                ArrayPool<byte>.Shared.Return(_rentedReadBuffer);
                _rentedReadBuffer = null;
            }

            Native.DecoderUninit(_decoder);
            Native.Free(_decoder);

            // Keep delegates alive until after Uninit to prevent GC during callback (defensive)
            GC.KeepAlive(_readCallback);
            GC.KeepAlive(_seekCallbackCallback);

            _input.Dispose();
            IsDisposed = true;
        }
    }
}
