using Clip = SoundFlow.Playback.Clip;
using FilterKind = SoundFlow.Effects.FilterKind;
using FilterSettings = SoundFlow.Effects.FilterSettings;
using IEffectSends = SoundFlow.Effects.IEffectSends;
using SourceSettings = SoundFlow.Spatial.SourceSettings;
using System.Numerics;
using SoundFlow.Backends.OpenAL;
using SoundFlow.Effects;
using SoundFlow.Features;
using SoundFlow.Playback;
using Xunit;

namespace SoundFlow.Tests;

public sealed class OpenAlSpatialTests
{
    [Fact]
    public void MovingAMonoSourceAcrossTheListenerChangesTheDominantOutputChannel()
    {
        using var engine = new SoundFlow.Backends.OpenAL.Engine();
        using var device = engine.CreateRenderDevice(OpenAlPlaybackTests.Stereo, new SoundFlow.Backends.OpenAL.DeviceConfiguration { Hrtf = HrtfMode.Disabled });
        using var source = device.CreateSpatialSource(Tone(8192));
        var spatial = source;
        spatial.Settings = new SourceSettings { Position = new Vector3(-3, 0, -1), RolloffFactor = 0 };
        source.Play();
        var left = new float[4096]; device.Render(left);
        spatial.Settings = spatial.Settings with { Position = new Vector3(3, 0, -1) };
        device.Render(new float[2048]);
        var right = new float[4096]; device.Render(right);
        Assert.True(Energy(left, 0) > Energy(left, 1) * 2);
        Assert.True(Energy(right, 1) > Energy(right, 0) * 2);
    }

    [Fact]
    public void DirectLowPassFilteringAttenuatesHighFrequencyAudio()
    {
        var dry = RenderEnergy(null);
        var filtered = RenderEnergy(new FilterSettings { Kind = FilterKind.LowPass, GainHF = .01f });
        Assert.True(filtered < dry * .25, $"Expected high-frequency attenuation: dry {dry}, filtered {filtered}");
    }

    [Fact]
    public void OfflineBufferEventsIdentifyTheFinalNativeMixAndItsFormat()
    {
        using var engine = new SoundFlow.Backends.OpenAL.Engine();
        using var device = engine.CreateRenderDevice(OpenAlPlaybackTests.Stereo);
        using var source = device.CreateSource(Tone(4096));
        var finalSamples = 0;
        device.AudioBufferProcessed += (samples, info) =>
        {
            if (info.Stage != ProcessingStage.FinalOutput) return;
            Assert.Equal(2, info.Format.Channels);
            finalSamples += samples.Length;
        };
        source.Play(); device.Render(new float[2048]);
        Assert.Equal(2048, finalSamples);
        Assert.True(device.AvailableProcessingStages.HasFlag(ProcessingStage.FinalOutput));
        Assert.True(device.AvailableProcessingStages.HasFlag(ProcessingStage.GraphOutput));
    }

    private static Clip Tone(int frames, float frequency = 440) => new(Enumerable.Range(0, frames)
        .Select(i => .2f * MathF.Sin(2 * MathF.PI * frequency * i / 48000)).ToArray(), TransportTests.Mono);
    private static double Energy(float[] samples, int channel) => Enumerable.Range(256, samples.Length / 2 - 256).Sum(i => (double)samples[i * 2 + channel] * samples[i * 2 + channel]);
    private static double RenderEnergy(FilterSettings? filter)
    {
        using var engine = new SoundFlow.Backends.OpenAL.Engine();
        using var device = engine.CreateRenderDevice(OpenAlPlaybackTests.Stereo, new SoundFlow.Backends.OpenAL.DeviceConfiguration { Hrtf = HrtfMode.Disabled });
        using var source = device.CreateSource(Tone(8192, 10000));
        Assert.True(source.TryGetFeature<IEffectSends>(out var sends)); sends.SetDirectFilter(filter);
        source.Play(); device.Render(new float[2048]);
        var output = new float[8192]; device.Render(output);
        return output.Sum(v => (double)v * v);
    }
}
