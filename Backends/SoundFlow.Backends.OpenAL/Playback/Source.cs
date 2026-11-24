using SoundFlow.Providers;
using Context = SoundFlow.Backends.OpenAL.Devices.Context;
using CoreSource = SoundFlow.Playback.Source;
using IEffectSends = SoundFlow.Effects.IEffectSends;
using ISourceRendering = SoundFlow.Backends.OpenAL.Features.ISourceRendering;
using SoundFlow.Abstracts;
using SoundFlow.Enums;
using SoundFlow.Features;
using SoundFlow.Interfaces;
using SoundFlow.Structs;

namespace SoundFlow.Backends.OpenAL.Playback;

/// <summary>Maps the shared nonspatial source interface onto a context-owned native voice.</summary>
internal sealed class Source : CoreSource, INativeSource
{
    public Voice Voice { get; }
    internal Source(Context context, IDataProvider provider, AudioFormat format, bool leaveOpen)
        : base(context.Owner, provider, format)
    {
        try
        {
            Voice = new Voice(context, provider, format, leaveOpen, false,
                (samples, count) => PublishBuffer(samples.AsSpan(0, count)), RaisePlaybackEnded);
            context.Sources.Add(Voice);
            RegisterFeature<ISourcePitch>(Voice);
            RegisterFeature<ISourceRendering>(Voice);
            if (context.HasEfx) RegisterFeature<IEffectSends>(Voice);
        }
        catch { ReleaseProviderOwnership(); throw; }
    }
    public override PlaybackState State => Voice.State;
    public override IDataProvider DataProvider => Voice.DataProvider;
    public override bool IsLooping { get => Voice.IsLooping; set => Voice.IsLooping = value; }
    public override float PlaybackSpeed { get => Voice.PlaybackSpeed; set => Voice.PlaybackSpeed = value; }
    public override float Volume { get => Voice.Volume; set => Voice.Volume = value; }
    public override float Time => Voice.Time;
    public override float Duration => Voice.Duration;
    public override int LoopStartSamples => Voice.LoopStartSamples;
    public override int LoopEndSamples => Voice.LoopEndSamples;
    public override void Play() => Voice.Play();
    public override void Pause() => Voice.Pause();
    public override void Stop() => Voice.Stop();
    public override bool Seek(int offset) => Voice.Seek(offset);
    public override void SetLoopPoints(int start, int end = -1) => Voice.SetLoopPoints(start, end);
    public override void AddModifier(SoundModifier modifier) => Voice.AddModifier(modifier);
    public override void RemoveModifier(SoundModifier modifier) => Voice.RemoveModifier(modifier);
    protected override void ValidateDisposal() => Voice.ValidateDisposal();
    protected override void DisposeSource() => Voice.Dispose();
}
