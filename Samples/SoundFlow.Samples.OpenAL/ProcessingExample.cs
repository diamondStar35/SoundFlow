using SoundFlow.Backends.OpenAL.Features;
using SoundFlow.Effects;
using SoundFlow.Features;
using SoundFlow.Playback;
using SoundFlow.Processing;
using SoundFlow.Structs;

/// <summary>A deterministic mono-to-stereo processor and an independent earlier-point EAX send.</summary>
internal static class ProcessingExample
{
    internal static void Run(SoundFlow.Backends.OpenAL.Engine engine, AudioFormat stereo, string? destination)
    {
        using var device = engine.CreateRenderDevice(stereo,
            new SoundFlow.Backends.OpenAL.DeviceConfiguration { Hrtf = HrtfMode.Disabled });
        var mono = stereo with { Channels = 1, Layout = ChannelLayout.Mono };
        var builder = new Builder();
        var environment = builder.Capture("environment");
        builder.Add(format => new StereoBlock(format));
        var definition = builder.Build();
        var samples = Enumerable.Range(0, 24000).Select(i => .15f * MathF.Sin(i * .08f)).ToArray();
        using var source = device.CreateSource(new Clip(samples, mono), new SourceOptions { Processing = definition });
        if (!device.TryGetFeature<IEffects>(out var effects) || !source.TryGetFeature<IProcessingSends>(out var sends))
            throw new NotSupportedException("Captured native sends are unavailable.");
        using var room = effects.CreateSlot(EfxReverbPresets.All["ROOM"]);
        // None is the default: this wet path retains its independent timing. Select Automatic here
        // to delay its input by the later processor's 256 frames (about 5.33 ms at this sample rate).
        sends.SetSend(0, environment, room, new() { Gain = .4f });
        if (source.TryGetFeature<ISourceRendering>(out var rendering))
            rendering.Settings = new() { ChannelMapping = ChannelMapping.Direct };
        source.Play();
        using var file = File.Create(destination ?? "openal-processing.wav");
        using var encoder = engine.CreateEncoder(file, "wav", stereo);
        var block = new float[512 * stereo.Channels];
        for (var frames = 0; frames < stereo.SampleRate * 3; frames += 512)
        {
            device.Render(block);
            encoder.Encode(block);
        }
        Console.WriteLine($"Mono capture -> EAX; stereo processor -> direct channels: {file.Name}");
    }

    // This is an adapter demonstration, not an HRTF implementation. An external spatializer belongs
    // in ProcessBlock, with its native state owned by this instance and released in Dispose.
    private sealed class StereoBlock(AudioFormat input) : FixedBlockProcessor(input,
        input with { Channels = 2, Layout = ChannelLayout.Stereo }, 256)
    {
        protected override void ProcessBlock(ReadOnlySpan<float> input, Span<float> output)
        {
            for (var frame = 0; frame < BlockFrames; frame++)
            {
                output[frame * 2] = input[frame];
                output[frame * 2 + 1] = input[frame] * .6f;
            }
        }
    }
}
