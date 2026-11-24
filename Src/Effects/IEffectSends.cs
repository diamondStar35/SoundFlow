using SoundFlow.Abstracts.Devices;

namespace SoundFlow.Effects;

/// <summary>Native direct filtering and wet sends, applied after software modifiers.</summary>
public interface IEffectSends
{
    /// <summary>Applies a direct-path filter; null restores the unfiltered path.</summary>
    void SetDirectFilter(FilterSettings? filter);
    /// <summary>Routes a send to a slot, or disconnects it with null. Gain is a wet multiplier from 0 to 1.</summary>
    void SetSend(int index, Slot? slot, float gain = 1, FilterSettings? filter = null);
}
