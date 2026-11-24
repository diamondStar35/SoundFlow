// Parameter values and ranges follow OpenAL Soft 1.25.2 include/AL/efx.h.
namespace SoundFlow.Effects;

/// <summary>Models early reflections and a diffuse late tail using the standard EFX room model.</summary>
/// <remarks>Values form an immutable effect snapshot. Validate before native allocation; use a record with-expression to customize without mutating shared presets.</remarks>
public sealed record ReverbSettings : Settings
{
    /// <summary>Modal density of the late reverberation; lower values make the tail more sparse. Range 0.0f through 1.0f; default 1.0f.</summary>
    public float Density { get; init; } = 1.0f;
    /// <summary>Echo density and smoothness of reflections; larger values produce a more blended tail. Range 0.0f through 1.0f; default 1.0f.</summary>
    public float Diffusion { get; init; } = 1.0f;
    /// <summary>Overall wet output gain as a linear multiplier, independent of the slot gain. Range 0.0f through 1.0f; default 0.32f.</summary>
    public float Gain { get; init; } = 0.32f;
    /// <summary>High-frequency wet gain relative to the overall wet level. Range 0.0f through 1.0f; default 0.89f.</summary>
    public float GainHF { get; init; } = 0.89f;
    /// <summary>Mid-frequency reverberation decay time in seconds. Range 0.1f through 20.0f; default 1.49f.</summary>
    public float DecayTime { get; init; } = 1.49f;
    /// <summary>High-frequency decay time divided by the mid-frequency decay time. Range 0.1f through 2.0f; default 0.83f.</summary>
    public float DecayHFRatio { get; init; } = 0.83f;
    /// <summary>Linear level of the early reflections relative to the overall reverb level. Range 0.0f through 3.16f; default 0.05f.</summary>
    public float ReflectionsGain { get; init; } = 0.05f;
    /// <summary>Delay in seconds before the early reflections begin. Range 0.0f through 0.3f; default 0.007f.</summary>
    public float ReflectionsDelay { get; init; } = 0.007f;
    /// <summary>Linear level of the late reverberation relative to the overall reverb level. Range 0.0f through 10.0f; default 1.26f.</summary>
    public float LateReverbGain { get; init; } = 1.26f;
    /// <summary>Additional delay in seconds between early reflections and the late tail. Range 0.0f through 0.1f; default 0.011f.</summary>
    public float LateReverbDelay { get; init; } = 0.011f;
    /// <summary>High-frequency gain remaining after one metre of propagation through the modeled air. Range 0.892f through 1.0f; default 0.994f.</summary>
    public float AirAbsorptionGainHF { get; init; } = 0.994f;
    /// <summary>Extra distance attenuation applied to this effect response. Range 0.0f through 10.0f; default 0.0f.</summary>
    public float RoomRolloffFactor { get; init; } = 0.0f;
    /// <summary>Limits high-frequency decay according to the air-absorption setting when enabled. Range false through true; default true.</summary>
    public bool DecayHFLimit { get; init; } = true;
    /// <inheritdoc />
    public override void Validate()
    {
        Range(Density, 0.0f, 1.0f, nameof(Density));
        Range(Diffusion, 0.0f, 1.0f, nameof(Diffusion));
        Range(Gain, 0.0f, 1.0f, nameof(Gain));
        Range(GainHF, 0.0f, 1.0f, nameof(GainHF));
        Range(DecayTime, 0.1f, 20.0f, nameof(DecayTime));
        Range(DecayHFRatio, 0.1f, 2.0f, nameof(DecayHFRatio));
        Range(ReflectionsGain, 0.0f, 3.16f, nameof(ReflectionsGain));
        Range(ReflectionsDelay, 0.0f, 0.3f, nameof(ReflectionsDelay));
        Range(LateReverbGain, 0.0f, 10.0f, nameof(LateReverbGain));
        Range(LateReverbDelay, 0.0f, 0.1f, nameof(LateReverbDelay));
        Range(AirAbsorptionGainHF, 0.892f, 1.0f, nameof(AirAbsorptionGainHF));
        Range(RoomRolloffFactor, 0.0f, 10.0f, nameof(RoomRolloffFactor));
    }
}
