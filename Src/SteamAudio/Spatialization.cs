using System.Numerics;

namespace SoundFlow.SteamAudio;

/// <summary>Render-time spatial controls associated with one logical voice.</summary>
public interface ISpatialization
{
    /// <summary>Atomically replaces source controls, independently of decoding and playback controls.</summary>
    SpatializationSettings Settings { get; set; }
    /// <summary>Latest source snapshot generation consumed by a processing quantum.</summary>
    long AppliedGeneration { get; }
    /// <summary>Latest listener generation consumed by a processing quantum.</summary>
    long AppliedListenerGeneration { get; }
}

/// <summary>Mutable control owner shared by a voice's direct and binaural processors.</summary>
public sealed class Spatialization : ISpatialization
{
    private sealed record Snapshot(SpatializationSettings Settings, long Generation)
    {
        internal Vector3 Right { get; } = Vector3.Normalize(Vector3.Cross(Settings.Forward, Settings.Up));
    }
    private Snapshot _snapshot;
    private long _applied;
    private long _listenerGeneration;
    internal SpatializationSettings Current = new();
    internal Vector3 Direction = -Vector3.UnitZ;
    internal float DistanceGain = 1;
    internal Renderer.ListenerState Listener = new(new(), 0);
    internal float DistanceMetres;
    private object? _quantumOwner;
    private long _quantum;
    private Vector3 _sourceRight = Vector3.UnitX;
    private int _neutralStereoOnly;
    internal void RequireNeutralStereoControls()
    {
        Volatile.Write(ref _neutralStereoOnly, 1);
        ValidateStereoControls(Settings);
    }
    private void ValidateStereoControls(SpatializationSettings settings)
    {
        if (Volatile.Read(ref _neutralStereoOnly) != 0 && (settings.Pan != .5f || settings.Width != 1))
            throw new ArgumentException("Stereo pan/width require a stereo output.", nameof(settings));
    }
    internal global::SteamAudio.Interop.CoordinateSpace3 SourceCoordinates
    {
        get
        {
            var forward = Current.Forward;
            var right = _sourceRight;
            var up = Current.Up;
            return new()
            {
                Origin = new()
                {
                    X = Current.Position.X,
                    Y = Current.Position.Y,
                    Z = Current.Position.Z
                },
                Ahead = new()
                {
                    X = forward.X,
                    Y = forward.Y,
                    Z = forward.Z
                },
                Right = new()
                {
                    X = right.X,
                    Y = right.Y,
                    Z = right.Z
                },
                Up = new()
                {
                    X = up.X,
                    Y = up.Y,
                    Z = up.Z
                }
            };
        }
    }
    internal void Capture(Renderer renderer, object owner, long sequence)
    {
        if (ReferenceEquals(_quantumOwner, owner) && _quantum == sequence)
            return;
        Capture(renderer, renderer.ListenerForQuantum(owner, sequence));
        _quantumOwner = owner;
        _quantum = sequence;
    }
    private object? _owner;
    private int _bindings;
    private readonly object _bindingGate = new();

    internal void Bind(object owner)
    {
        lock (_bindingGate)
        {
            if (_owner != null && !ReferenceEquals(_owner, owner))
                throw new InvalidOperationException("A spatialization owner belongs to one live render chain. Create fresh controls per source or pool lease.");
            _owner = owner;
            _bindings++;
        }
    }

    internal void Unbind()
    {
        lock (_bindingGate)
            if (--_bindings == 0)
                _owner = null;
    }

    /// <summary>Creates an independent control owner; immutable settings may be reused across voices.</summary>
    public Spatialization(SpatializationSettings? settings = null)
    {
        settings ??= new();
        settings.Validate();
        _snapshot = new(Normalize(settings), 0);
    }

    private static SpatializationSettings Normalize(SpatializationSettings settings)
    {
        var forward = Vector3.Normalize(settings.Forward);
        var right = Vector3.Normalize(Vector3.Cross(forward, settings.Up));
        return settings with
        {
            Forward = forward,
            Up = Vector3.Cross(right, forward)
        };
    }

    /// <inheritdoc />
    public SpatializationSettings Settings
    {
        get => Volatile.Read(ref _snapshot).Settings;
        set
        {
            ArgumentNullException.ThrowIfNull(value);
            value.Validate();
            ValidateStereoControls(value);
            value = Normalize(value);
            while (true)
            {
                var previous = Volatile.Read(ref _snapshot);
                var next = new Snapshot(value, previous.Generation + 1);
                if (ReferenceEquals(Interlocked.CompareExchange(ref _snapshot, next, previous), previous))
                    break;
            }
        }
    }
    /// <inheritdoc />
    public long AppliedGeneration => Interlocked.Read(ref _applied);
    /// <inheritdoc />
    public long AppliedListenerGeneration => Interlocked.Read(ref _listenerGeneration);

    internal void Capture(Renderer renderer) => Capture(renderer, renderer.ListenerSnapshot);
    private void Capture(Renderer renderer, Renderer.ListenerState listener)
    {
        var snapshot = Volatile.Read(ref _snapshot);
        Listener = listener;
        Current = snapshot.Settings;
        _sourceRight = snapshot.Right;
        var delta = Current.Relative ? Current.Position : Current.Position - listener.Pose.Position;
        if (!Current.Relative)
        {
            var forward = listener.Pose.Forward;
            var right = listener.Right;
            var up = Vector3.Cross(right, forward);
            delta = new(Vector3.Dot(delta, right), Vector3.Dot(delta, up), -Vector3.Dot(delta, forward));
        }
        var length = (float)Math.Sqrt((double)delta.X * delta.X + (double)delta.Y * delta.Y + (double)delta.Z * delta.Z);
        Direction = length > 1e-6f ? delta / length : -Vector3.UnitZ;
        DistanceGain = Current.Distance.Evaluate(length);
        DistanceMetres = length * renderer.Settings.MetresPerUnit;
        Interlocked.Exchange(ref _applied, snapshot.Generation);
        Interlocked.Exchange(ref _listenerGeneration, listener.Generation);
    }
}
