using System.Numerics;
using System.Runtime.InteropServices;
using SoundFlow.Structs;
using Native = global::SteamAudio.Interop;

namespace SoundFlow.SteamAudio;

/// <summary>Immutable speaker directions and channel order. Custom arrays are copied and normalized.</summary>
public sealed class SpeakerLayout
{
    internal Native.SpeakerLayoutType Type { get; }
    private readonly Vector3[] _directions;
    /// <summary>Number of speaker channels, in the authored/native layout order.</summary>
    public int Channels { get; }
    /// <summary>Managed speaker layout metadata; custom layouts remain explicitly unknown.</summary>
    public ChannelLayout ChannelLayout { get; }
    /// <summary>Custom normalized directions. Standard layouts use Steam Audio's predefined directions.</summary>
    public IReadOnlyList<Vector3> Directions { get; }
    private SpeakerLayout(Native.SpeakerLayoutType type, int channels, ChannelLayout layout, Vector3[]? directions = null)
    {
        Type = type;
        Channels = channels;
        ChannelLayout = layout;
        _directions = directions ?? [];
        Directions = Array.AsReadOnly(_directions);
    }
    /// <summary>Mono output.</summary>
    public static SpeakerLayout Mono { get; } = new(Native.SpeakerLayoutType.Mono, 1, ChannelLayout.Mono);
    /// <summary>Left/right stereo output.</summary>
    public static SpeakerLayout Stereo { get; } = new(Native.SpeakerLayoutType.Stereo, 2, ChannelLayout.Stereo);
    /// <summary>Front left/right followed by rear left/right.</summary>
    public static SpeakerLayout Quad { get; } = new(Native.SpeakerLayoutType.Quadraphonic, 4, ChannelLayout.Quad);
    /// <summary>Front left/right, center, LFE, rear left/right.</summary>
    public static SpeakerLayout Surround51 { get; } = new(Native.SpeakerLayoutType.Surround51, 6, ChannelLayout.Surround51);
    /// <summary>5.1 channels followed by side left/right.</summary>
    public static SpeakerLayout Surround71 { get; } = new(Native.SpeakerLayoutType.Surround71, 8, ChannelLayout.Surround71);

    /// <summary>Copies one through 32 nonzero finite directions in native channel order.</summary>
    public static SpeakerLayout Custom(ReadOnlySpan<Vector3> directions)
    {
        if (directions.Length is < 1 or > 32)
            throw new ArgumentOutOfRangeException(nameof(directions));
        var copy = directions.ToArray();
        for (var i = 0; i < copy.Length; i++)
        {
            if (!float.IsFinite(copy[i].LengthSquared()) || copy[i].LengthSquared() < 1e-12f)
                throw new ArgumentException("Speaker directions must be finite and nonzero.", nameof(directions));
            copy[i] = Vector3.Normalize(copy[i]);
        }
        return new(Native.SpeakerLayoutType.Custom, copy.Length, ChannelLayout.Unknown, copy);
    }

    internal AudioFormat Output(AudioFormat input) => input with { Channels = Channels, Layout = ChannelLayout, Ambisonics = null };
    internal LayoutLease Retain() => new(this);

    internal sealed unsafe class LayoutLease : IDisposable
    {
        private Native.Vector3* _directions;
        internal Native.SpeakerLayout Value { get; }
        internal LayoutLease(SpeakerLayout layout)
        {
            if (layout._directions.Length > 0)
            {
                _directions = (Native.Vector3*)NativeMemory.Alloc((nuint)(layout.Channels * sizeof(Native.Vector3)));
                if (_directions == null)
                    throw new OutOfMemoryException();
                for (var i = 0; i < layout.Channels; i++)
                    _directions[i] = new()
                    {
                        X = layout._directions[i].X,
                        Y = layout._directions[i].Y,
                        Z = layout._directions[i].Z
                    };
            }
            Value = new()
            {
                Type = layout.Type,
                NumSpeakers = layout.Channels,
                Speakers = _directions
            };
        }
        public void Dispose()
        {
            NativeMemory.Free(_directions);
            _directions = null;
        }
    }
}
