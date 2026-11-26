using SoundFlow.Abstracts;
using SoundFlow.Structs;

namespace SoundFlow.Processing;

/// <summary>Optional legacy-state metadata. TailFrames is a finite upper bound when processing silence after input ends.</summary>
public interface IModifierState
{
    /// <summary>Algorithmic buffering delay in frames; artistic delay is not buffering latency.</summary>
    int LatencyFrames { get; }
    /// <summary>Maximum frames required to release stored signal after input completion.</summary>
    int TailFrames { get; }
    /// <summary>Clears stored signal on seek or stop/restart.</summary>
    void Reset();
}

/// <summary>Explicit format-preserving adapter. Borrowed modifiers are not disposed; owned factory-created modifiers are.</summary>
public sealed class ModifierProcessor : Processor
{
    private readonly SoundModifier _modifier;
    private readonly bool _owns;
    private int _tail = -1;
    /// <summary>Wraps an existing modifier. External modifiers without metadata declare no latency or drainable tail.</summary>
    public ModifierProcessor(AudioFormat format, SoundModifier modifier, bool ownsModifier = false) : base(format, format)
    { _modifier = modifier ?? throw new ArgumentNullException(nameof(modifier)); _owns = ownsModifier; }
    /// <inheritdoc />
    public override int LatencyFrames => (_modifier as IModifierState)?.LatencyFrames ?? 0;
    /// <inheritdoc />
    public override int MaximumTailFrames => _modifier.Enabled ? (_modifier as IModifierState)?.TailFrames ?? 0 : 0;
    /// <inheritdoc />
    public override void Process(ReadOnlySpan<float> input, Span<float> output, int frames)
    {
        _tail = -1; input.CopyTo(output);
        if (_modifier.Enabled) _modifier.Process(output[..(frames * OutputFormat.Channels)], OutputFormat.Channels);
    }
    /// <inheritdoc />
    public override int Drain(Span<float> output)
    {
        if (_tail < 0) _tail = MaximumTailFrames;
        var frames = Math.Min(_tail, output.Length / OutputFormat.Channels);
        var samples = output[..(frames * OutputFormat.Channels)]; samples.Clear();
        if (frames > 0 && _modifier.Enabled) _modifier.Process(samples, OutputFormat.Channels);
        _tail -= frames; return frames;
    }
    /// <inheritdoc />
    public override void Reset() { _tail = -1; (_modifier as IModifierState)?.Reset(); }
    /// <inheritdoc />
    public override void Dispose() { if (_owns && _modifier is IDisposable disposable) disposable.Dispose(); }
}
