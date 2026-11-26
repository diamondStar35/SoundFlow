using Functions = SoundFlow.Backends.OpenAL.Interop.Functions;
using Settings = SoundFlow.Effects.Settings;
// Explicit EFX bindings for OpenAL Soft 1.25.2. No reflection or runtime-generated marshaling.
using SoundFlow.Effects;
namespace SoundFlow.Backends.OpenAL.Effects;
internal static unsafe class SettingsBinding
{
    internal static readonly Type[] Types = [
        typeof(ReverbSettings),
        typeof(EaxReverbSettings),
        typeof(ChorusSettings),
        typeof(DistortionSettings),
        typeof(EchoSettings),
        typeof(FlangerSettings),
        typeof(FrequencyShifterSettings),
        typeof(VocalMorpherSettings),
        typeof(PitchShifterSettings),
        typeof(RingModulatorSettings),
        typeof(AutowahSettings),
        typeof(CompressorSettings),
        typeof(EqualizerSettings),
        typeof(DedicatedDialogueSettings), typeof(DedicatedLowFrequencySettings), typeof(ConvolutionSettings)];
    internal static int Kind(Settings settings) => Kind(settings.GetType());
    internal static int Kind(Type type) => type switch
    {
        var value when value == typeof(ReverbSettings) => 0x0001,
        var value when value == typeof(EaxReverbSettings) => 0x8000,
        var value when value == typeof(ChorusSettings) => 0x0002,
        var value when value == typeof(DistortionSettings) => 0x0003,
        var value when value == typeof(EchoSettings) => 0x0004,
        var value when value == typeof(FlangerSettings) => 0x0005,
        var value when value == typeof(FrequencyShifterSettings) => 0x0006,
        var value when value == typeof(VocalMorpherSettings) => 0x0007,
        var value when value == typeof(PitchShifterSettings) => 0x0008,
        var value when value == typeof(RingModulatorSettings) => 0x0009,
        var value when value == typeof(AutowahSettings) => 0x000A,
        var value when value == typeof(CompressorSettings) => 0x000B,
        var value when value == typeof(EqualizerSettings) => 0x000C,
        var value when value == typeof(DedicatedDialogueSettings) => 0x9001,
        var value when value == typeof(DedicatedLowFrequencySettings) => 0x9000,
        var value when value == typeof(ConvolutionSettings) => 0xA000,
        _ => throw new NotSupportedException($"OpenAL does not implement {type.Name}."),
    };
    internal static void Apply(Functions al, uint effect, Settings settings)
    {
        al.alEffecti(effect, 0x8001, Kind(settings));
        al.CheckAl("Set effect type");
        switch (settings)
        {
            case DedicatedDialogueSettings value: al.alEffectf(effect, 1, value.Gain); break;
            case DedicatedLowFrequencySettings value: al.alEffectf(effect, 1, value.Gain); break;
            case ConvolutionSettings value:
            {
                float* orientation = stackalloc float[6] { value.Forward.X, value.Forward.Y, value.Forward.Z, value.Up.X, value.Up.Y, value.Up.Z };
                al.alEffectfv(effect, 0x100F, orientation); break;
            }
            case ReverbSettings value:
            {
                al.alEffectf(effect, 0x0001, value.Density);
                al.alEffectf(effect, 0x0002, value.Diffusion);
                al.alEffectf(effect, 0x0003, value.Gain);
                al.alEffectf(effect, 0x0004, value.GainHF);
                al.alEffectf(effect, 0x0005, value.DecayTime);
                al.alEffectf(effect, 0x0006, value.DecayHFRatio);
                al.alEffectf(effect, 0x0007, value.ReflectionsGain);
                al.alEffectf(effect, 0x0008, value.ReflectionsDelay);
                al.alEffectf(effect, 0x0009, value.LateReverbGain);
                al.alEffectf(effect, 0x000A, value.LateReverbDelay);
                al.alEffectf(effect, 0x000B, value.AirAbsorptionGainHF);
                al.alEffectf(effect, 0x000C, value.RoomRolloffFactor);
                al.alEffecti(effect, 0x000D, (value.DecayHFLimit ? 1 : 0));
                break;
            }
            case EaxReverbSettings value:
            {
                al.alEffectf(effect, 0x0001, value.Density);
                al.alEffectf(effect, 0x0002, value.Diffusion);
                al.alEffectf(effect, 0x0003, value.Gain);
                al.alEffectf(effect, 0x0004, value.GainHF);
                al.alEffectf(effect, 0x0005, value.GainLF);
                al.alEffectf(effect, 0x0006, value.DecayTime);
                al.alEffectf(effect, 0x0007, value.DecayHFRatio);
                al.alEffectf(effect, 0x0008, value.DecayLFRatio);
                al.alEffectf(effect, 0x0009, value.ReflectionsGain);
                al.alEffectf(effect, 0x000A, value.ReflectionsDelay);
                float* ReflectionsPan = stackalloc float[3] { value.ReflectionsPan.X, value.ReflectionsPan.Y, value.ReflectionsPan.Z };
                al.alEffectfv(effect, 0x000B, ReflectionsPan);
                al.alEffectf(effect, 0x000C, value.LateReverbGain);
                al.alEffectf(effect, 0x000D, value.LateReverbDelay);
                float* LateReverbPan = stackalloc float[3] { value.LateReverbPan.X, value.LateReverbPan.Y, value.LateReverbPan.Z };
                al.alEffectfv(effect, 0x000E, LateReverbPan);
                al.alEffectf(effect, 0x000F, value.EchoTime);
                al.alEffectf(effect, 0x0010, value.EchoDepth);
                al.alEffectf(effect, 0x0011, value.ModulationTime);
                al.alEffectf(effect, 0x0012, value.ModulationDepth);
                al.alEffectf(effect, 0x0013, value.AirAbsorptionGainHF);
                al.alEffectf(effect, 0x0014, value.HFReference);
                al.alEffectf(effect, 0x0015, value.LFReference);
                al.alEffectf(effect, 0x0016, value.RoomRolloffFactor);
                al.alEffecti(effect, 0x0017, (value.DecayHFLimit ? 1 : 0));
                break;
            }
            case ChorusSettings value:
            {
                al.alEffecti(effect, 0x0001, (int)value.Waveform);
                al.alEffecti(effect, 0x0002, value.Phase);
                al.alEffectf(effect, 0x0003, value.Rate);
                al.alEffectf(effect, 0x0004, value.Depth);
                al.alEffectf(effect, 0x0005, value.Feedback);
                al.alEffectf(effect, 0x0006, value.Delay);
                break;
            }
            case DistortionSettings value:
            {
                al.alEffectf(effect, 0x0001, value.Edge);
                al.alEffectf(effect, 0x0002, value.Gain);
                al.alEffectf(effect, 0x0003, value.LowPassCutoff);
                al.alEffectf(effect, 0x0004, value.EqCenter);
                al.alEffectf(effect, 0x0005, value.EqBandwidth);
                break;
            }
            case EchoSettings value:
            {
                al.alEffectf(effect, 0x0001, value.Delay);
                al.alEffectf(effect, 0x0002, value.LeftRightDelay);
                al.alEffectf(effect, 0x0003, value.Damping);
                al.alEffectf(effect, 0x0004, value.Feedback);
                al.alEffectf(effect, 0x0005, value.Spread);
                break;
            }
            case FlangerSettings value:
            {
                al.alEffecti(effect, 0x0001, (int)value.Waveform);
                al.alEffecti(effect, 0x0002, value.Phase);
                al.alEffectf(effect, 0x0003, value.Rate);
                al.alEffectf(effect, 0x0004, value.Depth);
                al.alEffectf(effect, 0x0005, value.Feedback);
                al.alEffectf(effect, 0x0006, value.Delay);
                break;
            }
            case FrequencyShifterSettings value:
            {
                al.alEffectf(effect, 0x0001, value.Frequency);
                al.alEffecti(effect, 0x0002, (int)value.LeftDirection);
                al.alEffecti(effect, 0x0003, (int)value.RightDirection);
                break;
            }
            case VocalMorpherSettings value:
            {
                al.alEffecti(effect, 0x0001, (int)value.PhonemeA);
                al.alEffecti(effect, 0x0002, value.PhonemeACoarseTuning);
                al.alEffecti(effect, 0x0003, (int)value.PhonemeB);
                al.alEffecti(effect, 0x0004, value.PhonemeBCoarseTuning);
                al.alEffecti(effect, 0x0005, (int)value.Waveform);
                al.alEffectf(effect, 0x0006, value.Rate);
                break;
            }
            case PitchShifterSettings value:
            {
                al.alEffecti(effect, 0x0001, value.CoarseTune);
                al.alEffecti(effect, 0x0002, value.FineTune);
                break;
            }
            case RingModulatorSettings value:
            {
                al.alEffectf(effect, 0x0001, value.Frequency);
                al.alEffectf(effect, 0x0002, value.HighPassCutoff);
                al.alEffecti(effect, 0x0003, (int)value.Waveform);
                break;
            }
            case AutowahSettings value:
            {
                al.alEffectf(effect, 0x0001, value.AttackTime);
                al.alEffectf(effect, 0x0002, value.ReleaseTime);
                al.alEffectf(effect, 0x0003, value.Resonance);
                al.alEffectf(effect, 0x0004, value.PeakGain);
                break;
            }
            case CompressorSettings value:
            {
                al.alEffecti(effect, 0x0001, (value.Enabled ? 1 : 0));
                break;
            }
            case EqualizerSettings value:
            {
                al.alEffectf(effect, 0x0001, value.LowGain);
                al.alEffectf(effect, 0x0002, value.LowCutoff);
                al.alEffectf(effect, 0x0003, value.Mid1Gain);
                al.alEffectf(effect, 0x0004, value.Mid1Center);
                al.alEffectf(effect, 0x0005, value.Mid1Width);
                al.alEffectf(effect, 0x0006, value.Mid2Gain);
                al.alEffectf(effect, 0x0007, value.Mid2Center);
                al.alEffectf(effect, 0x0008, value.Mid2Width);
                al.alEffectf(effect, 0x0009, value.HighGain);
                al.alEffectf(effect, 0x000A, value.HighCutoff);
                break;
            }
        }
        al.CheckAl("Apply effect settings");
    }
}
