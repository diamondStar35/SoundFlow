using System.Numerics;
using SoundFlow.Processing;
using SoundFlow.Spatial;
using SoundFlow.SteamAudio;
using SoundFlow.Structs;

/// <summary>Exercises every rendering adapter in both source and packaged NativeAOT consumers.</summary>
internal static class EffectVerification
{
    internal static void Run(AudioFormat mono)
    {
        using var renderer = new Renderer();
        var controls = new Spatialization(new()
        {
            Position = new(10, 0, -10),
            Distance = new() { Model = AttenuationModel.None },
            AirAbsorptionModel = new(),
            DirectivityModel = new() { DipoleWeight = .25f },
            SpatialBlend = .5f,
            Width = 1.25f
        });
        var input = Enumerable.Range(0, 256).Select(i => .2f * MathF.Sin(i * .13f)).ToArray();
        var stereo = mono with { Channels = 2, Layout = ChannelLayout.Stereo };
        var quad = mono with { Channels = 4, Layout = ChannelLayout.Quad };
        var headphones = new float[512];
        var speakers = new float[1024];

        using var direct = renderer.CreateDirectProcessor(mono, controls);
        var filtered = new float[256];
        direct.Process(input, filtered, 256);
        RequireSound(filtered);
        using var binaural = renderer.CreateBinauralProcessor(mono, controls);
        binaural.Process(filtered, headphones, 256);
        RequireSound(headphones);
        controls.Settings = controls.Settings with { Interpolation = HrtfInterpolation.Nearest, SpatialBlend = 0 };
        binaural.Process(filtered, headphones, 256);
        for (var i = 0; i < filtered.Length; i++)
            if (headphones[2 * i] != filtered[i] || headphones[2 * i + 1] != filtered[i])
                throw new InvalidOperationException("Zero spatial blend changed the ordinary mono signal.");

        using var panning = renderer.CreatePanningProcessor(mono, controls, SpeakerLayout.Quad);
        panning.Process(filtered, speakers, 256);
        RequireSound(speakers);
        using var surround = renderer.CreateVirtualSurroundProcessor(quad, SpeakerLayout.Quad);
        surround.Process(speakers, headphones, 256);
        RequireSound(headphones);
        using var shaping = renderer.CreateStereoProcessor(stereo, controls);
        var shaped = new float[512];
        shaping.Process(headphones, shaped, 256);
        RequireSound(shaped);

        controls.Settings = controls.Settings with { SpatialBlend = 1 };
        using var encoder = renderer.CreateAmbisonicsEncoder(mono, controls, 3);
        var field = new float[256 * 16];
        encoder.Process(filtered, field, 256);
        foreach (var convention in new[] { AmbisonicsConvention.SN3D, AmbisonicsConvention.FuMa })
        {
            using var convert = renderer.CreateAmbisonicsConverter(encoder.OutputFormat, convention);
            using var restore = renderer.CreateAmbisonicsConverter(convert.OutputFormat, AmbisonicsConvention.N3D);
            var converted = new float[field.Length]; var restored = new float[field.Length];
            convert.Process(field, converted, 256); restore.Process(converted, restored, 256);
            for (var i = 0; i < field.Length; i++)
                if (MathF.Abs(restored[i] - field[i]) > 2e-6f)
                    throw new InvalidOperationException("Ambisonics convention conversion changed the sound field.");
        }
        renderer.Listener = new() { Forward = Vector3.UnitX };
        using var rotation = renderer.CreateAmbisonicsRotation(encoder.OutputFormat);
        var oriented = new float[field.Length];
        rotation.Process(field, oriented, 256);
        RequireSound(oriented);
        using var fieldPanning = renderer.CreateAmbisonicsPanning(encoder.OutputFormat, SpeakerLayout.Quad);
        fieldPanning.Process(oriented, speakers, 256);
        RequireSound(speakers);
        using var fieldBinaural = renderer.CreateAmbisonicsBinaural(encoder.OutputFormat);
        fieldBinaural.Process(oriented, headphones, 256);
        RequireSound(headphones);
        foreach (var useHrtf in new[] { false, true })
        {
            var builder = new Builder().Add(format => renderer.CreateAmbisonicsDecoder(format, useHrtf));
            using var runner = builder.Build().CreateRunner(encoder.OutputFormat, 256);
            var captures = runner.CreateCaptures();
            runner.Process(field, headphones, 256, captures);
            RequireSound(headphones);
            var blocks = 0;
            while (runner.Drain(headphones, captures) != 0)
                if (++blocks > 64) throw new InvalidOperationException("Ambisonics tail exceeded its bounded drain.");
        }
    }

    private static void RequireSound(float[] pcm)
    {
        if (!pcm.All(float.IsFinite) || !pcm.Any(sample => MathF.Abs(sample) > 1e-5f))
            throw new InvalidOperationException("An extended Steam Audio effect produced invalid or silent PCM.");
    }
}
