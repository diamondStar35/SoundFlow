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
    public void RenderChainRunsAtMixerConsumptionAndPauseDoesNotRestartAtDecodedEof()
    {
        using var engine = new HeadlessEngine();
        using var device = new SoftwareDevice(engine);
        var calls = 0;
        var builder = new SoundFlow.Processing.Builder().Add(format => new RenderProbe(format, () => calls++));
        using var source = device.CreateSource(new Clip(Enumerable.Range(1, 512).Select(i => i / 1024f).ToArray(), TransportTests.Mono),
            new SourceOptions { RenderProcessing = builder.Build() });
        source.Play();
        device.MasterMixer.PrepareForRender();
        Assert.Equal(0, calls);
        var output = new float[256];
        device.MasterMixer.Process(output, 2);
        Assert.Equal(1, calls);
        Assert.Equal(1f / 1024, output[0]);
        source.Pause();
        source.Play();
        Array.Clear(output);
        device.MasterMixer.Process(output, 2);
        Assert.Equal(1, calls);
        Assert.Equal(129f / 1024, output[0]);
        Assert.Equal(0, source.RenderProcessingState!.Value.RetainedFrames);
    }

    private sealed class RenderProbe(SoundFlow.Structs.AudioFormat format, Action called)
        : SoundFlow.Processing.Processor(format, format)
    {
        public override void Process(ReadOnlySpan<float> input, Span<float> output, int frames)
        { called(); input.CopyTo(output); }
    }

    [Fact]
    public void MonoSourceFramesAreExpandedToStereoWithoutChangingPlaybackDuration()
    {
        using var engine = new HeadlessEngine();
        using var device = new SoftwareDevice(engine);
        using var source = device.CreateSource(new SoundFlow.Playback.Clip(new[] { .1f, .2f, .3f, .4f }, TransportTests.Mono));
        source.Play();
        var output = new float[8]; device.MasterMixer.PrepareForRender(); device.MasterMixer.Process(output, 2);
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
        var output = new float[8]; next.MasterMixer.PrepareForRender(); next.MasterMixer.Process(output, 2);
        Assert.Same(next, source.Device);
        Assert.False(source.IsDisposed);
        Assert.Equal(new[] { .1f, .1f, .2f, .2f, .3f, .3f, .4f, .4f }, output);
    }

    [Fact]
    public void TransferringSoftwareSourcesAlsoTransfersTheirOwningPlaybackPool()
    {
        using var engine = new HeadlessEngine();
        var old = new SoftwareDevice(engine);
        using var next = new SoftwareDevice(engine);
        using var pool = old.CreatePool();
        using var lease = pool.Play(PreparedClipTests.Tone());
        old.TransferSourcesTo(next); old.Dispose();
        Assert.Null(lease.Outcome); Assert.Same(next, lease.Source.Device);
        using var following = pool.Play(PreparedClipTests.Tone());
        Assert.Same(next, following.Source.Device);
    }

    private sealed class Config : DeviceConfiguration;
    private sealed class SoftwareDevice(Engine engine) : PlaybackDevice(engine, OpenAlPlaybackTests.Stereo, new Config())
    {
        public override void Start() => IsRunning = true;
        public override void Stop() => IsRunning = false;
        public override void Dispose() { DisposeSources(); MasterMixer.Dispose(); IsDisposed = true; }
    }
}
