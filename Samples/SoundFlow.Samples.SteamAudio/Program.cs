using System.Numerics;
using SoundFlow.Backends.OpenAL;
using SoundFlow.Enums;
using SoundFlow.Playback;
using SoundFlow.SteamAudio;
using SoundFlow.Structs;

var format = new AudioFormat { Format = SampleFormat.F32, Channels = 1, SampleRate = 48000, Layout = ChannelLayout.Mono };
var stereo = format with { Channels = 2, Layout = ChannelLayout.Stereo };
using var engine = new Engine();
using var renderer = new Renderer(new() { Mode = args.Contains("--speakers") ? RenderingMode.Speakers : RenderingMode.Headphones });
var pcm = Enumerable.Range(0, 48000).Select(i => .2f * MathF.Sin(2 * MathF.PI * 440 * i / 48000)).ToArray();
var clip = new Clip(pcm, format);
var controls = new SpatializationSettings { Position = new(3, 0, -3) };
if (args.Contains("--effects"))
    controls = controls with { AirAbsorptionModel = new(), SpatialBlend = .75f, Pan = .4f, Width = 1.25f };

if (args.Contains("--play"))
{
    using var output = engine.InitializePlaybackDevice(null, stereo, new DeviceConfiguration { Hrtf = SoundFlow.Features.HrtfMode.Disabled });
    using var source = renderer.CreateSource(output, clip, controls);
    source.IsLooping = true;
    source.Play();
    output.Start();
    Console.WriteLine("Turning the listener for ten seconds. The native output stays nonspatial and receives headphone stereo.");
    var clock = System.Diagnostics.Stopwatch.StartNew();
    while (clock.Elapsed.TotalSeconds < 10)
    {
        var angle = (float)clock.Elapsed.TotalSeconds;
        renderer.Listener = new() { Forward = new(MathF.Sin(angle), 0, -MathF.Cos(angle)) };
        Thread.Sleep(10);
    }
    return;
}

using var device = engine.CreateRenderDevice(stereo);
using var voice = renderer.CreateSource(device, clip, controls);
voice.Play();
var rendered = new float[48000 * 2 * 2];
for (var offset = 0; offset < rendered.Length; offset += 512)
{
    var angle = offset / (float)rendered.Length * MathF.PI;
    renderer.Listener = new() { Forward = new(MathF.Sin(angle), 0, -MathF.Cos(angle)) };
    device.Render(rendered.AsSpan(offset, Math.Min(512, rendered.Length - offset)));
}
if (!rendered.All(float.IsFinite) || rendered.Sum(sample => (double)sample * sample) < 1)
    throw new InvalidOperationException("Offline headphone rendering did not produce valid audible PCM.");
if (args.Contains("--verify"))
{
    EffectVerification.Run(format);
    Console.WriteLine("Steam Audio verification passed: callback rendering, moving listener, direct sound, panning, stereo controls, virtual surround, and Ambisonics.");
    return;
}

var path = args.FirstOrDefault(argument => !argument.StartsWith("--", StringComparison.Ordinal)) ?? "headphones.wav";
using var writer = new BinaryWriter(File.Create(path));
writer.Write("RIFF"u8);
writer.Write(36 + rendered.Length * sizeof(float));
writer.Write("WAVEfmt "u8);
writer.Write(16);
writer.Write((short)3);
writer.Write((short)2);
writer.Write(48000);
writer.Write(48000 * 2 * sizeof(float));
writer.Write((short)(2 * sizeof(float)));
writer.Write((short)32);
writer.Write("data"u8);
writer.Write(rendered.Length * sizeof(float));
foreach (var sample in rendered) writer.Write(sample);
Console.WriteLine($"Wrote {Path.GetFullPath(path)}");
