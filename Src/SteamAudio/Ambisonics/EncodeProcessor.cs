using SoundFlow.Structs;
using Native = global::SteamAudio;

namespace SoundFlow.SteamAudio.Ambisonics;

/// <summary>Encodes a mono point source into an ACN/N3D sound field.</summary>
public sealed class EncodeProcessor : Processor
{
    private readonly Native.Effects.AmbisonicsEncode _effect = null!;
    internal EncodeProcessor(Renderer renderer, AudioFormat format, Spatialization state, int order) : base(renderer, format, Format.Encode(format, order), state)
    {
        try
        {

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
        var direction = Warming ? -System.Numerics.Vector3.UnitZ : Spatialization.Direction;
        return _effect.Process(new()
        {
            Order = OutputFormat.Ambisonics!.Value.Order,
            Direction = new()
            {
                X = direction.X,
                Y = direction.Y,
                Z = direction.Z
            }
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
