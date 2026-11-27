using System.Buffers.Binary;
using SoundFlow.Enums;
using SoundFlow.Interfaces;

namespace SoundFlow.Codecs.FFMpeg;

/// <summary>
/// Decodes four-bit Microsoft IMA WAV blocks into interleaved F32 PCM. This path also works with
/// existing native codec bundles that omit ADPCM. Only one encoded and decoded block are retained.
/// </summary>
internal sealed class ImaWaveDecoder : ICancellableDecoder
{
    private static readonly int[] Steps =
    [
        7, 8, 9, 10, 11, 12, 13, 14, 16, 17, 19, 21, 23, 25, 28, 31,
        34, 37, 41, 45, 50, 55, 60, 66, 73, 80, 88, 97, 107, 118, 130, 143,
        157, 173, 190, 209, 230, 253, 279, 307, 337, 371, 408, 449, 494, 544,
        598, 658, 724, 796, 876, 963, 1060, 1166, 1282, 1411, 1552, 1707,
        1878, 2066, 2272, 2499, 2749, 3024, 3327, 3660, 4026, 4428, 4871,
        5358, 5894, 6484, 7132, 7845, 8630, 9493, 10442, 11487, 12635,
        13899, 15289, 16818, 18500, 20350, 22385, 24623, 27086, 29794, 32767
    ];
    private static readonly int[] IndexChanges = [-1, -1, -1, -1, 2, 4, 6, 8];
    private readonly Stream _stream;
    private readonly long _dataStart;
    private readonly long _dataBytes;
    private readonly int _framesPerBlock;
    private readonly byte[] _encoded;
    private readonly float[] _decoded;
    private int _cachedBlock = -1;
    private int _position;
    private bool _notifiedEnd;

    /// <summary>Probes seekable RIFF input without changing its cursor when another decoder should handle it.</summary>
    internal static ImaWaveDecoder? TryOpen(Stream stream)
    {
        if (!stream.CanSeek)
            return null;
        var start = stream.Position;
        try
        {
            Span<byte> header = stackalloc byte[12];
            if (stream.ReadAtLeast(header, header.Length, false) != header.Length ||
                !header[..4].SequenceEqual("RIFF"u8) || !header[8..].SequenceEqual("WAVE"u8))
                return null;
            var end = Math.Min(stream.Length, start + 8 + BinaryPrimitives.ReadUInt32LittleEndian(header[4..]));
            Span<byte> chunk = stackalloc byte[8];
            while (stream.Position + 8 <= end)
            {
                stream.ReadExactly(chunk);
                var bytes = BinaryPrimitives.ReadUInt32LittleEndian(chunk[4..]);
                var next = stream.Position + bytes + (bytes & 1);
                if (next > end)
                    return null;
                if (chunk[..4].SequenceEqual("fmt "u8) && bytes >= 2)
                {
                    Span<byte> tag = stackalloc byte[2];
                    stream.ReadExactly(tag);
                    if (BinaryPrimitives.ReadUInt16LittleEndian(tag) != 0x11)
                        return null;
                    stream.Position = start;
                    return new(stream);
                }
                stream.Position = next;
            }
            return null;
        }
        finally
        {
            stream.Position = start;
        }
    }

    private ImaWaveDecoder(Stream stream)
    {
        _stream = stream;
        using var reader = new BinaryReader(stream, System.Text.Encoding.UTF8, leaveOpen: true);
        reader.ReadUInt32();
        var end = Math.Min(stream.Length, stream.Position + 4 + reader.ReadUInt32());
        reader.ReadUInt32();
        var blockBytes = 0;
        long? factFrames = null;
        Span<byte> chunk = stackalloc byte[8];
        while (stream.Position + 8 <= end)
        {
            stream.ReadExactly(chunk);
            var bytes = BinaryPrimitives.ReadUInt32LittleEndian(chunk[4..]);
            var payload = stream.Position;
            if (bytes > end - payload)
                throw new InvalidDataException("Truncated IMA WAV chunk.");
            if (chunk[..4].SequenceEqual("fmt "u8))
            {
                if (bytes < 20 || reader.ReadUInt16() != 0x11)
                    throw new InvalidDataException("Invalid IMA WAV format.");
                Channels = reader.ReadUInt16();
                SampleRate = checked((int)reader.ReadUInt32());
                reader.ReadUInt32();
                blockBytes = reader.ReadUInt16();
                var bits = reader.ReadUInt16();
                var extra = reader.ReadUInt16();
                _framesPerBlock = reader.ReadUInt16();
                if (Channels is < 1 or > 2 || SampleRate <= 0 || bits != 4 || extra < 2 ||
                    blockBytes < 4 * Channels || (blockBytes - 4 * Channels) % (4 * Channels) != 0 ||
                    _framesPerBlock != 1 + (blockBytes - 4 * Channels) * 2 / Channels)
                    throw new NotSupportedException("Expected mono or stereo four-bit IMA WAV blocks.");
            }
            else if (chunk[..4].SequenceEqual("data"u8))
            {
                _dataStart = payload;
                _dataBytes = bytes;
            }
            else if (chunk[..4].SequenceEqual("fact"u8) && bytes >= 4)
            {
                factFrames = reader.ReadUInt32();
            }
            stream.Position = payload + bytes + (bytes & 1);
        }
        if (blockBytes == 0 || _dataStart == 0)
            throw new InvalidDataException("IMA WAV requires format and data chunks.");
        var tailBytes = (int)(_dataBytes % blockBytes);
        var tailFrames = tailBytes == 0 ? 0 : FramesInBlock(tailBytes);
        var frames = _dataBytes / blockBytes * _framesPerBlock + tailFrames;
        // fact counts valid frames, excluding encoder padding in the final block.
        if (factFrames is > 0)
            frames = Math.Min(frames, factFrames.Value);
        Length = checked((int)(frames * Channels));
        _encoded = new byte[blockBytes];
        _decoded = new float[_framesPerBlock * Channels];
    }

    public bool IsDisposed { get; private set; }
    public int Length { get; }
    public SampleFormat SampleFormat => SampleFormat.F32;
    public int Channels { get; }
    public int SampleRate { get; }
    public event EventHandler<EventArgs>? EndOfStreamReached;
    public int Decode(Span<float> samples) => Decode(samples, default);

    public int Decode(Span<float> samples, CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(IsDisposed, this);
        var written = 0;
        while (written < samples.Length && _position < Length)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var block = _position / _decoded.Length;
            if (block != _cachedBlock)
                ReadBlock(block);
            var offset = _position % _decoded.Length;
            var count = Math.Min(samples.Length - written, Math.Min(_decoded.Length - offset, Length - _position));
            _decoded.AsSpan(offset, count).CopyTo(samples[written..]);
            _position += count;
            written += count;
        }
        if (_position == Length && !_notifiedEnd && !samples.IsEmpty)
        {
            _notifiedEnd = true;
            EndOfStreamReached?.Invoke(this, EventArgs.Empty);
        }
        return written;
    }

    public bool Seek(int offset)
    {
        ObjectDisposedException.ThrowIf(IsDisposed, this);
        if (offset < 0 || offset > Length)
            return false;
        _position = offset;
        _notifiedEnd = false;
        return true;
    }

    private int FramesInBlock(int bytes)
    {
        var payload = bytes - 4 * Channels;
        if (payload < 0 || payload % (4 * Channels) != 0)
            throw new InvalidDataException("Incomplete IMA WAV channel group.");
        return 1 + payload * 2 / Channels;
    }

    private void ReadBlock(int block)
    {
        var encodedOffset = (long)block * _encoded.Length;
        var bytes = (int)Math.Min(_encoded.Length, _dataBytes - encodedOffset);
        FramesInBlock(bytes);
        _stream.Position = _dataStart + encodedOffset;
        _stream.ReadExactly(_encoded.AsSpan(0, bytes));
        for (var channel = 0; channel < Channels; channel++)
        {
            var predictor = (int)BinaryPrimitives.ReadInt16LittleEndian(_encoded.AsSpan(channel * 4));
            var index = (int)_encoded[channel * 4 + 2];
            if (index > 88)
                throw new InvalidDataException("IMA WAV step index exceeds 88.");
            _decoded[channel] = predictor / 32768f;
            var frame = 1;
            // Stereo alternates four encoded bytes per channel, rather than alternating nibbles.
            for (var group = 4 * Channels; group < bytes; group += 4 * Channels)
            {
                for (var i = 0; i < 4; i++)
                {
                    var value = _encoded[group + channel * 4 + i];
                    _decoded[frame++ * Channels + channel] = Expand(value & 15, ref predictor, ref index);
                    _decoded[frame++ * Channels + channel] = Expand(value >> 4, ref predictor, ref index);
                }
            }
        }
        _cachedBlock = block;
    }

    private static float Expand(int nibble, ref int predictor, ref int index)
    {
        var difference = ((2 * (nibble & 7) + 1) * Steps[index]) >> 3;
        predictor = Math.Clamp(predictor + ((nibble & 8) == 0 ? difference : -difference), short.MinValue, short.MaxValue);
        index = Math.Clamp(index + IndexChanges[nibble & 7], 0, 88);
        return predictor / 32768f;
    }

    public void Dispose()
    {
        if (IsDisposed)
            return;
        IsDisposed = true;
        EndOfStreamReached = null;
        _stream.Dispose();
    }
}
