using SoundFlow.Providers;
using CorePlaybackDevice = SoundFlow.Devices.PlaybackDevice;
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

/// <summary>Physical playback adapter. Its context owns all native rendering and resource state.</summary>
internal sealed class PlaybackDevice : CorePlaybackDevice
{
    public override ProcessingStage AvailableProcessingStages => ProcessingStage.GraphOutput;
    private readonly Context _context;
    internal PlaybackDevice(Engine engine, DeviceInfo? info, AudioFormat format, DeviceConfiguration config) : base(engine, format, config)
    {
        Info = info; Capability = Capability.Playback;
        _context = new Context(engine, this, info, config, false, graphTap: (data, count) => PublishBuffer(data.AsSpan(0, count), ProcessingStage.GraphOutput));
        RegisterFeature<IScene>(_context);
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
    public override Source CreateSource(IDataProvider provider, AudioFormat format, bool leaveOpen = false) => _context.CreateSource(provider, format, leaveOpen);
    public override SpatialSource CreateSpatialSource(IDataProvider provider, AudioFormat format, bool leaveOpen = false)
        => (SpatialSource)_context.CreateSource(provider, format, leaveOpen, true);
    public override float MasterGain { get => _context.MasterGain; set => _context.MasterGain = value; }
    public override void Start() { ObjectDisposedException.ThrowIf(IsDisposed, this); _context.Start(); IsRunning = true; Engine.RaiseDeviceStarted(this); }
    public override void Stop() { ObjectDisposedException.ThrowIf(IsDisposed, this); _context.Stop(); IsRunning = false; Engine.RaiseDeviceStopped(this); }
    internal void Reopen(DeviceInfo info, DeviceConfiguration? config) { _context.Reopen(info, config); Info = info; }
    internal void ValidateDisposal() { ProcessingScope.ThrowIfProcessing(); _context.EnsureOutsideBatch(); }
    public override void Dispose()
    {
        ProcessingScope.ThrowIfProcessing();
        _context.EnsureOutsideBatch();
        if (IsDisposed) return;
        IsDisposed = true;
        try { DisposeSources(); }
        finally { _context.Dispose(); MasterMixer.Dispose(); IsRunning = false; IsDisposed = true; OnDisposedHandler(); }
    }
}
