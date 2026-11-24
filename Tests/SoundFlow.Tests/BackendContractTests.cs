using Clip = SoundFlow.Playback.Clip;
using CustomSettings = BackendContract.CustomSettings;
using SoundFlow.Effects;
using SoundFlow.Features;
using SoundFlow.Providers;
using Xunit;

namespace SoundFlow.Tests;

public sealed class BackendContractTests
{
    [Fact]
    public void UnsupportedTransfersAreRejectedBeforeMovingAnySource()
    {
        using var engine = new HeadlessEngine();
        using var old = new BackendContract.Device(engine, TransportTests.Mono);
        using var next = new BackendContract.Device(engine, TransportTests.Mono);
        using var first = old.CreateSoftwareSource(new RawDataProvider(new[] { .1f }, 48000), TransportTests.Mono);
        using var second = old.CreateSource(new RawDataProvider(new[] { .2f }, 48000), TransportTests.Mono);
        Assert.Throws<NotSupportedException>(() => old.TransferSourcesTo(next));
        Assert.Same(old, first.Device);
        Assert.Same(old, second.Device);
    }

    [Fact]
    public void IndependentBackendsCanConsumeProvidersPublishBuffersAndDefineEffects()
    {
        using var engine = new HeadlessEngine();
        using var device = new BackendContract.Device(engine, TransportTests.Mono);
        using var provider = new RawDataProvider(new[] { .1f, .2f, .3f }, 48000);
        using (var source = device.CreateSource(provider, TransportTests.Mono, leaveOpen: true))
        {
            var sourceSamples = 0;
            var finalSamples = 0;
            source.AudioBufferProcessed += (samples, info) => { Assert.Equal(ProcessingStage.SourceInput, info.Stage); sourceSamples += samples.Length; };
            device.AudioBufferProcessed += (samples, info) => { Assert.Equal(ProcessingStage.FinalOutput, info.Stage); finalSamples += samples.Length; };
            Assert.Throws<InvalidOperationException>(() => device.CreateSource(provider, TransportTests.Mono));
            source.Play();
            var output = new float[8]; var result = device.Render(source, output);
            Assert.Equal(3, result.Samples);
            Assert.Equal(3, sourceSamples); Assert.Equal(8, finalSamples);
            Assert.Equal(new[] { .1f, .2f, .3f }, output[..3]);
        }
        Assert.False(provider.IsDisposed);
        using var replacement = device.CreateSource(provider, TransportTests.Mono, leaveOpen: true);
        Assert.True(device.TryGetFeature<IEffects>(out var effects));
        using var slot = effects.CreateSlot(new BackendContract.CustomSettings(.4f));
        Assert.Throws<ArgumentOutOfRangeException>(() => slot.Update(new CustomSettings(2)));
        Assert.Equal(.4f, ((CustomSettings)slot.Settings).Amount);
        Assert.Throws<NotSupportedException>(() => device.CreateSpatialSource(new Clip(new float[1], TransportTests.Mono)));
    }
}
