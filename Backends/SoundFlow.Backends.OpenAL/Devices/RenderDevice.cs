using SoundFlow.Providers;
using CoreRenderDevice = SoundFlow.Devices.RenderDevice;
using DeviceConfiguration = SoundFlow.Backends.OpenAL.DeviceConfiguration;
using Engine = SoundFlow.Backends.OpenAL.Engine;
using IEffects = SoundFlow.Effects.IEffects;
using IScene = SoundFlow.Spatial.IScene;
using Source = SoundFlow.Playback.Source;
using SpatialSource = SoundFlow.Playback.SpatialSource;
using SoundFlow.Devices;
using SoundFlow.Backends.OpenAL.Devices;
using SoundFlow.Effects;
using SoundFlow.Enums;
using SoundFlow.Features;
using SoundFlow.Interfaces;
using SoundFlow.Playback;
using SoundFlow.Structs;

namespace SoundFlow.Backends.OpenAL.Devices;

/// <summary>Offline playback adapter. Only explicit Render calls advance its native device clock.</summary>
internal sealed class RenderDevice : CoreRenderDevice
{
    public override ProcessingStage AvailableProcessingStages => ProcessingStage.GraphOutput | ProcessingStage.FinalOutput;
    private readonly Context _context;
    private readonly object _renderGate = new();
    internal RenderDevice(Engine engine, AudioFormat format, DeviceConfiguration config) : base(engine, format, config)
    {
        Capability = Capability.Playback;
        _context = new Context(engine, this, null, config, true, graphTap: (data, count) => PublishBuffer(data.AsSpan(0, count), ProcessingStage.GraphOutput));
        RegisterFeature<IScene>(_context);
        RegisterFeature<IPreparedClips>(_context);
        if (_context.HasDebug) RegisterFeature<Features.IDebugOutput>(_context);
        RegisterFeature<IDiagnostics>(_context);
        RegisterFeature<Features.IDeviceRendering>(_context);
        RegisterFeature<INotifications>(_context);
        RegisterFeature<ISourceGroups>(_context);
        if (_context.HasTiming) RegisterFeature<IPlaybackTiming>(_context);
        if (_context.HasScheduling) RegisterFeature<IScheduledPlayback>(_context);
        if (_context.HasDeferredUpdates) RegisterFeature<IUpdateBatch>(_context);
        if (_context.HasEfx) RegisterFeature<IEffects>(_context);
        if (_context.HasHrtf) RegisterFeature<IHrtfAudio>(_context);
    }
    public override Source CreateSource(IDataProvider provider, AudioFormat format, SoundFlow.Playback.SourceOptions options, bool leaveOpen = false)
        => _context.CreateSource(provider, format, leaveOpen, options: options);
    public override SpatialSource CreateSpatialSource(IDataProvider provider, AudioFormat format, SoundFlow.Playback.SourceOptions options, bool leaveOpen = false)
        => (SpatialSource)_context.CreateSource(provider, format, leaveOpen, true, options);
    public override Source CreateSource(IDataProvider provider, AudioFormat format, bool leaveOpen = false) => _context.CreateSource(provider, format, leaveOpen);
    public override SpatialSource CreateSpatialSource(IDataProvider provider, AudioFormat format, bool leaveOpen = false)
        => (SpatialSource)_context.CreateSource(provider, format, leaveOpen, true);
    public override float MasterGain { get => _context.MasterGain; set => _context.MasterGain = value; }
    public override void Start() { ObjectDisposedException.ThrowIf(IsDisposed, this); IsRunning = true; }
    public override void Stop() { ObjectDisposedException.ThrowIf(IsDisposed, this); IsRunning = false; }
    public override void Render(Span<float> output)
    {
        ProcessingScope.ThrowIfProcessing();
        _context.EnsureOutsideBatch();
        lock (_renderGate)
        {
            ObjectDisposedException.ThrowIf(IsDisposed, this);
            _context.Render(output);
            using (ProcessingScope.Enter())
            {
                InvokeOnAudioProcessed(output);
                NotifyFramesRendered(output.Length / Format.Channels);
            }
        }
    }
    internal void ValidateDisposal() { ProcessingScope.ThrowIfProcessing(); _context.EnsureOutsideBatch(); }
    public override void Dispose()
    {
        ProcessingScope.ThrowIfProcessing();
        _context.EnsureOutsideBatch();
        lock (_renderGate)
        {
            _context.EnsureOutsideBatch();
            if (IsDisposed) return;
            IsDisposed = true;
            try { DisposeSources(); }
            finally { _context.Dispose(); MasterMixer.Dispose(); IsDisposed = true; OnDisposedHandler(); }
        }
    }
}
