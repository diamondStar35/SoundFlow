using System.Numerics;
using SoundFlow.Devices;
using SoundFlow.Enums;
using SoundFlow.Interfaces;
using SoundFlow.Playback;
using SoundFlow.Processing;
using SoundFlow.Providers;
using SoundFlow.Spatial;
using SoundFlow.Structs;
using Native = global::SteamAudio;

namespace SoundFlow.SteamAudio;

/// <summary>
/// Owns shared native context/HRTF assets and the listener snapshot. DSP is created outside rendering;
/// each chain receives independent convolution/filter history. Dispose sources before their renderer.
/// </summary>
public sealed class Renderer : IDisposable
{
    internal sealed record ListenerState(Listener Pose, long Generation)
    {
        internal Vector3 Right { get; } = Vector3.Normalize(Vector3.Cross(Pose.Forward, Pose.Up));
    }
    private readonly object _assetsGate = new();
    private readonly Dictionary<(int Rate, bool Warm), Native.Hrtf> _hrtfs = new();
    private object? _renderDomain;
    private ListenerState _listener = new(new(), 0);
    private object? _quantumOwner;
    private long _quantumSequence;
    private ListenerState _quantumListener = new(new(), 0);
    private bool _disposed;
    internal Native.Context Context { get; }
    internal ListenerState ListenerSnapshot => Volatile.Read(ref _listener);
    /// <summary>Processing configuration fixed for this renderer's lifetime.</summary>
    public RendererSettings Settings { get; }

    /// <summary>Creates shared assets lazily at source construction, never inside audio processing.</summary>
    public Renderer(RendererSettings? settings = null)
    {
        Settings = settings ?? new();
        ArgumentNullException.ThrowIfNull(Settings.Speakers);
        if (Settings.BlockFrames is not (64 or 128 or 256 or 512) || !float.IsFinite(Settings.MetresPerUnit) || Settings.MetresPerUnit <= 0 || !Enum.IsDefined(Settings.Mode))
        {
            throw new ArgumentOutOfRangeException(nameof(settings), "Use a supported block size, a positive finite world scale, and a defined rendering mode.");
        }
        Context = new();
    }

    /// <summary>Listener pose read at the start of each new DSP quantum, without worker dispatch.</summary>
    public Listener Listener
    {
        get => ListenerSnapshot.Pose;
        set
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            ArgumentNullException.ThrowIfNull(value);
            if (!float.IsFinite(value.Position.LengthSquared()) || !float.IsFinite(value.Forward.LengthSquared()) ||
                !float.IsFinite(value.Up.LengthSquared()) || value.Forward.LengthSquared() < 1e-12f ||
                Vector3.Cross(value.Forward, value.Up).LengthSquared() < 1e-12f)
                throw new ArgumentException("Listener vectors must be finite and define a valid orientation.");
            var forward = Vector3.Normalize(value.Forward);
            var right = Vector3.Normalize(Vector3.Cross(forward, value.Up));
            value = value with
            {
                Forward = forward,
                Up = Vector3.Cross(right, forward)
            };
            while (true)
            {
                var previous = ListenerSnapshot;
                var next = new ListenerState(value, previous.Generation + 1);
                if (ReferenceEquals(Interlocked.CompareExchange(ref _listener, next, previous), previous))
                    break;
            }
        }
    }

    internal void BindDomain(object domain)
    {
        lock (_assetsGate)
        {
            if (_renderDomain != null && !ReferenceEquals(_renderDomain, domain))
                throw new InvalidOperationException("Create a separate Steam Audio renderer for each output/render domain.");
            _renderDomain = domain;
        }
    }

    internal void Warm(Action operation)
    {
        lock (_assetsGate)
            operation();
    }

    internal ListenerState ListenerForQuantum(object owner, long sequence)
    {
        if (!ReferenceEquals(_quantumOwner, owner) || _quantumSequence != sequence)
        {
            _quantumListener = ListenerSnapshot;
            _quantumOwner = owner;
            _quantumSequence = sequence;
        }
        return _quantumListener;
    }

    internal static Native.Interop.CoordinateSpace3 Orientation(ListenerState listener) => new()
    {
        Origin = new()
        {
            X = listener.Pose.Position.X,
            Y = listener.Pose.Position.Y,
            Z = listener.Pose.Position.Z
        },
        Ahead = new()
        {
            X = listener.Pose.Forward.X,
            Y = listener.Pose.Forward.Y,
            Z = listener.Pose.Forward.Z
        },
        Up = new()
        {
            X = listener.Pose.Up.X,
            Y = listener.Pose.Up.Y,
            Z = listener.Pose.Up.Z
        },
        Right = new()
        {
            X = listener.Right.X,
            Y = listener.Right.Y,
            Z = listener.Right.Z
        }
    };

    internal Native.Hrtf GetHrtf(int sampleRate, bool warm = false)
    {
        lock (_assetsGate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (!_hrtfs.TryGetValue((sampleRate, warm), out var hrtf))
            {
                var audio = new Native.AudioSettings(sampleRate, Settings.BlockFrames);
                hrtf = Settings.SofaPath == null ? new(Context, audio) : Native.Hrtf.LoadSofa(Context, audio, Settings.SofaPath);
                _hrtfs.Add((sampleRate, warm), hrtf);
            }
            return hrtf;
        }
    }

    /// <summary>Creates a direct processor for a custom render chain; place it before the binaural processor.</summary>
    public DirectProcessor CreateDirectProcessor(AudioFormat format, Spatialization spatialization) => new(this, format, spatialization);
    /// <summary>Creates a direct stage applying only the selected components, sharing the chain's spatial snapshot.</summary>
    public DirectProcessor CreateDirectProcessor(AudioFormat format, Spatialization spatialization, DirectEffects effects)
        => new(this, format, spatialization, effects);
    /// <summary>Creates independent headphone rendering for one voice. Add to the render stage, not decode-ahead processing.</summary>
    public BinauralProcessor CreateBinauralProcessor(AudioFormat format, Spatialization spatialization) => new(this, format, spatialization);
    /// <summary>Creates positional mono-to-speaker panning using an immutable layout.</summary>
    public PanningProcessor CreatePanningProcessor(AudioFormat format, Spatialization spatialization, SpeakerLayout? layout = null)
        => new(this, format, spatialization, layout ?? Settings.Speakers);
    /// <summary>Creates neutral-by-default stereo pan/width processing after spatial rendering.</summary>
    public StereoProcessor CreateStereoProcessor(AudioFormat format, Spatialization spatialization) => new(this, format, spatialization);
    /// <summary>Creates speaker-layout virtualization to headphones; input channels retain their separate directions.</summary>
    public VirtualSurroundProcessor CreateVirtualSurroundProcessor(AudioFormat format, SpeakerLayout layout) => new(this, format, layout);
    /// <summary>Encodes a mono source into an ACN/N3D sound field of order zero through three.</summary>
    public Ambisonics.EncodeProcessor CreateAmbisonicsEncoder(AudioFormat format, Spatialization spatialization, int order)
        => new(this, format, spatialization, order);
    /// <summary>Rotates native Ambisonics into the current listener frame.</summary>
    public Ambisonics.RotationProcessor CreateAmbisonicsRotation(AudioFormat format) => new(this, format);
    /// <summary>Decodes already-oriented native Ambisonics to speakers without another rotation.</summary>
    public Ambisonics.PanningProcessor CreateAmbisonicsPanning(AudioFormat format, SpeakerLayout layout) => new(this, format, layout);
    /// <summary>Decodes already-oriented native Ambisonics to headphones without another rotation.</summary>
    public Ambisonics.BinauralProcessor CreateAmbisonicsBinaural(AudioFormat format) => new(this, format);
    /// <summary>Rotates once and decodes native Ambisonics. Binaural output must use a stereo layout.</summary>
    public Ambisonics.DecodeProcessor CreateAmbisonicsDecoder(AudioFormat format, bool binaural = true, SpeakerLayout? layout = null)
        => new(this, format, layout ?? (binaural ? SpeakerLayout.Stereo : Settings.Speakers), binaural);
    /// <summary>Converts explicitly between N3D, SN3D and FuMa; native effects require N3D input.</summary>
    public Ambisonics.ConvertProcessor CreateAmbisonicsConverter(AudioFormat format, AmbisonicsConvention convention)
        => new(this, format, convention);

    /// <summary>Creates an independent headphone voice over an immutable decoded clip.</summary>
    public Source CreateSource(PlaybackDevice device, Clip clip, SpatializationSettings? settings = null, SourceOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(clip);
        var provider = clip.Open();
        try
        {
            return CreateSource(device, provider, clip.Format, settings, options);
        }
        catch
        {
            provider.Dispose();
            throw;
        }
    }

    /// <summary>
    /// Creates a direct-sound chain followed by headphone rendering or speaker panning according to
    /// Settings.Mode. Headphones accept mono/stereo input; speaker panning accepts mono input.
    /// The device must match the selected output layout. Custom chains can insert capture points.
    /// </summary>
    public Source CreateSource(PlaybackDevice device, IDataProvider provider, AudioFormat format,
        SpatializationSettings? settings = null, SourceOptions? options = null, bool leaveOpen = false)
    {
        ArgumentNullException.ThrowIfNull(device);
        ArgumentNullException.ThrowIfNull(provider);
        var channels = Settings.Mode == RenderingMode.Headphones ? 2 : Settings.Speakers.Channels;
        if (device.Format.Channels != channels)
            throw new NotSupportedException("The output device must match the selected rendering layout.");
        if (channels != 2 && settings is { } shape && (shape.Pan != .5f || shape.Width != 1))
            throw new ArgumentException("Stereo pan/width require a stereo output.", nameof(settings));
        BindDomain(device);
        if (options?.RenderProcessing != null)
            throw new ArgumentException("Use processor factories to construct a custom render chain.", nameof(options));
        var state = new Spatialization(settings);
        if (channels != 2)
            state.RequireNeutralStereoControls();
        var builder = new Builder();
        builder.Add(input => CreateDirectProcessor(input, state));
        if (Settings.Mode == RenderingMode.Headphones)
            builder.Add(input => CreateBinauralProcessor(input, state));
        else
            builder.Add(input => CreatePanningProcessor(input, state));
        if (channels == 2)
            builder.Add(input => CreateStereoProcessor(input, state));
        var source = device.CreateSource(provider, format, (options ?? new()) with
        {
            RenderProcessing = builder.Build(),
            RenderBlockFrames = Settings.BlockFrames
        }, leaveOpen);
        if (source.RenderProcessingChain == null)
        {
            source.Dispose();
            throw new NotSupportedException("The selected backend did not bind on-demand processing.");
        }
        source.AddProcessingFeature<ISpatialization>(state);
        return source;
    }

    /// <summary>Releases shared asset owners. Existing native effects retain their required native dependencies.</summary>
    public void Dispose()
    {
        lock (_assetsGate)
        {
            if (_disposed)
                return;
            _disposed = true;
            foreach (var hrtf in _hrtfs.Values)
                hrtf.Dispose();
            _hrtfs.Clear();
            Context.Dispose();
        }
    }
}
