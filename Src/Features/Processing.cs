using SoundFlow.Enums;
using SoundFlow.Structs;

namespace SoundFlow.Features;

/// <summary>Identifies where a borrowed PCM buffer exists in the rendering pipeline.</summary>
[Flags]
public enum ProcessingStage
{
    /// <summary>No PCM tap is available.</summary>
    None = 0,
    /// <summary>Software-processed source input, before native spatialization and effects.</summary>
    SourceInput = 1,
    /// <summary>A rendered software graph, before native spatialization and effects.</summary>
    GraphOutput = 2,
    /// <summary>Captured microphone/input PCM.</summary>
    Capture = 4,
    /// <summary>The complete final output mix, including native effects when present.</summary>
    FinalOutput = 8
}

/// <summary>Metadata for an interleaved float buffer. Format describes the buffer, rather than a device's hardware storage type.</summary>
public readonly record struct BufferInfo(AudioFormat Format, ProcessingStage Stage, Capability Capability);

/// <summary>A borrowed interleaved F32 buffer. Do not retain the span or block; callbacks run on processing workers.</summary>
public delegate void BufferCallback(Span<float> samples, BufferInfo info);
