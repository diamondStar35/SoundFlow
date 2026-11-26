using SoundFlow.Enums;
using SoundFlow.Interfaces;

namespace SoundFlow.Playback;

/// <summary>Converts decoded F32 frames between channel counts and sample rates using bounded linear interpolation.</summary>
public sealed class PcmFormatConvertingDecoder : ICancellableDecoder
{
    private readonly ISoundDecoder _inner;
    private readonly int _sourceChannels;
    private readonly int _sourceSampleRate;
    private readonly int _targetChannels;
    private readonly int _targetSampleRate;
    private float[] _sourceBuffer = [];
    private int _validSamples;
    private double _sourceFrameCursor;
    private bool _innerEnded;
    private CancellationToken _cancellation;
    private bool _endOfStreamRaised;

    /// <summary>Creates a converter which owns its input decoder. PCM is always represented as interleaved floats.</summary>
    public PcmFormatConvertingDecoder(ISoundDecoder inner, int targetChannels, int targetSampleRate)
    {
        _inner = inner ?? throw new ArgumentNullException(nameof(inner));
        _sourceChannels = Math.Max(1, inner.Channels);
        _sourceSampleRate = Math.Max(1, inner.SampleRate);
        _targetChannels = Math.Max(1, targetChannels);
        _targetSampleRate = Math.Max(1, targetSampleRate);

        if (inner.Channels is < 1 or > 8 || targetChannels is < 1 or > 8 || inner.SampleRate <= 0 || targetSampleRate <= 0) throw new ArgumentException("Expected 1 through 8 channels and positive sample rates.");
        _sourceBuffer = new float[8192 * _sourceChannels];
        _inner.EndOfStreamReached += InnerEnded;
        SampleFormat = inner.SampleFormat;
        Length = CalculateTargetLength(inner.Length);
    }

    private int DecodeInner(Span<float> samples) => _inner is ICancellableDecoder cooperative
        ? cooperative.Decode(samples, _cancellation) : _inner.Decode(samples);

    private void InnerEnded(object? sender, EventArgs args) => _innerEnded = true;

    /// <inheritdoc />
    /// <inheritdoc />
    public bool IsDisposed => _inner.IsDisposed;
    /// <inheritdoc />
    public int Length { get; }
    /// <inheritdoc />
    public SampleFormat SampleFormat { get; }
    /// <inheritdoc />
    public int Channels => _targetChannels;
    /// <inheritdoc />
    public int SampleRate => _targetSampleRate;

    /// <inheritdoc />
    public event EventHandler<EventArgs>? EndOfStreamReached;

    /// <inheritdoc />
    public int Decode(Span<float> samples) => Decode(samples, default);

    /// <inheritdoc />
    public int Decode(Span<float> samples, CancellationToken cancellationToken)
    {
        _cancellation = cancellationToken;
        cancellationToken.ThrowIfCancellationRequested();
        if (IsDisposed || samples.IsEmpty)
            return 0;

        samples.Clear();

        var targetFrames = samples.Length / _targetChannels;
        if (targetFrames <= 0)
            return 0;

        var sourceFramesPerTargetFrame = _sourceSampleRate / (double)_targetSampleRate;
        var requiredSourceFrames = Math.Max(2, (int)Math.Ceiling(_sourceFrameCursor + (targetFrames * sourceFramesPerTargetFrame)) + 1);
        EnsureBufferedSourceFrames(requiredSourceFrames);

        var availableSourceFrames = _validSamples / _sourceChannels;
        if (availableSourceFrames <= 0)
        {
            if (_innerEnded) RaiseEndOfStreamOnce();
            return 0;
        }

        var targetFrameIndex = 0;
        for (; targetFrameIndex < targetFrames; targetFrameIndex++)
        {
            var sourcePosition = _sourceFrameCursor + (targetFrameIndex * sourceFramesPerTargetFrame);
            var sourceFrame0 = (int)Math.Floor(sourcePosition);
            if (sourceFrame0 >= availableSourceFrames)
                break;

            var sourceFrame1 = Math.Min(sourceFrame0 + 1, Math.Max(0, availableSourceFrames - 1));
            var fraction = (float)(sourcePosition - sourceFrame0);
            WriteConvertedFrame(samples, targetFrameIndex, sourceFrame0, sourceFrame1, fraction);
        }

        AdvanceBufferedFrames(targetFrameIndex * sourceFramesPerTargetFrame);

        var samplesGenerated = targetFrameIndex * _targetChannels;
        if (samplesGenerated == 0 && _innerEnded)
            RaiseEndOfStreamOnce();

        return samplesGenerated;
    }

    /// <inheritdoc />
    public bool Seek(int offset)
    {
        if (IsDisposed || offset < 0)
            return false;

        var targetFrames = offset / _targetChannels;
        var sourceFrames = (int)Math.Round(targetFrames * (_sourceSampleRate / (double)_targetSampleRate));
        if (!_inner.Seek(sourceFrames * _sourceChannels))
            return false;

        _validSamples = 0;
        _sourceFrameCursor = 0d;
        _innerEnded = false;
        _endOfStreamRaised = false;
        return true;
    }

    /// <inheritdoc />
    public void Dispose()
    {
        EndOfStreamReached = null;
        _inner.EndOfStreamReached -= InnerEnded;
        _inner.Dispose();
    }

    private int CalculateTargetLength(int sourceLength)
    {
        if (sourceLength <= 0)
            return sourceLength;

        var sourceFrames = sourceLength / (double)_sourceChannels;
        var targetFrames = sourceFrames * _targetSampleRate / _sourceSampleRate;
        return (int)Math.Ceiling(targetFrames * _targetChannels);
    }

    private void EnsureBufferedSourceFrames(int requiredFrames)
    {
        while (!_innerEnded && (_validSamples / _sourceChannels) < requiredFrames)
        {
            var availableFrames = _validSamples / _sourceChannels;
            var framesToRead = Math.Max(64, requiredFrames - availableFrames);
            var writeOffset = _validSamples;
            EnsureCapacity(writeOffset + (framesToRead * _sourceChannels));

            var samplesRead = DecodeInner(_sourceBuffer.AsSpan(writeOffset, framesToRead * _sourceChannels));
            if (samplesRead <= 0)
            {
                break;
            }

            _validSamples += samplesRead;
        }
    }

    private void EnsureCapacity(int requiredSamples)
    {
        if (_sourceBuffer.Length >= requiredSamples)
            return;

        var nextSize = _sourceBuffer.Length == 0 ? requiredSamples : _sourceBuffer.Length;
        while (nextSize < requiredSamples)
            nextSize *= 2;

        Array.Resize(ref _sourceBuffer, nextSize);
    }

    private void AdvanceBufferedFrames(double framesAdvanced)
    {
        _sourceFrameCursor += framesAdvanced;
        var framesToDrop = (int)Math.Floor(_sourceFrameCursor);
        if (framesToDrop <= 0)
            return;

        framesToDrop = Math.Min(framesToDrop, Math.Max(0, (_validSamples / _sourceChannels) - 1));
        if (framesToDrop <= 0)
            return;

        var samplesToDrop = framesToDrop * _sourceChannels;
        var remainingSamples = _validSamples - samplesToDrop;
        if (remainingSamples > 0)
            Array.Copy(_sourceBuffer, samplesToDrop, _sourceBuffer, 0, remainingSamples);

        _validSamples = remainingSamples;
        _sourceFrameCursor -= framesToDrop;
    }

    private void WriteConvertedFrame(Span<float> destination, int targetFrame, int sourceFrame0, int sourceFrame1, float fraction)
    {
        Span<float> sourceFrame = stackalloc float[Math.Min(_sourceChannels, 8)];
        if (_sourceChannels > sourceFrame.Length)
            sourceFrame = new float[_sourceChannels];

        var base0 = sourceFrame0 * _sourceChannels;
        var base1 = sourceFrame1 * _sourceChannels;
        for (var channel = 0; channel < _sourceChannels; channel++)
        {
            var sample0 = _sourceBuffer[base0 + channel];
            var sample1 = _sourceBuffer[base1 + channel];
            sourceFrame[channel] = sample0 + ((sample1 - sample0) * fraction);
        }

        var targetBase = targetFrame * _targetChannels;
        if (_targetChannels == _sourceChannels)
        {
            sourceFrame.Slice(0, _targetChannels).CopyTo(destination.Slice(targetBase, _targetChannels));
            return;
        }

        if (_sourceChannels == 1)
        {
            var mono = sourceFrame[0];
            for (var channel = 0; channel < _targetChannels; channel++)
                destination[targetBase + channel] = mono;
            return;
        }

        if (_targetChannels == 1)
        {
            float sum = 0f;
            for (var channel = 0; channel < _sourceChannels; channel++)
                sum += sourceFrame[channel];
            destination[targetBase] = sum / _sourceChannels;
            return;
        }

        float average = 0f;
        for (var channel = 0; channel < _sourceChannels; channel++)
            average += sourceFrame[channel];
        average /= _sourceChannels;

        for (var channel = 0; channel < _targetChannels; channel++)
            destination[targetBase + channel] = channel < _sourceChannels ? sourceFrame[channel] : average;
    }

    private void RaiseEndOfStreamOnce()
    {
        if (_endOfStreamRaised)
            return;

        _endOfStreamRaised = true;
        EndOfStreamReached?.Invoke(this, EventArgs.Empty);
    }
}
