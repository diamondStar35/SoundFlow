using SoundFlow.Enums;
using SoundFlow.Interfaces;

namespace SoundFlow.Codecs.FFMpeg;

internal sealed class FormatConvertingDecoder : ISoundDecoder
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
    private bool _endOfStreamRaised;

    public FormatConvertingDecoder(ISoundDecoder inner, int targetChannels, int targetSampleRate)
    {
        _inner = inner ?? throw new ArgumentNullException(nameof(inner));
        _sourceChannels = Math.Max(1, inner.Channels);
        _sourceSampleRate = Math.Max(1, inner.SampleRate);
        _targetChannels = Math.Max(1, targetChannels);
        _targetSampleRate = Math.Max(1, targetSampleRate);

        SampleFormat = inner.SampleFormat;
        Length = CalculateTargetLength(inner.Length);
    }

    public bool IsDisposed => _inner.IsDisposed;
    public int Length { get; }
    public SampleFormat SampleFormat { get; }
    public int Channels => _targetChannels;
    public int SampleRate => _targetSampleRate;

    public event EventHandler<EventArgs>? EndOfStreamReached;

    public int Decode(Span<float> samples)
    {
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
            RaiseEndOfStreamOnce();
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

    public void Dispose()
    {
        EndOfStreamReached = null;
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

            var samplesRead = _inner.Decode(_sourceBuffer.AsSpan(writeOffset, framesToRead * _sourceChannels));
            if (samplesRead <= 0)
            {
                _innerEnded = true;
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
