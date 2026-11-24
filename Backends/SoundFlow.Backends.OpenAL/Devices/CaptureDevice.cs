using SoundFlow.Features;
using BackendException = SoundFlow.Backends.OpenAL.BackendException;
using CoreCaptureDevice = SoundFlow.Devices.CaptureDevice;
using DeviceConfiguration = SoundFlow.Backends.OpenAL.DeviceConfiguration;
using Engine = SoundFlow.Backends.OpenAL.Engine;
using Functions = SoundFlow.Backends.OpenAL.Interop.Functions;
using SoundFlow.Devices;
using SoundFlow.Backends.OpenAL.Devices;
using SoundFlow.Enums;
using SoundFlow.Structs;

namespace SoundFlow.Backends.OpenAL.Devices;

/// <summary>Polls native capture on its own worker and publishes converted F32 blocks. Capture/playback clocks are independent.</summary>
internal sealed unsafe class CaptureDevice : CoreCaptureDevice, INotifications
{
    private readonly Functions _al;
    private readonly SoundFlow.Backends.OpenAL.Engine _engine;
    public event EventHandler<BackendNotification>? Notification;
    public long DroppedNotifications => _engine.DroppedNotifications;
    private readonly ContextWorker _worker;
    private nint _device;
    private readonly short[] _native;
    private readonly float[] _pcm;
    internal CaptureDevice(Engine engine, DeviceInfo? info, AudioFormat format, DeviceConfiguration config)
        : base(engine, format with { Format = SampleFormat.F32 }, config)
    {
        if (format.Channels is not (1 or 2) || format.SampleRate <= 0) throw new ArgumentException("Capture supports positive-rate mono/stereo PCM.", nameof(format));
        _engine = engine;
        _al = engine.Native; Info = info; Capability = Capability.Record;
        _native = new short[config.BufferFrames * format.Channels];
        _pcm = new float[_native.Length];
        _worker = new ContextWorker(() =>
        {
            var name = global::SoundFlow.Backends.OpenAL.Engine.DeviceName(info, "capture");
            fixed (byte* pointer = name)
                _device = _al.alcCaptureOpenDevice(pointer, (uint)format.SampleRate, format.Channels == 1 ? 0x1101 : 0x1103, config.BufferFrames * config.QueueBuffers);
            if (_device == 0) throw new BackendException("Open capture", _al.alcGetError(0), _al.LibraryPath);
        }, Capture, () => { if (_device != 0) { _al.alcCaptureCloseDevice(_device); _device = 0; } }, CaptureFailed);
        RegisterFeature<INotifications>(this);
    }
    private void CaptureFailed(Exception error)
    {
        _al.alcCaptureStop(_device);
        _ = _al.alcGetError(_device);
        IsRunning = false;
        var notification = new BackendNotification { Kind = NotificationKind.ProcessingFailure, Message = error.Message, Device = this };
        _engine.Dispatch(() => { if (IsDisposed) return; Notification?.Invoke(this, notification); _engine.Publish(notification); });
    }

    private void Capture()
    {
        if (!IsRunning) return;
        var frames = _al.DeviceInteger(_device, 0x312);
        while (frames >= _native.Length / Format.Channels)
        {
            fixed (short* pointer = _native) _al.alcCaptureSamples(_device, pointer, _native.Length / Format.Channels);
            _al.CheckAlc(_device, "Capture PCM");
            for (var i = 0; i < _native.Length; i++) _pcm[i] = _native[i] / 32768f;
            using (ProcessingScope.Enter()) InvokeOnAudioProcessed(_pcm);
            frames -= _native.Length / Format.Channels;
        }
    }
    public override void Start()
    {
        ObjectDisposedException.ThrowIf(IsDisposed, this);
        _worker.Invoke(() => { if (IsRunning) return; _al.alcCaptureStart(_device); _al.CheckAlc(_device, "Start capture"); IsRunning = true; });
        Engine.RaiseDeviceStarted(this);
    }
    public override void Stop()
    {
        ObjectDisposedException.ThrowIf(IsDisposed, this);
        _worker.Invoke(() => { if (!IsRunning) return; _al.alcCaptureStop(_device); _al.CheckAlc(_device, "Stop capture"); IsRunning = false; });
        Engine.RaiseDeviceStopped(this);
    }
    public override void Dispose()
    {
        ProcessingScope.ThrowIfProcessing();
        if (IsDisposed) return;
        _worker.Dispose(); IsRunning = false; IsDisposed = true; OnDisposedHandler();
    }
}
