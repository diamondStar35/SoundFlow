using SoundFlow.Structs;
using Native = global::SteamAudio;

namespace SoundFlow.SteamAudio.Ambisonics;

/// <summary>Applies listener rotation once and decodes an ACN/N3D sound field for speakers or headphones.</summary>
public sealed class DecodeProcessor : Processor
{
    private readonly Native.Effects.AmbisonicsDecode _effect = null!;
    private readonly SpeakerLayout.LayoutLease _layout = null!;
    private readonly Native.Hrtf? _hrtf;
    private readonly Native.Hrtf? _warmHrtf;
    private readonly nint _hrtfAddress;
    private readonly bool _binaural;
    internal DecodeProcessor(Renderer renderer, AudioFormat format, SpeakerLayout layout, bool binaural) : base(renderer, format, layout.Output(format), new())
    {
        try
        {
            var order = Format.Require(format).Order;
            if (binaural && layout.Channels != 2)
                throw new ArgumentException("Headphone decoding requires stereo output.");
            _binaural = binaural;
            _layout = layout.Retain();
            if (binaural)
            {
                _hrtf = renderer.GetHrtf(format.SampleRate);
                _warmHrtf = renderer.GetHrtf(format.SampleRate, true);
                _hrtfAddress = _hrtf.DangerousGetHandle();
            }
            _effect = new(renderer.Context, new(format.SampleRate, renderer.Settings.BlockFrames), new()
            {
                MaxOrder = order,
                SpeakerLayout = _layout.Value,
                Hrtf = new(_hrtfAddress)
            }, _hrtf == null ? [] : [_hrtf]);
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
            Orientation = Renderer.Orientation(Spatialization.Listener),
            Binaural = _binaural ? Native.Interop.Bool.True : Native.Interop.Bool.False,
            Hrtf = new(Warming && _binaural ? _warmHrtf!.DangerousGetHandle() : _hrtfAddress)
        }, Input, Output);
    }
    private protected override Native.Interop.AudioEffectState DrainNative() => _effect.GetTail(Output);
    private protected override int TailFrames => _effect.TailSize;
    private protected override void ResetNative() => _effect.Reset();
    private protected override void ReleaseNative()
    {
        _effect?.Dispose();
        _layout?.Dispose();
    }
}
