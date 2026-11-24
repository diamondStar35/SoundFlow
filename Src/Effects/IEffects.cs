using SoundFlow.Devices;

namespace SoundFlow.Effects;

/// <summary>Creates shared auxiliary effect slots on a single initialized device.</summary>
public interface IEffects
{
    /// <summary>Explicitly supported parameter record types; no assembly scanning is used.</summary>
    IReadOnlyCollection<Type> SupportedEffects { get; }
    /// <summary>Number of auxiliary sends per source negotiated with the device.</summary>
    int MaximumSends { get; }
    /// <summary>Whether slot-to-slot routing is supported.</summary>
    bool SupportsSlotChaining { get; }
    /// <summary>Creates a slot with one effect. Unsupported effects and exhausted native resources throw.</summary>
    Slot CreateSlot(Settings settings);
}
