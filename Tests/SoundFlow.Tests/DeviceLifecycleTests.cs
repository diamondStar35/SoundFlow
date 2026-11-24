using CaptureDevice = SoundFlow.Devices.CaptureDevice;
using DeviceConfiguration = SoundFlow.Devices.DeviceConfiguration;
using PlaybackDevice = SoundFlow.Devices.PlaybackDevice;
using SoundFlow.Devices;
using SoundFlow.Structs;
using Xunit;

namespace SoundFlow.Tests;

public sealed class DeviceLifecycleTests
{
    [Fact]
    public void PairDisposalStillReleasesCaptureWhenPlaybackCleanupThrows()
    {
        using var engine = new PairEngine();
        var pair = new FullDuplexDevice(engine, null, null, OpenAlPlaybackTests.Stereo, new Config());
        Assert.Throws<AggregateException>(() => pair.Dispose());
        Assert.True(engine.Capture.IsDisposed);
        Assert.True(pair.IsDisposed);
    }

    private sealed class PairEngine : HeadlessEngine
    {
        public Capture Capture { get; }
        public PairEngine() => Capture = new Capture(this);
        public override SoundFlow.Devices.PlaybackDevice InitializePlaybackDevice(DeviceInfo? info, AudioFormat format, SoundFlow.Devices.DeviceConfiguration? config = null) => new BrokenPlayback(this);
        public override SoundFlow.Devices.CaptureDevice InitializeCaptureDevice(DeviceInfo? info, AudioFormat format, SoundFlow.Devices.DeviceConfiguration? config = null) => Capture;
    }
    private sealed class BrokenPlayback(HeadlessEngine engine) : SoundFlow.Devices.PlaybackDevice(engine, OpenAlPlaybackTests.Stereo, new Config())
    {
        public override void Start() => IsRunning = true;
        public override void Stop() => IsRunning = false;
        public override void Dispose() { IsDisposed = true; throw new IOException("Cleanup failed"); }
    }
    private sealed class Capture(HeadlessEngine engine) : SoundFlow.Devices.CaptureDevice(engine, OpenAlPlaybackTests.Stereo, new Config())
    {
        public override void Start() => IsRunning = true;
        public override void Stop() => IsRunning = false;
        public override void Dispose() => IsDisposed = true;
    }
    [Fact]
    public void FailedCaptureCreationReleasesThePairedPlaybackDevice()
    {
        using var engine = new FailingPairEngine();
        Assert.Throws<IOException>(() => new FullDuplexDevice(engine, null, null, OpenAlPlaybackTests.Stereo, new Config()));
        Assert.True(engine.Playback.IsDisposed);
    }

    private sealed class Config : DeviceConfiguration;
    private sealed class FailingPairEngine : HeadlessEngine
    {
        internal Playback Playback { get; }
        public FailingPairEngine() => Playback = new Playback(this);
        public override PlaybackDevice InitializePlaybackDevice(DeviceInfo? deviceInfo, AudioFormat format, DeviceConfiguration? config = null) => Playback;
        public override CaptureDevice InitializeCaptureDevice(DeviceInfo? deviceInfo, AudioFormat format, DeviceConfiguration? config = null) => throw new IOException("Simulated capture failure");
    }
    private sealed class Playback(HeadlessEngine engine) : PlaybackDevice(engine, OpenAlPlaybackTests.Stereo, new Config())
    {
        public override void Start() => IsRunning = true;
        public override void Stop() => IsRunning = false;
        public override void Dispose() => IsDisposed = true;
    }
}
