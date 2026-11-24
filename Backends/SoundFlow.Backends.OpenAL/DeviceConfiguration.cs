using CoreDeviceConfiguration = SoundFlow.Devices.DeviceConfiguration;
using SoundFlow.Devices;
using SoundFlow.Features;

namespace SoundFlow.Backends.OpenAL;

/// <summary>OpenAL context and streaming configuration. Configure before opening a device.</summary>
public sealed class DeviceConfiguration : CoreDeviceConfiguration
{
    /// <summary>Frames per queue buffer; 512 by default. Must be 64 through 16384.</summary>
    public int BufferFrames { get; init; } = 512;
    /// <summary>Buffers per source; 4 by default. Must be 2 through 16.</summary>
    public int QueueBuffers { get; init; } = 4;
    /// <summary>Requested auxiliary sends per source; default 4. The actual limit is reported by SoundFlow.Effects.IEffects.</summary>
    public int AuxiliarySends { get; init; } = 4;
    /// <summary>Initial HRTF request. Automatic respects the implementation's configuration.</summary>
    public HrtfMode Hrtf { get; init; } = HrtfMode.Automatic;

    /// <summary>Optional physical output-mode request. Null respects native configuration; offline output uses its explicit channel layout.</summary>
    public Features.OutputMode? OutputMode { get; init; }
    /// <summary>Optional output limiter request. Null respects native configuration. Disabling it can reduce processing latency but allows clipping.</summary>
    public bool? OutputLimiter { get; init; }

    internal void Validate()
    {
        if (BufferFrames is < 64 or > 16384) throw new ArgumentOutOfRangeException(nameof(BufferFrames));
        if (QueueBuffers is < 2 or > 16) throw new ArgumentOutOfRangeException(nameof(QueueBuffers));
        if (AuxiliarySends is < 0 or > 64) throw new ArgumentOutOfRangeException(nameof(AuxiliarySends));
        if (OutputMode.HasValue && !Enum.IsDefined(OutputMode.Value)) throw new ArgumentOutOfRangeException(nameof(OutputMode));
        if (!Enum.IsDefined(Hrtf)) throw new ArgumentOutOfRangeException(nameof(Hrtf));
    }
}
