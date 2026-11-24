using SoundFlow.Providers;
using SoundFlow.Enums;
using SoundFlow.Interfaces;
using SoundFlow.Metadata.Models;
using SoundFlow.Structs;

namespace SoundFlow.Playback;

/// <summary>Adapts a source provider to the software output format without introducing a native codec dependency.</summary>
internal sealed class ConvertedDataProvider : IDataProvider
{
    private readonly PcmFormatConvertingDecoder _decoder;
    private readonly bool _canSeek;
    public ConvertedDataProvider(IDataProvider provider, AudioFormat source, AudioFormat target)
    {
        _canSeek = provider.CanSeek;
        _decoder = new PcmFormatConvertingDecoder(new ProviderDecoder(provider, source), target.Channels, target.SampleRate);
        _decoder.EndOfStreamReached += (_, _) => EndOfStreamReached?.Invoke(this, EventArgs.Empty);
    }
    public int Position { get; private set; }
    public int Length => _decoder.Length;
    public bool CanSeek => _canSeek;
    public SampleFormat SampleFormat => SampleFormat.F32;
    public int SampleRate => _decoder.SampleRate;
    public bool IsDisposed => _decoder.IsDisposed;
    public SoundFormatInfo? FormatInfo => null;
    public event EventHandler<EventArgs>? EndOfStreamReached;
    public event EventHandler<PositionChangedEventArgs>? PositionChanged;
    public int ReadSamples(Span<float> buffer)
    {
        var read = _decoder.Decode(buffer); Position += read;
        if (read > 0) PositionChanged?.Invoke(this, new PositionChangedEventArgs(Position));
        return read;
    }
    public void Seek(int offset)
    {
        if (!_decoder.Seek(offset)) throw new InvalidOperationException("The source provider cannot seek to that position.");
        Position = offset;
    }
    public void Dispose() => _decoder.Dispose();

    private sealed class ProviderDecoder : ISoundDecoder
    {
        private readonly IDataProvider _provider;
        private readonly AudioFormat _format;
        public ProviderDecoder(IDataProvider provider, AudioFormat format)
        {
            _provider = provider; _format = format;
            _provider.EndOfStreamReached += ForwardEnd;
        }
        private void ForwardEnd(object? sender, EventArgs args) => EndOfStreamReached?.Invoke(this, EventArgs.Empty);
        public bool IsDisposed => _provider.IsDisposed;
        public int Length => _provider.Length;
        public SampleFormat SampleFormat => SampleFormat.F32;
        public int Channels => _format.Channels;
        public int SampleRate => _format.SampleRate;
        public event EventHandler<EventArgs>? EndOfStreamReached;
        public int Decode(Span<float> samples) => _provider.ReadSamples(samples);
        public bool Seek(int offset) { if (!_provider.CanSeek) return false; _provider.Seek(offset); return true; }
        public void Dispose() { _provider.EndOfStreamReached -= ForwardEnd; _provider.Dispose(); }
    }
}
