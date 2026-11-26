using CoreSlot = SoundFlow.Effects.Slot;
using IEffects = SoundFlow.Effects.IEffects;
using Settings = SoundFlow.Effects.Settings;
using SettingsBinding = SoundFlow.Backends.OpenAL.Effects.SettingsBinding;
using Slot = SoundFlow.Backends.OpenAL.Effects.Slot;
using SoundFlow.Devices;
using SoundFlow.Effects;

namespace SoundFlow.Backends.OpenAL.Devices;

internal sealed unsafe partial class Context : IEffects
{
    private IReadOnlyCollection<Type> _supportedEffects = Array.Empty<Type>();
    public IReadOnlyCollection<Type> SupportedEffects => _supportedEffects;
    int IEffects.MaximumSends => MaximumSends;
    public bool SupportsSlotChaining => SlotChaining;

    private void InitializeEffects()
    {
        if (!HasEfx) return;
        var supported = new List<Type>();
        uint effect;
        Al.alGenEffects(1, &effect); Al.CheckAl("Probe effect support");
        try
        {
            foreach (var type in SettingsBinding.Types)
            {
                if (type == typeof(ConvolutionSettings) && !HasExtension("AL_SOFTX_convolution_effect")) continue;
                if ((type == typeof(DedicatedDialogueSettings) || type == typeof(DedicatedLowFrequencySettings)) && !HasExtension("ALC_EXT_DEDICATED")) continue;
                Al.alEffecti(effect, 0x8001, SettingsBinding.Kind(type));
                if (Al.alGetError() == 0) supported.Add(type);
            }
        }
        finally { Al.alDeleteEffects(1, &effect); }
        _supportedEffects = supported.AsReadOnly();
    }

    public CoreSlot CreateSlot(Settings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        settings.Validate();
        return Worker.Invoke(() =>
        {
            if (!SupportedEffects.Contains(settings.GetType())) throw new NotSupportedException($"Effect {settings.GetType().Name} is unavailable on this device.");
            var slot = new Slot(this, settings);
            Slots.Add(slot);
            return slot;
        });
    }
}
