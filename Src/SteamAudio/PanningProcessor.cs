using SoundFlow.Structs;
using Native = global::SteamAudio;

namespace SoundFlow.SteamAudio;

/// <summary>Pans mono point-source PCM into an explicit speaker layout using the current listener pose.</summary>
public sealed class PanningProcessor : Processor
{
    private readonly Native.Effects.Panning _effect = null!;
    private readonly SpeakerLayout.LayoutLease _layout = null!;
    private readonly Native.Effects.Panning _centerEffect = null!;
    private readonly Native.AudioBuffer _center = null!;
    internal PanningProcessor(Renderer renderer, AudioFormat format, Spatialization state, SpeakerLayout layout)
        : base(renderer, format, layout.Output(format), state)
    {
        try
        {
            if (format.Channels != 1 || format.Ambisonics != null)
                throw new ArgumentException("Speaker panning requires mono speaker PCM; downmix explicitly first.");
            _layout = layout.Retain();
            _effect = new(renderer.Context, new(format.SampleRate, renderer.Settings.BlockFrames), new()
            {
                SpeakerLayout = _layout.Value
            });
            _centerEffect = new(renderer.Context, new(format.SampleRate, renderer.Settings.BlockFrames), new()
            {
                SpeakerLayout = _layout.Value
            });
            _center = new(renderer.Context, layout.Channels, renderer.Settings.BlockFrames);
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
        var result = _effect.Process(new()
        {
            Direction = new()
            {
                X = direction.X,
                Y = direction.Y,
                Z = direction.Z
            }
        }, Input, Output);
        var blend = Warming ? 1 : Spatialization.Current.SpatialBlend;
        if (blend < 1 || Warming)
        {
            _centerEffect.Process(new()
            {
                Direction = new()
                {
                    Z = -1
                }
            }, Input, _center);
            using var center = _center.Borrow();
            using var output = Output.Borrow();
            using var input = Input.Borrow();
            for (var channel = 0; channel < OutputFormat.Channels; channel++)
            {
                var dry = OutputFormat.Channels <= 2 ? input.Channel(0) : center.Channel(channel);
                var destination = output.Channel(channel);
                for (var i = 0; i < destination.Length; i++)
                    destination[i] = dry[i] * (1 - blend) + destination[i] * blend;
            }
        }
        return result;
    }
    private protected override Native.Interop.AudioEffectState DrainNative() => _effect.GetTail(Output);
    private protected override int TailFrames => _effect.TailSize;
    private protected override void ResetNative()
    {
        _effect.Reset();
        _centerEffect.Reset();
    }
    private protected override void ReleaseNative()
    {
        _effect?.Dispose();
        _centerEffect?.Dispose();
        _center?.Dispose();
        _layout?.Dispose();
    }
}
