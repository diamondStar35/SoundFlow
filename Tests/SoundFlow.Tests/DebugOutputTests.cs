using SoundFlow.Backends.OpenAL.Features;
using SoundFlow.Features;
using SoundFlow.Playback;
using Xunit;

namespace SoundFlow.Tests;

public sealed class DebugOutputTests
{
    [Fact]
    public void DebugMessagesAreFilteredAndDeliveredOutsideTheNativeContextThread()
    {
        using var engine = new SoundFlow.Backends.OpenAL.Engine();
        using var device = engine.CreateRenderDevice(OpenAlPlaybackTests.Stereo,
            new SoundFlow.Backends.OpenAL.DeviceConfiguration { DebugMessages = true });
        Assert.True(device.TryGetFeature<IDebugOutput>(out var debug));
        Assert.True(debug.Enabled);
        var messages = new System.Collections.Concurrent.ConcurrentQueue<string>();
        using var delivered = new ManualResetEventSlim();
        debug.Message += (_, message) =>
        {
            // This normal control would fail or deadlock if invoked from a native/PCM callback.
            Assert.True(device.TryGetFeature<IDiagnostics>(out var diagnostics)); diagnostics.GetSnapshot();
            messages.Enqueue(message.Text); delivered.Set();
        };
        debug.MinimumSeverity = DebugSeverity.Medium;
        debug.Write("filtered marker", DebugSeverity.Low);
        debug.Write("delivered marker", DebugSeverity.High);
        Assert.True(delivered.Wait(TimeSpan.FromSeconds(2)));
        Assert.Contains("delivered marker", messages); Assert.DoesNotContain("filtered marker", messages);
        using var pool = device.CreatePool(new() { Capacity = 1 });
        var clip = PreparedClipTests.Tone();
        using (var first = pool.Play(clip)) debug.SetLabel(first.Source, "first transient");
        using var second = pool.Play(clip);
        Assert.True(device.TryGetFeature<IDiagnostics>(out var snapshot));
        var state = snapshot.GetSnapshot();
        Assert.Equal(2, state.SourceAllocations); // One graph feed and one reusable prepared voice.
        Assert.Equal(1, state.ReusedSources);
        Assert.Equal(clip.Samples.Length * sizeof(float), state.PreparedClipBytes);
        Assert.Contains(typeof(IPreparedClips).FullName!, state.ImplementedFeatures);
    }
}
