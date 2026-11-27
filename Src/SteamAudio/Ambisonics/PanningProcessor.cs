using SoundFlow.Structs;
using Native = global::SteamAudio;

namespace SoundFlow.SteamAudio.Ambisonics;

/// <summary>Decodes an ACN/N3D sound field to an authored speaker layout.</summary>
public sealed class PanningProcessor : Processor
{
    private readonly Native.Effects.AmbisonicsPanning _effect = null!;
    private readonly SpeakerLayout.LayoutLease _layout = null!;
    internal PanningProcessor(Renderer renderer, AudioFormat format, SpeakerLayout layout) : base(renderer, format, layout.Output(format), new())
    {
        try
        {
            var order = Format.Require(format).Order;
            _layout = layout.Retain();
            _effect = new(renderer.Context, new(format.SampleRate, renderer.Settings.BlockFrames), new()
            {
                MaxOrder = order,
                SpeakerLayout = _layout.Value
            });
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
            Order = InputFormat.Ambisonics!.Value.Order
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
