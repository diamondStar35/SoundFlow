using SoundFlow.Structs;
using Native = global::SteamAudio;

namespace SoundFlow.SteamAudio.Ambisonics;

/// <summary>Rotates an ACN/N3D sound field into the current listener frame.</summary>
public sealed class RotationProcessor : Processor
{
    private readonly Native.Effects.AmbisonicsRotation _effect = null!;
    internal RotationProcessor(Renderer renderer, AudioFormat format) : base(renderer, format, format, new())
    {
        try
        {
            var order = Format.Require(format).Order;
            _effect = new(renderer.Context, new(format.SampleRate, renderer.Settings.BlockFrames), new()
            {
                MaxOrder = order
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
            Order = InputFormat.Ambisonics!.Value.Order,
            Orientation = Renderer.Orientation(Spatialization.Listener)
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
