using SpatialSource = SoundFlow.Playback.SpatialSource;
using SoundFlow.Features;
using SoundFlow.Playback;
using Xunit;

namespace SoundFlow.Tests;

public sealed class SourceIntentTests
{
    [Fact]
    public void OrdinaryMonoPlaybackDoesNotEnableSpatialization()
    {
        using var engine = new SoundFlow.Backends.OpenAL.Engine();
        using var device = engine.CreateRenderDevice(OpenAlPlaybackTests.Stereo);
        using var source = device.CreateSource(new Clip(new float[1024], TransportTests.Mono));
        Assert.IsNotAssignableFrom<SpatialSource>(source);
    }
}
