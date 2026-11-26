using SoundFlow.Components;
using SoundFlow.Providers;
using SoundFlow.Metadata.Models;
using SoundFlow.Enums;
using SoundFlow.Playback;
using Xunit;

namespace SoundFlow.Tests;

public sealed class DecodingTests
{
    [Fact]
    public void HundredsOfSessionsShareAFixedWorkerSetAndBoundedPcmStorage()
    {
        using var scheduler = new DecodeScheduler(new PlaybackConfiguration { WorkerCount = 2, BufferFrames = 64 });
        var sessions = Enumerable.Range(0, 256).Select(_ =>
        {
            var transport = new Transport(TransportTests.Mono, new RecordingProvider());
            transport.Play(); return new DecodeSession(scheduler, transport);
        }).ToArray();
        try
        {
            foreach (var session in sessions) session.Enable(true);
            Assert.True(SpinWait.SpinUntil(() => sessions.All(s => s.Done || s.ReadyCount > 0), TimeSpan.FromSeconds(5)));
            var snapshot = scheduler.GetSnapshot();
            Assert.Equal(2, snapshot.Workers);
            Assert.Equal(256, snapshot.Sessions);
            Assert.InRange(snapshot.Pending, 0, 256);
            Assert.InRange(snapshot.BufferedFrames, 0, 256 * 4 * 64);
        }
        finally { foreach (var session in sessions) session.Dispose(); }
    }

    [Fact]
    public void SoftwarePlaybackNeverReadsItsProviderOnTheRenderingThread()
    {
        using var engine = new HeadlessEngine();
        var provider = new RecordingProvider();
        using var player = new SoundPlayer(engine, TransportTests.Mono, provider);
        var renderThread = Environment.CurrentManagedThreadId;
        player.Play();
        player.Process(new float[128], 1);
        Assert.DoesNotContain(renderThread, provider.ReadThreads);
    }

    internal sealed class RecordingProvider : IDataProvider
    {
        private readonly RawDataProvider _inner = new(new float[48000], 48000);
        public readonly System.Collections.Concurrent.ConcurrentBag<int> ReadThreads = [];
        public int Position => _inner.Position;
        public int Length => _inner.Length;
        public bool CanSeek => true;
        public SampleFormat SampleFormat => SampleFormat.F32;
        public int SampleRate => 48000;
        public bool IsDisposed => _inner.IsDisposed;
        public SoundFormatInfo? FormatInfo => null;
        public event EventHandler<EventArgs>? EndOfStreamReached { add => _inner.EndOfStreamReached += value; remove => _inner.EndOfStreamReached -= value; }
        public event EventHandler<PositionChangedEventArgs>? PositionChanged { add => _inner.PositionChanged += value; remove => _inner.PositionChanged -= value; }
        public int ReadSamples(Span<float> buffer) { ReadThreads.Add(Environment.CurrentManagedThreadId); return _inner.ReadSamples(buffer); }
        public void Seek(int offset) => _inner.Seek(offset);
        public void Dispose() => _inner.Dispose();
    }
}
