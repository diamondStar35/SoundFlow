using SoundFlow.Enums;
using SoundFlow.Interfaces;
using SoundFlow.Providers;
using SoundFlow.Structs;

namespace SoundFlow.Playback;

/// <summary>An immutable decoded F32 clip. Each source gets an independent read cursor over the shared samples.</summary>
public sealed class Clip
{
    private readonly float[] _samples;
    /// <summary>Source format, independent of the output device's format.</summary>
    public AudioFormat Format { get; }
    /// <summary>Number of complete frames.</summary>
    public int Frames => _samples.Length / Format.Channels;
    /// <summary>Borrowed immutable interleaved F32 data for backend uploads. Valid while this clip is retained.</summary>
    public ReadOnlySpan<float> Samples => _samples;
    /// <summary>Copies complete interleaved frames into an immutable clip.</summary>
    public Clip(ReadOnlySpan<float> samples, AudioFormat format)
    {
        format.ValidateChannelLayout();
        if (format.Channels <= 0 || format.SampleRate <= 0 || samples.Length % format.Channels != 0)
            throw new ArgumentException("A clip requires a positive format and complete frames.", nameof(format));
        foreach (var sample in samples)
            if (!float.IsFinite(sample)) throw new ArgumentException("PCM must contain finite samples.", nameof(samples));
        Format = format with { Format = SampleFormat.F32 };
        _samples = samples.ToArray();
    }
    internal IDataProvider Open() => new RawDataProvider(_samples, Format.SampleRate);
    /// <summary>Creates a caller-owned independent read cursor without copying the clip's immutable PCM.</summary>
    public IDataProvider CreateProvider() => Open();
}
