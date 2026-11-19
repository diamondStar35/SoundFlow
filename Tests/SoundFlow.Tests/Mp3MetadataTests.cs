using SoundFlow.Metadata;
using SoundFlow.Metadata.Models;
using Xunit;

namespace SoundFlow.Tests;

public sealed class Mp3MetadataTests
{
    [Theory]
    [InlineData(0xfb, 9, 128, 44100, 1152)]
    [InlineData(0xfb, 14, 320, 44100, 1152)]
    [InlineData(0xf3, 8, 64, 22050, 576)]
    [InlineData(0xe3, 8, 64, 11025, 576)]
    public void Layer3WithoutXingHeaderUsesTheBitrateTableForItsMpegVersion(
        byte versionAndLayer, int bitrateIndex, int bitrateKbps, int sampleRate, int samplesPerFrame)
    {
        const int frameCount = 40;
        var coefficient = samplesPerFrame / 8;
        var frameLength = coefficient * bitrateKbps * 1000 / sampleRate;
        var bytes = new byte[frameCount * frameLength];
        for (var offset = 0; offset < bytes.Length; offset += frameLength)
        {
            bytes[offset] = 0xff;
            bytes[offset + 1] = versionAndLayer;
            bytes[offset + 2] = (byte)(bitrateIndex << 4);
        }

        using var stream = new MemoryStream(bytes);
        var result = SoundMetadataReader.Read(stream, new ReadOptions());

        Assert.True(result.IsSuccess);
        Assert.Equal(bitrateKbps * 1000, result.Value!.Bitrate);
        Assert.Equal(sampleRate, result.Value.SampleRate);
        var expectedSeconds = frameCount * samplesPerFrame / (double)sampleRate;
        Assert.InRange(result.Value.Duration.TotalSeconds, expectedSeconds - 0.01, expectedSeconds + 0.01);
    }
}
