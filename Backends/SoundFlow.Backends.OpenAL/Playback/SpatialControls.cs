using SourceSettings = SoundFlow.Spatial.SourceSettings;
using SpatialValidation = SoundFlow.Backends.OpenAL.Devices.SpatialValidation;
using System.Numerics;
using SoundFlow.Features;

namespace SoundFlow.Backends.OpenAL.Playback;

internal sealed unsafe partial class Voice
{
    private SourceSettings _spatial = new();
    private float _pitch = 1;
    public SourceSettings Settings
    {
        get => _spatial;
        set { ArgumentNullException.ThrowIfNull(value); Control(() => ApplySpatial(value)); }
    }
    private void ApplySpatial(SourceSettings value)
    {
        SpatialValidation.Vector(value.Position, nameof(value.Position));
        SpatialValidation.Vector(value.Velocity, nameof(value.Velocity));
        SpatialValidation.Vector(value.Direction, nameof(value.Direction));
        SpatialValidation.Number(value.ReferenceDistance, float.Epsilon, float.MaxValue, nameof(value.ReferenceDistance));
        SpatialValidation.Number(value.MaximumDistance, value.ReferenceDistance, float.MaxValue, nameof(value.MaximumDistance));
        SpatialValidation.Number(value.RolloffFactor, 0, float.MaxValue, nameof(value.RolloffFactor));
        SpatialValidation.Number(value.ConeInnerAngle, 0, 360, nameof(value.ConeInnerAngle));
        SpatialValidation.Number(value.ConeOuterAngle, value.ConeInnerAngle, 360, nameof(value.ConeOuterAngle));
        SpatialValidation.Number(value.ConeOuterGain, 0, 1, nameof(value.ConeOuterGain));
        SpatialValidation.Number(value.ConeOuterGainHF, 0, 1, nameof(value.ConeOuterGainHF));
        SpatialValidation.Number(value.AirAbsorptionFactor, 0, 10, nameof(value.AirAbsorptionFactor));
        SpatialValidation.Number(value.RoomRolloffFactor, 0, 10, nameof(value.RoomRolloffFactor));
        if (!_owner.HasEfx && (value.ConeOuterGainHF != 1 || value.AirAbsorptionFactor != 0 || value.RoomRolloffFactor != 0)) throw new NotSupportedException("The requested environmental source settings require EFX.");
        if (!_owner.HasExtension("AL_SOFT_source_spatialize")) throw new NotSupportedException("Explicit 2D/3D source intent requires AL_SOFT_source_spatialize.");
        _owner.ApplyNativeUpdates(() =>
        {
            Al.alSource3f(Id, 0x1004, value.Position.X, value.Position.Y, value.Position.Z);
            Al.alSource3f(Id, 0x1006, value.Velocity.X, value.Velocity.Y, value.Velocity.Z);
            Al.alSource3f(Id, 0x1005, value.Direction.X, value.Direction.Y, value.Direction.Z);
            Al.alSourcei(Id, 0x202, value.Relative ? 1 : 0);
            Al.alSourcef(Id, 0x1020, value.ReferenceDistance); Al.alSourcef(Id, 0x1023, value.MaximumDistance);
            Al.alSourcef(Id, 0x1021, value.RolloffFactor);
            Al.alSourcef(Id, 0x1001, value.ConeInnerAngle); Al.alSourcef(Id, 0x1002, value.ConeOuterAngle); Al.alSourcef(Id, 0x1022, value.ConeOuterGain);
            if (_owner.HasExtension("AL_SOFT_source_spatialize")) Al.alSourcei(Id, 0x1214, value.Spatialize ? 1 : 0);
            if (_owner.HasEfx)
            {
                Al.alSourcef(Id, 0x20009, value.ConeOuterGainHF);
                Al.alSourcef(Id, 0x20007, value.AirAbsorptionFactor);
                Al.alSourcef(Id, 0x20008, value.RoomRolloffFactor);
            }
            Al.CheckAl("Apply source spatial settings");
            _spatial = value;
        });
    }
    public float Pitch
    {
        get => _pitch;
        set
        {
            SpatialValidation.Number(value, float.Epsilon, float.MaxValue, nameof(value));
            Control(() => { Al.alSourcef(Id, 0x1003, value); Al.CheckAl("Set native pitch"); _pitch = value; });
        }
    }
}
