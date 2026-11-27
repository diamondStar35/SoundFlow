using SoundFlow.Structs;
using Native = global::SteamAudio;

namespace SoundFlow.SteamAudio;

/// <summary>
/// Applies software distance attenuation and optional Steam Audio direct-path filtering. Coefficients
/// are read from the voice's current snapshot for every processing quantum, without running simulation.
/// </summary>
public sealed unsafe class DirectProcessor : Processor
{
    private readonly Native.Effects.Direct _effect = null!;
    private readonly nint _context;
    internal DirectProcessor(Renderer renderer, AudioFormat format, Spatialization state)
        : base(renderer, format, format, state)
    {
        try
        {
            if (format.Ambisonics != null)
                throw new ArgumentException("Apply direct-sound filtering before Ambisonics encoding.");
            _context = renderer.Context.DangerousGetHandle();
            _effect = new(renderer.Context, new(format.SampleRate, renderer.Settings.BlockFrames), new()
            {
                NumChannels = format.Channels
            });
            Warm();
        }
        catch
        {
            Dispose();
            throw;
        }
    }

    private protected override Native.Interop.AudioEffectState ApplyNative()
    {
        var settings = Warming ? new SpatializationSettings() : Spatialization.Current;
        var parameters = new Native.Interop.DirectEffectParams
        {
            Flags = Native.Interop.DirectEffectFlags.ApplyDistanceAttenuation,
            DistanceAttenuation = Spatialization.DistanceGain,
            Directivity = settings.Directivity ?? 1,
            Occlusion = settings.Occlusion ?? 1,
            TransmissionType = Native.Interop.TransmissionType.FrequencyDependent
        };
        if (!Warming && settings.AirAbsorption == null && settings.AirAbsorptionModel is { } airModel)
        {
            var model = new Native.Interop.AirAbsorptionModel();
            if (airModel.Coefficients is { } coefficients)
            {
                model.Type = Native.Interop.AirAbsorptionModelType.Exponential;
                model.Coefficients[0] = coefficients.Low;
                model.Coefficients[1] = coefficients.Mid;
                model.Coefficients[2] = coefficients.High;
            }
            var distance = airModel.Strength == 0 ? 0 : Spatialization.DistanceMetres * airModel.Strength;
            Native.Interop.Api.AirAbsorptionCalculate(new(_context), new()
            {
                X = distance
            }, default, &model, parameters.AirAbsorption);
            if (airModel.Coefficients is { } zeroBands)
            {
                if (zeroBands.Low == 0)
                    parameters.AirAbsorption[0] = 1;
                if (zeroBands.Mid == 0)
                    parameters.AirAbsorption[1] = 1;
                if (zeroBands.High == 0)
                    parameters.AirAbsorption[2] = 1;
            }
            parameters.Flags |= Native.Interop.DirectEffectFlags.ApplyAirAbsorption;
        }
        if (!Warming && settings.Directivity == null && settings.DirectivityModel is { } pattern)
        {
            var model = new Native.Interop.Directivity { DipoleWeight = pattern.DipoleWeight, DipolePower = pattern.DipolePower };
            var source = Spatialization.SourceCoordinates;
            var listener = settings.Relative ? default : Spatialization.Listener.Pose.Position;
            parameters.Directivity = Spatialization.Current.Position == listener ? 1 :
                Native.Interop.Api.DirectivityCalculate(new(_context), source,
                    new()
                    {
                        X = listener.X,
                        Y = listener.Y,
                        Z = listener.Z
                    }, &model);
            parameters.Flags |= Native.Interop.DirectEffectFlags.ApplyDirectivity;
        }
        if (settings.Directivity.HasValue)
            parameters.Flags |= Native.Interop.DirectEffectFlags.ApplyDirectivity;
        if (settings.Occlusion.HasValue)
            parameters.Flags |= Native.Interop.DirectEffectFlags.ApplyOcclusion;
        if (settings.AirAbsorption is { } air)
        {
            parameters.Flags |= Native.Interop.DirectEffectFlags.ApplyAirAbsorption;
            parameters.AirAbsorption[0] = air.Low;
            parameters.AirAbsorption[1] = air.Mid;
            parameters.AirAbsorption[2] = air.High;
        }
        if (settings.Transmission is { } transmission)
        {
            parameters.Flags |= Native.Interop.DirectEffectFlags.ApplyTransmission;
            parameters.Transmission[0] = transmission.Low;
            parameters.Transmission[1] = transmission.Mid;
            parameters.Transmission[2] = transmission.High;
        }
        return _effect.Process(parameters, Input, Output);
    }
    private protected override Native.Interop.AudioEffectState DrainNative() => _effect.GetTail(Output);
    private protected override int TailFrames => _effect.TailSize;
    private protected override void ResetNative() => _effect.Reset();
    private protected override void ReleaseNative() => _effect?.Dispose();
}
