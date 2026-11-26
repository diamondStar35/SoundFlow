using SoundFlow.Structs;

namespace SoundFlow.Processing;

/// <summary>
/// Adapts a fixed-block processor to arbitrary producer chunks with one full block of constant buffering latency.
/// Partial input is accumulated; final padding is consumed only while draining and never becomes an extra input gap.
/// </summary>
public abstract class FixedBlockProcessor : Processor
{
    private readonly float[] _input;
    private readonly float[] _output;
    private readonly float[] _zeros;
    private int _cursor;
    private int _remaining = -1;
    /// <summary>Required complete input frames per ProcessBlock call.</summary>
    public int BlockFrames { get; }
    /// <inheritdoc />
    public sealed override int LatencyFrames => BlockFrames;
    /// <summary>Maximum additional effect tail beyond buffered input; subclasses report a finite bound.</summary>
    protected virtual int EffectTailFrames => 0;
    /// <inheritdoc />
    public sealed override int MaximumTailFrames => checked(BlockFrames + EffectTailFrames);
    /// <summary>Allocates fixed input/output blocks. Sample-rate changes are rejected when the chain binds.</summary>
    protected FixedBlockProcessor(AudioFormat input, AudioFormat output, int blockFrames) : base(input, output)
    {
        if (blockFrames is < 1 or > 65536) throw new ArgumentOutOfRangeException(nameof(blockFrames));
        BlockFrames = blockFrames;
        _input = new float[checked(blockFrames * input.Channels)]; _output = new float[checked(blockFrames * output.Channels)];
        _zeros = new float[_input.Length];
    }
    /// <summary>Processes exactly BlockFrames input/output frames. Output storage is cleared before each invocation.</summary>
    protected abstract void ProcessBlock(ReadOnlySpan<float> input, Span<float> output);
    /// <inheritdoc />
    public sealed override void Process(ReadOnlySpan<float> input, Span<float> output, int frames)
    { _remaining = -1; ProcessFrames(input, output, frames); }
    private void ProcessFrames(ReadOnlySpan<float> input, Span<float> output, int frames)
    {
        for (var frame = 0; frame < frames; frame++)
        {
            _output.AsSpan(_cursor * OutputFormat.Channels, OutputFormat.Channels).CopyTo(output[(frame * OutputFormat.Channels)..]);
            input.Slice(frame * InputFormat.Channels, InputFormat.Channels).CopyTo(_input.AsSpan(_cursor * InputFormat.Channels));
            if (++_cursor != BlockFrames) continue;
            _cursor = 0; Array.Clear(_output); ProcessBlock(_input, _output);
        }
    }
    /// <inheritdoc />
    public sealed override int Drain(Span<float> output)
    {
        if (_remaining < 0) _remaining = MaximumTailFrames;
        var frames = Math.Min(Math.Min(_remaining, BlockFrames), output.Length / OutputFormat.Channels);
        if (frames > 0) ProcessFrames(_zeros.AsSpan(0, frames * InputFormat.Channels), output, frames);
        _remaining -= frames; return frames;
    }
    /// <inheritdoc />
    public override void Reset() { _cursor = 0; _remaining = -1; Array.Clear(_input); Array.Clear(_output); }
}
