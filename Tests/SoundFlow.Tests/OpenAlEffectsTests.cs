using FilterSettings = SoundFlow.Effects.FilterSettings;
using IEffectSends = SoundFlow.Effects.IEffectSends;
using IEffects = SoundFlow.Effects.IEffects;
using Settings = SoundFlow.Effects.Settings;
using SoundFlow.Backends.OpenAL;
using SoundFlow.Effects;
using SoundFlow.Features;
using SoundFlow.Playback;
using Xunit;

namespace SoundFlow.Tests;

public sealed class OpenAlEffectsTests
{
    private static readonly Settings[] Defaults =
    [new ReverbSettings(), new EaxReverbSettings(), new ChorusSettings(), new DistortionSettings(), new EchoSettings(),
     new FlangerSettings(), new FrequencyShifterSettings(), new VocalMorpherSettings(), new PitchShifterSettings(),
     new RingModulatorSettings(), new AutowahSettings(), new CompressorSettings(), new EqualizerSettings()];

    [Fact]
    public void EveryStandardEffectAndEveryPresetWorksWithTheNativeLibrary()
    {
        using var engine = new SoundFlow.Backends.OpenAL.Engine();
        using var device = engine.CreateRenderDevice(OpenAlPlaybackTests.Stereo);
        Assert.True(device.TryGetFeature<IEffects>(out var effects));
        Assert.All(Defaults, setting => Assert.Contains(setting.GetType(), effects.SupportedEffects));
        using var source = device.CreateSource(new SoundFlow.Playback.Clip(Enumerable.Range(0, 16000).Select(i => .2f * MathF.Sin(i * .1f)).ToArray(), TransportTests.Mono));
        Assert.True(source.TryGetFeature<IEffectSends>(out var sends));
        source.Play();
        foreach (var settings in Defaults)
        {
            using var slot = effects.CreateSlot(settings);
            sends.SetSend(0, slot);
            var output = new float[1024];
            device.Render(output);
            Assert.All(output, sample => Assert.True(float.IsFinite(sample)));
        }
        using var reverb = effects.CreateSlot(new EaxReverbSettings());
        Assert.Equal(113, EfxReverbPresets.All.Count);
        foreach (var preset in EfxReverbPresets.All.Values) reverb.Update(preset);
    }

    [Fact]
    public void WetReverbProducesATailAfterTheDrySourceEnds()
    {
        using var engine = new SoundFlow.Backends.OpenAL.Engine();
        using var device = engine.CreateRenderDevice(OpenAlPlaybackTests.Stereo);
        Assert.True(device.TryGetFeature<IEffects>(out var effects));
        using var slot = effects.CreateSlot(EfxReverbPresets.All["CAVE"]);
        var impulse = new float[1024]; impulse[0] = 1;
        using var source = device.CreateSource(new SoundFlow.Playback.Clip(impulse, TransportTests.Mono));
        Assert.True(source.TryGetFeature<IEffectSends>(out var sends));
        sends.SetDirectFilter(new FilterSettings { Gain = 0 });
        sends.SetSend(0, slot);
        source.Play();
        device.Render(new float[8192]);
        var tail = new float[16384]; device.Render(tail);
        Assert.True(tail.Sum(x => Math.Abs(x)) > .001, "Native wet signal must survive after the finite source completes.");
    }

    [Fact]
    public void SlotRoutingRejectsCyclesForeignOwnersAndDisposedSlots()
    {
        using var engine = new SoundFlow.Backends.OpenAL.Engine();
        using var device = engine.CreateRenderDevice(OpenAlPlaybackTests.Stereo);
        using var other = engine.CreateRenderDevice(OpenAlPlaybackTests.Stereo);
        Assert.True(device.TryGetFeature<IEffects>(out var effects));
        Assert.True(other.TryGetFeature<IEffects>(out var foreign));
        using var a = effects.CreateSlot(new ChorusSettings());
        using var b = effects.CreateSlot(new EqualizerSettings());
        using var c = foreign.CreateSlot(new ReverbSettings());
        a.SetTarget(b);
        Assert.Throws<ArgumentException>(() => b.SetTarget(a));
        Assert.Throws<ArgumentException>(() => b.SetTarget(c));
        Assert.Null(b.Target);
        using var source = device.CreateSource(new SoundFlow.Playback.Clip(new float[1024], TransportTests.Mono));
        Assert.True(source.TryGetFeature<IEffectSends>(out var sends));
        sends.SetSend(0, b);
        b.Dispose();
        Assert.True(b.IsDisposed);
        Assert.Null(a.Target);
        Assert.Throws<ArgumentException>(() => sends.SetSend(0, b));
        Assert.Throws<ArgumentOutOfRangeException>(() => sends.SetSend(effects.MaximumSends, a));
    }

    [Fact]
    public void InvalidEffectUpdateKeepsThePreviousSnapshot()
    {
        using var engine = new SoundFlow.Backends.OpenAL.Engine();
        using var device = engine.CreateRenderDevice(OpenAlPlaybackTests.Stereo);
        Assert.True(device.TryGetFeature<IEffects>(out var effects));
        using var slot = effects.CreateSlot(new EaxReverbSettings());
        var previous = slot.Settings;
        Assert.Throws<ArgumentOutOfRangeException>(() => slot.Update(new EaxReverbSettings { DecayTime = float.NaN }));
        Assert.Same(previous, slot.Settings);
    }
}
