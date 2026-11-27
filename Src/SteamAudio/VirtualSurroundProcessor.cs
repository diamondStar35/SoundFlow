using SoundFlow.Structs;
using Native = global::SteamAudio;

namespace SoundFlow.SteamAudio;

/// <summary>Virtualizes an authored speaker mix as stereo headphones, preserving each virtual speaker's direction.</summary>
public sealed class VirtualSurroundProcessor : Processor
{
    private readonly Native.Effects.VirtualSurround _effect = null!;
    private readonly SpeakerLayout.LayoutLease _layout = null!;
    private readonly Native.Hrtf _hrtf;
    private readonly Native.Hrtf _warmHrtf;
    private readonly nint _hrtfAddress;
    internal VirtualSurroundProcessor(Renderer renderer, AudioFormat format, SpeakerLayout layout)
        : base(renderer, format, SpeakerLayout.Stereo.Output(format), new())
    {
        try
        {
            if (format.Channels != layout.Channels || format.Ambisonics != null)
                throw new ArgumentException("Virtual surround input must match the authored speaker layout.");
            _layout = layout.Retain();
            _hrtf = renderer.GetHrtf(format.SampleRate);
            _warmHrtf = renderer.GetHrtf(format.SampleRate, true);
            _hrtfAddress = _hrtf.DangerousGetHandle();
            _effect = new(renderer.Context, new(format.SampleRate, renderer.Settings.BlockFrames),
                new()
                {
                    SpeakerLayout = _layout.Value,
                    Hrtf = new(_hrtf.DangerousGetHandle())
                }, _hrtf);
            Warm();
        }
        catch
        {
            Dispose();
            throw;
        }
    }
    private protected override Native.Interop.AudioEffectState ApplyNative() => _effect.Process(
        new()
        {
            Hrtf = new(Warming ? _warmHrtf.DangerousGetHandle() : _hrtfAddress)
        }, Input, Output);
    private protected override Native.Interop.AudioEffectState DrainNative() => _effect.GetTail(Output);
    private protected override int TailFrames => _effect.TailSize;
    private protected override void ResetNative() => _effect.Reset();
    private protected override void ReleaseNative()
    {
        _effect?.Dispose();
        _layout?.Dispose();
    }
}
