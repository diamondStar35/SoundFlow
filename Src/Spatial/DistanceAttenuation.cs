namespace SoundFlow.Spatial;

/// <summary>Software distance curves, independent of native backend distance-model tokens.</summary>
public enum AttenuationModel
{
    /// <summary>Constant unity gain.</summary>
    None,
    /// <summary>Inverse-distance falloff, bounded to zero through one.</summary>
    Inverse,
    /// <summary>Linear amplitude falloff with a hard maximum-distance cutoff.</summary>
    Linear,
    /// <summary>Linear distance mapped to a 33 dB amplitude range, followed by a hard cutoff.</summary>
    LinearDb,
    /// <summary>Power-law distance falloff.</summary>
    Exponential
}

/// <summary>
/// Software distance attenuation. Curve scaling changes the effective reference and maximum distances;
/// it does not scale world coordinates. Inverse and exponential curves do not use a maximum cutoff.
/// </summary>
public sealed record DistanceAttenuation
{
    /// <summary>Selected curve; LinearDb is the default.</summary>
    public AttenuationModel Model { get; init; } = AttenuationModel.LinearDb;
    /// <summary>Distance below which gain stays at unity. The evaluator floors it at 0.001.</summary>
    public float MinimumDistance { get; init; } = 1;
    /// <summary>Hard cutoff of the linear curves, before optional curve scaling.</summary>
    public float MaximumDistance { get; init; } = 200;
    /// <summary>Curve slope. Exponential falloff floors its exponent at 0.001.</summary>
    public float Rolloff { get; init; } = 1;
    /// <summary>Optional positive curve scale. Zero and negative values leave the scale disabled.</summary>
    public float? CurveScaler { get; init; }

    /// <summary>Rejects non-finite controls before they can enter a render snapshot.</summary>
    public void Validate()
    {
        if (!Enum.IsDefined(Model) || !float.IsFinite(MinimumDistance) || !float.IsFinite(MaximumDistance) ||
            !float.IsFinite(Rolloff) || Rolloff < 0 || CurveScaler.HasValue && !float.IsFinite(CurveScaler.Value))
            throw new ArgumentException("Distance controls must be finite and rolloff must be nonnegative.");
    }

    /// <summary>
    /// Evaluates the curve without allocation. LinearDb retains the low-level gain immediately before
    /// its cutoff; it intentionally does not extrapolate the decibel curve down to digital silence.
    /// </summary>
    public float Evaluate(float distance)
    {
        var minimum = Math.Max(0.001f, MinimumDistance);
        if (CurveScaler is > 0) minimum *= CurveScaler.Value;
        var maximum = Math.Max(minimum, MaximumDistance);
        if (CurveScaler is > 0) maximum *= CurveScaler.Value;
        if (maximum < minimum) maximum = minimum;
        if (Model == AttenuationModel.None || distance <= minimum) return 1;
        if (Model == AttenuationModel.Inverse)
            return Math.Clamp(minimum / (minimum + Rolloff * (distance - minimum)), 0, 1);
        if (Model == AttenuationModel.Exponential)
            return Math.Clamp(MathF.Pow(distance / minimum, -Math.Max(0.001f, Rolloff)), 0, 1);
        if (distance >= maximum) return 0;
        var linear = Math.Clamp(1f - (distance - minimum) / Math.Max(0.001f, maximum - minimum) * Rolloff, 0, 1);
        return Model == AttenuationModel.LinearDb ? MathF.Pow(10f, (1f - linear) * -33f / 20f) : linear;
    }
}
