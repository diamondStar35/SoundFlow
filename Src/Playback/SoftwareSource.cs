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
    private readonly BufferTap _tap;
    public SoftwareSource(PlaybackDevice device, IDataProvider provider, AudioFormat format, bool leaveOpen)
        : base(device, provider, format)
    {
        try
        {
            _provider = provider;
            IDataProvider input = leaveOpen ? new BorrowedProvider(provider) : provider;
            if (format.Channels != device.Format.Channels || format.SampleRate != device.Format.SampleRate)
                input = new ConvertedDataProvider(input, format, device.Format);
            _player = new SoundPlayer(device.Engine, device.Format, input);
            _tap = new BufferTap(this);
            _player.AddModifier(_tap);
            _player.PlaybackEnded += (_, _) => RaisePlaybackEnded();
            device.MasterMixer.AddComponent(_player);
        }
        catch { ReleaseProviderOwnership(); throw; }
    }
    public override PlaybackState State => _player.State;
    public override IDataProvider DataProvider => _provider;
    public override bool IsLooping { get => _player.IsLooping; set => _player.IsLooping = value; }
    public override float PlaybackSpeed { get => _player.PlaybackSpeed; set => _player.PlaybackSpeed = value; }
    public override float Volume { get => _player.Volume; set => _player.Volume = value; }
    public override float Time => _player.Time;
    public override float Duration => _player.Duration;
    public override int LoopStartSamples => ToInput(_player.LoopStartSamples);
    public override int LoopEndSamples => _player.LoopEndSamples < 0 ? -1 : ToInput(_player.LoopEndSamples);
    public override void Play() { ObjectDisposedException.ThrowIf(IsDisposed, this); _player.Play(); }
    public override void Pause() { ObjectDisposedException.ThrowIf(IsDisposed, this); _player.Pause(); }
    public override void Stop() { ObjectDisposedException.ThrowIf(IsDisposed, this); _player.Stop(); }
    public override bool Seek(int sampleOffset) => _player.Seek(ToOutput(sampleOffset));
    public override void SetLoopPoints(int startSample, int endSample = -1) => _player.SetLoopPoints(ToOutput(startSample), endSample < 0 ? -1 : ToOutput(endSample));
    public override void AddModifier(SoundModifier modifier)
    {
        _player.RemoveModifier(_tap); _player.AddModifier(modifier); _player.AddModifier(_tap);
    }
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

    private int ToOutput(int samples) => checked((int)((long)(samples / Format.Channels) * Device.Format.SampleRate / Format.SampleRate * Device.Format.Channels));
    private int ToInput(int samples) => checked((int)((long)(samples / Device.Format.Channels) * Format.SampleRate / Device.Format.SampleRate * Format.Channels));

    private sealed class BufferTap(SoftwareSource source) : SoundModifier
    {
        public override float ProcessSample(float sample, int channel) => sample;
        public override void Process(Span<float> buffer, int channels) => source.PublishBuffer(buffer, source.Device.Format);
    }

    private sealed class BorrowedProvider(IDataProvider inner) : IDataProvider
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
        public void Seek(int offset) => inner.Seek(offset);
        public void Dispose() { }
    }
}
