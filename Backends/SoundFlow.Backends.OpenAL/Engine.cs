using CaptureDevice = SoundFlow.Backends.OpenAL.Devices.CaptureDevice;
using CoreCaptureDevice = SoundFlow.Devices.CaptureDevice;
using CoreEngine = SoundFlow.Engine;
using CorePlaybackDevice = SoundFlow.Devices.PlaybackDevice;
using CoreRenderDevice = SoundFlow.Devices.RenderDevice;
using Device = SoundFlow.Devices.Device;
using CoreDeviceConfiguration = SoundFlow.Devices.DeviceConfiguration;
using Functions = SoundFlow.Backends.OpenAL.Interop.Functions;
using PlaybackDevice = SoundFlow.Backends.OpenAL.Devices.PlaybackDevice;
using RenderDevice = SoundFlow.Backends.OpenAL.Devices.RenderDevice;
using StateTransfer = SoundFlow.Devices.StateTransfer;
using System.Runtime.InteropServices;
using System.Text;
using SoundFlow.Abstracts;
using SoundFlow.Devices;
using SoundFlow.Backends.OpenAL.Devices;
using SoundFlow.Codecs.FFMpeg;
using SoundFlow.Features;
using SoundFlow.Structs;

namespace SoundFlow.Backends.OpenAL;

/// <summary>OpenAL Soft engine with FFmpeg codecs registered by default. Creating the engine opens no playback or capture hardware.</summary>
/// <remarks>The engine owns its devices. Dispose sources/devices before the engine; no MiniAudio dependency or fallback is used.</remarks>
public sealed unsafe class Engine : CoreEngine, IOfflineRendering, INotifications
{
    internal Functions Native { get; }
    private readonly NotificationDispatcher _dispatcher;
    private readonly IDisposable? _systemEvents;
    /// <summary>Receives copied native state/device changes and processing failures on a managed notification thread.</summary>
    public event EventHandler<BackendNotification>? Notification;
    /// <summary>Diagnostic notifications dropped when their bounded queue was full. Completion events use a separate guaranteed-delivery path.</summary>
    public long DroppedNotifications => _dispatcher.Dropped;
    internal void Dispatch(Action callback, bool important = false) => _dispatcher.Post(callback, important);
    internal void Publish(BackendNotification notification) => Notification?.Invoke(this, notification);

    private readonly List<Device> _devices = new();
    private readonly object _devicesLock = new();
    private bool _disposing;
    private int _creatingDevices;

    /// <summary>Loads the bundled OpenAL Soft implementation, or an explicitly supplied library path, and registers FFmpeg.</summary>
    /// <param name="nativeLibraryPath">Optional absolute library path. The implementation must provide OpenAL Soft extensions.</param>
    public Engine(string? nativeLibraryPath = null)
    {
        Native = new Functions(nativeLibraryPath);
        _dispatcher = new NotificationDispatcher();
        try
        {
            RegisterCodecFactory(new FFmpegCodecFactory());
            RegisterFeature<INotifications>(this);
            _systemEvents = Interop.SystemEvents.Subscribe(Native, notification => Dispatch(() => Publish(notification)));
        }
        catch { _dispatcher.Dispose(); Native.Dispose(); throw; }
        if (Native.alcLoopbackOpenDeviceSOFT != null) RegisterFeature<IOfflineRendering>(this);
    }

    internal static DeviceConfiguration Configuration(CoreDeviceConfiguration? config)
    {
        if (config is not null and not DeviceConfiguration) throw new ArgumentException("Expected SoundFlow.Backends.OpenAL.DeviceConfiguration.", nameof(config));
        var result = (DeviceConfiguration?)config ?? new DeviceConfiguration();
        result.Validate();
        return result;
    }
    private T CreateDevice<T>(Func<T> factory) where T : Device
    {
        lock (_devicesLock)
        {
            if (_disposing || IsDisposed) throw new ObjectDisposedException(nameof(Engine));
            _creatingDevices++;
        }
        try { return Track(factory()); }
        finally { lock (_devicesLock) { _creatingDevices--; Monitor.PulseAll(_devicesLock); } }
    }

    /// <inheritdoc />
    protected override void Dispose(bool disposing)
    {
        if (!disposing) { base.Dispose(false); return; }
        ProcessingScope.ThrowIfProcessing();
        lock (_devicesLock)
        {
            if (_disposing || IsDisposed) return;
            foreach (var device in _devices)
            {
                if (device is PlaybackDevice playback) playback.ValidateDisposal();
                if (device is RenderDevice render) render.ValidateDisposal();
            }
            _disposing = true;
            while (_creatingDevices != 0) Monitor.Wait(_devicesLock);
        }
        base.Dispose(true);
    }

    private T Track<T>(T device) where T : Device
    {
        lock (_devicesLock) _devices.Add(device);
        device.OnDisposed += (_, _) => { lock (_devicesLock) _devices.Remove(device); };
        return device;
    }

    /// <inheritdoc />
    public override CorePlaybackDevice InitializePlaybackDevice(DeviceInfo? deviceInfo, AudioFormat format, CoreDeviceConfiguration? config = null)
    {
        ObjectDisposedException.ThrowIf(IsDisposed, this);
        return CreateDevice(() => new PlaybackDevice(this, deviceInfo, format, Configuration(config)));
    }
    /// <inheritdoc />
    public CoreRenderDevice CreateRenderDevice(AudioFormat format, CoreDeviceConfiguration? config = null)
    {
        ObjectDisposedException.ThrowIf(IsDisposed, this);
        return CreateDevice(() => new RenderDevice(this, format, Configuration(config)));
    }
    /// <inheritdoc />
    public override CoreCaptureDevice InitializeCaptureDevice(DeviceInfo? deviceInfo, AudioFormat format, CoreDeviceConfiguration? config = null)
    {
        ObjectDisposedException.ThrowIf(IsDisposed, this);
        return CreateDevice(() => new CaptureDevice(this, deviceInfo, format, Configuration(config)));
    }
    /// <inheritdoc />
    public override FullDuplexDevice InitializeFullDuplexDevice(DeviceInfo? playbackDeviceInfo, DeviceInfo? captureDeviceInfo, AudioFormat format, CoreDeviceConfiguration? config = null)
    {
        ObjectDisposedException.ThrowIf(IsDisposed, this);
        return CreateDevice(() => new FullDuplexDevice(this, playbackDeviceInfo, captureDeviceInfo, format, Configuration(config)));
    }
    /// <inheritdoc />
    public override CoreCaptureDevice InitializeLoopbackDevice(AudioFormat format, CoreDeviceConfiguration? config = null)
        => throw new NotSupportedException("OpenAL loopback renders its own mix; it cannot capture desktop audio. Use IOfflineRendering for offline rendering.");
    /// <inheritdoc />
    public override CorePlaybackDevice SwitchDevice(CorePlaybackDevice oldDevice, DeviceInfo newDeviceInfo, CoreDeviceConfiguration? config = null)
    {
        if (oldDevice is not PlaybackDevice device || oldDevice.Engine != this)
            throw new ArgumentException("Expected a live playback device owned by this engine.", nameof(oldDevice));
        device.Reopen(newDeviceInfo, config is null ? null : Configuration(config));
        return oldDevice;
    }
    /// <inheritdoc />
    public override CoreCaptureDevice SwitchDevice(CoreCaptureDevice oldDevice, DeviceInfo newDeviceInfo, CoreDeviceConfiguration? config = null)
    {
        if (oldDevice.Engine != this) throw new ArgumentException("Device belongs to another engine.", nameof(oldDevice));
        var replacement = InitializeCaptureDevice(newDeviceInfo, oldDevice.Format, config ?? oldDevice.Config);
        try
        {
            StateTransfer.RestoreEventSubscribers(replacement, StateTransfer.PreserveEventSubscribers(oldDevice));
            if (oldDevice.IsRunning) replacement.Start();
        }
        catch { replacement.Dispose(); throw; }
        oldDevice.Dispose();
        return replacement;
    }
    /// <inheritdoc />
    public override FullDuplexDevice SwitchDevice(FullDuplexDevice oldDevice, DeviceInfo? newPlaybackInfo, DeviceInfo? newCaptureInfo, CoreDeviceConfiguration? config = null)
    {
        ObjectDisposedException.ThrowIf(oldDevice.IsDisposed, oldDevice);
        if (oldDevice.Engine != this || oldDevice.PlaybackDevice is not PlaybackDevice playback)
            throw new ArgumentException("The pair must belong to this OpenAL engine.", nameof(oldDevice));
        CoreCaptureDevice? replacement = null;
        try
        {
            if (newCaptureInfo.HasValue)
            {
                replacement = InitializeCaptureDevice(newCaptureInfo, oldDevice.Format, config ?? oldDevice.Config);
                StateTransfer.RestoreEventSubscribers(replacement, StateTransfer.PreserveEventSubscribers(oldDevice.CaptureDevice));
                if (oldDevice.IsRunning) replacement.Start();
            }
            if (newPlaybackInfo.HasValue) playback.Reopen(newPlaybackInfo.Value, config is null ? null : Configuration(config));
        }
        catch { replacement?.Dispose(); throw; }
        if (replacement != null)
        {
            var previous = oldDevice.CaptureDevice;
            oldDevice.ReplaceCapture(replacement);
            previous.Dispose();
        }
        return oldDevice;
    }

    /// <inheritdoc />
    public override void UpdateAudioDevicesInfo()
    {
        ObjectDisposedException.ThrowIf(IsDisposed, this);
        PlaybackDevices = Enumerate(0x1013, 0x1012, "playback");
        CaptureDevices = Enumerate(0x310, 0x311, "capture");
    }
    private DeviceInfo[] Enumerate(int listToken, int defaultToken, string direction)
    {
        var defaultName = Functions.Text(Native.alcGetString(0, defaultToken));
        var list = Native.alcGetString(0, listToken);
        if (list == null) return [];
        var devices = new List<DeviceInfo>();
        for (var current = list; *current != 0;)
        {
            var name = Functions.Text(current);
            devices.Add(new DeviceInfo { Id = $"openal:{direction}:{Uri.EscapeDataString(name)}", Name = name, IsDefault = name == defaultName, SupportedDataFormats = [] });
            current += Encoding.UTF8.GetByteCount(name) + 1;
        }
        return devices.ToArray();
    }
    internal static byte[]? DeviceName(DeviceInfo? info, string direction)
    {
        if (info is null) return null;
        if (info.Value.Id is null || !info.Value.Id.StartsWith($"openal:{direction}:", StringComparison.Ordinal))
            throw new ArgumentException("Device identifier belongs to another backend or direction.", nameof(info));
        return Encoding.UTF8.GetBytes(Uri.UnescapeDataString(info.Value.Id[$"openal:{direction}:".Length..]) + "\0");
    }
    /// <inheritdoc />
    protected override void CleanupBackend()
    {
        Device[] devices;
        lock (_devicesLock) devices = _devices.ToArray();
        List<Exception>? failures = null;
        try
        {
            foreach (var device in devices)
                try { device.Dispose(); } catch (Exception error) { (failures ??= []).Add(error); }
        }
        finally { _systemEvents?.Dispose(); _dispatcher.Dispose(); Native.Dispose(); }
        if (failures != null) throw new AggregateException("One or more OpenAL resources failed to dispose.", failures);
    }
}
