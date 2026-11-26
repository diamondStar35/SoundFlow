using SoundFlow.Providers;
using SoundFlow.Components;
using SoundFlow.Enums;
using SoundFlow.Interfaces;
using SoundFlow.Structs;

namespace SoundFlow.Playback;

/// <summary>
/// Backend-independent PCM playback cursor with looping, reverse interpolation and positive-speed WSOLA processing.
/// It owns its provider unless leaveOpen is true. All control/read calls must be serialized by the consuming backend.
/// </summary>
public sealed class Transport : IDisposable
{
    private int _rawSamplePosition;
    private readonly bool _leaveOpen;
    private volatile bool _providerEnded;
    private float[] _reverseCache = [];
    private int _reverseCacheStart = -1;
    private int _reverseCacheFrames;
    private double _reverseCursor;
    private double _forwardClock;
    /// <summary>Format of complete interleaved frames supplied by the provider. Native output resampling is a backend responsibility.</summary>
    public AudioFormat Format { get; }
    /// <summary>Whether transport processing is enabled. Pausing retains all unread transport state.</summary>
    public bool Enabled { get; private set; }
    /// <summary>Whether provider subscriptions and owned resources have been released.</summary>
    public bool IsDisposed { get; private set; }
    /// <summary>Valid sample count produced by the latest read. The remainder of the destination is silence.</summary>
    public int LastReadSamples { get; private set; }
    /// <summary>Logical content cursor in interleaved samples, excluding WSOLA lookahead. This is not a native audible clock.</summary>
    public int SamplePosition => _rawSamplePosition;
    private void ProviderEnded(object? sender, EventArgs args) => _providerEnded = true;

    private readonly IDataProvider _dataProvider;
    private float _currentFractionalFrame;
    private float[] _resampleBuffer;
    private int _resampleBufferValidSamples;
    private float _playbackSpeed = 1.0f;
    private int _loopStartSamples;
    private int _loopEndSamples = -1;
    private WsolaTimeStretcher? _stretcher;
    private WsolaTimeStretcher _timeStretcher => _stretcher ??= new WsolaTimeStretcher(Format.Channels, _playbackSpeed, _timeStretchConfig);
    private CancellationToken _readCancellation;
    private int ReadProvider(Span<float> samples)
    {
        _readCancellation.ThrowIfCancellationRequested();
        return _dataProvider is ICancellableDataProvider cooperative
            ? cooperative.ReadSamples(samples, _readCancellation) : _dataProvider.ReadSamples(samples);
    }
    private float[] _timeStretcherInputBuffer;
    private int _timeStretcherInputBufferValidSamples;
    private int _timeStretcherInputBufferReadOffset;
    private WsolaConfig _timeStretchConfig;

    /// <summary>Signed transport speed. Positive values use pitch-preserving WSOLA; negative values read seekable input backwards with interpolation.</summary>
    public float PlaybackSpeed
    {
        get => _playbackSpeed;
        set
        {
            if (!float.IsFinite(value) || value == 0)
                throw new ArgumentOutOfRangeException(nameof(value), "Playback speed must be greater than or less than zero.");

            // If the sign of the speed changes (e.g., forward to reverse), reset internal buffers
            // to prevent processing stale data in the wrong direction.
            if (Math.Sign(_playbackSpeed) != Math.Sign(value) && _playbackSpeed != 0)
            {
                _stretcher?.Reset();
                _resampleBufferValidSamples = 0;
                _timeStretcherInputBufferValidSamples = 0;
                _timeStretcherInputBufferReadOffset = 0;
                _currentFractionalFrame = 0f;
                _reverseCursor = (double)_rawSamplePosition / Format.Channels;
                _reverseCacheStart = -1;
            }

            if (Math.Abs(_playbackSpeed - value) > 1e-6f)
            {
                _playbackSpeed = value;
                // WSOLA does not support negative speeds; we bypass it for reverse playback.
                if (value > 0)
                {
                    _timeStretcher.SetSpeed(_playbackSpeed);
                }
            }
        }
    }

    /// <summary>
    /// Gets or sets the detailed configuration for the WSOLA time stretcher.
    /// Allows fine-tuning of window size, hop size, and search radius.
    /// </summary>
    public WsolaConfig TimeStretchConfig
    {
        get => _timeStretchConfig;
        set
        {
            ArgumentNullException.ThrowIfNull(value);
            if (_timeStretchConfig != value)
            {
                _timeStretchConfig = value;
                _timeStretcher.Configure(value);
                EnsureTimeStretcherBufferSize();
            }
        }
    }

    /// <summary>
    /// Sets the time stretcher configuration based on a performance preset.
    /// </summary>
    /// <param name="preset">The desired balance between quality and performance.</param>
    public void SetTimeStretchQuality(WsolaPerformancePreset preset)
    {
        TimeStretchConfig = WsolaConfig.FromPreset(preset);
    }

    /// <summary>Transport processing state. Stopped input can still have decoded samples queued in a native voice.</summary>
    public PlaybackState State { get; internal set; }

    /// <summary>The leased input provider. Callers must not read or seek it concurrently with this transport.</summary>
    public IDataProvider DataProvider => _dataProvider;

    private bool _isLooping;
    /// <summary>Whether reads wrap at the configured exclusive loop end. Enabling looping requires a seekable provider.</summary>
    public bool IsLooping
    {
        get => _isLooping;
        set
        {
            if (value && !_dataProvider.CanSeek) throw new NotSupportedException("Looping requires a seekable provider.");
            _isLooping = value;
        }
    }

    /// <summary>
    /// Occurs when playback ends.
    /// </summary>
    public event EventHandler<EventArgs>? PlaybackEnded;

    /// <summary>Logical content time in seconds, excluding decoder/WSOLA lookahead and any native queue latency.</summary>
    public float Time =>
        Format.Channels == 0 || Format.SampleRate == 0
            ? 0
            : (float)_rawSamplePosition / Format.Channels / Format.SampleRate;

    /// <summary>Estimated content duration in seconds. Zero means the provider has no positive length estimate.</summary>
    public float Duration =>
        _dataProvider.Length <= 0 || Format.Channels == 0 || Format.SampleRate == 0
            ? 0f
            : (float)_dataProvider.Length / Format.Channels / Format.SampleRate;

    /// <summary>Inclusive loop start in interleaved samples, aligned to a complete frame.</summary>
    public int LoopStartSamples => _loopStartSamples;

    /// <summary>Exclusive loop end in interleaved samples, or -1 for the natural input end.</summary>
    public int LoopEndSamples => _loopEndSamples;

    /// <summary>Inclusive loop start converted to content seconds.</summary>
    public float LoopStartSeconds => (Format.Channels == 0 || Format.SampleRate == 0)
        ? 0
        : (float)_loopStartSamples / Format.Channels / Format.SampleRate;

    /// <summary>Exclusive loop end converted to seconds; -1 retains the natural-end sentinel.</summary>
    public float LoopEndSeconds =>
        _loopEndSamples == -1 || Format.Channels == 0 || Format.SampleRate == 0
            ? -1
            : (float)_loopEndSamples / Format.Channels / Format.SampleRate;


    /// <summary>
    /// Creates a transport using the provider format. No output device or native resources are opened.
    /// </summary>
    /// <param name="format">Positive-rate interleaved source format; channel counts are never inferred from output hardware.</param>
    /// <param name="dataProvider">The sound data provider.</param>
    /// <param name="leaveOpen">Whether the caller retains ownership of the provider.</param>
    /// <exception cref="ArgumentNullException">Thrown if dataProvider is null.</exception>
    public Transport(AudioFormat format, IDataProvider dataProvider, bool leaveOpen = false)
    {
        Format = format;
        _leaveOpen = leaveOpen;
        _dataProvider = dataProvider ?? throw new ArgumentNullException(nameof(dataProvider));
        _dataProvider.EndOfStreamReached += ProviderEnded;
        _resampleBuffer = [];
        

        // Initialize with default fast config
        _timeStretchConfig = WsolaConfig.FromPreset(WsolaPerformancePreset.Fast);
        _timeStretcherInputBuffer = [];
    }

    /// <summary>Fills complete interleaved frames and clears any unproduced remainder. LastReadSamples identifies valid PCM; use Read for explicit starvation/completion status.</summary>
    public void Render(Span<float> output, int channels) => Render(output, channels, default);

    /// <summary>Renders a serialized block with cooperative cancellation of underlying reads.</summary>
    public void Render(Span<float> output, int channels, CancellationToken cancellationToken)
    {
        _readCancellation = cancellationToken;
        cancellationToken.ThrowIfCancellationRequested();
        LastReadSamples = 0;
        // Clear output if not playing or no channels.
        if (State != PlaybackState.Playing || channels == 0)
        {
            output.Clear();
            return;
        }

        if (_playbackSpeed < 0)
        {
            RenderReverse(output, channels);
            return;
        }
        if (Math.Abs(_playbackSpeed - 1f) < 0.001f)
        {
            output.Clear();
            var written = 0;
            while (written < output.Length)
            {
                var read = ReadForward(output[written..]);
                if (read == 0)
                {
                    if (_providerEnded || _dataProvider.Length > 0) Finish();
                    break;
                }
                written += read;
                _rawSamplePosition = _dataProvider.Position;
                _forwardClock = _rawSamplePosition;
            }
            LastReadSamples = written;
            return;
        }

        EnsureTimeStretcherBufferSize();
        // Ensure time stretcher has correct channel count.
        if (_playbackSpeed > 0 && _timeStretcher.GetTargetSpeed() == 0f && _playbackSpeed != 0f && channels > 0)
            _timeStretcher.SetChannels(channels);

        var outputFramesTotal = output.Length / channels;
        var outputBufferOffset = 0;
        var totalSourceSamplesAdvancedThisCall = 0; // Total samples advanced in the original source.

        for (var i = 0; i < outputFramesTotal; i++)
        {
            var currentIntegerFrame = (int)Math.Floor(_currentFractionalFrame);
            // We need 2 frames for linear interpolation (current and next).
            var samplesRequiredInBufferForInterpolation = (currentIntegerFrame + 2) * channels;

            // Fill _resampleBuffer if not enough data for interpolation.
            if (_resampleBufferValidSamples < samplesRequiredInBufferForInterpolation)
            {
                var sourceSamplesForFill = FillResampleBuffer(samplesRequiredInBufferForInterpolation, channels);
                totalSourceSamplesAdvancedThisCall += sourceSamplesForFill;

                // If still not enough data after filling and the provider is truly exhausted and can't provide more data, end of stream.
                if (_resampleBufferValidSamples < samplesRequiredInBufferForInterpolation)
                {
                    HandleEndOfStream(output[outputBufferOffset..], channels);
                    AdvanceClock(LastReadSamples);
                    return;
                }
            }

            // Perform linear interpolation.
            var frameIndex0 = currentIntegerFrame;
            var t = _currentFractionalFrame - frameIndex0;
            for (var ch = 0; ch < channels; ch++)
            {
                var sampleIndex0 = frameIndex0 * channels + ch;
                var sampleIndex1 = (frameIndex0 + 1) * channels + ch;
                if (sampleIndex1 >= _resampleBufferValidSamples)
                {
                    // If next sample is out of bounds, use current or 0.
                    output[outputBufferOffset + ch] = (sampleIndex0 < _resampleBufferValidSamples && sampleIndex0 >= 0) ? _resampleBuffer[sampleIndex0] : 0f;
                    continue;
                }
                if (sampleIndex0 < 0)
                {
                    output[outputBufferOffset + ch] = 0f;
                    continue;
                }
                // Interpolate sample value.
                output[outputBufferOffset + ch] = _resampleBuffer[sampleIndex0] * (1.0f - t) + _resampleBuffer[sampleIndex1] * t;
            }

            outputBufferOffset += channels;
            LastReadSamples = outputBufferOffset;
            _currentFractionalFrame += 1.0f;

            // Discard consumed samples from the resample buffer.
            var framesConsumedFromResampleBuffer = (int)Math.Floor(_currentFractionalFrame);
            if (framesConsumedFromResampleBuffer > 0)
            {
                var samplesConsumedFromResampleBuf = framesConsumedFromResampleBuffer * channels;
                var actualDiscard = Math.Min(samplesConsumedFromResampleBuf, _resampleBufferValidSamples);
                if (actualDiscard > 0)
                {
                    var remaining = _resampleBufferValidSamples - actualDiscard;
                    if (remaining > 0) // Shift remaining samples to the beginning.
                        Buffer.BlockCopy(_resampleBuffer, actualDiscard * sizeof(float), _resampleBuffer, 0, remaining * sizeof(float));
                    _resampleBufferValidSamples = remaining;
                }
                _currentFractionalFrame -= framesConsumedFromResampleBuffer;
            }
        }

        // Update raw sample position based on actual source samples advanced.
        AdvanceClock(LastReadSamples);
    }

    /// <summary>
    /// Fills the internal resample buffer using the time stretcher and data provider.
    /// </summary>
    /// <param name="minSamplesRequiredInOutputBuffer">Minimum samples needed in _resampleBuffer.</param>
    /// <param name="channels">The number of channels to process.</param>
    /// <returns>The total number of original source samples advanced by this fill operation.</returns>
    private int FillResampleBuffer(int minSamplesRequiredInOutputBuffer, int channels)
    {
        if (channels == 0) return 0;

        // Resize the resampling buffer if too small.
        if (_resampleBuffer.Length < minSamplesRequiredInOutputBuffer)
        {
            Array.Resize(ref _resampleBuffer,
                Math.Max(minSamplesRequiredInOutputBuffer, Math.Max(8192 * channels, _resampleBuffer.Length * 2)));
        }

        // When playback speed is close to 1.0, use simpler interpolation.
        if (Math.Abs(_playbackSpeed - 1.0f) < 0.001f)
        {
            // Implement a persistent read loop instead of a single read call.
            var totalDirectRead = 0;
            var directReadSlice = _resampleBuffer.AsSpan(_resampleBufferValidSamples);

            while (!directReadSlice.IsEmpty)
            {
                var readThisCall = ReadForward(directReadSlice);
                if (readThisCall == 0)
                {
                    break; // True end of stream.
                }
                totalDirectRead += readThisCall;
                directReadSlice = directReadSlice.Slice(readThisCall);
            }

            _resampleBufferValidSamples += totalDirectRead;
            return totalDirectRead;
        }

        var totalSourceSamplesRepresented = 0;
        // Forward playback with time stretching
        // Loop to fill _resampleBuffer until minimum required samples are met.
        while (_resampleBufferValidSamples < minSamplesRequiredInOutputBuffer)
        {
            var spaceAvailableInResampleBuffer = _resampleBuffer.Length - _resampleBufferValidSamples;
            if (spaceAvailableInResampleBuffer == 0) break;

            var availableInStretcherInput =
                _timeStretcherInputBufferValidSamples - _timeStretcherInputBufferReadOffset;

            // Use a flag to track the true end of the data provider.
            var providerExhausted = false;

            // If time stretcher input buffer needs more data.
            if (availableInStretcherInput < _timeStretcher.MinInputSamplesToProcess)
            {
                // Compact the buffer by moving the remaining valid samples to the start if we have a read offset.
                if (_timeStretcherInputBufferReadOffset > 0)
                {
                    // Calculate remaining samples. It should not be negative, but we defend against it.
                    var remaining = _timeStretcherInputBufferValidSamples - _timeStretcherInputBufferReadOffset;
                    if (remaining > 0)
                    {
                        // Shift the remaining valid data to the beginning of the input buffer.
                        Buffer.BlockCopy(_timeStretcherInputBuffer, _timeStretcherInputBufferReadOffset * sizeof(float),
                            _timeStretcherInputBuffer, 0, remaining * sizeof(float));
                        _timeStretcherInputBufferValidSamples = remaining;
                    }
                    else
                    {
                        // If no samples remain, the buffer is effectively empty.
                        _timeStretcherInputBufferValidSamples = 0;
                    }
                    // After compacting, the next read position is the start of the buffer.
                    _timeStretcherInputBufferReadOffset = 0;
                }

                // Read more data from the data provider into the time stretcher input buffer.
                var spaceToReadIntoInput = _timeStretcherInputBuffer.Length - _timeStretcherInputBufferValidSamples;
                if (spaceToReadIntoInput > 0)
                {
                    // Use a persistent read loop to fill the available space.
                    var targetSpan = _timeStretcherInputBuffer.AsSpan(_timeStretcherInputBufferValidSamples, spaceToReadIntoInput);
                    var totalReadFromProvider = 0;
                    while (!targetSpan.IsEmpty)
                    {
                        var readThisCall = ReadForward(targetSpan);
                        if (readThisCall == 0)
                        {
                            providerExhausted = true; // True end of stream signaled.
                            break;
                        }
                        totalReadFromProvider += readThisCall;
                        targetSpan = targetSpan.Slice(readThisCall);
                    }

                    _timeStretcherInputBufferValidSamples += totalReadFromProvider;
                    availableInStretcherInput = _timeStretcherInputBufferValidSamples - _timeStretcherInputBufferReadOffset;
                }
            }

            // Prepare spans for time stretcher processing.
            var inputSpanForStretcher = ReadOnlySpan<float>.Empty;
            if (availableInStretcherInput > 0)
            {
                inputSpanForStretcher = _timeStretcherInputBuffer.AsSpan(_timeStretcherInputBufferReadOffset,
                    availableInStretcherInput);
            }

            var outputSpanForStretcher =
                _resampleBuffer.AsSpan(_resampleBufferValidSamples, spaceAvailableInResampleBuffer);
            int samplesWrittenToResample, samplesConsumedFromStretcherInputBuf, sourceSamplesForThisProcessCall;

            // Determine how to call the time stretcher (Process or Flush).
            if (inputSpanForStretcher.IsEmpty && providerExhausted)
            {
                // If the input buffer for the stretcher is empty AND we know the provider is exhausted, flush the stretcher.
                samplesWrittenToResample = _timeStretcher.Flush(outputSpanForStretcher);
                samplesConsumedFromStretcherInputBuf = 0;
                sourceSamplesForThisProcessCall = 0;
            }
            else if (availableInStretcherInput >= _timeStretcher.MinInputSamplesToProcess)
            {
                // If there's enough input, process it.
                samplesWrittenToResample = _timeStretcher.Process(inputSpanForStretcher, outputSpanForStretcher,
                    out samplesConsumedFromStretcherInputBuf,
                    out sourceSamplesForThisProcessCall);
            }
            else
            {
                break; // Not enough input to process, and the provider is not yet exhausted.
            }

            // Update read offset and valid samples for time stretcher input buffer.
            if (samplesConsumedFromStretcherInputBuf > 0) _timeStretcherInputBufferReadOffset += samplesConsumedFromStretcherInputBuf;

            // Update resample buffer valid samples and total source samples advanced.
            _resampleBufferValidSamples += samplesWrittenToResample;
            totalSourceSamplesRepresented += sourceSamplesForThisProcessCall;

            // Break if no progress was made and no more data is expected.
            if (samplesWrittenToResample == 0 && samplesConsumedFromStretcherInputBuf == 0 && providerExhausted) break;
        }

        return totalSourceSamplesRepresented;
    }

    private void EnsureTimeStretcherBufferSize()
    {
        var minSize = _timeStretcher.MinInputSamplesToProcess;
        // Keep the logic similar to constructor: * 2 or min 8192*channels
        var targetSize = Math.Max(minSize * 2, 8192 * Format.Channels);

        if (_timeStretcherInputBuffer.Length < targetSize)
        {
            // Compact existing data to start before resizing
            if (_timeStretcherInputBufferValidSamples > 0)
            {
                if (_timeStretcherInputBufferReadOffset > 0)
                {
                    var remaining = _timeStretcherInputBufferValidSamples - _timeStretcherInputBufferReadOffset;
                    if (remaining > 0)
                    {
                        Buffer.BlockCopy(_timeStretcherInputBuffer, _timeStretcherInputBufferReadOffset * sizeof(float),
                            _timeStretcherInputBuffer, 0, remaining * sizeof(float));
                    }
                    _timeStretcherInputBufferValidSamples = remaining;
                    _timeStretcherInputBufferReadOffset = 0;
                }
            }
            else
            {
                _timeStretcherInputBufferValidSamples = 0;
                _timeStretcherInputBufferReadOffset = 0;
            }

            Array.Resize(ref _timeStretcherInputBuffer, targetSize);
        }
    }

    /// <summary>
    /// Handles the end-of-stream condition, including looping and stopping.
    /// This is called when the data provider is fully exhausted (ReadBytes returns 0).
    /// </summary>
    /// <param name="remainingOutputBuffer">The buffer for remaining output.</param>
    /// <param name="channels">The number of channels.</param>
    private void HandleEndOfStream(Span<float> remainingOutputBuffer, int channels)
    {
        // Not looping, and it's a file with a known length. This is the definitive end.
        if (!IsLooping && (_dataProvider.Length > 0 || _providerEnded))
        {
            // Original end-of-stream handling
            if (!remainingOutputBuffer.IsEmpty)
            {
                var spaceToFill = remainingOutputBuffer.Length;
                var currentlyValidInResample = _resampleBufferValidSamples;

                if (currentlyValidInResample < spaceToFill)
                {
                    FillResampleBuffer(Math.Max(currentlyValidInResample, spaceToFill), channels);
                }

                var toCopy = Math.Min(spaceToFill, _resampleBufferValidSamples);
                if (toCopy > 0)
                {
                    SafeCopyTo(_resampleBuffer.AsSpan(0, toCopy), remainingOutputBuffer.Slice(0, toCopy));
                    LastReadSamples += toCopy;
                    var remainingInResampleAfterCopy = _resampleBufferValidSamples - toCopy;
                    if (remainingInResampleAfterCopy > 0)
                    {
                        Buffer.BlockCopy(_resampleBuffer, toCopy * sizeof(float), _resampleBuffer, 0,
                            remainingInResampleAfterCopy * sizeof(float));
                    }

                    _resampleBufferValidSamples = remainingInResampleAfterCopy;
                    if (toCopy < spaceToFill)
                    {
                        remainingOutputBuffer.Slice(toCopy).Clear();
                    }
                }
                else
                {
                    remainingOutputBuffer.Clear();
                }
            }

            State = PlaybackState.Stopped;
            OnPlaybackEnded();
        }
        // Looping is enabled.
        else if (IsLooping)
        {
            var targetLoopStart = Math.Max(0, _loopStartSamples);
            var actualLoopEnd = (_loopEndSamples == -1)
                ? _dataProvider.Length
                : Math.Min(_loopEndSamples, _dataProvider.Length);

            // Check if the loop is valid (start < end, and start is within bounds).
            if (targetLoopStart < actualLoopEnd && targetLoopStart < _dataProvider.Length)
            {
                // Forward playback seeks to start. Reverse seeks to end.
                Seek(_playbackSpeed < 0 ? actualLoopEnd : targetLoopStart, channels);
                if (!remainingOutputBuffer.IsEmpty)
                    Render(remainingOutputBuffer, channels);
            }
            else
            {
                // Loop is not valid (e.g., start >= end), so treat as a normal end-of-stream.
                State = PlaybackState.Stopped;
                OnPlaybackEnded();
                remainingOutputBuffer.Clear();
            }
        }
        // For live streams (Length <= 0), just clear the buffer and continue
        else
        {
            remainingOutputBuffer.Clear();
        }
    }

    /// <summary>
    /// Invokes the PlaybackEnded event.
    /// </summary>
    private void OnPlaybackEnded()
    {
        PlaybackEnded?.Invoke(this, EventArgs.Empty);
        var isEffectivelyLooping = IsLooping && (_loopEndSamples == -1 || _loopStartSamples < _loopEndSamples) &&
                                   _loopStartSamples < _dataProvider.Length;
        // If not effectively looping, disable the component.
        if (!isEffectivelyLooping) Enabled = false;
    }

    #region Audio Playback Control

    /// <summary>Reads processed interleaved F32 samples without confusing a temporarily empty live input with completion.</summary>
    /// <remarks>Call from one processing worker. Control operations must be serialized with reads by the backend.</remarks>
    public ReadResult Read(Span<float> output)
    {
        Render(output, Format.Channels);
        return new ReadResult(LastReadSamples, State == PlaybackState.Stopped ? ReadStatus.Completed : LastReadSamples == 0 ? ReadStatus.Starved : ReadStatus.Data);
    }

    /// <summary>Enables reading from the current cursor. Restart/rewind policy after completion belongs to the source adapter.</summary>
    public void Play()
    {
        Enabled = true;
        State = PlaybackState.Playing;
    }

    /// <summary>Suspends reads without rewinding or discarding transport lookahead.</summary>
    public void Pause()
    {
        Enabled = false;
        State = PlaybackState.Paused;
    }

    /// <summary>Disables reads, clears lookahead and rewinds seekable input. Nonseekable input retains its consumed cursor.</summary>
    public void Stop()
    {
        State = PlaybackState.Stopped;
        Enabled = false;
        Seek(0, Format.Channels);
        _stretcher?.Reset();
        _resampleBufferValidSamples = 0;
        Array.Clear(_resampleBuffer, 0, _resampleBuffer.Length);
        _timeStretcherInputBufferValidSamples = 0;
        _timeStretcherInputBufferReadOffset = 0;
        Array.Clear(_timeStretcherInputBuffer, 0, _timeStretcherInputBuffer.Length);
        _currentFractionalFrame = 0f;
    }

    /// <summary>Seeks relative to the beginning, current logical time or known duration; successful seeks invalidate all lookahead.</summary>
    public bool Seek(TimeSpan time, SeekOrigin seekOrigin = SeekOrigin.Begin)
    {
        if (Format.Channels == 0 || Format.SampleRate == 0) return false;
        float targetTimeSeconds;
        var currentDuration = Duration;
        switch (seekOrigin)
        {
            case SeekOrigin.Begin:
                targetTimeSeconds = (float)time.TotalSeconds;
                break;
            case SeekOrigin.Current:
                targetTimeSeconds = Time + (float)time.TotalSeconds;
                break;
            case SeekOrigin.End:
                // If duration is 0, treat as seeking relative to 0.
                targetTimeSeconds = (currentDuration > 0 ? currentDuration : 0) + (float)time.TotalSeconds;
                break;
            default: return false;
        }

        // Clamp target time within valid duration.
        targetTimeSeconds = currentDuration > 0 ? Math.Clamp(targetTimeSeconds, 0, currentDuration) : Math.Max(0, targetTimeSeconds);
        return Seek(targetTimeSeconds, Format.Channels);
    }

    /// <summary>Seeks to content seconds from the beginning, clamping within known input bounds. Returns false for nonseekable input.</summary>
    public bool Seek(float timeInSeconds)
    {
        return Seek(timeInSeconds, Format.Channels);
    }

    private bool Seek(float timeInSeconds, int channels)
    {
        if (channels == 0 || Format.SampleRate == 0) return false;
        timeInSeconds = Math.Max(0, timeInSeconds);
        // Convert time in seconds to sample offset in source data.
        var sampleOffset = checked((int)(timeInSeconds * Format.SampleRate * channels));
        return Seek(sampleOffset, channels);
    }

    /// <summary>Seeks to an interleaved sample offset, aligned to a complete frame. The exclusive input end is permitted for reverse playback.</summary>
    public bool Seek(int sampleOffset)
    {
        return Seek(sampleOffset, Format.Channels);
    }

    private bool Seek(int sampleOffset, int channels)
    {
        if (!_dataProvider.CanSeek || channels == 0) return false;

        var maxSeekableSample = _dataProvider.Length > 0 ? _dataProvider.Length : int.MaxValue - channels;
        maxSeekableSample = Math.Max(0, maxSeekableSample);
        // Align sample offset to frame boundary.
        sampleOffset = (sampleOffset / channels) * channels;
        sampleOffset = Math.Clamp(sampleOffset, 0, maxSeekableSample);
        _dataProvider.Seek(sampleOffset);
        _providerEnded = false;
        _rawSamplePosition = sampleOffset;
        _forwardClock = sampleOffset;
        _reverseCursor = (double)sampleOffset / channels;
        _reverseCacheStart = -1;
        _currentFractionalFrame = 0f;
        _resampleBufferValidSamples = 0;
        _stretcher?.Reset();
        _timeStretcherInputBufferValidSamples = 0;
        _timeStretcherInputBufferReadOffset = 0;
        return true;
    }

    #endregion

    private static void SafeCopyTo(Span<float> source, Span<float> destination)
    {
        for (var i = 0; i < Math.Min(source.Length, destination.Length); i++)
        {
            destination[i] = Math.Clamp(source[i], -1f, 1f);
        }
    }

    #region Loop Point Configuration Methods

    /// <summary>Sets loop boundaries in content seconds. A null or -1 end selects the natural end; the end boundary is exclusive.</summary>
    public void SetLoopPoints(float startTime, float? endTime = null)
    {
        var channels = Format.Channels;
        var sampleRate = Format.SampleRate;
        if (channels == 0 || sampleRate == 0) return;

        if (startTime < 0)
            throw new ArgumentOutOfRangeException(nameof(startTime), "Loop start time cannot be negative.");

        var effectiveEndTime = endTime ?? -1f;
        if (Math.Abs(effectiveEndTime - -1f) > 1e-6f && effectiveEndTime < startTime)
            throw new ArgumentOutOfRangeException(nameof(endTime),
                "Loop end time must be greater than or equal to start time, or -1.");

        // Convert seconds to samples.
        _loopStartSamples = (int)(startTime * sampleRate * channels);
        _loopEndSamples = Math.Abs(effectiveEndTime - -1f) < 1e-6f
            ? -1
            : (int)(effectiveEndTime * sampleRate * channels);

        // Align to frame boundaries and clamp within data provider length.
        _loopStartSamples = (_loopStartSamples / channels) * channels;
        _loopStartSamples = Math.Clamp(_loopStartSamples, 0, _dataProvider.Length > 0 ? _dataProvider.Length : int.MaxValue);

        if (_loopEndSamples != -1)
        {
            _loopEndSamples = _loopEndSamples / channels * channels;
            _loopEndSamples = Math.Clamp(_loopEndSamples, _loopStartSamples, _dataProvider.Length > 0 ? _dataProvider.Length : int.MaxValue);
        }
    }

    /// <summary>Sets frame-aligned interleaved sample boundaries. Rejects a negative start or an end preceding the start.</summary>
    public void SetLoopPoints(int startSample, int endSample = -1)
    {
        var channels = Format.Channels;
        if (channels == 0) return;

        if (startSample < 0)
            throw new ArgumentOutOfRangeException(nameof(startSample), "Loop start sample cannot be negative.");
        if (endSample != -1 && endSample < startSample)
            throw new ArgumentOutOfRangeException(nameof(endSample),
                "Loop end sample must be greater than or equal to start sample, or -1.");

        // Align to frame boundaries and clamp.
        _loopStartSamples = (startSample / channels) * channels;
        _loopStartSamples = Math.Clamp(_loopStartSamples, 0, _dataProvider.Length > 0 ? _dataProvider.Length : int.MaxValue);

        if (endSample != -1)
        {
            endSample = Math.Max(startSample, endSample);
            _loopEndSamples = (endSample / channels) * channels;
            _loopEndSamples = Math.Clamp(_loopEndSamples, _loopStartSamples, _dataProvider.Length > 0 ? _dataProvider.Length : int.MaxValue);
        }
        else
        {
            _loopEndSamples = -1;
        }
    }

    /// <summary>Sets loop boundaries using content durations; an omitted end selects the natural end.</summary>
    public void SetLoopPoints(TimeSpan startTime, TimeSpan? endTime = null)
    {
        SetLoopPoints((float)startTime.TotalSeconds, (float?)endTime?.TotalSeconds);
    }

    #endregion

    /// <summary>Reads complete source frames without crossing an authored forward-loop boundary.</summary>
    private int ReadForward(Span<float> output)
    {
        var start = IsLooping ? _loopStartSamples : 0;
        var end = IsLooping && _loopEndSamples >= 0 ? _loopEndSamples : _dataProvider.Length;
        if (end > 0 && _dataProvider.Position >= end)
        {
            if (!IsLooping || !_dataProvider.CanSeek || end <= start) return 0;
            _dataProvider.Seek(start); _providerEnded = false;
        }
        var count = end > 0 ? Math.Min(output.Length, end - _dataProvider.Position) : output.Length;
        var read = ReadProvider(output[..count]);
        if (read < 0 || read > count || read % Format.Channels != 0)
            throw new InvalidOperationException("A provider must return complete frames within its requested buffer.");
        if (read == 0 && IsLooping && _dataProvider.CanSeek && end > start && _dataProvider.Position > start)
        {
            _dataProvider.Seek(start); _providerEnded = false;
            read = ReadProvider(output[..Math.Min(output.Length, end - start)]);
        }
        return read;
    }

    /// <summary>Interpolates backwards using a persistent fractional frame cursor and a bounded seek/read cache.</summary>
    private void RenderReverse(Span<float> output, int channels)
    {
        if (!_dataProvider.CanSeek) throw new NotSupportedException("Reverse playback requires seeking.");
        if (_reverseCache.Length == 0) _reverseCache = new float[8192 * channels];
        output.Clear();
        var start = IsLooping ? _loopStartSamples / channels : 0;
        var end = (IsLooping && _loopEndSamples >= 0 ? _loopEndSamples : _dataProvider.Length) / channels;
        var written = 0;
        while (written < output.Length)
        {
            if (_reverseCursor <= start)
            {
                if (IsLooping && end > start) _reverseCursor = end;
                else { Finish(); break; }
            }
            var position = Math.Max(start, _reverseCursor - 1);
            var frame = (int)Math.Floor(position);
            var next = Math.Min(frame + 1, Math.Max(frame, end - 1));
            if (_reverseCacheStart < 0 || frame < _reverseCacheStart || next >= _reverseCacheStart + _reverseCacheFrames)
            {
                _reverseCacheStart = Math.Max(start, next - _reverseCache.Length / channels + 1);
                _dataProvider.Seek(_reverseCacheStart * channels);
                var capacity = Math.Min(_reverseCache.Length, Math.Max(0, end - _reverseCacheStart) * channels);
                var read = 0;
                while (read < capacity)
                {
                    var added = ReadProvider(_reverseCache.AsSpan(read, capacity - read));
                    if (added == 0) break;
                    read += added;
                }
                _reverseCacheFrames = read / channels;
                if (frame >= _reverseCacheStart + _reverseCacheFrames) { Finish(); break; }
                next = Math.Min(next, _reverseCacheStart + _reverseCacheFrames - 1);
            }
            var fraction = (float)(position - frame);
            for (var channel = 0; channel < channels; channel++)
            {
                var a = _reverseCache[(frame - _reverseCacheStart) * channels + channel];
                var b = _reverseCache[(next - _reverseCacheStart) * channels + channel];
                output[written + channel] = a + (b - a) * fraction;
            }
            written += channels;
            _reverseCursor = Math.Max(start, _reverseCursor + _playbackSpeed);
        }
        _rawSamplePosition = (int)Math.Ceiling(_reverseCursor) * channels;
        LastReadSamples = written;
    }

    /// <summary>Tracks rendered source time rather than WSOLA's decoded lookahead.</summary>
    private void AdvanceClock(int renderedSamples)
    {
        _forwardClock += renderedSamples * (double)_playbackSpeed;
        var end = _loopEndSamples >= 0 && IsLooping ? _loopEndSamples : _dataProvider.Length;
        if (IsLooping && end > _loopStartSamples && _forwardClock >= end)
            _forwardClock = _loopStartSamples + (_forwardClock - end) % (end - _loopStartSamples);
        if (!IsLooping && _dataProvider.Length > 0) _forwardClock = Math.Min(_forwardClock, _dataProvider.Length);
        _rawSamplePosition = (int)Math.Clamp(_forwardClock, 0, int.MaxValue);
    }

    private void Finish()
    {
        if (State != PlaybackState.Playing) return;
        State = PlaybackState.Stopped;
        OnPlaybackEnded();
    }

    /// <summary>Unsubscribes provider events and releases transport resources; leaveOpen providers remain usable.</summary>
    public void Dispose()
    {
        if (IsDisposed) return;
        IsDisposed = true;
        _dataProvider.EndOfStreamReached -= ProviderEnded;
        if (!_leaveOpen) _dataProvider.Dispose();
    }
}
