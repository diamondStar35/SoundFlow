using SoundFlow.Backends.OpenAL.Devices;
using Xunit;

namespace SoundFlow.Tests;

public sealed class CallbackSafetyTests
{
    [Fact]
    public void EngineDisposalInsideAnUpdateBatchIsRejectedBeforeChangingLifetimeState()
    {
        using var engine = new SoundFlow.Backends.OpenAL.Engine();
        using var device = engine.CreateRenderDevice(OpenAlPlaybackTests.Stereo);
        Assert.True(device.TryGetFeature<SoundFlow.Features.IUpdateBatch>(out var batch));
        Assert.Throws<InvalidOperationException>(() => batch.Apply(engine.Dispose));
        Assert.False(engine.IsDisposed);
        Assert.False(device.IsDisposed);
        device.Render(new float[512]);
    }

    [Fact]
    public void RealSourceCallbacksCannotDisposeTheirVoiceButCanBeDisposedAfterRendering()
    {
        using var engine = new SoundFlow.Backends.OpenAL.Engine();
        using var device = engine.CreateRenderDevice(OpenAlPlaybackTests.Stereo);
        using var source = device.CreateSource(new SoundFlow.Playback.Clip(new float[4096], TransportTests.Mono));
        Exception? rejected = null;
        source.AudioBufferProcessed += (samples, info) => rejected = Record.Exception(source.Dispose);
        source.Play(); device.Render(new float[1024]);
        Assert.IsType<InvalidOperationException>(rejected);
        Assert.False(source.IsDisposed);
        source.Dispose();
        Assert.True(source.IsDisposed);
    }

    [Fact]
    public void AProcessorFailureIsReportedWithoutPoisoningOtherVoicesOrCleanup()
    {
        using var engine = new SoundFlow.Backends.OpenAL.Engine();
        using var device = engine.CreateRenderDevice(OpenAlPlaybackTests.Stereo);
        using var failed = device.CreateSource(new SoundFlow.Playback.Clip(new float[4096], TransportTests.Mono));
        using var healthy = device.CreateSource(new SoundFlow.Playback.Clip(Enumerable.Repeat(.2f, 8192).ToArray(), TransportTests.Mono));
        using var notification = new ManualResetEventSlim();
        engine.Notification += (_, value) => { if (value.Kind == SoundFlow.Features.NotificationKind.ProcessingFailure) notification.Set(); };
        failed.AddModifier(new BrokenProcessor()); failed.Play(); healthy.Play();
        var output = new float[4096]; device.Render(output);
        Assert.True(notification.Wait(TimeSpan.FromSeconds(2)));
        Assert.Contains(output, value => Math.Abs(value) > .01f);
        device.MasterGain = .5f;
        Assert.Equal(.5f, device.MasterGain);
        Assert.True(device.TryGetFeature<SoundFlow.Features.IDiagnostics>(out var diagnostics));
        Assert.Contains("processor failed", diagnostics.GetSnapshot().LastFailure);
    }

    private sealed class BrokenProcessor : SoundFlow.Abstracts.SoundModifier
    {
        public override float ProcessSample(float value, int channel) => throw new IOException("processor failed");
    }

    [Fact]
    public void ProcessingCallbacksRejectNativeControlsBeforeEnqueuingThem()
    {
        using var engine = new SoundFlow.Backends.OpenAL.Engine();
        using var device = engine.CreateRenderDevice(OpenAlPlaybackTests.Stereo);
        using (ProcessingScope.Enter())
            Assert.Throws<InvalidOperationException>(() => device.MasterGain = .5f);
        device.MasterGain = .4f;
        Assert.Equal(.4f, device.MasterGain);
    }
}
