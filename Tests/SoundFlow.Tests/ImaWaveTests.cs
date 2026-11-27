using System.Text;
using SoundFlow.Codecs.FFMpeg;
using SoundFlow.Enums;
using SoundFlow.Structs;
using Xunit;

namespace SoundFlow.Tests;

public sealed class ImaWaveTests
{
    private static readonly byte[] Blocks =
    [
        232, 3, 30, 0, 24, 252, 20, 0, 18, 52, 86, 120, 137, 171, 205, 239,
        212, 254, 10, 0, 244, 1, 40, 0, 255, 238, 221, 204, 1, 35, 69, 103
    ];
    // Independent reference PCM produced by FFmpeg from these two stereo IMA blocks.
    private static readonly short[] Expected =
    [
        1000, -1000, 1081, -1018, 1125, -1023, 1245, -1058, 1358, -1081,
        1549, -1127, 1836, -1183, 1798, -1295, 2321, -1506,
        -300, 500, -335, 626, -411, 664, -554, 908, -809, 1066,
        -1192, 1382, -1753, 1761, -2425, 2526, -3239, 3949
    ];
    private static readonly AudioFormat Format = new()
    {
        Channels = 2, SampleRate = 44100, Format = SampleFormat.F32, Layout = ChannelLayout.Stereo
    };

    private static MemoryStream Wave(byte[] blocks, uint validFrames = 18, ushort channels = 2)
    {
        var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream, Encoding.UTF8, true);
        writer.Write("RIFF"u8);
        writer.Write(52 + blocks.Length);
        writer.Write("WAVEfmt "u8);
        writer.Write(20);
        writer.Write((ushort)17);
        writer.Write(channels);
        writer.Write(44100);
        writer.Write(78400);
        writer.Write((ushort)(8 * channels));
        writer.Write((ushort)4);
        writer.Write((ushort)2);
        writer.Write((ushort)9);
        writer.Write("fact"u8);
        writer.Write(4);
        writer.Write(validFrames);
        writer.Write("data"u8);
        writer.Write(blocks.Length);
        writer.Write(blocks);
        stream.Position = 0;
        return stream;
    }

    [Fact]
    public void StereoBlocksMatchReferenceAcrossSmallReadsAndUnalignedSeeks()
    {
        using var input = Wave(Blocks);
        using var decoder = new FFmpegCodecFactory().CreateDecoder(input, "wav", Format)!;
        var pcm = new List<float>();
        var block = new float[5];
        int count;
        while ((count = decoder.Decode(block)) > 0) pcm.AddRange(block.Take(count));
        Assert.Equal(Expected.Select(value => value / 32768f), pcm);
        Assert.True(decoder.Seek(7));
        Assert.Equal(5, decoder.Decode(block));
        Assert.Equal(Expected.Skip(7).Take(5).Select(value => value / 32768f), block);
        Assert.True(decoder.Seek(decoder.Length));
        Assert.Equal(0, decoder.Decode(block));
        Assert.False(decoder.Seek(decoder.Length + 1));
    }

    [Fact]
    public void ProbingHonorsFactLengthAndConvertsChannelCountAndRate()
    {
        using var input = Wave(Blocks, 17);
        var format = Format with { Channels = 1, Layout = ChannelLayout.Mono, SampleRate = 48000 };
        using var decoder = new FFmpegCodecFactory().TryCreateDecoder(input, out var detected, format)!;
        Assert.NotNull(decoder);
        Assert.Equal(2, detected.Channels);
        Assert.Equal(44100, detected.SampleRate);
        Assert.Equal(1, decoder.Channels);
        Assert.Equal(48000, decoder.SampleRate);
        var pcm = new float[100];
        var read = decoder.Decode(pcm);
        Assert.InRange(read, 18, 19);
        Assert.Contains(pcm.Take(read), value => Math.Abs(value) > .001f);
        Assert.Equal(0, decoder.Decode(pcm));
    }

    [Fact]
    public void MonoBlocksDecodeAndReportCompletionOncePerSeek()
    {
        var mono = Blocks.Take(4).Concat(Blocks.Skip(8).Take(4)).ToArray();
        using var input = Wave(mono, 9, 1);
        using var decoder = new FFmpegCodecFactory().CreateDecoder(input, "wav",
            Format with { Channels = 1, Layout = ChannelLayout.Mono })!;
        var ends = 0;
        decoder.EndOfStreamReached += (_, _) => ends++;
        var pcm = new float[9];
        Assert.Equal(9, decoder.Decode(pcm));
        Assert.Equal(Expected.Take(18).Where((_, i) => i % 2 == 0).Select(value => value / 32768f), pcm);
        Assert.Equal(0, decoder.Decode(pcm));
        Assert.Equal(1, ends);
        Assert.True(decoder.Seek(0));
        Assert.Equal(9, decoder.Decode(pcm));
        Assert.Equal(2, ends);
    }

    [Fact]
    public void InvalidStepIndexFailsInsteadOfReturningCorruptAudio()
    {
        var blocks = Blocks.ToArray();
        blocks[2] = 89;
        using var input = Wave(blocks);
        using var decoder = new FFmpegCodecFactory().CreateDecoder(input, "wav", Format)!;
        Assert.Throws<InvalidDataException>(() => decoder.Decode(new float[36]));
    }
}
