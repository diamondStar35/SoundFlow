using Engine = SoundFlow.Engine;
using System.Buffers;
using System.Diagnostics.CodeAnalysis;
using System.Numerics;
using System.Runtime.CompilerServices;
using SoundFlow.Components;
using SoundFlow.Interfaces;
using SoundFlow.Structs;

namespace SoundFlow.Abstracts;

/// <summary>
///     Base class for audio processing components.
/// </summary>
[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicProperties | DynamicallyAccessedMemberTypes.PublicMethods)]
public abstract class SoundComponent : IDisposable, IMidiMappable
{
    private static readonly ArrayPool<float> BufferPool = ArrayPool<float>.Shared;

    // Connection state (Staging for modification)
    private readonly List<SoundComponent> _inputsStaging = [];
    private readonly List<SoundComponent> _outputsStaging = [];
    private readonly object _connectionsLock = new();

    // Connection snapshots (For audio thread)
    private volatile SoundComponent[] _inputsSnapshot = [];
    private volatile SoundComponent[] _outputsSnapshot = [];

    // Processing state (Staging)
    private readonly List<SoundModifier> _modifiersStaging = [];
    private readonly List<AudioAnalyzer> _analyzersStaging = [];

    // Processing snapshots (For audio thread)
    private volatile SoundModifier[] _modifiersSnapshot = [];
    private volatile AudioAnalyzer[] _analyzersSnapshot = [];

    private float _pan = 0.5f;
    private bool _solo;
    private float _volume = 1f;

    // Cached calculations to avoid math in hot path
    private volatile float _volumePanLeft;
    private volatile float _volumePanRight;
    // The volume/pan gains applied at the end of the previous processed block. The current block ramps from
    // these to the target gains per-sample, so a volume change (e.g. a fade) never steps at the block boundary
    // — which on a steady tone is an audible click. Unchanged volume takes the fast constant path.
    private float _appliedVolumePanLeft;
    private float _appliedVolumePanRight;
    private bool _hasAppliedVolumePan;
    private readonly object _stateLock = new();

    /// <inheritdoc />
    public Guid Id { get; } = Guid.NewGuid();

    /// <summary>
    /// Gets a value indicating whether this component has been disposed.
    /// </summary>
    public bool IsDisposed { get; private set; }

    /// <summary>
    /// Gets the engine context this component belongs to.
    /// </summary>
    public Engine Engine { get; }

    /// <summary>
    /// Gets the audio format of the component.
    /// </summary>
    public AudioFormat Format { get; private set; }

    /// <summary>Changes a stopped component's declared output and updates channel-dependent gain state.</summary>
    protected void SetProcessingFormat(AudioFormat format) { Format = format; UpdateVolumePanFactors(format.Channels); }
    /// <summary>Players executing modifiers on their producer skip the outer graph modifier pass.</summary>
    protected virtual bool ProcessesModifiersInternally => false;

    /// <summary>
    ///     Name of the component
    /// </summary>
    public virtual string Name { get; set; } = "Component";

    /// <summary>
    ///     Parent mixer of the component
    /// </summary>
    public Mixer? Parent { get; set; }

    /// <summary>
    ///     Initializes a new instance of the <see cref="SoundComponent" /> class.
    /// </summary>
    protected SoundComponent(Engine engine, AudioFormat format)
    {
        Engine = engine;
        Format = format;
        UpdateVolumePanFactors(Format.Channels);
    }

    /// <summary>
    ///     Finalizes an instance of the <see cref="SoundComponent"/> class.
    /// </summary>
    ~SoundComponent()
    {
        Dispose(false);
    }

    /// <summary>
    ///     Input connections
    /// </summary>
    public IReadOnlyList<SoundComponent> Inputs
    {
        get
        {
            lock (_connectionsLock) return new List<SoundComponent>(_inputsStaging);
        }
    }

    /// <summary>
    ///     Output connections
    /// </summary>
    public IReadOnlyList<SoundComponent> Outputs
    {
        get
        {
            lock (_connectionsLock) return new List<SoundComponent>(_outputsStaging);
        }
    }

    /// <summary>
    ///     Volume of the component
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">Thrown if the volume is negative.</exception>
    /// <exception cref="ObjectDisposedException">Thrown if the component has been disposed.</exception>
    public virtual float Volume
    {
        get => _volume;
        set
        {
            ObjectDisposedException.ThrowIf(IsDisposed, this);
            ArgumentOutOfRangeException.ThrowIfNegative(value);
            lock (_stateLock)
            {
                _volume = value;
                UpdateVolumePanFactors(Format.Channels);
            }
        }
    }

    /// <summary>
    ///     Pan of the component
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">Thrown if the pan is outside the range [0, 1].</exception>
    /// <exception cref="ObjectDisposedException">Thrown if the component has been disposed.</exception>
    public virtual float Pan
    {
        get => _pan;
        set
        {
            ObjectDisposedException.ThrowIf(IsDisposed, this);
            if (value is < 0f or > 1f) throw new ArgumentOutOfRangeException(nameof(value), "Pan must be between 0.0 and 1.0.");
            lock (_stateLock)
            {
                _pan = value;
                UpdateVolumePanFactors(Format.Channels);
            }
        }
    }

    /// <summary>
    ///     Whether the component is enabled
    /// </summary>
    public virtual bool Enabled { get; set; } = true;

    /// <summary>
    ///     Whether the component is soloed
    /// </summary>
    /// <exception cref="ObjectDisposedException">Thrown if the component has been disposed.</exception>
    public virtual bool Solo
    {
        get => _solo;
        set
        {
            ObjectDisposedException.ThrowIf(IsDisposed, this);
            lock (_stateLock)
            {
                if (_solo == value) return;
                _solo = value;
                if (_solo) Engine.SoloComponent(this);
                else Engine.UnsoloComponent(this);
            }
        }
    }

    /// <summary>
    ///     Whether the component is muted
    /// </summary>
    public virtual bool Mute { get; set; }

    /// <summary>
    ///     Modifiers applied to the component
    /// </summary>
    public IReadOnlyList<SoundModifier?> Modifiers
    {
        get
        {
            lock (_stateLock) return new List<SoundModifier?>(_modifiersStaging);
        }
    }

    /// <summary>
    ///     Analyzers applied to the component
    /// </summary>
    public IReadOnlyList<AudioAnalyzer> Analyzers
    {
        get
        {
            lock (_stateLock) return new List<AudioAnalyzer>(_analyzersStaging);
        }
    }

    private void UpdateVolumePanFactors(int channels)
    {
        var panValue = Math.Clamp(_pan, 0f, 1f);
        if (channels == 1)
        {
            _volumePanLeft = _volume;
            _volumePanRight = 0f;
        }
        else
        {
            // Use a unity-center pan law so centered stereo and mixers do not lose level
            // at every stage of the graph.
            var left = panValue <= 0.5f ? 1f : MathF.Sqrt(Math.Max(0f, 2f * (1f - panValue)));
            var right = panValue >= 0.5f ? 1f : MathF.Sqrt(Math.Max(0f, 2f * panValue));
            _volumePanLeft = _volume * left;
            _volumePanRight = _volume * right;
        }
    }

    /// <summary>
    ///     Connects this component to another component.
    /// </summary>
    /// <param name="input">The component to connect to.</param>
    /// <exception cref="InvalidOperationException">Thrown if the connection would create a cycle.</exception>
    /// <exception cref="ObjectDisposedException">Thrown if the component has been disposed.</exception>
    public void ConnectInput(SoundComponent input)
    {
        ObjectDisposedException.ThrowIf(IsDisposed, this);
        ArgumentNullException.ThrowIfNull(input);
        if (input.IsDisposed) throw new ArgumentException("Cannot connect to a disposed component.", nameof(input));
        if (input == this) throw new InvalidOperationException("Cannot connect to self");

        SoundComponent first, second;
        if (GetHashCode() < input.GetHashCode())
        {
            first = this;
            second = input;
        }
        else
        {
            first = input;
            second = this;
        }

        lock (first._connectionsLock)
        lock (second._connectionsLock)
        {
            if (_inputsStaging.Contains(input)) return;

            if (IsReachable(input, this))
                throw new InvalidOperationException("Connection would create a cycle");

            // Update Staging
            _inputsStaging.Add(input);
            input._outputsStaging.Add(this);

            // Update Snapshots
            _inputsSnapshot = _inputsStaging.ToArray();
            input._outputsSnapshot = input._outputsStaging.ToArray();
        }
    }

    /// <summary>
    ///     Disconnects this component from another component.
    /// </summary>
    /// <param name="input">The component to disconnect from.</param>
    /// <exception cref="ObjectDisposedException">Thrown if the component has been disposed.</exception>
    public void DisconnectInput(SoundComponent input)
    {
        ObjectDisposedException.ThrowIf(IsDisposed, this);
        ArgumentNullException.ThrowIfNull(input);

        // If the other component is disposed, its connections are already being torn down.
        if (input.IsDisposed) return;

        lock (_connectionsLock)
        {
            if (!_inputsStaging.Remove(input)) return;
            // Update Snapshot
            _inputsSnapshot = _inputsStaging.ToArray();

            lock (input._connectionsLock)
            {
                input._outputsStaging.Remove(this);
                // Update Snapshot
                input._outputsSnapshot = input._outputsStaging.ToArray();
            }
        }
    }

    private static bool IsReachable(SoundComponent start, SoundComponent target)
    {
        var visited = new HashSet<SoundComponent>();
        var queue = new Queue<SoundComponent>();
        queue.Enqueue(start);

        while (queue.Count > 0)
        {
            var current = queue.Dequeue();
            if (current == target) return true;
            if (!visited.Add(current)) continue;

            List<SoundComponent> currentOutputs;
            lock (current._connectionsLock)
                currentOutputs = [..current._outputsStaging];

            foreach (var output in currentOutputs)
                queue.Enqueue(output);
        }

        return false;
    }

    /// <summary>
    ///     Adds a modifier to the component.
    /// </summary>
    /// <param name="modifier">The modifier to add.</param>
    /// <exception cref="ObjectDisposedException">Thrown if the component has been disposed.</exception>
    public virtual void AddModifier(SoundModifier modifier)
    {
        ObjectDisposedException.ThrowIf(IsDisposed, this);
        lock (_stateLock)
        {
            if (!_modifiersStaging.Contains(modifier))
            {
                _modifiersStaging.Add(modifier);
                _modifiersSnapshot = _modifiersStaging.ToArray();
            }
        }
    }

    /// <summary>
    ///     Removes a modifier from the component.
    /// </summary>
    /// <param name="modifier">The modifier to remove.</param>
    /// <exception cref="ObjectDisposedException">Thrown if the component has been disposed.</exception>
    public virtual void RemoveModifier(SoundModifier modifier)
    {
        ObjectDisposedException.ThrowIf(IsDisposed, this);
        lock (_stateLock)
        {
            if (_modifiersStaging.Remove(modifier))
                _modifiersSnapshot = _modifiersStaging.ToArray();
        }
    }

    /// <summary>
    ///     Adds an analyzer to the component.
    /// </summary>
    /// <param name="analyzer">The analyzer to add.</param>
    /// <exception cref="ObjectDisposedException">Thrown if the component has been disposed.</exception>
    public void AddAnalyzer(AudioAnalyzer analyzer)
    {
        ObjectDisposedException.ThrowIf(IsDisposed, this);
        lock (_stateLock)
        {
            if (!_analyzersStaging.Contains(analyzer))
            {
                _analyzersStaging.Add(analyzer);
                _analyzersSnapshot = _analyzersStaging.ToArray();
            }
        }
    }

    /// <summary>
    ///     Removes an analyzer from the component.
    /// </summary>
    /// <param name="analyzer">The analyzer to remove.</param>
    /// <exception cref="ObjectDisposedException">Thrown if the component has been disposed.</exception>
    public void RemoveAnalyzer(AudioAnalyzer analyzer)
    {
        ObjectDisposedException.ThrowIf(IsDisposed, this);
        lock (_stateLock)
        {
            if (_analyzersStaging.Remove(analyzer))
                _analyzersSnapshot = _analyzersStaging.ToArray();
        }
    }

    /// <summary>
    /// Processes the audio data through the component.
    /// This method is called by the audio engine for each buffer of audio data.
    /// The component processes the audio data by reading the audio data from the component's inputs,
    /// generating new audio data, and applying any enabled modifiers and analyzers.
    /// The processed audio data is then mixed into the <paramref name="outputBuffer"/>.
    /// </summary>
    /// <param name="outputBuffer">The buffer to mix the processed audio data into.</param>
    /// <param name="channels">The number of channels in the <paramref name="outputBuffer"/>.</param>
    /// <remarks>
    /// The <paramref name="outputBuffer"/> is cleared before the audio data is processed but not after.
    /// This method is for internal use only to allow custom audio engine implementations
    /// to manually drive component processing. Do not call from application code; use
    /// component connection graphs instead.
    /// </remarks>
    public void Process(Span<float> outputBuffer, int channels)
    {
        if (!Enabled || Mute || IsDisposed) return;
        using var processing = SoundFlow.Playback.ProcessingScope.Enter();

        float[]? rentedBuffer = null;
        try
        {
            rentedBuffer = BufferPool.Rent(outputBuffer.Length);
            var workingBuffer = rentedBuffer.AsSpan(0, outputBuffer.Length);
            workingBuffer.Clear();

            // Read snapshots
            var currentInputs = _inputsSnapshot;
            var currentModifiers = _modifiersSnapshot;
            var currentAnalyzers = _analyzersSnapshot;
            var targetVolumePan = new Vector2(_volumePanLeft, _volumePanRight);
            var startVolumePan = _hasAppliedVolumePan
                ? new Vector2(_appliedVolumePanLeft, _appliedVolumePanRight)
                : targetVolumePan;

            foreach (var input in currentInputs)
                input.Process(workingBuffer, channels);

            GenerateAudio(workingBuffer, channels);

            foreach (var modifier in currentModifiers)
                if (!ProcessesModifiersInternally && modifier.Enabled)
                    modifier.Process(workingBuffer, channels);

            ApplyVolumeAndPanning(workingBuffer, startVolumePan, targetVolumePan, channels);
            _appliedVolumePanLeft = targetVolumePan.X;
            _appliedVolumePanRight = targetVolumePan.Y;
            _hasAppliedVolumePan = true;

            MixBuffers(workingBuffer, outputBuffer);

            foreach (var analyzer in currentAnalyzers)
                analyzer.Process(workingBuffer, channels);
        }
        catch (Exception error) when (HandleProcessingFailure(error)) { }
        finally
        {
            if (rentedBuffer != null)
                BufferPool.Return(rentedBuffer);
        }
    }

    /// <summary>Prepares built-in buffered inputs for an offline processing step. Live callbacks must never call this method.</summary>
    public virtual void PrepareForRender()
    {
        foreach (var input in _inputsSnapshot)
            if (input.Enabled && !input.Mute) input.PrepareForRender();
    }

    /// <summary>Lets a player isolate a terminal processing failure. Returning false preserves ordinary exception propagation for other graph types.</summary>
    protected virtual bool HandleProcessingFailure(Exception error) => false;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void MixBuffers(ReadOnlySpan<float> source, Span<float> destination)
    {
        if (source.Length != destination.Length)
            throw new ArgumentException("Source and destination buffers must have the same length.");

        // SIMD-accelerated mixing
        var count = 0;
        var simdLength = source.Length - (source.Length % Vector<float>.Count);

        // Ensure there's enough data for SIMD operations
        if (simdLength > 0 && Vector<float>.Count <= source.Length && Vector<float>.Count <= destination.Length)
        {
            while (count < simdLength)
            {
                var vs = new Vector<float>(source.Slice(count, Vector<float>.Count));
                var vd = new Vector<float>(destination.Slice(count, Vector<float>.Count));
                (vd + vs).CopyTo(destination.Slice(count, Vector<float>.Count));
                count += Vector<float>.Count;
            }
        }

        // Scalar remainder
        while (count < source.Length)
        {
            destination[count] += source[count];
            count++;
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void ApplyVolumeAndPanning(Span<float> buffer, Vector2 startVolumePan, Vector2 endVolumePan, int channels)
    {
        // Gain unchanged across the block: use the fast constant (SIMD) path.
        if (MathF.Abs(startVolumePan.X - endVolumePan.X) < 1e-6f &&
            MathF.Abs(startVolumePan.Y - endVolumePan.Y) < 1e-6f)
        {
            ApplyVolumeAndPanning(buffer, endVolumePan, channels);
            return;
        }

        // Gain changed: ramp per-sample from the previous block's gain to the new target, so there is no step
        // discontinuity at the block boundary (which would click on a steady tone such as the ship engine).
        var frames = channels > 0 ? buffer.Length / channels : 0;
        if (frames <= 0)
        {
            ApplyVolumeAndPanning(buffer, endVolumePan, channels);
            return;
        }

        var invFrames = 1f / frames;
        switch (channels)
        {
            case 1:
            {
                var gStart = MathF.Sqrt((startVolumePan.X * startVolumePan.X) + (startVolumePan.Y * startVolumePan.Y));
                var gEnd = MathF.Sqrt((endVolumePan.X * endVolumePan.X) + (endVolumePan.Y * endVolumePan.Y));
                for (var f = 0; f < frames; f++)
                    buffer[f] *= gStart + ((gEnd - gStart) * (f * invFrames));
                break;
            }
            case 2:
            {
                for (var f = 0; f < frames; f++)
                {
                    var t = f * invFrames;
                    var b = f * 2;
                    buffer[b] *= startVolumePan.X + ((endVolumePan.X - startVolumePan.X) * t);
                    buffer[b + 1] *= startVolumePan.Y + ((endVolumePan.Y - startVolumePan.Y) * t);
                }
                break;
            }
            default:
            {
                var avgStart = (startVolumePan.X + startVolumePan.Y) * 0.5f;
                var avgEnd = (endVolumePan.X + endVolumePan.Y) * 0.5f;
                for (var f = 0; f < frames; f++)
                {
                    var t = f * invFrames;
                    var baseIndex = f * channels;
                    buffer[baseIndex] *= startVolumePan.X + ((endVolumePan.X - startVolumePan.X) * t);
                    buffer[baseIndex + 1] *= startVolumePan.Y + ((endVolumePan.Y - startVolumePan.Y) * t);
                    var avg = avgStart + ((avgEnd - avgStart) * t);
                    for (var c = 2; c < channels; c++)
                        buffer[baseIndex + c] *= avg;
                }
                break;
            }
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void ApplyVolumeAndPanning(Span<float> buffer, Vector2 volumePan, int channels)
    {
        switch (channels)
        {
            case 1:
                // Constant power calculation for mono
                var monoGain = MathF.Sqrt(volumePan.X * volumePan.X + volumePan.Y * volumePan.Y);
                ApplyMonoVolume(buffer, monoGain);
                break;
            case 2:
                ApplyStereoVolume(buffer, volumePan);
                break;
            default:
                ApplyMultiChannelVolume(buffer, channels, volumePan);
                break;
        }
    }

    private static void ApplyMonoVolume(Span<float> buffer, float volume)
    {
        if (Math.Abs(volume - 1f) < 1e-6f) return;

        if (Vector.IsHardwareAccelerated && buffer.Length >= Vector<float>.Count)
        {
            var vecVolume = new Vector<float>(volume);
            var count = 0;
            for (; count <= buffer.Length - Vector<float>.Count; count += Vector<float>.Count)
            {
                var vec = new Vector<float>(buffer.Slice(count, Vector<float>.Count));
                (vec * vecVolume).CopyTo(buffer.Slice(count, Vector<float>.Count));
            }

            for (; count < buffer.Length; count++)
                buffer[count] *= volume;
        }
        else
        {
            for (var i = 0; i < buffer.Length; i++)
                buffer[i] *= volume;
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void ApplyStereoVolume(Span<float> buffer, Vector2 volume)
    {
        // Early exit for unity volume (both channels at 1.0)
        if (Math.Abs(volume.X - 1f) < 1e-7f && Math.Abs(volume.Y - 1f) < 1e-7f)
            return;

        var volX = volume.X;
        var volY = volume.Y;

        var i = 0;

        if (Vector.IsHardwareAccelerated && buffer.Length >= Vector<float>.Count)
        {
            var vectorSize = Vector<float>.Count;

            if (vectorSize % 2 == 0)
            {
                Span<float> gainFactorsSpan = stackalloc float[vectorSize];
                for (var k = 0; k < vectorSize; k += 2)
                {
                    gainFactorsSpan[k] = volX;
                    gainFactorsSpan[k + 1] = volY;
                }
                var simdGainFactors = new Vector<float>(gainFactorsSpan);

                for (; i <= buffer.Length - vectorSize; i += vectorSize)
                {
                    var audioSimd = new Vector<float>(buffer.Slice(i, vectorSize));
                    (audioSimd * simdGainFactors).CopyTo(buffer.Slice(i, vectorSize));
                }
            }
        }

        // Scalar processing for the remainder or if SIMD is not applicable/enabled
        for (; i <= buffer.Length - 2; i += 2)
        {
            buffer[i] *= volX;
            buffer[i + 1] *= volY;
        }

        // Handle the last odd element if the buffer length is odd
        if (i < buffer.Length)
        {
            buffer[i] *= (volX + volY) * 0.5f;
        }
    }

    private static void ApplyMultiChannelVolume(Span<float> buffer, int channels, Vector2 volumePan)
    {
        if (channels < 2) return;

        var weights = new float[channels];
        weights[0] = volumePan.X;
        weights[1] = volumePan.Y;
        var avg = (volumePan.X + volumePan.Y) * 0.5f;

        for (var i = 2; i < channels; i++)
            weights[i] = avg;

        for (var i = 0; i < buffer.Length; i++)
            buffer[i] *= weights[i % channels];
    }

    /// <summary>
    ///     Generates audio data for the component.
    /// </summary>
    /// <param name="buffer">The buffer to write audio data to.</param>
    /// <param name="channels">The number of channels to generate for.</param>
    protected abstract void GenerateAudio(Span<float> buffer, int channels);

    /// <inheritdoc />
    public virtual void Dispose()
    {
        Dispose(true);
        GC.SuppressFinalize(this);
    }

    /// <summary>
    ///     Releases unmanaged and managed resources.
    /// </summary>
    /// <param name="disposing"><c>true</c> to release both managed and unmanaged resources; <c>false</c> to release only unmanaged resources.</param>
    protected virtual void Dispose(bool disposing)
    {
        if (IsDisposed) return;

        if (disposing)
        {
            // Stop processing immediately.
            Enabled = false;

            // Unregister from the engine's solo system.
            if (_solo) Engine.UnsoloComponent(this);

            // Explicitly remove from parent mixer.
            Parent?.RemoveComponent(this);
            Parent = null;

            // Disconnect all inputs and outputs.
            var inputsToDisconnect = Inputs;
            var outputsToDisconnect = Outputs;

            // Tell our outputs to disconnect from us.
            foreach (var output in outputsToDisconnect)
            {
                output.DisconnectInput(this);
            }

            // Disconnect from our inputs.
            foreach (var input in inputsToDisconnect)
            {
                DisconnectInput(input);
            }

            lock (_stateLock)
            {
                _modifiersStaging.Clear();
                _analyzersStaging.Clear();
                _modifiersSnapshot = [];
                _analyzersSnapshot = [];
            }
        }

        // Clear connection lists.
        lock (_connectionsLock)
        {
            _inputsStaging.Clear();
            _outputsStaging.Clear();
            _inputsSnapshot = [];
            _outputsSnapshot = [];
        }

        IsDisposed = true;
    }
}
