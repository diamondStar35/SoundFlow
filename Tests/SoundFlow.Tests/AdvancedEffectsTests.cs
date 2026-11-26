using SoundFlow.Effects;
using SoundFlow.Playback;
using Xunit;

namespace SoundFlow.Tests;

public sealed class AdvancedEffectsTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void DedicatedEffectsRouteToTheRequestedSurroundChannel(bool lfe)
    {
        using var engine = new SoundFlow.Backends.OpenAL.Engine();
        using var device = engine.CreateRenderDevice(OpenAlPlaybackTests.Stereo with { Channels = 6 });
        Assert.True(device.TryGetFeature<IEffects>(out var effects));
        using var slot = effects.CreateSlot(lfe ? new DedicatedLowFrequencySettings() : new DedicatedDialogueSettings());
        using var source = device.CreateSource(PreparedClipTests.Tone());
        Assert.True(source.TryGetFeature<IEffectSends>(out var sends));
        sends.SetDirectFilter(new FilterSettings { Gain = 0 }); sends.SetSend(0, slot); source.Play();
        var output = new float[4096 * 6]; device.Render(output);
        var channel = lfe ? 3 : 2;
        Assert.True(Enumerable.Range(0, 4096).Sum(i => Math.Abs(output[i * 6 + channel])) > .01);
    }

    [Fact]
    public void ConvolutionRendersTheRetainedResponseAndFailedReplacementPreservesItsSnapshot()
    {
        using var engine = new SoundFlow.Backends.OpenAL.Engine();
        using var device = engine.CreateRenderDevice(OpenAlPlaybackTests.Stereo,
            new SoundFlow.Backends.OpenAL.DeviceConfiguration { PreparedClipBytes = 16384 });
        Assert.True(device.TryGetFeature<IEffects>(out var effects));
        Assert.Contains(typeof(ConvolutionSettings), effects.SupportedEffects);
        var response = new float[4096]; response[2048] = .5f; response[3000] = .25f;
        var settings = new ConvolutionSettings { ImpulseResponse = new Clip(response, TransportTests.Mono) };
        using var slot = effects.CreateSlot(settings);
        Assert.Throws<InvalidOperationException>(() => slot.Update(settings with { ImpulseResponse = PreparedClipTests.Tone(8192) }));
        Assert.Same(settings, slot.Settings);
        var impulse = new float[128]; impulse[0] = .5f;
        using var source = device.CreateSource(new Clip(impulse, TransportTests.Mono));
        Assert.True(source.TryGetFeature<IEffectSends>(out var sends));
        sends.SetDirectFilter(new FilterSettings { Gain = 0 }); sends.SetSend(0, slot); source.Play();
        var output = new float[8192 * 2]; device.Render(output);
        Assert.True(output.Skip(1800 * 2).Sum(x => Math.Abs(x)) > .01);
        slot.Update(new EqualizerSettings());
        Assert.IsType<EqualizerSettings>(slot.Settings);
    }
}
