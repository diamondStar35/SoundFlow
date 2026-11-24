// Parameter values and ranges follow OpenAL Soft 1.25.2 include/AL/efx.h.
using System.Numerics;
namespace SoundFlow.Effects;

/// <summary>Models a room with separate early/late directions, three decay bands, echo and modulation controls.</summary>
/// <remarks>Values form an immutable effect snapshot. Validate before native allocation; use a record with-expression to customize without mutating shared presets.</remarks>
public sealed record EaxReverbSettings : Settings
{
    /// <summary>Modal density of the late reverberation; lower values make the tail more sparse. Range 0.0f through 1.0f; default 1.0f.</summary>
    public float Density { get; init; } = 1.0f;
    /// <summary>Echo density and smoothness of reflections; larger values produce a more blended tail. Range 0.0f through 1.0f; default 1.0f.</summary>
    public float Diffusion { get; init; } = 1.0f;
    /// <summary>Overall wet output gain as a linear multiplier, independent of the slot gain. Range 0.0f through 1.0f; default 0.32f.</summary>
    public float Gain { get; init; } = 0.32f;
    /// <summary>High-frequency wet gain relative to the overall wet level. Range 0.0f through 1.0f; default 0.89f.</summary>
    public float GainHF { get; init; } = 0.89f;
    /// <summary>Low-frequency wet gain relative to the overall wet level. Range 0.0f through 1.0f; default 1.0f.</summary>
    public float GainLF { get; init; } = 1.0f;
    /// <summary>Mid-frequency reverberation decay time in seconds. Range 0.1f through 20.0f; default 1.49f.</summary>
    public float DecayTime { get; init; } = 1.49f;
    /// <summary>High-frequency decay time divided by the mid-frequency decay time. Range 0.1f through 2.0f; default 0.83f.</summary>
    public float DecayHFRatio { get; init; } = 0.83f;
    /// <summary>Low-frequency decay time divided by the mid-frequency decay time. Range 0.1f through 2.0f; default 1.0f.</summary>
    public float DecayLFRatio { get; init; } = 1.0f;
    /// <summary>Linear level of the early reflections relative to the overall reverb level. Range 0.0f through 3.16f; default 0.05f.</summary>
    public float ReflectionsGain { get; init; } = 0.05f;
    /// <summary>Delay in seconds before the early reflections begin. Range 0.0f through 0.3f; default 0.007f.</summary>
    public float ReflectionsDelay { get; init; } = 0.007f;
    /// <summary>Direction and focus of early reflections; zero is diffuse and vector length cannot exceed one. </summary>
    public Vector3 ReflectionsPan { get; init; } = Vector3.Zero;
    /// <summary>Linear level of the late reverberation relative to the overall reverb level. Range 0.0f through 10.0f; default 1.26f.</summary>
    public float LateReverbGain { get; init; } = 1.26f;
    /// <summary>Additional delay in seconds between early reflections and the late tail. Range 0.0f through 0.1f; default 0.011f.</summary>
    public float LateReverbDelay { get; init; } = 0.011f;
    /// <summary>Direction and focus of the late tail; zero is diffuse and vector length cannot exceed one. </summary>
    public Vector3 LateReverbPan { get; init; } = Vector3.Zero;
    /// <summary>Time in seconds between discrete echoes embedded in the reverb tail. Range 0.075f through 0.25f; default 0.25f.</summary>
    public float EchoTime { get; init; } = 0.25f;
    /// <summary>Strength of the discrete echo structure; zero disables that structure. Range 0.0f through 1.0f; default 0.0f.</summary>
    public float EchoDepth { get; init; } = 0.0f;
    /// <summary>Period in seconds of the reverb modulation cycle. Range 0.04f through 4.0f; default 0.25f.</summary>
    public float ModulationTime { get; init; } = 0.25f;
    /// <summary>Strength of reverb modulation; zero disables modulation. Range 0.0f through 1.0f; default 0.0f.</summary>
    public float ModulationDepth { get; init; } = 0.0f;
    /// <summary>High-frequency gain remaining after one metre of propagation through the modeled air. Range 0.892f through 1.0f; default 0.994f.</summary>
    public float AirAbsorptionGainHF { get; init; } = 0.994f;
    /// <summary>Reference frequency in Hz separating the high-frequency reverb band. Range 1000.0f through 20000.0f; default 5000.0f.</summary>
    public float HFReference { get; init; } = 5000.0f;
    /// <summary>Reference frequency in Hz separating the low-frequency reverb band. Range 20.0f through 1000.0f; default 250.0f.</summary>
    public float LFReference { get; init; } = 250.0f;
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
        Range(GainLF, 0.0f, 1.0f, nameof(GainLF));
        Range(DecayTime, 0.1f, 20.0f, nameof(DecayTime));
        Range(DecayHFRatio, 0.1f, 2.0f, nameof(DecayHFRatio));
        Range(DecayLFRatio, 0.1f, 2.0f, nameof(DecayLFRatio));
        Range(ReflectionsGain, 0.0f, 3.16f, nameof(ReflectionsGain));
        Range(ReflectionsDelay, 0.0f, 0.3f, nameof(ReflectionsDelay));
        if (!float.IsFinite(ReflectionsPan.LengthSquared()) || ReflectionsPan.LengthSquared() > 1) throw new ArgumentOutOfRangeException(nameof(ReflectionsPan));
        Range(LateReverbGain, 0.0f, 10.0f, nameof(LateReverbGain));
        Range(LateReverbDelay, 0.0f, 0.1f, nameof(LateReverbDelay));
        if (!float.IsFinite(LateReverbPan.LengthSquared()) || LateReverbPan.LengthSquared() > 1) throw new ArgumentOutOfRangeException(nameof(LateReverbPan));
        Range(EchoTime, 0.075f, 0.25f, nameof(EchoTime));
        Range(EchoDepth, 0.0f, 1.0f, nameof(EchoDepth));
        Range(ModulationTime, 0.04f, 4.0f, nameof(ModulationTime));
        Range(ModulationDepth, 0.0f, 1.0f, nameof(ModulationDepth));
        Range(AirAbsorptionGainHF, 0.892f, 1.0f, nameof(AirAbsorptionGainHF));
        Range(HFReference, 1000.0f, 20000.0f, nameof(HFReference));
        Range(LFReference, 20.0f, 1000.0f, nameof(LFReference));
        Range(RoomRolloffFactor, 0.0f, 10.0f, nameof(RoomRolloffFactor));
    }
}
