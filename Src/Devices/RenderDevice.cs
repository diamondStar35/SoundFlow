using Engine = SoundFlow.Engine;
using SoundFlow.Structs;

namespace SoundFlow.Devices;

/// <summary>A playback device whose clock advances only when Render is called. This is not desktop-audio capture.</summary>
public abstract class RenderDevice : PlaybackDevice
{
    /// <summary>Initializes an offline device.</summary>
    protected RenderDevice(Engine engine, AudioFormat format, DeviceConfiguration config) : base(engine, format, config) { }
    /// <summary>Fills complete interleaved output frames, including native effects and their tails. Calls are serialized.</summary>
    public abstract void Render(Span<float> output);
}
