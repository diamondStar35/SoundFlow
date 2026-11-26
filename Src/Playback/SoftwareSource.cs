using SoundFlow.Providers;
using PlaybackDevice = SoundFlow.Devices.PlaybackDevice;
using SoundFlow.Abstracts;
using SoundFlow.Devices;
using SoundFlow.Components;
using SoundFlow.Enums;
using SoundFlow.Interfaces;
using SoundFlow.Structs;

namespace SoundFlow.Playback;

/// <summary>Maps the common source interface to the software mixer, without loading any native library.</summary>
internal sealed class SoftwareSource : Source
{
    private readonly SoundPlayer _player;
    private readonly IDataProvider _provider;
    public SoftwareSource(PlaybackDevice device, IDataProvider provider, AudioFormat format, bool leaveOpen, SourceOptions? options = null)
        : base(device, provider, format)
    {
        try
        {
            _provider = provider;
            IDataProvider input = leaveOpen ? new BorrowedProvider(provider) : provider;
            var inputFormat = format;
            if (format.SampleRate != device.Format.SampleRate)
            {
                inputFormat = format with { SampleRate = device.Format.SampleRate };
                input = new ConvertedDataProvider(input, format, inputFormat);
            }
            _player = new SoundPlayer(device.Engine, inputFormat, input, options?.Processing, device.Format.Channels);
            _player.SourceBufferProcessed += (samples, info) => PublishBuffer(samples, info.Format);
            _player.PlaybackEnded += (_, _) => RaisePlaybackEnded();
            _player.PlaybackFailed += (_, error) => RaisePlaybackTerminated(PlaybackEndReason.Failed, error);
            device.MasterMixer.AddComponent(_player);
        }
        catch { ReleaseProviderOwnership(); throw; }
    }
    public override AudioFormat RenderedFormat => _player.Format;
    public override Processing.Chain? ProcessingChain => _player.ProcessingChain;
    public override void ConfigureProcessing(Processing.Chain? chain) => _player.ConfigureProcessing(chain);
    public override PlaybackState State => _player.State;
    public override IDataProvider DataProvider => _provider;
    public override bool IsLooping { get => _player.IsLooping; set => _player.IsLooping = value; }
    public override float PlaybackSpeed { get => _player.PlaybackSpeed; set => _player.PlaybackSpeed = value; }
    public override float Volume { get => _player.Volume; set => _player.Volume = value; }
    public override float Time => _player.Time;
    public override float Duration => _player.Duration;
    public override int LoopStartSamples => ToInput(_player.LoopStartSamples);
    public override int LoopEndSamples => _player.LoopEndSamples < 0 ? -1 : ToInput(_player.LoopEndSamples);
    public override void Play() { ObjectDisposedException.ThrowIf(IsDisposed, this); BeginPlayback(); _player.Play(); }
    public override void Pause() { ObjectDisposedException.ThrowIf(IsDisposed, this); _player.Pause(); }
    public override void Stop() { ObjectDisposedException.ThrowIf(IsDisposed, this); _player.Stop(); RaisePlaybackTerminated(PlaybackEndReason.Stopped); }
    public override bool Seek(int sampleOffset) => _player.Seek(ToOutput(sampleOffset));
    public override void SetLoopPoints(int startSample, int endSample = -1) => _player.SetLoopPoints(ToOutput(startSample), endSample < 0 ? -1 : ToOutput(endSample));
    public override void AddModifier(SoundModifier modifier) => _player.AddModifier(modifier);
    public override void RemoveModifier(SoundModifier modifier) => _player.RemoveModifier(modifier);
    protected internal override bool CanTransferTo(PlaybackDevice device) => !IsDisposed && device.Format == Device.Format && device.Engine == Device.Engine;
    protected internal override void RebindDevice(PlaybackDevice device)
    {
        if (device.Format != Device.Format || device.Engine != Device.Engine)
            throw new ArgumentException("A software source transfer requires the same engine and output format.", nameof(device));
        Device.MasterMixer.RemoveComponent(_player);
        device.MasterMixer.AddComponent(_player);
        SetDeviceOwner(device);
    }
    protected override void DisposeSource()
    {
        if (IsDisposed) return;
        IsDisposed = true;
        Device.MasterMixer.RemoveComponent(_player);
        _player.Dispose();
    }

    private int ToOutput(int samples) => checked((int)((long)(samples / Format.Channels) * _player.InputFormat.SampleRate / Format.SampleRate * _player.InputFormat.Channels));
    private int ToInput(int samples) => checked((int)((long)(samples / _player.InputFormat.Channels) * Format.SampleRate / _player.InputFormat.SampleRate * Format.Channels));

    private sealed class BorrowedProvider(IDataProvider inner) : ICancellableDataProvider
    {
        public IDataProvider Inner => inner;
        public int Position => inner.Position;
        public int Length => inner.Length;
        public bool CanSeek => inner.CanSeek;
        public SampleFormat SampleFormat => inner.SampleFormat;
        public int SampleRate => inner.SampleRate;
        public bool IsDisposed => inner.IsDisposed;
        public Metadata.Models.SoundFormatInfo? FormatInfo => inner.FormatInfo;
        public event EventHandler<EventArgs> EndOfStreamReached { add => inner.EndOfStreamReached += value; remove => inner.EndOfStreamReached -= value; }
        public event EventHandler<PositionChangedEventArgs> PositionChanged { add => inner.PositionChanged += value; remove => inner.PositionChanged -= value; }
        public int ReadSamples(Span<float> buffer) => inner.ReadSamples(buffer);
        public int ReadSamples(Span<float> buffer, CancellationToken cancellationToken)
            => inner is ICancellableDataProvider cooperative ? cooperative.ReadSamples(buffer, cancellationToken) : inner.ReadSamples(buffer);
        public void Seek(int offset) => inner.Seek(offset);
        public void Dispose() { }
    }
}
