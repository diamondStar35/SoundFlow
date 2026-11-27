using SoundFlow.Structs;
using Native = global::SteamAudio;

namespace SoundFlow.SteamAudio;

/// <summary>Applies unity-center stereo balance and mid/side width after spatial rendering, without clipping.</summary>
public sealed class StereoProcessor : Processor
{
    internal StereoProcessor(Renderer renderer, AudioFormat format, Spatialization state) : base(renderer, format, format, state)
    {
        try
        {
            if (format.Channels != 2 || format.Ambisonics != null)
                throw new ArgumentException("Stereo shaping requires two speaker channels.");
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
        var pan = Warming ? .5f : Spatialization.Current.Pan;
        var width = Warming ? 1 : Spatialization.Current.Width;
        var leftGain = pan <= .5f ? 1 : MathF.Sqrt(2 * (1 - pan));
        var rightGain = pan >= .5f ? 1 : MathF.Sqrt(2 * pan);
        using var input = Input.Borrow();
        using var output = Output.Borrow();
        var left = input.Channel(0);
        var right = input.Channel(1);
        var outLeft = output.Channel(0);
        var outRight = output.Channel(1);
        if (width == 1)
        {
            for (var i = 0; i < left.Length; i++)
            {
                outLeft[i] = left[i] * leftGain;
                outRight[i] = right[i] * rightGain;
            }
            return Native.Interop.AudioEffectState.TailComplete;
        }
        for (var i = 0; i < left.Length; i++)
        {
            var mid = (left[i] + right[i]) * .5f;
            var side = (left[i] - right[i]) * .5f * width;
            outLeft[i] = (mid + side) * leftGain;
            outRight[i] = (mid - side) * rightGain;
        }
        return Native.Interop.AudioEffectState.TailComplete;
    }
    private protected override Native.Interop.AudioEffectState DrainNative() => Native.Interop.AudioEffectState.TailComplete;
    private protected override int TailFrames => 0;
    private protected override void ResetNative()
    {
    }
    private protected override void ReleaseNative()
    {
    }
}
