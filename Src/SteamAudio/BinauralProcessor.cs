using SoundFlow.Structs;
using Native = global::SteamAudio;

namespace SoundFlow.SteamAudio;

/// <summary>
/// Renders one point source to headphone stereo. The current snapshot selects interpolation and HRTF
/// blend; bilinear/full spatialization are the defaults. Stereo input occupies one point at full blend.
/// Zero blend preserves the ordinary input exactly, duplicating mono to left/right.
/// </summary>
public sealed class BinauralProcessor : Processor
{
    private readonly Native.Effects.Binaural _effect = null!;
    private readonly Native.Hrtf _warmHrtf;
    internal BinauralProcessor(Renderer renderer, AudioFormat format, Spatialization state)
        : base(renderer, format, format with
        {
            Channels = 2,
            Layout = AudioFormat.GetLayoutFromChannels(2)
        }, state)
    {
        try
        {
            if (format.Channels is < 1 or > 2 || format.Ambisonics != null)
                throw new ArgumentException("Binaural point sources require mono or stereo speaker PCM.");
            _warmHrtf = renderer.GetHrtf(format.SampleRate, true);
            _effect = new(renderer.Context, renderer.GetHrtf(format.SampleRate));
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
        if (Warming)
            return _effect.Process(-System.Numerics.Vector3.UnitZ, Input, Output, 1,
            Native.Interop.HrtfInterpolation.Bilinear, _warmHrtf);
        var settings = Spatialization.Current;
        var interpolation = settings.Interpolation == HrtfInterpolation.Nearest
            ? Native.Interop.HrtfInterpolation.Nearest : Native.Interop.HrtfInterpolation.Bilinear;
        var state = _effect.Process(Spatialization.Direction, Input, Output, settings.SpatialBlend, interpolation);
        if (settings.SpatialBlend == 0)
        {
            using var input = Input.Borrow();
            using var output = Output.Borrow();
            input.Channel(0).CopyTo(output.Channel(0));
            input.Channel(InputFormat.Channels == 1 ? 0 : 1).CopyTo(output.Channel(1));
        }
        return state;
    }

    private protected override Native.Interop.AudioEffectState DrainNative()
    {
        var result = _effect.GetTail(Output);
        if (Spatialization.Current.SpatialBlend == 0)
            Output.Clear();
        return result;
    }
    private protected override int TailFrames => _effect.TailSize;
    private protected override void ResetNative() => _effect.Reset();
    private protected override void ReleaseNative() => _effect?.Dispose();
}
