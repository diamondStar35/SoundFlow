using BufferInfo = SoundFlow.Features.BufferInfo;
using Engine = SoundFlow.Engine;
using FeatureHost = SoundFlow.Features.FeatureHost;
using ProcessingStage = SoundFlow.Features.ProcessingStage;
using SampleFormat = SoundFlow.Enums.SampleFormat;
using SoundFlow.Enums;
using SoundFlow.Structs;

namespace SoundFlow.Devices;

/// <summary>
/// Represents an initialized audio device, managed by an <see cref="SoundFlow.Engine"/>.
/// </summary>
public abstract class Device : FeatureHost, IDisposable
{
    /// <summary>
    /// Gets the parent engine that manages this device.
    /// </summary>
    public Engine Engine { get; }

    /// <summary>
    /// Gets the informational struct for the physical device.
    /// </summary>
    public DeviceInfo? Info { get; protected set; }

    /// <summary>
    /// Gets the configuration struct for the physical device initialization process.
    /// </summary>
    public DeviceConfiguration Config { get; protected set; }

    /// <summary>
    /// Gets the capability of this device (e.g., Playback, Record).
    /// </summary>
    public Capability Capability { get; protected set; }

    /// <summary>
    /// Gets the audio format information (sample rate, channels, etc.) for this device.
    /// </summary>
    public AudioFormat Format { get; }

    /// <summary>
    /// Gets a value indicating whether the device is currently running.
    /// </summary>
    public bool IsRunning { get; protected set; }

    /// <summary>
    /// Gets a value indicating whether this device has been disposed.
    /// </summary>
    public bool IsDisposed { get; protected set; }

    /// <summary>
    /// Occurs when the audio device is disposed.
    /// </summary>
    public EventHandler? OnDisposed;

    /// <summary>
    /// Occurs when audio samples are processed by this device.
    /// For capture devices, this is raised when new audio is captured from the hardware.
    /// For playback devices, this is raised after the master mixer renders the final output buffer,
    /// just before it is sent to the hardware.
    /// </summary>
    public event AudioProcessCallback? OnAudioProcessed;

    /// <summary>PCM stages this device can publish. A native hardware mixer may not expose its final output.</summary>
    public virtual ProcessingStage AvailableProcessingStages =>
        (Capability & Capability.Record) != 0 ? ProcessingStage.Capture : ProcessingStage.FinalOutput;

    /// <summary>Publishes a borrowed PCM buffer with its exact processing stage and format.</summary>
    public event SoundFlow.Features.BufferCallback? AudioBufferProcessed;

    /// <summary>
    /// Initializes a new instance of the <see cref="SoundFlow.Devices.Device"/> class.
    /// </summary>
    /// <param name="engine">The parent audio engine.</param>
    /// <param name="format">The desired audio format.</param>
    /// <param name="config">The device configuration.</param>
    protected Device(Engine engine, AudioFormat format, DeviceConfiguration config)
    {
        Format = format;
        Engine = engine;
        Config = config;
    }

    /// <summary>
    /// Starts the audio device.
    /// </summary>
    public abstract void Start();

    /// <summary>
    /// Stops the audio device.
    /// </summary>
    public abstract void Stop();

    /// <summary>
    /// Releases all resources used by the audio device.
    /// </summary>
    public abstract void Dispose();

    /// <summary>
    /// Invokes the <see cref="OnAudioProcessed"/> event with the processed samples.
    /// This method is intended to be called by the backend implementation.
    /// </summary>
    /// <param name="samples">The processed audio samples.</param>
    protected virtual void InvokeOnAudioProcessed(Span<float> samples)
    {
        PublishBuffer(samples, (Capability & Capability.Record) != 0
            ? ProcessingStage.Capture : ProcessingStage.FinalOutput);
    }

    /// <summary>Publishes borrowed F32 PCM at its actual processing stage. The span is valid only during the callbacks.</summary>
    /// <remarks>Native backends must not label a source/graph feed as their final hardware mix. Subscriber calls run synchronously on the processing thread.</remarks>
    protected internal void PublishBuffer(Span<float> samples, ProcessingStage stage)
    {
        AudioBufferProcessed?.Invoke(samples, new BufferInfo(
            Format with { Format = SampleFormat.F32 }, stage, Capability));
        if (stage is ProcessingStage.Capture or ProcessingStage.FinalOutput)
            OnAudioProcessed?.Invoke(samples, Capability);
    }

    internal Delegate[] GetBufferSubscribers() => AudioBufferProcessed?.GetInvocationList() ?? [];

    /// <summary>
    /// Gets the invocation list of the OnAudioProcessed event. For internal engine use only.
    /// </summary>
    /// <returns>An array of delegates subscribed to the event.</returns>
    internal virtual Delegate[] GetEventSubscribers()
    {
        return OnAudioProcessed?.GetInvocationList() ?? [];
    }

    /// <summary>
    /// Called when the audio device is disposed.
    /// </summary>
    protected virtual void OnDisposedHandler() => OnDisposed?.Invoke(this, EventArgs.Empty);
}
