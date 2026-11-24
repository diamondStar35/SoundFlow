using System.Numerics;

namespace SoundFlow.Spatial;

/// <summary>Device-wide listener and Doppler controls; control operations are serialized on the backend worker.</summary>
public interface IScene
{
    /// <summary>Listener pose.</summary>
    Listener Listener { get; set; }
    /// <summary>Distance attenuation model.</summary>
    DistanceModel DistanceModel { get; set; }
    /// <summary>Nonnegative Doppler scale; zero disables Doppler.</summary>
    float DopplerFactor { get; set; }
    /// <summary>Speed of sound in metres/second; greater than zero.</summary>
    float SpeedOfSound { get; set; }
    /// <summary>Metres per coordinate unit for environmental air absorption. Default 1; this does not rescale positions or the speed-of-sound setting.</summary>
    float MetresPerUnit { get; set; }
}
