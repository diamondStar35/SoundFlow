using SoundFlow.Providers;
using Engine = SoundFlow.Engine;
using SoundFlow.Abstracts;
using SoundFlow.Interfaces;
using SoundFlow.Structs;

namespace SoundFlow.Components;

/// <summary>
/// A sound player that plays audio from a data provider.
/// </summary>
public sealed class SoundPlayer : SoundPlayerBase
{
    /// <summary>
    /// Initializes a new instance of the <see cref="SoundPlayer"/> class.
    /// </summary>
    /// <param name="engine">The audio engine used for managing audio playback.</param>
    /// <param name="format">The format of the audio stream, including sample rate, sample format, and channel count.</param>
    /// <param name="dataProvider">The data provider that supplies audio data for playback.</param>
    public SoundPlayer(Engine engine, AudioFormat format, IDataProvider dataProvider) : base(engine, format, dataProvider) { }

    /// <summary>Creates a channel-aware buffered player. Endpoint channel conversion is optional and occurs after the chain.</summary>
    public SoundPlayer(Engine engine, AudioFormat format, IDataProvider dataProvider, Processing.Chain? processing, int endpointChannels = 0, Processing.Chain? renderProcessing = null, int renderFrames = 256, object? renderDomain = null)
        : base(engine, format, dataProvider, processing, endpointChannels, renderProcessing, renderFrames, renderDomain) { }

    /// <inheritdoc />
    public override string Name { get; set; } = "Sound Player";
}
