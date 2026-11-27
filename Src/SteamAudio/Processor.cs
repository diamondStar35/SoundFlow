using SoundFlow.Enums;
using SoundFlow.Structs;
using Native = global::SteamAudio;

namespace SoundFlow.SteamAudio;

/// <summary>
/// Fixed-quantum native processing with preallocated planar buffers. The owning render consumer gathers
/// the input quantum before calling Process, so this adapter contributes no extra buffering block.
/// </summary>
public abstract class Processor : Processing.Processor, Processing.IChainBinding
{
    private readonly int _frames;
    private int _tailBound;
    private bool _tailRemaining;
    private bool _disposed;
    private bool _bound;
    private object? _owner;
    private bool _prepared;
    private protected bool Warming { get; private set; }
    private protected Native.AudioBuffer Input { get; }
    private protected Native.AudioBuffer Output { get; }
    private protected Renderer Renderer { get; }
    private protected Spatialization Spatialization { get; }

    private protected Processor(Renderer renderer, AudioFormat input, AudioFormat output, Spatialization spatialization)
        : base(input, output)
    {
        ArgumentNullException.ThrowIfNull(renderer);
        ArgumentNullException.ThrowIfNull(spatialization);
        input.ValidateChannelLayout();
        output.ValidateChannelLayout();
        if (input.Format != SampleFormat.F32 || input.Channels is < 1 or > 32 || output.Channels is < 1 or > 32)
            throw new ArgumentException("Steam Audio processing requires F32 PCM and valid channel dimensions.");
        Renderer = renderer;
        Spatialization = spatialization;
        _frames = renderer.Settings.BlockFrames;
        Input = new(renderer.Context, input.Channels, _frames);
        try
        {
            Output = new(renderer.Context, output.Channels, _frames);
        }
        catch
        {
            Input.Dispose();
            throw;
        }
    }

    /// <inheritdoc />
    public sealed override int RequiredBlockFrames => _frames;
    /// <inheritdoc />
    public sealed override int MaximumTailFrames => _tailBound;
    private protected abstract Native.Interop.AudioEffectState ApplyNative();
    private protected abstract Native.Interop.AudioEffectState DrainNative();
    private protected abstract int TailFrames { get; }
    private protected abstract void ResetNative();
    private protected abstract void ReleaseNative();

    /// <summary>Warms native DSP and records a finite tail bound before it can reach a render callback.</summary>
    private protected void Warm()
    {
        Warming = true;
        try
        {
            Renderer.Warm(() =>
            {
                ApplyNative();
                _tailBound = checked((TailFrames + _frames - 1) / _frames * _frames);
                Reset();
            });
        }
        finally
        {
            Warming = false;
        }
    }

    void Processing.IChainBinding.Bind(object owner)
    {
        Spatialization.Bind(owner);
        _owner = owner;
        _bound = true;
    }
    void Processing.IChainBinding.BeginQuantum(long sequence)
    {
        Spatialization.Capture(Renderer, _owner!, sequence);
        _prepared = true;
    }
    void Processing.IChainBinding.BindRenderDomain(object domain) => Renderer.BindDomain(domain);
    bool Processing.IChainBinding.RequiresRenderDomainAffinity => true;

    /// <inheritdoc />
    public sealed override void Process(ReadOnlySpan<float> input, Span<float> output, int frames)
    {
        if (frames != _frames)
            throw new ArgumentException("A native effect requires one complete processing quantum.");
        if (!_prepared)
            Spatialization.Capture(Renderer);
        _prepared = false;
        Input.Deinterleave(input);
        _tailRemaining = ApplyNative() == Native.Interop.AudioEffectState.TailRemaining;
        Output.Interleave(output[..(_frames * OutputFormat.Channels)]);
    }

    /// <inheritdoc />
    public sealed override int Drain(Span<float> output)
    {
        if (!_tailRemaining || TailFrames == 0)
            return 0;
        _tailRemaining = DrainNative() == Native.Interop.AudioEffectState.TailRemaining;
        Output.Interleave(output[..(_frames * OutputFormat.Channels)]);
        return _frames;
    }

    /// <inheritdoc />
    public sealed override void Reset()
    {
        ResetNative();
        Input.Clear();
        Output.Clear();
        _tailRemaining = false;
    }

    /// <inheritdoc />
    public sealed override void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        try
        {
            ReleaseNative();
        }
        finally
        {
            try
            {
                Output.Dispose();
            }
            finally
            {
                Input.Dispose();
                if (_bound)
                    Spatialization.Unbind();
            }
        }
    }
}
