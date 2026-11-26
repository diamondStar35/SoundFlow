using SoundFlow.Processing;
using SoundFlow.Structs;

namespace BackendContract;

/// <summary>A processor compiled against public core contracts, without access to either native backend.</summary>
public sealed class StereoProcessor(AudioFormat input) : FixedBlockProcessor(input,
    input with { Channels = 2, Layout = ChannelLayout.Stereo }, 4)
{
    /// <inheritdoc />
    protected override void ProcessBlock(ReadOnlySpan<float> input, Span<float> output)
    {
        for (var frame = 0; frame < BlockFrames; frame++)
        {
            output[frame * 2] = input[frame];
            output[frame * 2 + 1] = -input[frame];
        }
    }
}
