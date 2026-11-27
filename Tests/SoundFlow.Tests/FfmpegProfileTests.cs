using SoundFlow.Codecs.FFMpeg;
using SoundFlow.Enums;
using SoundFlow.Providers;
using SoundFlow.Structs;
using Xunit;

namespace SoundFlow.Tests;

public sealed class FfmpegProfileTests
{
    private static readonly AudioFormat Format = new()
    {
        Format = SampleFormat.F32,
        Channels = 2,
        Layout = ChannelLayout.Stereo,
        SampleRate = 48000
    };

    [Fact]
    public void RuntimeReportsNativeAdpcmAndNetworkingWithoutVideoCodecs()
    {
        var capabilities = new Capabilities();
        Assert.Contains("adpcm_ima_wav", capabilities.Decoders);
        Assert.Contains("adpcm_ms", capabilities.Decoders);
        Assert.Contains("https", capabilities.InputProtocols);
        Assert.Contains("rtp", capabilities.InputProtocols);
        Assert.Contains("hls", capabilities.Demuxers);
        Assert.Equal(0, capabilities.NonAudioCodecCount);
    }

    [Theory]
    [InlineData("wav")]
    [InlineData("mp3")]
    [InlineData("aac")]
    [InlineData("m4a")]
    [InlineData("flac")]
    [InlineData("ogg")]
    [InlineData("opus")]
    public void FactoryRoundTripsCommonContainersWithFiniteAudiblePcm(string container)
    {
        using var encoded = new MemoryStream();
        var factory = new FFmpegCodecFactory();
        using (var encoder = factory.CreateEncoder(encoded, container, Format))
        {
            Assert.NotNull(encoder);
            var tone = Enumerable.Range(0, 9600).Select(i => .2f * MathF.Sin((i / 2) * .13f)).ToArray();
            Assert.Equal(tone.Length, encoder.Encode(tone));
        }
        if (container == "m4a")
            Assert.Equal("ftyp", System.Text.Encoding.ASCII.GetString(encoded.ToArray(), 4, 4));
        encoded.Position = 0;
        using var decoder = factory.TryCreateDecoder(encoded, out _, Format);
        Assert.NotNull(decoder);
        Assert.Equal(SampleFormat.F32, decoder.SampleFormat);
        var pcm = new float[16384];
        var count = decoder.Decode(pcm);
        Assert.InRange(count, 7000, 14000);
        Assert.All(pcm.Take(count), sample => Assert.True(float.IsFinite(sample)));
        var rms = Math.Sqrt(pcm.Take(count).Select(value => (double)value * value).Average());
        Assert.InRange(rms, .02, .3);
    }

    [Fact]
    public void MetadataDetectedIntegerWavStillProducesInterleavedFloatPcm()
    {
        using var stream = new MemoryStream();
        using (var writer = new BinaryWriter(stream, System.Text.Encoding.UTF8, true))
        {
            writer.Write("RIFF"u8);
            writer.Write(44);
            writer.Write("WAVEfmt "u8);
            writer.Write(16);
            writer.Write((short)1);
            writer.Write((short)2);
            writer.Write(48000);
            writer.Write(192000);
            writer.Write((short)4);
            writer.Write((short)16);
            writer.Write("data"u8);
            writer.Write(8);
            writer.Write((short)32767);
            writer.Write(short.MinValue);
            writer.Write((short)16384);
            writer.Write((short)0);
        }
        stream.Position = 0;
        using var engine = new Backends.OpenAL.Engine();
        using var provider = new StreamDataProvider(engine, stream);
        var pcm = new float[4];
        Assert.Equal(4, provider.ReadSamples(pcm));
        Assert.Equal(SampleFormat.F32, provider.SampleFormat);
        Assert.Equal(new[] { 32767 / 32768f, -1f, .5f, 0f }, pcm);
    }
}
