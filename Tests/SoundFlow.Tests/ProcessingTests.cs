using SoundFlow.Components;
using SoundFlow.Modifiers;
using SoundFlow.Providers;
using Xunit;
using SoundFlow.Processing;
using SoundFlow.Structs;
using SoundFlow.Enums;
using SoundFlow.Playback;

namespace SoundFlow.Tests;

public sealed class ProcessingTests
{
    internal sealed class StereoBlock(AudioFormat format, int block = 4) : FixedBlockProcessor(format,
        format with { Channels = 2, Layout = ChannelLayout.Stereo }, block)
    {
        protected override void ProcessBlock(ReadOnlySpan<float> input, Span<float> output)
        {
            for (var i = 0; i < BlockFrames; i++) { output[i * 2] = input[i]; output[i * 2 + 1] = -input[i]; }
        }
    }
    internal sealed class Gain(AudioFormat format, float gain) : Processor(format, format)
    {
        public override void Process(ReadOnlySpan<float> input, Span<float> output, int frames)
        { for (var i = 0; i < input.Length; i++) output[i] = input[i] * gain; }
    }
    [Fact]
    public void ReplacementResetsOnceAndFailedPreparationRetainsThePreviousRunner()
    {
        using var engine = new HeadlessEngine();
        var transport = new Transport(TransportTests.Mono, new RawDataProvider(new float[16], 48000));
        using var session = new DecodeSession(engine.Decoding, transport, frames: 8);
        var state = new ResetCounter(TransportTests.Mono);
        var replacement = new Builder().Add(_ => state).Build().CreateRunner(TransportTests.Mono, 8);
        session.ReplaceProcessing(replacement); Assert.Equal(1, state.Resets);
        using var rejected = new Builder().Add(f => new ResetCounter(f) { FailReset = true }).Build().CreateRunner(TransportTests.Mono, 8);
        Assert.Throws<InvalidOperationException>(() => session.ReplaceProcessing(rejected));
        Assert.Same(replacement, session.Processing); Assert.Equal(1, state.Resets);
    }
    private sealed class ResetCounter(AudioFormat format) : Processor(format, format)
    {
        public int Resets;
        public bool FailReset;
        public override void Process(ReadOnlySpan<float> input, Span<float> output, int frames) => input.CopyTo(output);
        public override void Reset() { if (FailReset) throw new InvalidOperationException("reset failure"); Resets++; }
    }

    [Fact]
    public void ProcessingAndCaptureCopiesAllocateNoStorageAfterBinding()
    {
        var builder = new Builder(); builder.Capture("input"); builder.Add(f => new Gain(f, .5f)); builder.Capture("output");
        using var runner = builder.Build().CreateRunner(TransportTests.Mono, 8);
        var input = new float[8]; var output = new float[8]; var captures = runner.CreateCaptures();
        for (var i = 0; i < 50; i++) runner.Process(input, output, 8, captures);
        var before = GC.GetAllocatedBytesForCurrentThread();
        for (var i = 0; i < 1000; i++) runner.Process(input, output, 8, captures);
        Assert.Equal(before, GC.GetAllocatedBytesForCurrentThread());
    }
    [Fact]
    public void BorrowedLegacyModifiersAreNotDisposedButFactoryOwnedModifiersAre()
    {
        var borrowed = new DisposableModifier();
        using (var runner = Chain.Empty.CreateRunner(TransportTests.Mono, 8)) runner.AddModifier(borrowed);
        Assert.False(borrowed.Disposed);
        var owned = new DisposableModifier();
        using (new Builder().AddModifier(_ => owned).Build().CreateRunner(TransportTests.Mono, 8)) { }
        Assert.True(owned.Disposed);
    }
    private sealed class DisposableModifier : SoundFlow.Abstracts.SoundModifier, IDisposable
    {
        public bool Disposed;
        public override float ProcessSample(float sample, int channel) => sample;
        public void Dispose() => Disposed = true;
    }

    [Fact]
    public void CapturesKeepTheirOwnFormatAndDoNotObserveLaterMutations()
    {
        var builder = new Builder(); var raw = builder.Capture("raw");
        builder.Add(f => new StereoBlock(f)); var stereo = builder.Capture("stereo");
        builder.Add(f => new Gain(f, 0));
        using var runner = builder.Build().CreateRunner(TransportTests.Mono, 8);
        var captures = runner.CreateCaptures(); var output = new float[16];
        runner.Process(Enumerable.Repeat(.5f, 8).ToArray(), output, 8, captures);
        Assert.Equal(1, runner.GetFormat(raw).Channels); Assert.Equal(2, runner.OutputFormat.Channels);
        Assert.All(captures[raw], sample => Assert.Equal(.5f, sample));
        Assert.Equal(.5f, captures[stereo][8]); Assert.Equal(-.5f, captures[stereo][9]);
        Assert.All(output, sample => Assert.Equal(0, sample));
    }
    [Fact]
    public void FixedBlockLatencyIsConstantAcrossPartialChunksAndDrainsExactlyOnce()
    {
        var builder = new Builder().Add(f => new StereoBlock(f));
        using var runner = builder.Build().CreateRunner(TransportTests.Mono, 7);
        var captures = runner.CreateCaptures(); var collected = new List<float>(); var consumed = 0;
        foreach (var frames in new[] { 1, 3, 2, 7 })
        {
            var input = Enumerable.Range(consumed + 1, frames).Select(n => (float)n).ToArray();
            var output = new float[frames * 2]; runner.Process(input, output, frames, captures);
            collected.AddRange(output.Where((_, index) => index % 2 == 0)); consumed += frames;
        }
        var tail = new float[14]; int count;
        while ((count = runner.Drain(tail, captures)) > 0) collected.AddRange(tail.Take(count * 2).Where((_, i) => i % 2 == 0));
        Assert.Equal(Enumerable.Repeat(0f, 4).Concat(Enumerable.Range(1, 13).Select(n => (float)n)), collected);
        Assert.True(runner.Completed); Assert.Equal(0, runner.Drain(tail, captures));
        runner.Reset(); runner.Process(new float[7], tail, 7, captures); Assert.All(tail, n => Assert.Equal(0, n));
    }
    [Fact]
    public void CompensationRequiresSendOptInEvenWhenAProcessorOptsIn()
    {
        var builder = new Builder(); var point = builder.Capture("before");
        builder.Add(f => new StereoBlock(f) { CompensateLatency = true });
        builder.Add(f => new Gain(f, 1));
        using var runner = builder.Build().CreateRunner(TransportTests.Mono, 8);
        Assert.Equal(LatencyCompensation.None, new SoundFlow.Features.SendSettings().Compensation);
        Assert.Equal(0, runner.GetCompensationFrames(point, LatencyCompensation.None));
        Assert.Equal(4, runner.GetCompensationFrames(point, LatencyCompensation.ProcessorOptIn));
        Assert.Equal(4, runner.GetCompensationFrames(point, LatencyCompensation.Automatic));
        var other = new Builder(); var foreign = other.Capture("before"); other.Build();
        Assert.Throws<ArgumentException>(() => runner.GetFormat(foreign));
    }
    [Fact]
    public void StateIsExclusiveAndRateChangesAreRejectedAtBinding()
    {
        var shared = new Gain(TransportTests.Mono, 1);
        var chain = new Builder().Add(_ => shared).Build();
        using var first = chain.CreateRunner(TransportTests.Mono, 8);
        Assert.Throws<InvalidOperationException>(() => chain.CreateRunner(TransportTests.Mono, 8));
        var invalid = new Builder().Add(f => new WrongRate(f)).Build();
        Assert.Throws<ArgumentException>(() => invalid.CreateRunner(TransportTests.Mono, 8));
    }
    private sealed class WrongRate(AudioFormat input) : Processor(input, input with { SampleRate = 44100 })
    { public override void Process(ReadOnlySpan<float> input, Span<float> output, int frames) { } }
    [Fact]
    public void DelayLineHasNoImplicitDelayAndPreservesFrameOrderWhenEnabled()
    {
        var output = new float[6]; var samples = new float[] { 1, 2, 3, 4, 5, 6 };
        new DelayLine(0, 2).Process(samples, output); Assert.Equal(samples, output);
        var delay = new DelayLine(2, 2); delay.Process(samples, output);
        Assert.Equal(new float[] { 0, 0, 0, 0, 1, 2 }, output);
        delay.Process(new float[4], output); Assert.Equal(new float[] { 3, 4, 5, 6 }, output.Take(4));
        delay.Reset(); delay.Process(new float[6], output); Assert.All(output, n => Assert.Equal(0, n));
    }
    [Fact]
    public void SessionMapsDelayedStereoBackToMonoContentAndResetsOnSeek()
    {
        using var engine = new HeadlessEngine();
        var transport = new Transport(TransportTests.Mono, new RawDataProvider(Enumerable.Repeat(.5f, 9).ToArray(), 48000));
        var builder = new Builder(); var point = builder.Capture("input"); builder.Add(f => new StereoBlock(f));
        using var session = new DecodeSession(engine.Decoding, transport, frames: 8,
            processing: builder.Build().CreateRunner(TransportTests.Mono, 8));
        transport.Play(); session.Enable(true);
        Assert.True(session.TryTake(true, out var first)); Assert.Equal(16, first!.Count);
        Assert.Equal(0, first.Positions[4]); Assert.Equal(4, first.Positions[8]); session.Release(first);
        session.Enable(false); session.Change(() => { transport.Seek(0); transport.Play(); }, true); session.Enable(true);
        Assert.True(session.TryTake(true, out first)); Assert.All(first!.Samples.Take(8), n => Assert.Equal(0, n)); session.Release(first);
        var last = 0; var tailFrames = 0;
        while (session.TryTake(true, out var block))
        { last = block!.EndPosition; tailFrames += block.Count / 2; session.Release(block); }
        Assert.Equal(9, last); Assert.Equal(5, tailFrames); Assert.True(session.Done);
    }
    [Fact]
    public void BufferedPlayerDrainsAnEffectAfterItsInputEnds()
    {
        using var engine = new HeadlessEngine();
        var impulse = new float[32]; impulse[0] = .5f;
        using var player = new SoundPlayer(engine, TransportTests.Mono, new RawDataProvider(impulse, 48000));
        player.AddModifier(new DelayModifier(TransportTests.Mono, 1024, 0, 1));
        player.Play();
        var output = new List<float>();
        for (var i = 0; i < 5; i++)
        {
            player.PrepareForRender(); var block = new float[512]; player.Process(block, 1); output.AddRange(block);
        }
        Assert.Contains(output.Skip(1024), value => Math.Abs(value) > .01);
    }
}
