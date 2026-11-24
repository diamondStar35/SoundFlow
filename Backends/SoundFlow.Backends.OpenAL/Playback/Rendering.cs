using System.Numerics;
using SoundFlow.Backends.OpenAL.Features;
using SoundFlow.Spatial;

namespace SoundFlow.Backends.OpenAL.Playback;

internal sealed unsafe partial class Voice : ISourceRendering
{
    private RenderingSettings _rendering = new();
    public RenderingCapabilities Capabilities => _owner.RenderingSupport;
    public IReadOnlyList<string> AvailableResamplers => _owner.Resamplers;
    RenderingSettings ISourceRendering.Settings
    {
        get => _rendering;
        set
        {
            ArgumentNullException.ThrowIfNull(value);
            Control(() => ApplyRendering(value));
        }
    }

    /// <summary>Validates the whole snapshot before making any AL calls, including extension-dependent requests.</summary>
    private void ApplyRendering(RenderingSettings value)
    {
        Devices.SpatialValidation.Number(value.MinimumGain, 0, 1, nameof(value.MinimumGain));
        Devices.SpatialValidation.Number(value.MaximumGain, value.MinimumGain, 1, nameof(value.MaximumGain));
        Devices.SpatialValidation.Number(value.Radius, 0, float.MaxValue, nameof(value.Radius));
        if (!Enum.IsDefined(value.ChannelMapping)) throw new ArgumentOutOfRangeException(nameof(value.ChannelMapping));
        if (value.DistanceModel.HasValue && !Enum.IsDefined(value.DistanceModel.Value)) throw new ArgumentOutOfRangeException(nameof(value.DistanceModel));
        if (value.Resampler.HasValue && (value.Resampler < 0 || value.Resampler >= AvailableResamplers.Count)) throw new ArgumentOutOfRangeException(nameof(value.Resampler));
        if (value.StereoAngles is { } angles && (!float.IsFinite(angles.X) || !float.IsFinite(angles.Y))) throw new ArgumentOutOfRangeException(nameof(value.StereoAngles));
        Require(RenderingCapabilities.DistanceModel, value.DistanceModel.HasValue);
        Require(RenderingCapabilities.Radius, value.Radius != 0);
        Require(RenderingCapabilities.StereoAngles, value.StereoAngles.HasValue);
        Require(RenderingCapabilities.Resampler, value.Resampler.HasValue);
        Require(RenderingCapabilities.DirectChannels, value.ChannelMapping != ChannelMapping.Normal);
        Require(RenderingCapabilities.ChannelRemix, value.ChannelMapping == ChannelMapping.RemixUnmatched);
        Require(RenderingCapabilities.AutomaticFilterGain, !value.DirectGainHFAutomatic || !value.SendGainAutomatic || !value.SendGainHFAutomatic);
        _owner.ApplyNativeUpdates(() =>
        {
            // Relax the old bounds first so replacing [low,high] with a disjoint valid range is accepted by AL.
            Al.alSourcef(Id, 0x100D, 0); Al.alSourcef(Id, 0x100E, value.MaximumGain); Al.alSourcef(Id, 0x100D, value.MinimumGain);
            if (Capabilities.HasFlag(RenderingCapabilities.DistanceModel)) Al.alSourcei(Id, 0xD000, Devices.Context.DistanceToken(value.DistanceModel ?? _owner.DistanceModel));
            if (Capabilities.HasFlag(RenderingCapabilities.Radius)) Al.alSourcef(Id, 0x1031, value.Radius);
            if (Capabilities.HasFlag(RenderingCapabilities.StereoAngles))
            {
                var stereo = value.StereoAngles ?? new Vector2(MathF.PI / 6, -MathF.PI / 6);
                float* pointer = stackalloc float[2] { stereo.X, stereo.Y }; Al.alSourcefv(Id, 0x1030, pointer);
            }
            if (Capabilities.HasFlag(RenderingCapabilities.Resampler)) Al.alSourcei(Id, 0x1212, value.Resampler ?? _owner.DefaultResampler);
            if (Capabilities.HasFlag(RenderingCapabilities.DirectChannels)) Al.alSourcei(Id, 0x1033, (int)value.ChannelMapping);
            if (_owner.HasEfx)
            {
                Al.alSourcei(Id, 0x2000A, value.DirectGainHFAutomatic ? 1 : 0);
                Al.alSourcei(Id, 0x2000B, value.SendGainAutomatic ? 1 : 0);
                Al.alSourcei(Id, 0x2000C, value.SendGainHFAutomatic ? 1 : 0);
            }
            Al.CheckAl("Apply rendering settings");
            _rendering = value;
        });
    }

    private void Require(RenderingCapabilities capability, bool requested)
    {
        if (requested && !Capabilities.HasFlag(capability)) throw new NotSupportedException($"The current device does not support {capability}.");
    }

    internal void ApplySceneDistance(DistanceModel model)
    {
        if (!_rendering.DistanceModel.HasValue && Capabilities.HasFlag(RenderingCapabilities.DistanceModel))
            Al.alSourcei(Id, 0xD000, Devices.Context.DistanceToken(model));
    }
}
