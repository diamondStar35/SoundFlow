using SoundFlow.Providers;
using DeviceConfiguration = SoundFlow.Devices.DeviceConfiguration;
using Engine = SoundFlow.Engine;
using PlaybackDevice = SoundFlow.Devices.PlaybackDevice;
using SoundModifier = SoundFlow.Abstracts.SoundModifier;
using SoundFlow.Effects;
using SoundFlow.Enums;
using SoundFlow.Features;
using SoundFlow.Interfaces;
using SoundFlow.Playback;
using SoundFlow.Structs;

namespace BackendContract;

/// <summary>An independent adapter compiled without core internals or a native-backend reference.</summary>
public sealed class Device : PlaybackDevice, IEffects
{
    public Device(Engine engine, AudioFormat format) : base(engine, format, new Configuration()) => RegisterFeature<IEffects>(this);
    public Source CreateSoftwareSource(IDataProvider provider, AudioFormat format, bool leaveOpen = false) => base.CreateSource(provider, format, leaveOpen);
    public override Source CreateSource(IDataProvider provider, AudioFormat format, bool leaveOpen = false) => new Voice(this, provider, format, leaveOpen);
    public override void Start() => IsRunning = true;
    public override void Stop() => IsRunning = false;
    public override void Dispose() { if (IsDisposed) return; DisposeSources(); MasterMixer.Dispose(); IsDisposed = true; OnDisposedHandler(); }
    public IReadOnlyCollection<Type> SupportedEffects => new[] { typeof(CustomSettings) };
    public int MaximumSends => 0;
    public bool SupportsSlotChaining => false;
    public Slot CreateSlot(Settings settings)
    {
        if (settings is not CustomSettings custom) throw new NotSupportedException();
        custom.Validate();
        return new CustomSlot(this, custom);
    }
    private sealed class Configuration : DeviceConfiguration;

    /// <summary>A third-party source reusing the transport and lifetime template entirely through public/protected members.</summary>
    private sealed class Voice : Source
    {
        private readonly Transport _transport;
        public Voice(Device owner, IDataProvider provider, AudioFormat format, bool leaveOpen) : base(owner, provider, format)
        {
            try { _transport = new Transport(format, provider, leaveOpen); }
            catch { ReleaseProviderOwnership(); throw; }
        }
        public override PlaybackState State => _transport.State;
        public override IDataProvider DataProvider => _transport.DataProvider;
        public override bool IsLooping { get => _transport.IsLooping; set => _transport.IsLooping = value; }
        public override float PlaybackSpeed { get => _transport.PlaybackSpeed; set => _transport.PlaybackSpeed = value; }
        public override float Volume { get; set; } = 1;
        public override float Time => _transport.Time;
        public override float Duration => _transport.Duration;
        public override int LoopStartSamples => _transport.LoopStartSamples;
        public override int LoopEndSamples => _transport.LoopEndSamples;
        public override void Play() => _transport.Play();
        public override void Pause() => _transport.Pause();
        public override void Stop() => _transport.Stop();
        public override bool Seek(int offset) => _transport.Seek(offset);
        public override void SetLoopPoints(int start, int end = -1) => _transport.SetLoopPoints(start, end);
        public override void AddModifier(SoundModifier modifier) => throw new NotSupportedException();
        public override void RemoveModifier(SoundModifier modifier) => throw new NotSupportedException();
        protected override void DisposeSource() => _transport.Dispose();

        internal ReadResult Read(Span<float> samples)
        {
            var result = _transport.Read(samples);
            PublishBuffer(samples[..result.Samples]);
            return result;
        }
    }

    /// <summary>Exposes a deterministic rendering step for the contract test.</summary>
    public ReadResult Render(Source source, Span<float> samples)
    {
        if (source.Device != this || source is not Voice voice) throw new ArgumentException("Foreign source", nameof(source));
        var result = voice.Read(samples);
        PublishBuffer(samples, ProcessingStage.FinalOutput);
        NotifyFramesRendered(samples.Length / Format.Channels);
        return result;
    }
}
