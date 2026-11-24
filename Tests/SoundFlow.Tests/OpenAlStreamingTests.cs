using System.Text;
using SoundFlow.Backends.OpenAL;
using SoundFlow.Enums;
using SoundFlow.Features;
using SoundFlow.Playback;
using SoundFlow.Providers;
using Xunit;

namespace SoundFlow.Tests;

public sealed class OpenAlStreamingTests
{
    [Fact]
    public void StarvedLiveSourceStaysPlayingAndCompletesOnlyAfterItsInputAndNativeQueueDrain()
    {
        using var engine = new SoundFlow.Backends.OpenAL.Engine();
        using var device = engine.CreateRenderDevice(OpenAlPlaybackTests.Stereo);
        var provider = new QueueDataProvider(TransportTests.Mono);
        using var source = device.CreateSource(provider, TransportTests.Mono);
        var ended = 0; source.PlaybackEnded += (_, _) => ended++;
        source.Play(); device.Render(new float[1024]);
        Assert.Equal(PlaybackState.Playing, source.State);
        Assert.Equal(0, ended);
        provider.AddSamples(Enumerable.Repeat(.2f, 2048).ToArray());
        provider.CompleteAdding();
        var output = new float[8192]; device.Render(output);
        Assert.Contains(output, s => Math.Abs(s) > .01);
        Assert.Equal(PlaybackState.Stopped, source.State);
        Assert.True(SpinWait.SpinUntil(() => Volatile.Read(ref ended) == 1, TimeSpan.FromSeconds(2)));
    }

    [Fact]
    public void SeekDiscardsDecodeAheadAndPauseDoesNotAdvanceTheAudibleCursor()
    {
        using var engine = new SoundFlow.Backends.OpenAL.Engine();
        using var device = engine.CreateRenderDevice(OpenAlPlaybackTests.Stereo);
        using var source = device.CreateSource(new SoundFlow.Playback.Clip(Enumerable.Range(0, 8192).Select(i => .05f * MathF.Sin(i * .13f)).Concat(Enumerable.Range(0, 8192).Select(i => .7f * MathF.Sin(i * .13f))).ToArray(), TransportTests.Mono));
        source.Play(); device.Render(new float[1024]);
        source.Pause(); var pausedTime = source.Time;
        device.Render(new float[2048]); Assert.Equal(pausedTime, source.Time);
        Assert.True(source.Seek(9000)); source.Play();
        var output = new float[2048]; device.Render(output);
        Assert.True(output.Skip(256).Max(Math.Abs) > .2, $"Maximum {output.Max(Math.Abs)}, time {source.Time}, state {source.State}; first [{string.Join(",", output.Take(12))}]");
    }

    [Fact]
    public void ProviderOwnershipIsExclusiveAndLeaveOpenPermitsReuseAfterDisposal()
    {
        using var engine = new SoundFlow.Backends.OpenAL.Engine();
        using var device = engine.CreateRenderDevice(OpenAlPlaybackTests.Stereo);
        using var provider = new RawDataProvider(new float[4096], 48000);
        var first = device.CreateSource(provider, TransportTests.Mono, leaveOpen: true);
        Assert.Throws<InvalidOperationException>(() => device.CreateSource(provider, TransportTests.Mono));
        first.Dispose(); Assert.False(provider.IsDisposed);
        using var second = device.CreateSource(provider, TransportTests.Mono, leaveOpen: true);
    }

    [Fact]
    public void HrtfConfigurationReportsTheActualNegotiatedStatus()
    {
        using var engine = new SoundFlow.Backends.OpenAL.Engine();
        using var device = engine.CreateRenderDevice(OpenAlPlaybackTests.Stereo);
        Assert.True(device.TryGetFeature<IHrtfAudio>(out var hrtf));
        hrtf.Configure(HrtfMode.Disabled);
        Assert.Equal("Disabled", hrtf.Status);
        Assert.Throws<ArgumentOutOfRangeException>(() => hrtf.Configure(HrtfMode.Enabled, hrtf.Datasets.Count));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void DefaultFfmpegCodecDecodesSeekableAndNonseekableWavStreams(bool nonseekable)
    {
        using var engine = new SoundFlow.Backends.OpenAL.Engine();
        using var device = engine.CreateRenderDevice(OpenAlPlaybackTests.Stereo);
        var wav = new MemoryStream();
        using (var writer = new BinaryWriter(wav, Encoding.ASCII, true))
        {
            writer.Write(Encoding.ASCII.GetBytes("RIFF")); writer.Write(36 + 8192);
            writer.Write(Encoding.ASCII.GetBytes("WAVEfmt ")); writer.Write(16);
            writer.Write((short)1); writer.Write((short)1); writer.Write(48000); writer.Write(96000);
            writer.Write((short)2); writer.Write((short)16);
            writer.Write(Encoding.ASCII.GetBytes("data")); writer.Write(8192);
            for (var i = 0; i < 4096; i++) writer.Write((short)(Math.Sin(i * .1) * 12000));
        }
        wav.Position = 0;
        using Stream stream = nonseekable ? new NonseekableStream(wav) : wav;
        var provider = new StreamDataProvider(engine, TransportTests.Mono, stream);
        using var source = device.CreateSource(provider, TransportTests.Mono);
        source.Play(); var output = new float[4096]; device.Render(output);
        Assert.Contains(output, s => Math.Abs(s) > .01);
    }

    private sealed class NonseekableStream(Stream inner) : Stream
    {
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override int Read(byte[] buffer, int offset, int count) => inner.Read(buffer, offset, count);
        public override int Read(Span<byte> buffer) => inner.Read(buffer);
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        protected override void Dispose(bool disposing) { if (disposing) inner.Dispose(); base.Dispose(disposing); }
    }
}
