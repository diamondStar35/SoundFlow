using Native = SoundFlow.Backends.MiniAudio.Interop.Functions;
using NativeWasapiConfiguration = SoundFlow.Backends.MiniAudio.Interop.WasapiConfiguration;
using NativePulseConfiguration = SoundFlow.Backends.MiniAudio.Interop.PulseConfiguration;
using NativeOpenSlConfiguration = SoundFlow.Backends.MiniAudio.Interop.OpenSlConfiguration;
using NativeEndpointConfiguration = SoundFlow.Backends.MiniAudio.Interop.EndpointConfiguration;
using NativeDeviceConfiguration = SoundFlow.Backends.MiniAudio.Interop.DeviceConfiguration;
using NativeCoreAudioConfiguration = SoundFlow.Backends.MiniAudio.Interop.CoreAudioConfiguration;
using NativeAlsaConfiguration = SoundFlow.Backends.MiniAudio.Interop.AlsaConfiguration;
using NativeAAudioConfiguration = SoundFlow.Backends.MiniAudio.Interop.AAudioConfiguration;
using CoreCaptureDevice = SoundFlow.Devices.CaptureDevice;
using CoreDeviceConfiguration = SoundFlow.Devices.DeviceConfiguration;
using Device = SoundFlow.Devices.Device;
using DeviceConfiguration = SoundFlow.Backends.MiniAudio.DeviceConfiguration;
using Engine = SoundFlow.Backends.MiniAudio.Engine;
using Result = SoundFlow.Backends.MiniAudio.Enums.Result;
using System;
using System.Runtime.InteropServices;
using SoundFlow.Devices;
using SoundFlow.Backends.MiniAudio.Enums;
using SoundFlow.Enums;
using SoundFlow.Structs;

namespace SoundFlow.Backends.MiniAudio.Devices;

internal delegate void OnProcessCallback(nint pOutput, nint pInput, uint frameCount, NativeDevice device);

internal sealed class NativeDevice : IDisposable
{
    private readonly nint _device;
    private readonly OnProcessCallback _onProcess;

    public DeviceInfo? Info { get; }
    public Capability Capability { get; }
    public AudioFormat Format { get; }
    public Engine Engine { get; }

    public NativeDevice(Device owner, nint context, DeviceInfo? info, AudioFormat format, CoreDeviceConfiguration config,
        OnProcessCallback onProcess)
    {
        if (config is not DeviceConfiguration miniAudioDeviceConfig)
            throw new ArgumentException($"config must be of type {typeof(SoundFlow.Backends.MiniAudio.DeviceConfiguration)}");

        Info = info;
        Format = format;
        _onProcess = onProcess;
        Engine = (Engine)owner.Engine;

        Capability = owner is CoreCaptureDevice
            ? miniAudioDeviceConfig is { Capture.IsLoopback: true } ? Capability.Loopback : Capability.Record
            : Capability.Playback;

        var configHandles = new List<nint>();

        try
        {
            var pSfConfig = MarshalConfig(
                miniAudioDeviceConfig, configHandles);

            var deviceConfig = Native.AllocateDeviceConfig(
                Capability,
                (uint)Format.SampleRate,
                Engine.DataCallback,
                pSfConfig
            );

            _device = Native.AllocateDevice();
            var result = Native.DeviceInit(context, deviceConfig, _device);
            Native.Free(deviceConfig);

            if (result != Result.Success)
            {
                Native.Free(_device);
                throw new InvalidOperationException($"Unable to init device {info?.Name ?? "Default Device"}. Result: {result}");
            }

            Engine.RegisterEngineHandle(_device, Engine);
        }
        finally
        {
            // Free all marshaled memory
            foreach (var handle in configHandles)
            {
                Marshal.FreeHGlobal(handle);
            }
        }

        Engine.RegisterDevice(_device, this);
    }

    private nint MarshalConfig(DeviceConfiguration maConfig, List<nint> handles)
    {
        var mainDto = new NativeDeviceConfiguration
        {
            PeriodSizeInFrames = maConfig.PeriodSizeInFrames,
            PeriodSizeInMilliseconds = maConfig.PeriodSizeInMilliseconds,
            Periods = maConfig.Periods,
            NoPreSilencedOutputBuffer = maConfig.NoPreSilencedOutputBuffer,
            NoClip = maConfig.NoClip,
            NoDisableDenormals = maConfig.NoDisableDenormals,
            NoFixedSizedCallback = maConfig.NoFixedSizedCallback,
            Playback = MarshalStruct(new NativeEndpointConfiguration
            {
                Format = Format.Format,
                Channels = (uint)Format.Channels,
                pDeviceID = Engine.ResolveDeviceId(Info),
                ShareMode = maConfig.Playback.ShareMode
            }, handles),
            Capture = MarshalStruct(new NativeEndpointConfiguration
            {
                Format = Format.Format,
                Channels = (uint)Format.Channels,
                pDeviceID = Engine.ResolveDeviceId(Info),
                ShareMode = maConfig.Capture.ShareMode
            }, handles)
        };

        if (maConfig.Wasapi != null)
        {
            var dto = new NativeWasapiConfiguration
            {
                Usage = maConfig.Wasapi.Usage,
                NoAutoConvertSRC = maConfig.Wasapi.NoAutoConvertSRC,
                NoDefaultQualitySRC = maConfig.Wasapi.NoDefaultQualitySRC,
                NoAutoStreamRouting = maConfig.Wasapi.NoAutoStreamRouting,
                NoHardwareOffloading = maConfig.Wasapi.NoHardwareOffloading
            };
            mainDto.Wasapi = MarshalStruct(dto, handles);
        }

        if (maConfig.CoreAudio != null)
        {
            mainDto.CoreAudio =
                MarshalStruct(new NativeCoreAudioConfiguration
                {
                    AllowNominalSampleRateChange = (uint)(maConfig.CoreAudio.AllowNominalSampleRateChange ? 1 : 0)
                }, handles);
        }

        if (maConfig.Alsa != null)
        {
            mainDto.Alsa = MarshalStruct(new NativeAlsaConfiguration
            {
                NoMMap = ToUInt(maConfig.Alsa.NoMMap),
                NoAutoFormat = ToUInt(maConfig.Alsa.NoAutoFormat),
                NoAutoChannels = ToUInt(maConfig.Alsa.NoAutoChannels),
                NoAutoResample = ToUInt(maConfig.Alsa.NoAutoResample)
            }, handles);
        }

        if (maConfig.Pulse != null)
        {
            mainDto.Pulse = MarshalStruct(new NativePulseConfiguration
            {
                pStreamNamePlayback = Marshal.StringToHGlobalAnsi(maConfig.Pulse.StreamNamePlayback),
                pStreamNameCapture = Marshal.StringToHGlobalAnsi(maConfig.Pulse.StreamNameCapture)
            }, handles);
        }

        if (maConfig.OpenSL != null)
        {
            mainDto.OpenSL = MarshalStruct(new NativeOpenSlConfiguration
            {
                StreamType = maConfig.OpenSL.StreamType,
                RecordingPreset = maConfig.OpenSL.RecordingPreset
            }, handles);
        }

        if (maConfig.AAudio != null)
        {
            mainDto.AAudio = MarshalStruct(
                new NativeAAudioConfiguration
                {
                    Usage = maConfig.AAudio.Usage,
                    ContentType = maConfig.AAudio.ContentType,
                    InputPreset = maConfig.AAudio.InputPreset,
                    AllowedCapturePolicy = maConfig.AAudio.AllowedCapturePolicy
                }, handles);
        }

        return MarshalStruct(mainDto, handles);
    }

    private static nint MarshalStruct<T>(T structure, List<nint> handles) where T : struct
    {
        var ptr = Marshal.AllocHGlobal(Marshal.SizeOf<T>());
        Marshal.StructureToPtr(structure, ptr, false);
        handles.Add(ptr);
        return ptr;
    }

    private static uint ToUInt(bool value) => (uint)(value ? 1 : 0);


    public void Start()
    {
        var result = Native.DeviceStart(_device);
        if (result != Result.Success)
            throw new InvalidOperationException($"Unable to start MiniAudio {Capability} device {Info?.Name ?? "Default Device"}. Result: {result}");
    }

    public void Stop()
    {
        var result = Native.DeviceStop(_device);
        if (result == Result.Success
            || result == Result.DeviceNotStarted
            || result == Result.DeviceNotInitialized)
        {
            return;
        }

        throw new InvalidOperationException($"Unable to stop MiniAudio {Capability} device {Info?.Name ?? "Default Device"}. Result: {result}");
    }

    public void Process(nint pOutput, nint pInput, uint frameCount)
    {
        _onProcess(pOutput, pInput, frameCount, this);
    }

    public void Dispose()
    {
        Stop();

        // Remove the device from the engine's instance map.
        Engine.UnregisterDevice(_device);
        Engine.UnregisterEngineHandle(_device);

        Native.DeviceUninit(_device);
        Native.Free(_device);
    }
}
