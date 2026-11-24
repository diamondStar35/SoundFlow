using SoundFlow.Enums;
using SoundFlow.Interfaces;
using SoundFlow.Providers;
using SoundFlow.Structs;
using Xunit;

namespace SoundFlow.Tests;

public sealed class StreamCompletionTests
{
    [Fact]
    public void DecoderCompletionIsForwardedToSubscribersAddedAfterProviderConstruction()
    {
        using var engine = new HeadlessEngine(); engine.RegisterCodecFactory(new Codec());
        using var provider = new StreamDataProvider(engine, TransportTests.Mono, new MemoryStream(new byte[16]));
        var ended = 0; provider.EndOfStreamReached += (_, _) => ended++;
        var samples = new float[4]; provider.ReadSamples(samples); provider.ReadSamples(samples);
        Assert.Equal(1, ended);
    }

    private sealed class Codec : ICodecFactory
    {
        public string FactoryId => "Test completion codec";
        public IReadOnlyCollection<string> SupportedFormatIds => ["test"];
        public int Priority => 0;
        public ISoundDecoder CreateDecoder(Stream stream, string formatId, AudioFormat format) => new Decoder();
        public ISoundDecoder TryCreateDecoder(Stream stream, out AudioFormat detectedFormat, AudioFormat? hintFormat = null) { detectedFormat = TransportTests.Mono; return new Decoder(); }
        public ISoundEncoder? CreateEncoder(Stream stream, string formatId, AudioFormat format) => null;
    }
    private sealed class Decoder : ISoundDecoder
    {
        private bool _read;
        public bool IsDisposed { get; private set; }
        public int Length => 0;
        public SampleFormat SampleFormat => SampleFormat.F32;
        public int Channels => 1;
        public int SampleRate => 48000;
        public event EventHandler<EventArgs>? EndOfStreamReached;
        public bool Seek(int offset) => false;
        public int Decode(Span<float> samples)
        {
            if (_read) { EndOfStreamReached?.Invoke(this, EventArgs.Empty); return 0; }
            _read = true; samples.Fill(.1f); return samples.Length;
        }
        public void Dispose() => IsDisposed = true;
    }
}
