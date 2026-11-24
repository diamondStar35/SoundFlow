using SoundFlow.Providers;
using Engine = SoundFlow.Engine;
using Transport = SoundFlow.Playback.Transport;
using SoundFlow.Components;
using SoundFlow.Enums;
using SoundFlow.Interfaces;
using SoundFlow.Playback;
using SoundFlow.Structs;
namespace SoundFlow.Abstracts;

/// <summary>A software graph player backed by the shared PCM transport used by native sources.</summary>
public abstract class SoundPlayerBase : SoundComponent, IPlayer
{
    internal Transport Transport { get; }
    /// <summary>Creates a graph player which owns its provider.</summary>
    protected SoundPlayerBase(Engine engine, AudioFormat format, IDataProvider dataProvider) : base(engine, format)
    {
        Transport = new Transport(format, dataProvider);
        Transport.PlaybackEnded += (_, args) => OnPlaybackEnded();
    }
    /// <inheritdoc />
    public float PlaybackSpeed { get => Transport.PlaybackSpeed; set => Transport.PlaybackSpeed = value; }
    /// <inheritdoc />
    public PlaybackState State { get => Transport.State; internal set => Transport.State = value; }
    /// <inheritdoc />
    public IDataProvider DataProvider { get => Transport.DataProvider; }
    /// <inheritdoc />
    public bool IsLooping { get => Transport.IsLooping; set => Transport.IsLooping = value; }
    /// <inheritdoc />
    public float Time { get => Transport.Time; }
    /// <inheritdoc />
    public float Duration { get => Transport.Duration; }
    /// <inheritdoc />
    public int LoopStartSamples { get => Transport.LoopStartSamples; }
    /// <inheritdoc />
    public int LoopEndSamples { get => Transport.LoopEndSamples; }
    /// <inheritdoc />
    public float LoopStartSeconds { get => Transport.LoopStartSeconds; }
    /// <inheritdoc />
    public float LoopEndSeconds { get => Transport.LoopEndSeconds; }
    /// <inheritdoc />
    public WsolaConfig TimeStretchConfig { get => Transport.TimeStretchConfig; set => Transport.TimeStretchConfig = value; }
    /// <summary>Raised once when the transport reaches the end of its input.</summary>
    public event EventHandler<EventArgs>? PlaybackEnded;
    /// <summary>Raises completion and disables further graph processing.</summary>
    protected virtual void OnPlaybackEnded() { Enabled = false; PlaybackEnded?.Invoke(this, EventArgs.Empty); }
    /// <inheritdoc />
    protected override void GenerateAudio(Span<float> buffer, int channels) => Transport.Render(buffer, channels);
    /// <inheritdoc />
    public void Play() { Enabled = true; Transport.Play(); }
    /// <inheritdoc />
    public void Pause() { Enabled = false; Transport.Pause(); }
    /// <inheritdoc />
    public void Stop() { Enabled = false; Transport.Stop(); }
    /// <inheritdoc />
    public bool Seek(TimeSpan time, SeekOrigin seekOrigin = SeekOrigin.Begin) => Transport.Seek(time, seekOrigin);
    /// <inheritdoc />
    public bool Seek(float time) => Transport.Seek(time);
    /// <inheritdoc />
    public bool Seek(int sampleOffset) => Transport.Seek(sampleOffset);
    /// <inheritdoc />
    public void SetLoopPoints(float startTime, float? endTime = -1f) => Transport.SetLoopPoints(startTime, endTime);
    /// <inheritdoc />
    public void SetLoopPoints(int startSample, int endSample = -1) => Transport.SetLoopPoints(startSample, endSample);
    /// <inheritdoc />
    public void SetLoopPoints(TimeSpan startTime, TimeSpan? endTime = null) => Transport.SetLoopPoints(startTime, endTime);
    /// <summary>Selects the WSOLA quality used for positive speed changes.</summary>
    public void SetTimeStretchQuality(WsolaPerformancePreset preset) => Transport.SetTimeStretchQuality(preset);
    /// <inheritdoc />
    protected override void Dispose(bool disposing) { if (disposing) Transport.Dispose(); base.Dispose(disposing); }
}
