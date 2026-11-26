namespace SoundFlow.Effects;

/// <summary>Routes a wet dialogue signal toward the output's dialogue/centre presentation rather than treating it as a positional source.</summary>
/// <remarks>Requires dedicated-effect support. Stereo and headphone rendering follow the implementation's channel mapping; a physical centre speaker is not guaranteed.</remarks>
public sealed record DedicatedDialogueSettings : Settings
{
    /// <summary>Nonnegative linear gain applied by the dedicated effect; default one. The slot and send gains apply separately.</summary>
    public float Gain { get; init; } = 1;
    /// <inheritdoc />
    public override void Validate() => Range(Gain, 0, float.MaxValue, nameof(Gain));
}

/// <summary>Routes a wet signal to the output's low-frequency effects channel when the output layout provides one.</summary>
/// <remarks>This routes to LFE; it is not a general bass-boost or automatic low-pass effect. Stereo output need not reproduce an LFE send.</remarks>
public sealed record DedicatedLowFrequencySettings : Settings
{
    /// <summary>Nonnegative linear gain applied to the LFE route; default one.</summary>
    public float Gain { get; init; } = 1;
    /// <inheritdoc />
    public override void Validate() => Range(Gain, 0, float.MaxValue, nameof(Gain));
}
