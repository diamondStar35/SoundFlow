using SoundFlow.Playback;
using SoundFlow.Structs;

namespace SoundFlow.Processing;

/// <summary>
/// Pulls complete processing quanta from predecoded PCM. Several native endpoints may read the same
/// timeline in any order: a quantum is processed once and its copied captures remain available in a
/// bounded cache. There is no rendered queue to fill ahead of a render request.
/// </summary>
public sealed class RenderSession : IDisposable
{
    private sealed class Packet
    {
        internal long Sequence = -1;
        internal int Frames;
        internal readonly float[] Output;
        internal readonly float[] Processed;
        internal readonly int[] Positions;
        internal Dictionary<Point, float[]> Captures;

        internal Packet(RenderSession owner)
        {
            Output = new float[owner.QuantumFrames * owner.OutputFormat.Channels];
            Processed = owner.OutputFormat == owner.Processing.OutputFormat ? Output : new float[owner.QuantumFrames * owner.Processing.OutputFormat.Channels];
            Positions = new int[owner.QuantumFrames + 1];
            Captures = owner.Processing.CreateCaptures();
            foreach (var point in owner._inputCaptures)
                Captures.Add(point.Key, new float[point.Value.Length]);
        }
    }

    private readonly DecodeSession _decode;
    private readonly Packet[] _packets;
    private readonly float[] _input;
    private readonly float[] _silence;
    private readonly int[] _inputPositions;
    private Dictionary<Point, float[]> _inputCaptures = new();
    private readonly long[] _cursors = new long[65];
    private readonly int[] _clock;
    private int _clockCursor;
    private DecodeSession.Block? _block;
    private int _blockOffset;
    private int _gathered;
    private int _position;
    private int _inputPosition;
    private long _produced;
    private long _generatedFrames;
    private readonly float[] _tail;
    private readonly Dictionary<Point, float[]> _tailCaptures;
    private int _tailOffset;
    private int _tailFrames;
    private bool _inputEnded;
    private bool _finished;
    private int _reading;
    private int _suspended;
    private int _endpoints = 1;
    private readonly bool[] _activeEndpoints = new bool[65];
    private bool _disposed;
    private long _underruns;
    private readonly AudioFormat _outputFormat;

    /// <summary>Exclusively owned late processing state, released with this session.</summary>
    public Runner Processing
    {
        get;
    }
    /// <summary>Frames per late processing call, independent of callback and decoding packet lengths.</summary>
    public int QuantumFrames
    {
        get;
    }
    /// <summary>PCM format delivered to the endpoint.</summary>
    public AudioFormat OutputFormat => _outputFormat;
    /// <summary>True after permanent EOF and all effect tails have been rendered.</summary>
    public bool Completed => _finished && Volatile.Read(ref _cursors[0]) >= Volatile.Read(ref _generatedFrames);
    /// <summary>Content position consumed by the primary endpoint, in original transport samples.</summary>
    public int Position => Volatile.Read(ref _position);
    /// <summary>Number of quanta for which input was temporarily unavailable.</summary>
    public long Underruns => Interlocked.Read(ref _underruns);
    /// <summary>Primary-endpoint frames already rendered but not yet consumed.</summary>
    public int RetainedFrames => (int)Math.Max(0, Volatile.Read(ref _generatedFrames) - Volatile.Read(ref _cursors[0]));
    /// <summary>Copies decode-ahead and rendered-remainder counters without waiting for a worker.</summary>
    public RenderProcessingSnapshot GetSnapshot() => new(QuantumFrames, _decode.BufferedFrames, RetainedFrames, Underruns);

    /// <summary>
    /// Creates all scratch and fan-out storage before playback. The decoder must use render-consumer
    /// mode, and the owner must serialize transport edits with Suspend/Resume.
    /// </summary>
    public RenderSession(DecodeSession decode, Runner processing, int quantumFrames, int endpointChannels = 0, object? renderDomain = null)
    {
        if (quantumFrames is < 64 or > 2048 || processing.MaximumFrames != quantumFrames ||
            (processing.RequiredBlockFrames != 0 && processing.RequiredBlockFrames != quantumFrames) ||
            processing.InputFormat != decode.OutputFormat)
            throw new ArgumentException("Render quantum and processing formats must agree.");
        _decode = decode;
        Processing = processing;
        QuantumFrames = quantumFrames;
        _outputFormat = processing.OutputFormat.WithEndpointChannels(endpointChannels);
        processing.BindRenderDomain(renderDomain ?? decode);
        _input = new float[quantumFrames * processing.InputFormat.Channels];
        _silence = new float[_input.Length];
        _inputPositions = new int[quantumFrames + 1];
        _tail = new float[quantumFrames * processing.OutputFormat.Channels];
        _tailCaptures = processing.CreateCaptures();
        foreach (var point in decode.Processing.Definition.Points)
            _inputCaptures.Add(point, new float[quantumFrames * decode.Processing.GetFormat(point).Channels]);
        _clock = new int[processing.LatencyFrames];
        // Native mixing requests are bounded; 16K retained frames also cover endpoint resampler
        // lookahead. Only requested quanta are generated, regardless of this retention capacity.
        _packets = Enumerable.Range(0, Math.Max(8, 16384 / quantumFrames)).Select(_ => new Packet(this)).ToArray();
        Volatile.Write(ref decode.RenderConsumer, this);
    }

    /// <summary>Reserves a wet endpoint cursor before playback. There is no extra decoder or DSP instance.</summary>
    public int AddEndpoint()
    {
        for (var i = 1; i < _activeEndpoints.Length; i++)
            if (!_activeEndpoints[i])
            {
                _activeEndpoints[i] = true;
                _cursors[i] = 0;
                _endpoints = Math.Max(_endpoints, i + 1);
                return i;
            }
        throw new InvalidOperationException("Too many captured endpoints.");
    }
    /// <summary>Returns an endpoint cursor after native callbacks have quiesced.</summary>
    public void RemoveEndpoint(int endpoint)
    {
        if (endpoint > 0)
            _activeEndpoints[endpoint] = false;
    }

    /// <summary>Returns the format at a capture point in either processing stage.</summary>
    public AudioFormat GetFormat(Point point) => Processing.Definition.Points.Contains(point)
        ? Processing.GetFormat(point) : _decode.Processing.GetFormat(point);

    /// <summary>Returns only explicitly selected downstream algorithmic buffering delay.</summary>
    public int GetCompensationFrames(Point point, LatencyCompensation policy)
    {
        if (Processing.Definition.Points.Contains(point))
            return Processing.GetCompensationFrames(point, policy);
        var frames = _decode.Processing.GetCompensationFrames(point, policy);
        return frames + Processing.GetCompensationFramesAtStart(policy);
    }

    /// <summary>
    /// Replaces stopped producer processing while retaining late DSP ownership. Capture storage is
    /// prepared before installation; a changed handoff format requires recreating the source.
    /// </summary>
    public void ReplaceProducer(Runner producer)
    {
        var format = producer.OutputFormat;
        if (format != Processing.InputFormat)
            throw new ArgumentException("Recreate the source to change the format entering its render chain.");
        var inputs = new Dictionary<Point, float[]>();
        foreach (var point in producer.Definition.Points)
            inputs.Add(point, new float[QuantumFrames * producer.GetFormat(point).Channels]);
        var captures = new Dictionary<Point, float[]>[_packets.Length];
        for (var i = 0; i < captures.Length; i++)
        {
            captures[i] = Processing.CreateCaptures();
            foreach (var point in inputs)
                captures[i].Add(point.Key, new float[point.Value.Length]);
        }
        Suspend();
        try
        {
            Reset(Position);
            try
            {
                _decode.ReplaceProcessing(producer);
            }
            finally
            {
                if (ReferenceEquals(_decode.Processing, producer))
                {
                    _inputCaptures = inputs;
                    for (var i = 0; i < captures.Length; i++)
                        _packets[i].Captures = captures[i];
                }
            }
        }
        finally { Resume(); }
    }

    /// <summary>
    /// Fills requested frames without waiting for the producer. A short result means permanent EOF,
    /// including all processor tails; temporary starvation writes silence and returns the full request.
    /// Only the primary endpoint advances the reported content cursor.
    /// </summary>
    public int Read(Span<float> output, int endpoint = 0, Point? point = null)
    {
        output.Clear();
        if (_disposed)
            return 0;
        var channels = point == null ? OutputFormat.Channels : GetFormat(point).Channels;
        if (Volatile.Read(ref _suspended) != 0 || Interlocked.CompareExchange(ref _reading, 1, 0) != 0)
            return output.Length / channels;
        try
        {
            if (Volatile.Read(ref _suspended) != 0)
                return output.Length / channels;
            var requested = output.Length / channels;
            var written = 0;
            while (written < requested)
            {
                var cursor = _cursors[endpoint];
                var sequence = cursor / QuantumFrames;
                var packet = _packets[(int)(sequence % _packets.Length)];
                if (packet.Sequence != sequence)
                {
                    if (sequence < _produced)
                        throw new InvalidOperationException("A native endpoint exceeded the bounded capture retention window.");
                    Produce(packet, sequence);
                }
                var offset = (int)(cursor % QuantumFrames);
                var count = Math.Min(requested - written, packet.Frames - offset);
                if (count <= 0)
                    break;
                var samples = point == null ? packet.Output : packet.Captures[point];
                samples.AsSpan(offset * channels, count * channels).CopyTo(output[(written * channels)..]);
                _cursors[endpoint] += count;
                if (endpoint == 0)
                    Volatile.Write(ref _position, packet.Positions[offset + count]);
                written += count;
                if (packet.Frames < QuantumFrames)
                    break;
            }
            return written;
        }
        finally { Volatile.Write(ref _reading, 0); }
    }

    private void Produce(Packet packet, long sequence)
    {
        if (_finished)
        {
            packet.Frames = 0;
            packet.Sequence = sequence;
            return;
        }
        var channels = Processing.InputFormat.Channels;
        while (_gathered < QuantumFrames && !_inputEnded)
        {
            if (_block == null && !_decode.TryTake(false, out _block))
            {
                _inputEnded = _decode.Done && _decode.ReadyCount == 0;
                break;
            }
            var available = _block!.Count / channels - _blockOffset;
            var frames = Math.Min(available, QuantumFrames - _gathered);
            _block.Samples.AsSpan(_blockOffset * channels, frames * channels).CopyTo(_input.AsSpan(_gathered * channels));
            _block.Positions.AsSpan(_blockOffset, frames + 1).CopyTo(_inputPositions.AsSpan(_gathered));
            foreach (var capture in _inputCaptures)
            {
                var count = _decode.Processing.GetFormat(capture.Key).Channels;
                _block.Captures[capture.Key].AsSpan(_blockOffset * count, frames * count).CopyTo(capture.Value.AsSpan(_gathered * count));
            }
            _gathered += frames;
            _blockOffset += frames;
            if (_blockOffset == _block.Count / channels)
            {
                _decode.Release(_block);
                _block = null;
                _blockOffset = 0;
            }
        }
        foreach (var capture in packet.Captures)
            Array.Clear(capture.Value);
        Array.Clear(packet.Output);
        if (!ReferenceEquals(packet.Output, packet.Processed))
            Array.Clear(packet.Processed);
        if (_gathered == QuantumFrames || (_inputEnded && _gathered > 0))
        {
            var valid = _gathered;
            _input.AsSpan(valid * channels).Clear();
            Processing.Process(_input, packet.Processed, QuantumFrames, packet.Captures);
            foreach (var capture in _inputCaptures)
            {
                var samples = valid * _decode.Processing.GetFormat(capture.Key).Channels;
                capture.Value.AsSpan(0, samples).CopyTo(packet.Captures[capture.Key]);
            }
            _inputPosition = _inputPositions[valid];
            for (var i = 0; i <= QuantumFrames; i++)
                packet.Positions[i] = _inputPositions[Math.Min(i, valid)];
            packet.Frames = QuantumFrames;
            _gathered = 0;
        }
        else if (_inputEnded)
        {
            packet.Frames = 0;
            while (packet.Frames < QuantumFrames)
            {
                if (_tailOffset == _tailFrames)
                {
                    _tailOffset = 0;
                    _tailFrames = Processing.Drain(_tail, _tailCaptures);
                    if (_tailFrames == 0)
                    {
                        _finished = true;
                        break;
                    }
                }
                var frames = Math.Min(_tailFrames - _tailOffset, QuantumFrames - packet.Frames);
                var outputChannels = Processing.OutputFormat.Channels;
                _tail.AsSpan(_tailOffset * outputChannels, frames * outputChannels).CopyTo(packet.Processed.AsSpan(packet.Frames * outputChannels));
                foreach (var capture in _tailCaptures)
                {
                    var count = Processing.GetFormat(capture.Key).Channels;
                    capture.Value.AsSpan(_tailOffset * count, frames * count).CopyTo(packet.Captures[capture.Key].AsSpan(packet.Frames * count));
                }
                packet.Frames += frames;
                _tailOffset += frames;
            }
            Array.Fill(packet.Positions, _inputPosition);
        }
        else
        {
            Interlocked.Increment(ref _underruns);
            Processing.Process(_silence, packet.Processed, QuantumFrames, packet.Captures);
            Array.Fill(packet.Positions, _inputPosition);
            packet.Frames = QuantumFrames;
        }
        if (!ReferenceEquals(packet.Processed, packet.Output))
            for (var frame = 0; frame < packet.Frames; frame++)
                for (var channel = 0; channel < OutputFormat.Channels; channel++)
                {
                    var sourceChannels = Processing.OutputFormat.Channels;
                    var value = 0f;
                    if (OutputFormat.Channels == 1)
                    {
                        for (var i = 0; i < sourceChannels; i++)
                            value += packet.Processed[frame * sourceChannels + i] / sourceChannels;
                    }
                    else
                        value = packet.Processed[frame * sourceChannels + Math.Min(channel, sourceChannels - 1)];
                    packet.Output[frame * OutputFormat.Channels + channel] = value;
                }
        if (_clock.Length > 0)
            for (var i = 1; i <= packet.Frames; i++)
            {
                var position = packet.Positions[i];
                packet.Positions[i] = _clock[_clockCursor];
                _clock[_clockCursor] = position;
                _clockCursor = (_clockCursor + 1) % _clock.Length;
            }
        packet.Sequence = sequence;
        _produced = sequence + 1;
        Volatile.Write(ref _generatedFrames, sequence * QuantumFrames + packet.Frames);
    }

    /// <summary>Prevents new render operations and waits on the control thread for active DSP to finish.</summary>
    public void Suspend()
    {
        Playback.ProcessingScope.ThrowIfProcessing();
        Interlocked.Increment(ref _suspended);
        var spin = new SpinWait();
        while (Volatile.Read(ref _reading) != 0)
            spin.SpinOnce();
    }

    /// <summary>Allows rendering after a serialized control operation.</summary>
    public void Resume() => Interlocked.Decrement(ref _suspended);

    /// <summary>Discards all retained input/output and clears DSP history while suspended.</summary>
    public void Reset(int position = 0)
    {
        if (_block != null)
        {
            _decode.Release(_block);
            _block = null;
        }
        _blockOffset = _gathered = _clockCursor = 0;
        _tailFrames = _tailOffset = 0;
        _position = _inputPosition = position;
        _inputEnded = _finished = false;
        _produced = 0;
        _generatedFrames = 0;
        Array.Clear(_cursors);
        Array.Fill(_clock, position);
        foreach (var packet in _packets)
            packet.Sequence = -1;
        Processing.Reset();
    }

    /// <summary>Quiesces rendering before releasing owned late processors.</summary>
    public void Dispose()
    {
        if (_disposed)
            return;
        Suspend();
        Reset();
        _disposed = true;
        Interlocked.CompareExchange(ref _decode.RenderConsumer, null, this);
        Processing.Dispose();
    }
}
