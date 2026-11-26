using System.Numerics;
using SoundFlow.Backends.OpenAL;
using SoundFlow.Components;
using SoundFlow.Effects;
using SoundFlow.Enums;
using SoundFlow.Features;
using SoundFlow.Modifiers;
using SoundFlow.Playback;
using SoundFlow.Providers;
using SoundFlow.Structs;

// All commands are keyboard/console accessible. Playback, graph, capture and enumeration modes access physical devices.
// Both offline modes are suitable for CI and NativeAOT smoke tests; they open no physical audio device.
var mode = args.FirstOrDefault() ?? "--help";
if (mode == "--help")
{
    Console.WriteLine("OpenAL examples: --offline [output.wav], --processing-offline [output.wav], --scheduled-offline [output.wav], --prepared-offline [output.wav], --convolution-offline [output.wav], --debug-offline [output.wav], --pool-stress [report.txt], --play [audio-file], --graph, --capture [output.wav], --devices");
    return;
}
using var engine = new SoundFlow.Backends.OpenAL.Engine(); // Automatically registers FFmpeg. MiniAudio is neither referenced nor loaded.
var stereo = new AudioFormat { Format = SampleFormat.F32, Channels = 2, Layout = ChannelLayout.Stereo, SampleRate = 48000 };
var mono = stereo with { Channels = 1, Layout = ChannelLayout.Mono };
if (mode == "--processing-offline")
{
    ProcessingExample.Run(engine, stereo, args.ElementAtOrDefault(1));
    return;
}
if (mode is "--prepared-offline" or "--convolution-offline" or "--debug-offline" or "--pool-stress")
{
    AdvancedExamples.Run(engine, stereo, mode, args.ElementAtOrDefault(1));
    return;
}

if (mode == "--devices")
{
    engine.UpdateAudioDevicesInfo();
    foreach (var device in engine.PlaybackDevices) Console.WriteLine($"Playback: {device.Name}, default={device.IsDefault}, ID={device.Id}");
    foreach (var device in engine.CaptureDevices) Console.WriteLine($"Capture: {device.Name}, default={device.IsDefault}, ID={device.Id}");
    return;
}
if (mode == "--capture")
{
    using var capture = engine.InitializeCaptureDevice(null, mono);
    using var recorder = new Recorder(capture, args.ElementAtOrDefault(1) ?? "openal-capture.wav");
    var result = recorder.StartRecording();
    if (!result.IsSuccess) throw new InvalidOperationException($"Recorder initialization failed: {result}");
    capture.Start();
    Console.WriteLine("Recording the default microphone through OpenAL and FFmpeg. Press Enter to finish.");
    Console.ReadLine();
    capture.Stop(); recorder.StopRecording();
    return;
}
if (mode is not ("--offline" or "--scheduled-offline" or "--play" or "--graph")) throw new ArgumentException("Unknown sample mode. Use --help.");

using var output = mode is "--offline" or "--scheduled-offline" ? engine.CreateRenderDevice(stereo) : engine.InitializePlaybackDevice(null, stereo);
using var source = mode == "--play" && args.Length > 1
    ? output.CreateSpatialSource(new StreamDataProvider(engine, mono, File.OpenRead(args[1])), mono)
    : mode == "--graph" ? MakeGraphSource() : output.CreateSpatialSource(Tone());
source.AddModifier(new HighPassModifier(mono, 60)); // CPU DSP happens before the native source/effects stage.

if (source is SoundFlow.Playback.SpatialSource spatial)
    spatial.Settings = new SoundFlow.Spatial.SourceSettings { Position = new Vector3(1, 0, -2), RolloffFactor = .2f };
if (output.TryGetFeature<SoundFlow.Spatial.IScene>(out var scene))
    scene.Listener = new SoundFlow.Spatial.Listener { Position = Vector3.Zero, Forward = -Vector3.UnitZ, Up = Vector3.UnitY };

// Slots are device resources, shared by any number of sources. The reverb wet output passes through the equalizer.
if (!output.TryGetFeature<SoundFlow.Effects.IEffects>(out var effects)) throw new NotSupportedException("The loaded device does not support EFX.");
using var reverb = effects.CreateSlot(EfxReverbPresets.All["CONCERTHALL"] with { DecayTime = 2.5f });
using var equalizer = effects.CreateSlot(new EqualizerSettings { HighGain = .5f });
if (effects.SupportsSlotChaining) reverb.SetTarget(equalizer);
if (source.TryGetFeature<SoundFlow.Effects.IEffectSends>(out var sends))
{
    sends.SetDirectFilter(new SoundFlow.Effects.FilterSettings { Kind = SoundFlow.Effects.FilterKind.LowPass, GainHF = .8f });
    sends.SetSend(0, reverb, .6f);
}
if (output.TryGetFeature<IHrtfAudio>(out var hrtf))
    Console.WriteLine($"HRTF: {hrtf.Status}; datasets: {string.Join(", ", hrtf.Datasets)}");

using var companion = mode == "--scheduled-offline" ? output.CreateSpatialSource(Tone()) : null;
using var scheduledGroup = companion != null && output.TryGetFeature<ISourceGroups>(out var groups) ? groups.CreateGroup(source, companion) : null;
if (scheduledGroup != null)
{
    if (!output.TryGetFeature<IScheduledPlayback>(out var scheduler) || !output.TryGetFeature<IPlaybackTiming>(out var timing))
        throw new NotSupportedException("Native scheduling is unavailable.");
    if (output.TryGetFeature<IUpdateBatch>(out var updates))
        updates.Apply(() => companion!.Settings = new SoundFlow.Spatial.SourceSettings { Position = new Vector3(-1, 0, -2) });
    scheduler.PlayAt(scheduledGroup, timing.GetClock().Nanoseconds + 250_000_000);
    Console.WriteLine("Two native spatial voices scheduled together after 250 ms of device-clock time.");
}
else source.Play();
if (output is SoundFlow.Devices.RenderDevice render)
{
    using var file = File.Create(args.ElementAtOrDefault(1) ?? "openal-offline.wav");
    using var encoder = engine.CreateEncoder(file, "wav", stereo);
    var pcm = new float[512 * stereo.Channels];
    // Render five seconds: the finite source is one second; the remaining time preserves the native reverb tail.
    for (var frames = 0; frames < 5 * stereo.SampleRate; frames += pcm.Length / stereo.Channels)
    {
        render.Render(pcm);
        encoder.Encode(pcm);
    }
    Console.WriteLine($"Rendered native effects and their tails to {file.Name} without opening hardware.");
}
else
{
    source.IsLooping = true;
    output.Start();
    Console.WriteLine("Playing with spatial audio and EAX reverb. Press Enter to finish.");
    Console.ReadLine();
    source.Stop(); output.Stop();
}
if (output.TryGetFeature<IDiagnostics>(out var diagnostics)) Console.WriteLine(diagnostics.GetSnapshot());

SoundFlow.Playback.Clip Tone() => new(Enumerable.Range(0, mono.SampleRate).Select(i => .2f * MathF.Sin(2 * MathF.PI * 440 * i / mono.SampleRate)).ToArray(), mono);

SoundFlow.Playback.Source MakeGraphSource()
{
    // A graph becomes one native source. Its children are mixed in software before this source's native effects.
    var graph = new Mixer(engine, mono);
    var player = new SoundPlayer(engine, mono, new RawDataProvider(Enumerable.Range(0, 48000)
        .Select(i => .15f * MathF.Sin(i * .07f)).ToArray(), mono.SampleRate)) { IsLooping = true };
    player.Play(); graph.AddComponent(player);
    return output.CreateSpatialSource(graph); // The source owns the graph and its children.
}
