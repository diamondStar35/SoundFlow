using SoundFlow.Features;
using SoundFlow.Playback;
using Xunit;

namespace SoundFlow.Tests;

public sealed class ConcurrentLifecycleTests
{
    [Fact]
    public async Task ConcurrentOfflineRendersSerializeFinalBufferDelivery()
    {
        using var engine = new SoundFlow.Backends.OpenAL.Engine();
        using var device = engine.CreateRenderDevice(OpenAlPlaybackTests.Stereo);
        var active = 0;
        var maximum = 0;
        device.AudioBufferProcessed += (samples, info) =>
        {
            if (info.Stage != ProcessingStage.FinalOutput) return;
            var count = Interlocked.Increment(ref active);
            maximum = Math.Max(maximum, count);
            Thread.Sleep(25); // Keep the delivery window open long enough to expose overlapping callers.
            Interlocked.Decrement(ref active);
        };
        using var start = new Barrier(2);
        Task Render() => Task.Factory.StartNew(() => { start.SignalAndWait(); device.Render(new float[1024]); }, TaskCreationOptions.LongRunning);
        await Task.WhenAll(Render(), Render()).WaitAsync(TimeSpan.FromSeconds(3));
        Assert.Equal(1, maximum);
    }

    [Fact]
    public async Task ConcurrentSourceDisposalReleasesOneNativeVoiceAndLeavesTheContextUsable()
    {
        using var engine = new SoundFlow.Backends.OpenAL.Engine();
        using var device = engine.CreateRenderDevice(OpenAlPlaybackTests.Stereo);
        using var source = device.CreateSource(new Clip(new float[4096], TransportTests.Mono));
        using var start = new Barrier(2);
        Task Dispose() => Task.Factory.StartNew(() => { start.SignalAndWait(); source.Dispose(); }, TaskCreationOptions.LongRunning);
        await Task.WhenAll(Dispose(), Dispose()).WaitAsync(TimeSpan.FromSeconds(3));
        Assert.True(device.TryGetFeature<IDiagnostics>(out var diagnostics));
        Assert.Equal(0, diagnostics.GetSnapshot().Sources);
        device.Render(new float[1024]);
    }
}
