using SoundFlow.Providers;
using SoundFlow.Abstracts;
using SoundFlow.Enums;
using SoundFlow.Interfaces;
using SoundFlow.Metadata.Models;

namespace SoundFlow.Playback;

/// <summary>Adapts one software graph to a continuous native source input.</summary>
public sealed class GraphProvider(SoundComponent graph, bool leaveOpen) : IDataProvider
{
    /// <summary>Prepares buffered graph leaves for deterministic offline rendering, outside a decode worker.</summary>
    public void PrepareForRender() => graph.PrepareForRender();
    /// <inheritdoc />
    public int Position { get; private set; }
    /// <inheritdoc />
    public int Length => -1;
    /// <inheritdoc />
    public bool CanSeek => false;
    /// <inheritdoc />
    public SampleFormat SampleFormat => SampleFormat.F32;
    /// <inheritdoc />
    public int SampleRate => graph.Format.SampleRate;
    /// <inheritdoc />
    public bool IsDisposed { get; private set; }
    /// <inheritdoc />
    public SoundFormatInfo? FormatInfo => null;
    /// <inheritdoc />
    public event EventHandler<EventArgs>? EndOfStreamReached { add { } remove { } }
    /// <inheritdoc />
    public event EventHandler<PositionChangedEventArgs>? PositionChanged { add { } remove { } }
    /// <inheritdoc />
    public int ReadSamples(Span<float> buffer)
    {
        ObjectDisposedException.ThrowIf(IsDisposed, this);
        buffer.Clear();
        graph.Process(buffer, graph.Format.Channels);
        Position += buffer.Length;
        return buffer.Length;
    }
    /// <inheritdoc />
    public void Seek(int offset) => throw new NotSupportedException("A live graph has no seekable cursor.");
    /// <inheritdoc />
    public void Dispose() { if (IsDisposed) return; IsDisposed = true; if (!leaveOpen) graph.Dispose(); }
}
