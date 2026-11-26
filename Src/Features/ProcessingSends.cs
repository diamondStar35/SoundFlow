using SoundFlow.Effects;
using SoundFlow.Processing;

namespace SoundFlow.Features;

/// <summary>Settings for a native send from an explicitly captured software point.</summary>
public sealed record SendSettings
{
    /// <summary>Wet multiplier from zero through one. Source volume is applied separately and affects all paths.</summary>
    public float Gain { get; init; } = 1;
    /// <summary>Optional native filter for this wet path, independent of subsequent software/direct filtering.</summary>
    public FilterSettings? Filter { get; init; }
    /// <summary>No compensation by default. Automatic aligns all later buffering; ProcessorOptIn selects only explicitly marked processors.</summary>
    public LatencyCompensation Compensation { get; init; } = LatencyCompensation.None;
    /// <summary>Rejects invalid controls before native routes or processor storage change.</summary>
    public void Validate()
    {
        if (!float.IsFinite(Gain) || Gain < 0 || Gain > 1) throw new ArgumentOutOfRangeException(nameof(Gain));
        if (!Enum.IsDefined(Compensation)) throw new ArgumentOutOfRangeException(nameof(Compensation));
        Filter?.Validate();
    }
}

/// <summary>Optional native effects routing from earlier software stages. Shares indices and capacity with IEffectSends.</summary>
public interface IProcessingSends
{
    /// <summary>
    /// Binds an earlier point while stopped, or updates an existing route's gain/filter/target during playback.
    /// Null disconnects the route. Foreign points/slots are rejected. Compensation and point changes require stopped playback.
    /// </summary>
    void SetSend(int index, Point point, Slot? slot, SendSettings? settings = null);
}
