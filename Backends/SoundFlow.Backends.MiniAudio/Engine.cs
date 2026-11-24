using Native = SoundFlow.Backends.MiniAudio.Interop.Functions;
using InteropDataFormat = SoundFlow.Backends.MiniAudio.Interop.DataFormat;
using NativeDeviceInfo = SoundFlow.Backends.MiniAudio.Interop.DeviceInfo;
using Backend = SoundFlow.Backends.MiniAudio.Enums.Backend;
using CaptureDevice = SoundFlow.Backends.MiniAudio.Devices.CaptureDevice;
using CoreCaptureDevice = SoundFlow.Devices.CaptureDevice;
using CoreEngine = SoundFlow.Engine;
using CorePlaybackDevice = SoundFlow.Devices.PlaybackDevice;
using Device = SoundFlow.Devices.Device;
using CoreDeviceConfiguration = SoundFlow.Devices.DeviceConfiguration;
using NativeDevice = SoundFlow.Backends.MiniAudio.Devices.NativeDevice;
using PlaybackDevice = SoundFlow.Backends.MiniAudio.Devices.PlaybackDevice;
using Result = SoundFlow.Backends.MiniAudio.Enums.Result;
using StateTransfer = SoundFlow.Devices.StateTransfer;
using SoundFlow.Abstracts;
using SoundFlow.Structs;
using SoundFlow.Utils;
using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using System.Text;
using SoundFlow.Devices;
using SoundFlow.Backends.MiniAudio.Devices;
using SoundFlow.Backends.MiniAudio.Enums;

namespace SoundFlow.Backends.MiniAudio;

/// <summary>
/// An audio engine based on the MiniAudio library.
/// </summary>
public class Engine : CoreEngine
{
    private nint _context;
    private readonly Dictionary<string, nint> _deviceIds = new();

    internal nint ResolveDeviceId(DeviceInfo? info)
    {
        if (info is null) return 0;
        if (!_deviceIds.TryGetValue(info.Value.Id, out var id)) throw new ArgumentException("Device does not belong to this MiniAudio enumeration.", nameof(info));
        return id;
    }
    private readonly List<Device> _activeDevices = [];
    private readonly Backend[]? _backendPriority;

    internal static readonly Native.AudioCallback DataCallback = OnAudioData;

    private readonly ConcurrentDictionary<nint, NativeDevice> _deviceMap = new();
    private static readonly ConcurrentDictionary<nint, GCHandle> ActiveEngineHandles = new();

    /// <summary>
    /// Gets a list of audio backends that are available on the current operating system.
    /// </summary>
    public static IReadOnlyList<Backend> AvailableBackends { get; }

    /// <summary>
    /// Gets the low-level audio backend that was successfully initialized and is currently active.
    /// </summary>
    /// <remarks>
    /// This property will be null if the user didn't pass the preferred backends array while creating the engine.
    /// In this case, MiniAudio will automatically select a backend and won't return it back.
    /// </remarks>
    public Backend ActiveBackend { get; private set; }

    static Engine()
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            AvailableBackends =
            [
                Backend.Wasapi,
                Backend.DirectSound,
                Backend.WinMm
            ];
        }
        else if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX) || OperatingSystem.IsMacCatalyst())
        {
            AvailableBackends =
            [
                Backend.CoreAudio,
                Backend.Jack
            ];
        }
        else if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
        {
            AvailableBackends =
            [
                Backend.Alsa,
                Backend.PulseAudio,
                Backend.Jack,
                Backend.Oss
            ];
        }
        else if (OperatingSystem.IsAndroid())
        {
            AvailableBackends =
            [
                Backend.AAudio,
                Backend.OpenSl
            ];
        }
        else if (OperatingSystem.IsIOS())
        {
            AvailableBackends = [Backend.CoreAudio];
        }
        else if (OperatingSystem.IsFreeBSD())
        {
            AvailableBackends =
            [
                Backend.Oss,
                Backend.Sndio,
                Backend.PulseAudio
            ];
        }
        else
        {
            AvailableBackends = [];
        }
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="SoundFlow.Backends.MiniAudio.Engine"/> class.
    /// </summary>
    /// <param name="backendPriority">
    /// An optional, ordered list of backends to try for initialization. If provided, MiniAudio will
    /// attempt to use them in the specified order. If null or empty, MiniAudio's default probing
    /// mechanism will be used.
    /// </param>
    public Engine(IEnumerable<Backend>? backendPriority = null)
    {
        _backendPriority = backendPriority?.ToArray();
        InitializeBackend();
    }

    private static void OnAudioData(nint pDevice, nint pOutput, nint pInput, uint frameCount)
    {
        // Look up the GCHandle using the native device pointer.
        if (!ActiveEngineHandles.TryGetValue(pDevice, out var engineHandle) ||
            engineHandle.Target is not Engine engine ||
            !engine._deviceMap.TryGetValue(pDevice, out var managedDevice)) return;

        // Safely get the engine instance from the handle.
        managedDevice.Process(pOutput, pInput, frameCount);
    }

    internal static void RegisterEngineHandle(nint pDevice, Engine engine)
    {
        var engineHandle = GCHandle.Alloc(engine);
        ActiveEngineHandles.TryAdd(pDevice, engineHandle);
    }

    internal static void UnregisterEngineHandle(nint pDevice)
    {
        if (ActiveEngineHandles.TryRemove(pDevice, out var engineHandle))
        {
            engineHandle.Free();
        }
    }

    internal void RegisterDevice(nint pDevice, NativeDevice device) => _deviceMap.TryAdd(pDevice, device);
    internal void UnregisterDevice(nint pDevice) => _deviceMap.TryRemove(pDevice, out _);

    /// <summary>
    /// Initializes the audio backend context.
    /// </summary>
    /// <exception cref="InvalidOperationException"></exception>
    private void InitializeBackend()
    {
        _context = Native.AllocateContext();

        var pBackends = nint.Zero;
        uint backendCount = 0;

        // If a specific backend priority list is provided, marshal it for the native call.
        if (_backendPriority is { Length: > 0 })
        {
            // Convert the C# enum array to a native integer array.
            var nativeBackends = _backendPriority.Select(b => (int)b).ToArray();
            backendCount = (uint)nativeBackends.Length;

            // Allocate unmanaged memory and copy the array to it.
            pBackends = Marshal.AllocHGlobal(sizeof(int) * nativeBackends.Length);
            Marshal.Copy(nativeBackends, 0, pBackends, nativeBackends.Length);
        }

        try
        {
            // Use the marshaled pointer and count in the native call.
            var result = Native.ContextInit(pBackends, backendCount, nint.Zero, _context);
            if (result != Result.Success)
                throw new InvalidOperationException($"Unable to init MiniAudio context. Result: {result}");

            // Query and store the active backend for user feedback.
            ActiveBackend = Native.ContextGetBackend(_context);
        }
        finally
        {
            if (pBackends != nint.Zero) Marshal.FreeHGlobal(pBackends);
        }

        UpdateAudioDevicesInfo();

        // Register the built-in codec factory for formats supported by MiniAudio.
        RegisterCodecFactory(new CodecFactory());
    }

    /// <inheritdoc />
    protected override void CleanupBackend()
    {
        foreach (var device in _activeDevices.ToList())
        {
            device.Dispose();
        }

        _activeDevices.Clear();

        Native.ContextUninit(_context);
        Native.Free(_context);
    }

    /// <inheritdoc />
    public override CorePlaybackDevice InitializePlaybackDevice(DeviceInfo? deviceInfo, AudioFormat format,
        CoreDeviceConfiguration? config = null)
    {
        if (config != null && config is not DeviceConfiguration)
            throw new ArgumentException($"config must be of type {typeof(SoundFlow.Backends.MiniAudio.DeviceConfiguration)}");

        config ??= GetDefaultDeviceConfig();
        var device = new PlaybackDevice(this, _context, deviceInfo, format, config);
        _activeDevices.Add(device);
        device.OnDisposed += OnDeviceDisposing;
        return device;
    }

    /// <inheritdoc />
    public override CoreCaptureDevice InitializeCaptureDevice(DeviceInfo? deviceInfo, AudioFormat format,
        CoreDeviceConfiguration? config = null)
    {
        if (config != null && config is not DeviceConfiguration)
            throw new ArgumentException($"config must be of type {typeof(SoundFlow.Backends.MiniAudio.DeviceConfiguration)}");

        config ??= GetDefaultDeviceConfig();
        var device = new CaptureDevice(this, _context, deviceInfo, format, config);
        _activeDevices.Add(device);
        device.OnDisposed += OnDeviceDisposing;
        return device;
    }

    /// <inheritdoc />
    public override FullDuplexDevice InitializeFullDuplexDevice(DeviceInfo? playbackDeviceInfo,
        DeviceInfo? captureDeviceInfo, AudioFormat format, CoreDeviceConfiguration? config = null)
    {
        if (config != null && config is not DeviceConfiguration)
            throw new ArgumentException($"config must be of type {typeof(SoundFlow.Backends.MiniAudio.DeviceConfiguration)}");

        config ??= GetDefaultDeviceConfig();
        var device = new FullDuplexDevice(this, playbackDeviceInfo, captureDeviceInfo, format, config);
        _activeDevices.Add(device);
        device.OnDisposed += OnDeviceDisposing;
        return device;
    }

    /// <inheritdoc />
    public override CoreCaptureDevice InitializeLoopbackDevice(AudioFormat format, CoreDeviceConfiguration? config = null)
    {
        if (config != null && config is not DeviceConfiguration)
            throw new ArgumentException($"config must be of type {typeof(SoundFlow.Backends.MiniAudio.DeviceConfiguration)}");

        // Loopback devices are only supported on WASAPI
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            throw new NotSupportedException("Loopback devices are only supported on Windows using WASAPI.");

        UpdateAudioDevicesInfo();

        // WASAPI loopback is achieved by using the default playback device in capture mode.
        var defaultPlaybackDevice = PlaybackDevices.FirstOrDefault(d => d.IsDefault);

        if (string.IsNullOrEmpty(defaultPlaybackDevice.Id))
            throw new NotSupportedException(
                "Could not find a default playback device to use for loopback recording. Ensure a default sound output device is set in your operating system.");

        config ??= GetDefaultDeviceConfig();

        ((DeviceConfiguration)config).Capture.IsLoopback = true;
        var device = InitializeCaptureDevice(defaultPlaybackDevice, format, config);
        return device;
    }

    /// <inheritdoc />
    public override CorePlaybackDevice SwitchDevice(CorePlaybackDevice oldDevice, DeviceInfo newDeviceInfo,
        CoreDeviceConfiguration? config = null)
    {
        if (oldDevice.Engine != this) throw new ArgumentException("Device belongs to another engine.", nameof(oldDevice));
        var wasRunning = oldDevice.IsRunning;
        var subscribers = StateTransfer.PreserveEventSubscribers(oldDevice);
        var newDevice = InitializePlaybackDevice(newDeviceInfo, oldDevice.Format, config ?? oldDevice.Config);
        IReadOnlyCollection<SoundComponent> components = [];
        try
        {
            if (wasRunning) oldDevice.Stop();
            oldDevice.TransferSourcesTo(newDevice);
            components = oldDevice.MasterMixer.Components;
            foreach (var component in components) oldDevice.MasterMixer.RemoveComponent(component);
            StateTransfer.RestorePlaybackState(newDevice, components);
            StateTransfer.RestoreEventSubscribers(newDevice, subscribers);
            if (wasRunning) newDevice.Start();
        }
        catch
        {
            newDevice.TransferSourcesTo(oldDevice);
            foreach (var component in components) newDevice.MasterMixer.RemoveComponent(component);
            StateTransfer.RestorePlaybackState(oldDevice, components);
            newDevice.Dispose();
            if (wasRunning && !oldDevice.IsRunning) oldDevice.Start();
            throw;
        }
        oldDevice.Dispose();
        return newDevice;
    }

    /// <inheritdoc />
    public override CoreCaptureDevice SwitchDevice(CoreCaptureDevice oldDevice, DeviceInfo newDeviceInfo,
        CoreDeviceConfiguration? config = null)
    {
        if (oldDevice.Engine != this) throw new ArgumentException("Device belongs to another engine.", nameof(oldDevice));
        var newDevice = InitializeCaptureDevice(newDeviceInfo, oldDevice.Format, config ?? oldDevice.Config);
        try
        {
            StateTransfer.RestoreEventSubscribers(newDevice, StateTransfer.PreserveEventSubscribers(oldDevice));
            if (oldDevice.IsRunning) newDevice.Start();
        }
        catch { newDevice.Dispose(); throw; }
        oldDevice.Dispose();
        return newDevice;
    }

    /// <inheritdoc />
    public override FullDuplexDevice SwitchDevice(FullDuplexDevice oldDevice, DeviceInfo? newPlaybackInfo,
        DeviceInfo? newCaptureInfo, CoreDeviceConfiguration? config = null)
    {
        if (oldDevice.Engine != this) throw new ArgumentException("Device belongs to another engine.", nameof(oldDevice));
        var wasRunning = oldDevice.IsRunning;
        var replacement = InitializeFullDuplexDevice(newPlaybackInfo ?? oldDevice.PlaybackDevice.Info,
            newCaptureInfo ?? oldDevice.CaptureDevice.Info, oldDevice.Format, config ?? oldDevice.Config);
        IReadOnlyCollection<SoundComponent> components = [];
        try
        {
            if (wasRunning) oldDevice.Stop();
            oldDevice.PlaybackDevice.TransferSourcesTo(replacement.PlaybackDevice);
            components = oldDevice.MasterMixer.Components;
            foreach (var component in components) oldDevice.MasterMixer.RemoveComponent(component);
            StateTransfer.RestorePlaybackState(replacement.PlaybackDevice, components);
            replacement.PlaybackDevice.MasterGain = oldDevice.PlaybackDevice.MasterGain;
            StateTransfer.RestoreEventSubscribers(replacement.PlaybackDevice, StateTransfer.PreserveEventSubscribers(oldDevice.PlaybackDevice));
            StateTransfer.RestoreEventSubscribers(replacement.CaptureDevice, StateTransfer.PreserveEventSubscribers(oldDevice.CaptureDevice));
            if (wasRunning) replacement.Start();
        }
        catch
        {
            replacement.PlaybackDevice.TransferSourcesTo(oldDevice.PlaybackDevice);
            foreach (var component in components) replacement.MasterMixer.RemoveComponent(component);
            StateTransfer.RestorePlaybackState(oldDevice.PlaybackDevice, components);
            replacement.Dispose();
            if (wasRunning && !oldDevice.IsRunning) oldDevice.Start();
            throw;
        }
        oldDevice.Dispose();
        return replacement;
    }

    private void OnDeviceDisposing(object? sender, EventArgs e)
    {
        if (sender is Device device)
        {
            _activeDevices.Remove(device);
        }
    }

    private static DeviceConfiguration GetDefaultDeviceConfig()
    {
        return new DeviceConfiguration
        {
            PeriodSizeInFrames = 960,
            Playback = new DeviceSubConfig
            {
                ShareMode = ShareMode.Shared
            },
            Capture = new DeviceSubConfig
            {
                ShareMode = ShareMode.Shared
            }
        };
    }

    /// <inheritdoc />
    public override void UpdateAudioDevicesInfo()
    {
        var result = Native.GetDevices(_context, out var pPlaybackDevices, out var pCaptureDevices,
            out var playbackCountUint, out var captureCountUint);

        if (result != Result.Success)
            throw new InvalidOperationException($"Unable to get devices. MiniAudio result: {result}");

        _deviceIds.Clear();
        var playbackCount = (int)playbackCountUint;
        var captureCount = (int)captureCountUint;

        try
        {
            // Marshal playback devices
            if (playbackCount > 0 && pPlaybackDevices != nint.Zero)
            {
                // 1. Create array for native structs
                var nativePlayback = new NativeDeviceInfo[playbackCount];
                // 2. Read from pointer into native structs
                pPlaybackDevices.ReadIntoArray(nativePlayback, playbackCount);

                // 3. Convert to public structs (Deep Copy)
                PlaybackDevices = new DeviceInfo[playbackCount];
                for (var i = 0; i < playbackCount; i++)
                {
                    PlaybackDevices[i] = ConvertFromNative(nativePlayback[i], "playback", i);
                }
            }
            else
            {
                PlaybackDevices = [];
            }

            // Marshal capture devices
            if (captureCount > 0 && pCaptureDevices != nint.Zero)
            {
                var nativeCapture = new NativeDeviceInfo[captureCount];
                pCaptureDevices.ReadIntoArray(nativeCapture, captureCount);

                CaptureDevices = new DeviceInfo[captureCount];
                for (var i = 0; i < captureCount; i++)
                {
                    CaptureDevices[i] = ConvertFromNative(nativeCapture[i], "capture", i);
                }
            }
            else
            {
                CaptureDevices = [];
            }
        }
        finally
        {
            // 4. Now it is safe to free native memory, as we have copied the data to managed arrays
            if (pPlaybackDevices != nint.Zero) Native.FreeDeviceInfos(pPlaybackDevices, playbackCountUint);
            if (pCaptureDevices != nint.Zero) Native.FreeDeviceInfos(pCaptureDevices, captureCountUint);
        }
    }

    private DeviceInfo ConvertFromNative(NativeDeviceInfo native, string direction, int index)
    {
        // Decode Name
        var name = string.Empty;
        if (native.NameBytes.Length > 0)
        {
            var count = Array.IndexOf(native.NameBytes, (byte)0);
            if (count == -1) count = native.NameBytes.Length;
            name = Encoding.UTF8.GetString(native.NameBytes, 0, count);
        }

        // Deep copy formats
        var formats = Array.Empty<NativeDataFormat>();
        if (native.NativeDataFormatCount > 0 && native.NativeDataFormats != IntPtr.Zero)
        {
            formats = native.NativeDataFormats.ReadArray<InteropDataFormat>((int)native.NativeDataFormatCount)
                .Select(f => new NativeDataFormat { Format = f.Format, Channels = f.Channels, SampleRate = f.SampleRate }).ToArray();
        }

        var id = $"miniaudio:{direction}:{index}:{name}";
        _deviceIds[id] = native.Id;
        return new DeviceInfo
        {
            Id = id,
            Name = name,
            IsDefault = native.IsDefault,
            SupportedDataFormats = formats
        };
    }
}
