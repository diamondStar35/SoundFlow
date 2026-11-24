using PlaybackState = SoundFlow.Enums.PlaybackState;
using SoundFlow.Backends.OpenAL.Features;
using SoundFlow.Features;
using SoundFlow.Playback;
using Xunit;

namespace SoundFlow.Tests;

public sealed class RenderingControlTests
{
    [Fact]
    public void NewVoicesInheritTheSceneDistanceModelAndOverridesSurviveSceneChanges()
    {
        using var engine = new SoundFlow.Backends.OpenAL.Engine();
        using var device = engine.CreateRenderDevice(OpenAlPlaybackTests.Stereo);
        Assert.True(device.TryGetFeature<SoundFlow.Spatial.IScene>(out var scene));
        Assert.True(device.TryGetFeature<IUpdateBatch>(out var batch));
        scene.DistanceModel = SoundFlow.Spatial.DistanceModel.Linear;
        using var source = device.CreateSpatialSource(new Clip(new float[1024], TransportTests.Mono));
        var voice = ((SoundFlow.Backends.OpenAL.Playback.INativeSource)source).Voice;
        var model = 0;
        batch.Apply(() => model = engine.Native.SourceInteger(voice.Id, 0xD000));
        Assert.Equal(0xD003, model);
        Assert.True(source.TryGetFeature<ISourceRendering>(out var rendering));
        rendering.Settings = new RenderingSettings { DistanceModel = SoundFlow.Spatial.DistanceModel.None };
        scene.DistanceModel = SoundFlow.Spatial.DistanceModel.Exponential;
        batch.Apply(() => model = engine.Native.SourceInteger(voice.Id, 0xD000));
        Assert.Equal(0, model);
    }

    [Fact]
    public void LimiterAndEnvironmentalUnitsSurviveHrtfReset()
    {
        using var engine = new SoundFlow.Backends.OpenAL.Engine();
        using var device = engine.CreateRenderDevice(OpenAlPlaybackTests.Stereo, new SoundFlow.Backends.OpenAL.DeviceConfiguration { OutputLimiter = false });
        Assert.True(device.TryGetFeature<IDeviceRendering>(out var rendering));
        Assert.False(rendering.GetRenderingState().OutputLimiter);
        Assert.True(device.TryGetFeature<SoundFlow.Spatial.IScene>(out var scene));
        scene.MetresPerUnit = 2;
        Assert.Throws<ArgumentOutOfRangeException>(() => scene.MetresPerUnit = 0);
        Assert.Equal(2, scene.MetresPerUnit);
        Assert.True(device.TryGetFeature<IHrtfAudio>(out var hrtf));
        hrtf.Configure(HrtfMode.Disabled);
        Assert.False(rendering.GetRenderingState().OutputLimiter);
    }

    [Fact]
    public void ScheduledGroupsRemainSilentUntilTheDeviceClockReachesTheirStart()
    {
        using var engine = new SoundFlow.Backends.OpenAL.Engine();
        using var device = engine.CreateRenderDevice(OpenAlPlaybackTests.Stereo, new SoundFlow.Backends.OpenAL.DeviceConfiguration { Hrtf = HrtfMode.Disabled });
        var clip = new Clip(Enumerable.Range(0, 8192).Select(i => .2f * MathF.Sin(i * .1f)).ToArray(), TransportTests.Mono);
        using var first = device.CreateSource(clip);
        using var second = device.CreateSource(clip);
        Assert.True(device.TryGetFeature<ISourceGroups>(out var groups));
        Assert.True(device.TryGetFeature<IPlaybackTiming>(out var timing));
        Assert.True(device.TryGetFeature<IScheduledPlayback>(out var scheduled));
        using var group = groups.CreateGroup(first, second);
        var start = timing.GetClock().Nanoseconds + 50_000_000;
        scheduled.PlayAt(group, start);
        var before = new float[4096]; device.Render(before);
        Assert.All(before, sample => Assert.InRange(Math.Abs(sample), 0, 1e-6f));
        var after = new float[8192]; device.Render(after);
        Assert.Contains(after, sample => Math.Abs(sample) > .01f);
        Assert.Equal(timing.GetTiming(first).ContentSeconds, timing.GetTiming(second).ContentSeconds, 5);
        group.Stop();
        Assert.Equal(PlaybackState.Stopped, first.State);
        group.Dispose();
        Assert.False(first.IsDisposed);
        first.Play();
        Assert.Equal(PlaybackState.Playing, first.State);
    }

    [Fact]
    public void AFailedBatchReleasesNativeDeferralAndKeepsSuccessfulAssignments()
    {
        using var engine = new SoundFlow.Backends.OpenAL.Engine();
        using var device = engine.CreateRenderDevice(OpenAlPlaybackTests.Stereo);
        Assert.True(device.TryGetFeature<IUpdateBatch>(out var batch));
        Assert.Throws<IOException>(() => batch.Apply(() => { device.MasterGain = .3f; throw new IOException("Interrupted update"); }));
        Assert.Equal(.3f, device.MasterGain);
        batch.Apply(() => device.MasterGain = .6f);
        Assert.Equal(.6f, device.MasterGain);
        device.Render(new float[512]);
    }

    [Fact]
    public void SpatialCreationPreservesStereoInputAndRejectsUnsupportedLayouts()
    {
        using var engine = new SoundFlow.Backends.OpenAL.Engine();
        using var device = engine.CreateRenderDevice(OpenAlPlaybackTests.Stereo);
        using var source = device.CreateSpatialSource(new Clip(new float[4096], OpenAlPlaybackTests.Stereo));
        Assert.Equal(2, source.Format.Channels);
        Assert.True(source.Settings.Spatialize);
        Assert.Throws<NotSupportedException>(() => device.CreateSpatialSource(new Clip(new float[300], TransportTests.Mono with { Channels = 3 })));
    }

    [Fact]
    public void NativeVoicesExposeValidatedRenderingControls()
    {
        using var engine = new SoundFlow.Backends.OpenAL.Engine();
        using var device = engine.CreateRenderDevice(OpenAlPlaybackTests.Stereo);
        using var source = device.CreateSpatialSource(new Clip(new float[1024], TransportTests.Mono));
        Assert.True(source.TryGetFeature<ISourceRendering>(out var rendering));
        Assert.True(rendering.Capabilities.HasFlag(RenderingCapabilities.Radius));
        rendering.Settings = new RenderingSettings { Radius = 2, MinimumGain = .1f, MaximumGain = .8f };
        Assert.Equal(2, rendering.Settings.Radius);
        Assert.Throws<ArgumentOutOfRangeException>(() => rendering.Settings = new RenderingSettings { MinimumGain = .9f, MaximumGain = .1f });
        Assert.Equal(2, rendering.Settings.Radius);
    }

    [Fact]
    public void RenderDevicesExposeTimingGroupsSchedulingAndDeferredUpdates()
    {
        using var engine = new SoundFlow.Backends.OpenAL.Engine();
        using var device = engine.CreateRenderDevice(OpenAlPlaybackTests.Stereo);
        Assert.True(device.TryGetFeature<IPlaybackTiming>(out _));
        Assert.True(device.TryGetFeature<ISourceGroups>(out _));
        Assert.True(device.TryGetFeature<IScheduledPlayback>(out _));
        Assert.True(device.TryGetFeature<IUpdateBatch>(out _));
    }
}
