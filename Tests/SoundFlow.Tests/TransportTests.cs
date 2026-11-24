using CaptureDevice = SoundFlow.Abstracts.Devices.AudioCaptureDevice;
using DeviceConfiguration = SoundFlow.Abstracts.Devices.DeviceConfig;
using Engine = SoundFlow.Abstracts.AudioEngine;
using PlaybackDevice = SoundFlow.Abstracts.Devices.AudioPlaybackDevice;
using SoundFlow.Abstracts;
using SoundFlow.Abstracts.Devices;
using SoundFlow.Components;
using SoundFlow.Enums;
using SoundFlow.Providers;
using SoundFlow.Structs;
using Xunit;

namespace SoundFlow.Tests;

public sealed class TransportTests
{
    internal static readonly AudioFormat Mono = new() { Channels = 1, SampleRate = 48000, Format = SampleFormat.F32 };

    [Fact]
    public void LoopBoundaryInsideABlockNeverLeaksAudioBeyondTheLoop()
    {
        using var engine = new HeadlessEngine();
        using var player = new SoundPlayer(engine, Mono, new RawDataProvider(new[] { .1f, .2f, .3f, .4f, .5f, .6f }, 48000));
        player.SetLoopPoints(2, 4);
        player.IsLooping = true;
        Assert.True(player.Seek(2));
        player.Play();
        var output = new float[5];
        player.Process(output, 1);
        Assert.Equal(new[] { .3f, .4f, .3f, .4f, .3f }, output);
    }

    [Fact]
    public void ReversePlaybackAdvancesBackwardsAcrossSuccessiveBlocks()
    {
        using var engine = new HeadlessEngine();
        using var player = new SoundPlayer(engine, Mono, new RawDataProvider(new[] { .1f, .2f, .3f, .4f, .5f, .6f }, 48000));
        player.PlaybackSpeed = -1;
        Assert.True(player.Seek(6));
        player.Play();
        var first = new float[2]; var second = new float[2];
        player.Process(first, 1); player.Process(second, 1);
        Assert.Equal(new[] { .6f, .5f }, first);
        Assert.Equal(new[] { .4f, .3f }, second);
    }

    [Fact]
    public void ReversePlaybackHonorsItsSpeedInsteadOfRepeatingDecodeAhead()
    {
        using var engine = new HeadlessEngine();
        using var player = new SoundPlayer(engine, Mono, new RawDataProvider(new[] { .1f, .2f, .3f, .4f, .5f, .6f, .7f, .8f }, 48000));
        player.PlaybackSpeed = -2;
        player.Seek(8); player.Play();
        var first = new float[2]; var second = new float[2];
        player.Process(first, 1); player.Process(second, 1);
        Assert.Equal(new[] { .8f, .6f }, first);
        Assert.Equal(new[] { .4f, .2f }, second);
    }

    [Theory]
    [InlineData(.5f)]
    [InlineData(1.5f)]
    [InlineData(2f)]
    public void PositiveSpeedPreservesPitchAndProducesTheExpectedDuration(float speed)
    {
        using var engine = new HeadlessEngine();
        using var player = new SoundPlayer(engine, Mono, new RawDataProvider(Enumerable.Range(0, 48000)
            .Select(i => .2f * MathF.Sin(2 * MathF.PI * 440 * i / 48000)).ToArray(), 48000));
        player.PlaybackSpeed = speed; player.Play();
        var rendered = new List<float>(); var block = new float[512];
        for (var i = 0; i < 400 && player.State == PlaybackState.Playing; i++)
        {
            Array.Clear(block); player.Process(block, 1);
            rendered.AddRange(block.Take(player.Transport.LastReadSamples));
        }
        Assert.Equal(PlaybackState.Stopped, player.State);
        Assert.InRange(rendered.Count, (int)(48000 / speed * .8), (int)(48000 / speed * 1.2));
        var steady = rendered.Skip(4000).Take(8000).ToArray();
        var crossings = Enumerable.Range(1, steady.Length - 1).Count(i => steady[i - 1] <= 0 && steady[i] > 0);
        Assert.InRange(crossings * 48000f / steady.Length, 425, 455);
    }

    [Fact]
    public void CompletedUnknownLengthInputFinishesAtNonunitSpeed()
    {
        using var engine = new HeadlessEngine();
        var provider = new QueueDataProvider(Mono);
        provider.AddSamples(Enumerable.Range(0, 16000).Select(i => .2f * MathF.Sin(i * .1f)).ToArray());
        provider.CompleteAdding();
        using var player = new SoundPlayer(engine, Mono, provider) { PlaybackSpeed = 1.5f };
        player.Play();
        var block = new float[512];
        for (var i = 0; i < 100 && player.State == PlaybackState.Playing; i++) player.Process(block, 1);
        Assert.Equal(PlaybackState.Stopped, player.State);
    }

    [Fact]
    public void PlaybackClockExcludesTimeStretcherDecodeAhead()
    {
        using var engine = new HeadlessEngine();
        using var player = new SoundPlayer(engine, Mono, new RawDataProvider(new float[48000], 48000)) { PlaybackSpeed = 2 };
        player.Play(); player.Process(new float[512], 1);
        Assert.InRange(player.Time, 1023f / 48000, 1025f / 48000);
    }
}

internal class HeadlessEngine : Engine
{
    protected override void CleanupBackend() { }
    public override void UpdateAudioDevicesInfo() { }
    public override PlaybackDevice InitializePlaybackDevice(DeviceInfo? deviceInfo, AudioFormat format, DeviceConfiguration? config = null) => throw new NotSupportedException();
    public override CaptureDevice InitializeCaptureDevice(DeviceInfo? deviceInfo, AudioFormat format, DeviceConfiguration? config = null) => throw new NotSupportedException();
    public override FullDuplexDevice InitializeFullDuplexDevice(DeviceInfo? playbackDeviceInfo, DeviceInfo? captureDeviceInfo, AudioFormat format, DeviceConfiguration? config = null) => throw new NotSupportedException();
    public override CaptureDevice InitializeLoopbackDevice(AudioFormat format, DeviceConfiguration? config = null) => throw new NotSupportedException();
    public override PlaybackDevice SwitchDevice(PlaybackDevice oldDevice, DeviceInfo newDeviceInfo, DeviceConfiguration? config = null) => throw new NotSupportedException();
    public override CaptureDevice SwitchDevice(CaptureDevice oldDevice, DeviceInfo newDeviceInfo, DeviceConfiguration? config = null) => throw new NotSupportedException();
    public override FullDuplexDevice SwitchDevice(FullDuplexDevice oldDevice, DeviceInfo? newPlaybackInfo, DeviceInfo? newCaptureInfo, DeviceConfiguration? config = null) => throw new NotSupportedException();
}
