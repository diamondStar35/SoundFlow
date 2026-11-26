namespace SoundFlow.Processing;

/// <summary>A bounded, resettable frame delay. Used only when a send explicitly requests latency compensation.</summary>
public sealed class DelayLine
{
    private readonly float[] _samples;
    private int _cursor;
    /// <summary>Complete frame delay; zero copies without buffering.</summary>
    public int Frames { get; }
    /// <summary>Channels whose samples travel together through the ring.</summary>
    public int Channels { get; }
    /// <summary>Allocates bounded storage outside processing.</summary>
    public DelayLine(int frames, int channels)
    {
        if (frames < 0 || channels < 1) throw new ArgumentOutOfRangeException(nameof(frames));
        Frames = frames; Channels = channels; _samples = new float[checked(frames * channels)];
    }
    /// <summary>Writes delayed samples into independent destination storage without retaining either span.</summary>
    public void Process(ReadOnlySpan<float> input, Span<float> output)
    {
        if (input.Length % Channels != 0 || output.Length < input.Length) throw new ArgumentException("Expected complete frames and sufficient output storage.");
        if (_samples.Length == 0) { input.CopyTo(output); return; }
        for (var i = 0; i < input.Length; i++)
        { var sample = input[i]; output[i] = _samples[_cursor]; _samples[_cursor] = sample; if (++_cursor == _samples.Length) _cursor = 0; }
    }
    /// <summary>Discards pre-seek or pre-restart samples.</summary>
    public void Reset() { Array.Clear(_samples); _cursor = 0; }
}
