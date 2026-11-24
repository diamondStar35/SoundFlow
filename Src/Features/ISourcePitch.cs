namespace SoundFlow.Features;

/// <summary>Playback pitch independent of pitch-preserving transport speed.</summary>
public interface ISourcePitch
{
    /// <summary>Positive pitch/rate multiplier; 1 is unchanged. Also changes the source's playback duration.</summary>
    float Pitch { get; set; }
}
