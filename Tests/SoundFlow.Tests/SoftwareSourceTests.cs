using DeviceConfiguration = SoundFlow.Devices.DeviceConfiguration;
using Engine = SoundFlow.Engine;
using PlaybackDevice = SoundFlow.Devices.PlaybackDevice;
using SoundFlow.Abstracts;
using SoundFlow.Devices;
using SoundFlow.Playback;
using Xunit;

namespace SoundFlow.Tests;

public sealed class SoftwareSourceTests
{
    [Fact]
    public void MonoSourceFramesAreExpandedToStereoWithoutChangingPlaybackDuration()
    {
        using var engine = new HeadlessEngine();
        using var device = new SoftwareDevice(engine);
        using var source = device.CreateSource(new SoundFlow.Playback.Clip(new[] { .1f, .2f, .3f, .4f }, TransportTests.Mono));
        source.Play();
        var output = new float[8]; device.MasterMixer.Process(output, 2);
        Assert.Equal(new[] { .1f, .1f, .2f, .2f, .3f, .3f, .4f, .4f }, output);
    }

    [Fact]
    public void SwitchingSoftwareDevicesPreservesOwnedSourcesAfterTheOldDeviceIsDisposed()
    {
        using var engine = new HeadlessEngine();
        using var old = new SoftwareDevice(engine);
        using var next = new SoftwareDevice(engine);
        using var source = old.CreateSource(new SoundFlow.Playback.Clip(new[] { .1f, .2f, .3f, .4f }, TransportTests.Mono));
        source.Play();
        old.TransferSourcesTo(next);
        old.Dispose();
        var output = new float[8]; next.MasterMixer.Process(output, 2);
        Assert.Same(next, source.Device);
        Assert.False(source.IsDisposed);
        Assert.Equal(new[] { .1f, .1f, .2f, .2f, .3f, .3f, .4f, .4f }, output);
    }

    private sealed class Config : DeviceConfiguration;
    private sealed class SoftwareDevice(Engine engine) : PlaybackDevice(engine, OpenAlPlaybackTests.Stereo, new Config())
    {
        public override void Start() => IsRunning = true;
        public override void Stop() => IsRunning = false;
        public override void Dispose() { DisposeSources(); MasterMixer.Dispose(); IsDisposed = true; }
    }
}
