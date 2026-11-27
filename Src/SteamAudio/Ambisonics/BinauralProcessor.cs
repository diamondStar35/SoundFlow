using SoundFlow.Structs;
using Native = global::SteamAudio;

namespace SoundFlow.SteamAudio.Ambisonics;

/// <summary>Decodes an ACN/N3D sound field to stereo headphones without applying listener rotation.</summary>
public sealed class BinauralProcessor : Processor
{
    private readonly Native.Effects.AmbisonicsBinaural _effect = null!;
    private readonly Native.Hrtf? _hrtf;
    private readonly Native.Hrtf? _warmHrtf;
    private readonly nint _hrtfAddress;
    internal BinauralProcessor(Renderer renderer, AudioFormat format) : base(renderer, format, SpeakerLayout.Stereo.Output(format), new())
    {
        try
        {
            var order = Format.Require(format).Order;
            _hrtf = renderer.GetHrtf(format.SampleRate);
            _warmHrtf = renderer.GetHrtf(format.SampleRate, true);
            _hrtfAddress = _hrtf.DangerousGetHandle();
            _effect = new(renderer.Context, new(format.SampleRate, renderer.Settings.BlockFrames), new()
            {
                MaxOrder = order,
                Hrtf = new(_hrtfAddress)
            }, _hrtf);
            Warm();
        }
        catch
        {
            Dispose();
            throw;
        }
    }
    private protected override Native.Interop.AudioEffectState ApplyNative()
    {
        return _effect.Process(new()
        {
            Order = InputFormat.Ambisonics!.Value.Order,
            Hrtf = new(Warming ? _warmHrtf!.DangerousGetHandle() : _hrtfAddress)
        }, Input, Output);
    }
    private protected override Native.Interop.AudioEffectState DrainNative() => _effect.GetTail(Output);
    private protected override int TailFrames => _effect.TailSize;
    private protected override void ResetNative() => _effect.Reset();
    private protected override void ReleaseNative()
    {
        _effect?.Dispose();
    }
}
