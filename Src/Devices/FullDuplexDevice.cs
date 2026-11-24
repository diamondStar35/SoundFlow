using Engine = SoundFlow.Engine;
using SoundFlow.Components;
using SoundFlow.Structs;

namespace SoundFlow.Devices;

/// <summary>
/// A high-level abstraction that manages a paired playback and capture device for
/// easy full-duplex (simultaneous input and output) operation.
/// Ideal for live effects processing, VoIP, or instrument monitoring.
/// </summary>
public sealed class FullDuplexDevice : Device, IDisposable
{
    /// <summary>
    /// Gets the underlying playback device.
    /// </summary>
    public PlaybackDevice PlaybackDevice { get; }

    /// <summary>
    /// Gets the underlying capture device.
    /// </summary>
    public CaptureDevice CaptureDevice { get; private set; }

    /// <summary>
    /// Gets the master mixer for the playback device. You can add other components
    /// (e.g., music players, synthesizers) to this mixer to play them.
    /// </summary>
    public Mixer MasterMixer => PlaybackDevice.MasterMixer;

    /// <summary>
    /// Occurs when audio data is processed by either the capture or playback device.
    /// Subscribers will receive both near-end (capture) and far-end (playback) frames.
    /// </summary>
    public new event AudioProcessCallback? OnAudioProcessed
    {
        add
        {
            CaptureDevice.OnAudioProcessed += value;
            PlaybackDevice.OnAudioProcessed += value;
        }
        remove
        {
            CaptureDevice.OnAudioProcessed -= value;
            PlaybackDevice.OnAudioProcessed -= value;
        }
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="FullDuplexDevice"/> class.
    /// </summary>
    /// <param name="engine">The audio engine to use.</param>
    /// <param name="playbackDeviceInfo">The device information for the playback device.</param>
    /// <param name="captureDeviceInfo">The device information for the capture device.</param>
    /// <param name="format">The audio format to use.</param>
    /// <param name="config">The device configuration to use.</param>
    public FullDuplexDevice(Engine engine, DeviceInfo? playbackDeviceInfo, DeviceInfo? captureDeviceInfo, AudioFormat format, DeviceConfiguration config) : base(engine, format, config)
    {
        PlaybackDevice = engine.InitializePlaybackDevice(playbackDeviceInfo, format, config);
        try { CaptureDevice = engine.InitializeCaptureDevice(captureDeviceInfo, format, config); }
        catch { PlaybackDevice.Dispose(); throw; }
        Capability = Enums.Capability.Mixed;
    }

    /// <summary>Processing stages available from the paired devices; the two hardware clocks are independent.</summary>
    public override Features.ProcessingStage AvailableProcessingStages => PlaybackDevice.AvailableProcessingStages | CaptureDevice.AvailableProcessingStages;

    /// <summary>Subscribes to stage-labelled PCM from both devices.</summary>
    public new event Features.BufferCallback? AudioBufferProcessed
    {
        add { PlaybackDevice.AudioBufferProcessed += value; CaptureDevice.AudioBufferProcessed += value; }
        remove { PlaybackDevice.AudioBufferProcessed -= value; CaptureDevice.AudioBufferProcessed -= value; }
    }

    /// <summary>Replaces the capture half after the backend has prepared it. The caller owns cleanup of the previous capture device.</summary>
    /// <remarks>The replacement must be live and belong to this engine. Preserve processing subscriptions before calling this method.</remarks>
    public void ReplaceCapture(CaptureDevice capture)
    {
        ObjectDisposedException.ThrowIf(IsDisposed, this);
        ArgumentNullException.ThrowIfNull(capture);
        ObjectDisposedException.ThrowIf(capture.IsDisposed, capture);
        if (capture.Engine != Engine) throw new ArgumentException("Capture belongs to another engine.", nameof(capture));
        CaptureDevice = capture;
    }

    /// <summary>
    /// Starts both the capture and playback devices.
    /// </summary>
    public override void Start()
    {
        if (IsRunning || IsDisposed) return;
        CaptureDevice.Start();
        try { PlaybackDevice.Start(); }
        catch { CaptureDevice.Stop(); throw; }
        IsRunning = true;
    }

    /// <summary>
    /// Stops both the capture and playback devices.
    /// </summary>
    public override void Stop()
    {
        if (!IsRunning || IsDisposed) return;
        List<Exception>? failures = null;
        try { PlaybackDevice.Stop(); } catch (Exception error) { (failures ??= []).Add(error); }
        try { CaptureDevice.Stop(); } catch (Exception error) { (failures ??= []).Add(error); }
        IsRunning = false;
        if (failures != null) throw new AggregateException("Stopping the paired devices failed.", failures);
    }

    /// <summary>
    /// Disposes of all resources, including the underlying capture and playback devices.
    /// </summary>
    public override void Dispose()
    {
        if (IsDisposed) return;

        List<Exception>? failures = null;
        try { Stop(); } catch (Exception error) { (failures ??= []).Add(error); }
        try { PlaybackDevice.Dispose(); } catch (Exception error) { (failures ??= []).Add(error); }
        try { CaptureDevice.Dispose(); } catch (Exception error) { (failures ??= []).Add(error); }
        IsRunning = false;
        IsDisposed = true;
        OnDisposedHandler();
        if (failures != null) throw new AggregateException("Releasing the paired devices failed.", failures);
    }

    /// <inheritdoc />
    internal override Delegate[] GetEventSubscribers()
    {
        // Subscribers are routed to the child devices, so we retrieve them from there.
        // Returning CaptureDevice's list is sufficient as both will have the same subscribers.
        return CaptureDevice.GetEventSubscribers();
    }
}
