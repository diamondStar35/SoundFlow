using SoundFlow.Structs;

namespace SoundFlow.Features;

/// <summary>How a device requests binaural HRTF rendering.</summary>
public enum HrtfMode
{
    /// <summary>Respect the implementation and user configuration.</summary>
    Automatic,
    /// <summary>Request HRTF rendering; inspect Status to determine whether it was accepted.</summary>
    Enabled,
    /// <summary>Request non-HRTF rendering.</summary>
    Disabled
}

/// <summary>Device HRTF controls and actual negotiated state.</summary>
public interface IHrtfAudio
{
    /// <summary>Available dataset names.</summary>
    IReadOnlyList<string> Datasets { get; }
    /// <summary>Actual backend status, including reasons a request was declined.</summary>
    string Status { get; }
    /// <summary>Requests a mode and optional dataset index. Invalid indices throw; native failures retain their error.</summary>
    void Configure(HrtfMode mode, int? dataset = null);
}
