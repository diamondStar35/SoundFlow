using SoundFlow.Abstracts;
using SoundFlow.Structs;

namespace SoundFlow.Processing;

/// <summary>A named location between processors. Its identity belongs to one immutable chain definition.</summary>
public sealed class Point
{
    internal readonly object Owner;
    internal readonly int Index;
    /// <summary>Unique ordinal name within its chain.</summary>
    public string Name { get; }
    internal Point(object owner, int index, string name) { Owner = owner; Index = index; Name = name; }
}

/// <summary>Builds an ordered definition without creating or sharing mutable processor state.</summary>
public sealed class Builder
{
    private readonly object _owner = new();
    private readonly List<Func<AudioFormat, Processor>> _factories = [];
    private readonly List<Point> _points = [];
    private bool _built;
    /// <summary>Adds a factory invoked once per source. The returned processor must accept the preceding output format.</summary>
    public Builder Add(Func<AudioFormat, Processor> factory)
    {
        ObjectDisposedException.ThrowIf(_built, this); ArgumentNullException.ThrowIfNull(factory);
        _factories.Add(factory); return this;
    }
    /// <summary>Adds an owned, format-preserving legacy modifier through an explicit factory.</summary>
    public Builder AddModifier(Func<AudioFormat, SoundModifier> factory)
    {
        ArgumentNullException.ThrowIfNull(factory);
        return Add(format => new ModifierProcessor(format, factory(format), true));
    }
    /// <summary>Captures the current position before subsequently added processors. Capture storage cannot be mutated by downstream processing.</summary>
    public Point Capture(string name)
    {
        ObjectDisposedException.ThrowIf(_built, this); ArgumentException.ThrowIfNullOrEmpty(name);
        if (_points.Any(point => point.Name == name)) throw new ArgumentException("Capture names must be unique.", nameof(name));
        var point = new Point(_owner, _factories.Count, name); _points.Add(point); return point;
    }
    /// <summary>Freezes the definition. Reuse the result to create independent source instances.</summary>
    public Chain Build() { ObjectDisposedException.ThrowIf(_built, this); _built = true; return new(_owner, _factories.ToArray(), _points.ToArray()); }
}

/// <summary>Reusable ordered processor factories and capture points. Contains no live DSP state or native handles.</summary>
public sealed class Chain
{
    /// <summary>Identity processing with no captures or owned processor state.</summary>
    public static Chain Empty { get; } = new Builder().Build();
    internal readonly object Owner;
    internal readonly Func<AudioFormat, Processor>[] Factories;
    internal readonly Point[] CapturePoints;
    /// <summary>Named points captured by the builder.</summary>
    public IReadOnlyList<Point> Points { get; }
    internal Chain(object owner, Func<AudioFormat, Processor>[] factories, Point[] points)
    { Owner = owner; Factories = factories; CapturePoints = points; Points = Array.AsReadOnly(points); }
    /// <summary>Finds a declared point by its exact ordinal name.</summary>
    public Point GetPoint(string name) => Points.FirstOrDefault(point => point.Name == name) ?? throw new ArgumentException("Unknown processing point.", nameof(name));
    /// <summary>Creates exclusively owned processor state and bounded scratch storage for a backend's producer.</summary>
    public Runner CreateRunner(AudioFormat input, int maximumFrames) => new(this, input, maximumFrames);
}
