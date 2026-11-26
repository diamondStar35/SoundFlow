using SoundFlow.Components;
using SoundFlow.Playback;
using SoundFlow.Providers;
using SoundFlow.Metadata.Models;
using SoundFlow.Enums;
using SoundFlow.Features;
using Xunit;

namespace SoundFlow.Tests;

public sealed class SharedWorkerLifecycleTests
{
    [Fact]
    public async Task ASeekWaitingForInputDoesNotOccupyTheOpenAlContextWorker()
    {
        using var engine = new SoundFlow.Backends.OpenAL.Engine();
        using var device = engine.CreateRenderDevice(OpenAlPlaybackTests.Stereo);
        using var provider = new WaitingProvider();
        using var source = device.CreateSource(provider, TransportTests.Mono, true);
        source.Play(); Assert.True(provider.Entered.Wait(TimeSpan.FromSeconds(2)));
        var seek = Task.Run(() => source.Seek(0));
        try
        {
            await Task.Run(() =>
            {
                device.MasterGain = .8f;
                Assert.True(device.TryGetFeature<IDiagnostics>(out var diagnostics)); diagnostics.GetSnapshot();
            }).WaitAsync(TimeSpan.FromSeconds(2));
        }
        finally { provider.Release.Set(); }
        Assert.True(await seek.WaitAsync(TimeSpan.FromSeconds(2)));
    }

    [Fact]
    public async Task DisposingASessionCancelsItsCooperativeInputBeforeReleasingTheProvider()
    {
        using var engine = new HeadlessEngine { PlaybackConfiguration = new() { WorkerCount = 1 } };
        var provider = new WaitingProvider();
        var player = new SoundPlayer(engine, TransportTests.Mono, provider);
        player.Play(); Assert.True(provider.Entered.Wait(TimeSpan.FromSeconds(2)));
        await Task.Run(player.Dispose).WaitAsync(TimeSpan.FromSeconds(2));
        Assert.True(provider.IsDisposed);
        Assert.Equal(0, engine.Decoding.GetSnapshot().Sessions);
    }

    [Fact]
    public void OfflineGraphsWithOneDecodeWorkerPrepareLeafInputsWithoutNestedWaits()
    {
        using var engine = new SoundFlow.Backends.OpenAL.Engine { PlaybackConfiguration = new() { WorkerCount = 1 } };
        using var device = engine.CreateRenderDevice(OpenAlPlaybackTests.Stereo);
        using var player = new SoundPlayer(engine, TransportTests.Mono, PreparedClipTests.Tone().CreateProvider());
        device.MasterMixer.AddComponent(player); player.Play();
        var output = new float[4096]; device.Render(output);
        Assert.Contains(output.Take(1024), x => Math.Abs(x) > .01);
        Assert.Contains(output.Skip(2048), x => Math.Abs(x) > .01);
        Assert.Equal(1, engine.Decoding.GetSnapshot().Workers);
    }

    [Fact]
    public void SoftwarePcmCallbacksRejectDisposalBeforeChangingTheSourceLifetime()
    {
        using var engine = new HeadlessEngine();
        using var player = new SoundPlayer(engine, TransportTests.Mono, PreparedClipTests.Tone().CreateProvider());
        player.AddModifier(new Callback(() => Assert.Throws<InvalidOperationException>(player.Dispose)));
        player.Play(); player.PrepareForRender(); player.Process(new float[512], 1);
        Assert.False(player.IsDisposed); Assert.False(engine.IsDisposed);
    }

    private sealed class Callback(Action callback) : SoundFlow.Abstracts.SoundModifier
    {
        public override float ProcessSample(float sample, int channel) => sample;
        public override void Process(Span<float> buffer, int channels) => callback();
    }

    private sealed class WaitingProvider : ICancellableDataProvider
    {
        internal readonly ManualResetEventSlim Entered = new();
        internal readonly ManualResetEventSlim Release = new();
        public int Position { get; private set; }
        public int Length => 48000;
        public bool CanSeek => true;
        public SampleFormat SampleFormat => SampleFormat.F32;
        public int SampleRate => 48000;
        public bool IsDisposed { get; private set; }
        public SoundFormatInfo? FormatInfo => null;
        public event EventHandler<EventArgs>? EndOfStreamReached { add { } remove { } }
        public event EventHandler<PositionChangedEventArgs>? PositionChanged { add { } remove { } }
        public int ReadSamples(Span<float> buffer) => ReadSamples(buffer, default);
        public int ReadSamples(Span<float> buffer, CancellationToken cancellationToken)
        {
            Entered.Set(); Release.Wait(cancellationToken); cancellationToken.ThrowIfCancellationRequested();
            buffer.Fill(.1f); Position += buffer.Length; return buffer.Length;
        }
        public void Seek(int offset) => Position = offset;
        public void Dispose() { IsDisposed = true; Entered.Dispose(); Release.Dispose(); }
    }
}
