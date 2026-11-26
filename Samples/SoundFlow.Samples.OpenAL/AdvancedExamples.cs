using System.Diagnostics;
using System.Numerics;
using SoundFlow.Backends.OpenAL.Features;
using SoundFlow.Components;
using SoundFlow.Effects;
using SoundFlow.Features;
using SoundFlow.Playback;
using SoundFlow.Spatial;
using SoundFlow.Structs;
using BackendEngine = SoundFlow.Backends.OpenAL.Engine;

/// <summary>Hardware-free examples for native clip reuse, measured-response effects and bounded decoding.</summary>
internal static class AdvancedExamples
{
    internal static void Run(BackendEngine engine, AudioFormat stereo, string mode, string? destination)
    {
        using var device = engine.CreateRenderDevice(stereo, new SoundFlow.Backends.OpenAL.DeviceConfiguration
            { DebugMessages = mode == "--debug-offline" });
        if (mode == "--pool-stress") { Stress(engine, device, destination); return; }
        if (device.TryGetFeature<IDebugOutput>(out var debug) && debug.Enabled)
        {
            debug.MinimumSeverity = DebugSeverity.Notification;
            debug.Message += (_, message) => Console.WriteLine($"OpenAL {message.Severity}: {message.Text}");
            debug.Write("Preparing a native clip and its independent spatial source.");
        }
        var mono = stereo with { Channels = 1, Layout = ChannelLayout.Mono };
        var clip = new Clip(Enumerable.Range(0, 12000).Select(i => .15f * MathF.Sin(2 * MathF.PI * 440 * i / mono.SampleRate)).ToArray(), mono);
        if (!device.TryGetFeature<IPreparedClips>(out var clips)) throw new NotSupportedException("Prepared clips are unavailable.");
        using var prepared = clips.Prepare(clip);
        using var source = prepared.CreateSpatialSource();
        source.Settings = new SourceSettings { Position = new Vector3(.5f, 0, -1), RolloffFactor = 0 };
        if (!device.TryGetFeature<IEffects>(out var effects) || !source.TryGetFeature<IEffectSends>(out var sends))
            throw new NotSupportedException("Native wet routing is unavailable.");
        Settings settings;
        if (mode == "--convolution-offline")
        {
            if (!effects.SupportedEffects.Contains(typeof(ConvolutionSettings))) throw new NotSupportedException("This runtime does not advertise convolution.");
            // Authored sparse impulse response: direct arrival followed by diminishing room reflections.
            // Its samples are used as supplied, with no automatic normalization or file decoding during playback.
            var response = new float[24000]; response[0] = .5f; response[4000] = .3f; response[9000] = .2f; response[18000] = .1f;
            settings = new ConvolutionSettings { ImpulseResponse = new Clip(response, mono) };
        }
        else settings = EfxReverbPresets.All["ROOM"];
        using var slot = effects.CreateSlot(settings);
        sends.SetSend(0, slot, .6f);
        source.Play();
        using var file = File.Create(destination ?? "openal-prepared.wav");
        using var encoder = engine.CreateEncoder(file, "wav", stereo);
        var block = new float[512 * stereo.Channels];
        for (var frames = 0; frames < stereo.SampleRate * 3; frames += 512) { device.Render(block); encoder.Encode(block); }
        if (device.TryGetFeature<IDiagnostics>(out var diagnostics)) Console.WriteLine(diagnostics.GetSnapshot());
        Console.WriteLine($"Prepared clip and native effect tail rendered to {file.Name}.");
    }

    private static void Stress(BackendEngine engine, SoundFlow.Devices.RenderDevice device, string? destination)
    {
        var mono = device.Format with { Channels = 1, Layout = ChannelLayout.Mono };
        var clip = new Clip(Enumerable.Range(0, 4096).Select(i => .005f * MathF.Sin(i * .1f)).ToArray(), mono);
        var before = GC.GetTotalAllocatedBytes(true); var timer = Stopwatch.StartNew();
        var producers = new List<SoundPlayer>();
        DecodingSnapshot workers;
        try
        {
            // Logical producers do not each receive a thread. Explicit preparation makes this exercise deterministic offline.
            for (var i = 0; i < 256; i++) { var player = new SoundPlayer(engine, mono, clip.CreateProvider()); producers.Add(player); player.Play(); }
            foreach (var player in producers) player.PrepareForRender();
            workers = engine.Decoding.GetSnapshot();
            if (workers.Workers != engine.PlaybackConfiguration.WorkerCount || workers.Pending > 256)
                throw new InvalidOperationException("Worker or pending-job bounds were exceeded.");
        }
        finally { foreach (var player in producers) player.Dispose(); }

        using var pool = device.CreatePool(new() { Capacity = 32 });
        var leases = new List<PlaybackLease>();
        try
        {
            for (var i = 0; i < 256; i++) leases.Add(pool.Play(clip, new() { Priority = i, Protected = i == 0, Looping = true }));
            device.Render(new float[1024]);
        }
        finally { foreach (var lease in leases) lease.Dispose(); }
        // Repeated one-shot admissions exercise warmed native reuse, independent of the priority-stealing burst above.
        for (var i = 0; i < 1000; i++) { using var lease = pool.Play(clip); }
        timer.Stop();
        if (!device.TryGetFeature<IDiagnostics>(out var diagnostics)) throw new NotSupportedException("Diagnostics unavailable.");
        var native = diagnostics.GetSnapshot();
        if (native.SourceAllocations > 33 || native.PreparedClips != 1 || native.Decoding.Sessions != 1)
            throw new InvalidOperationException("Transient playback retained unexpected native or decoding resources.");
        var report = $"256 logical producers: {workers}\n" +
            $"Clip pool: {pool.GetSnapshot()}\n" +
            $"Native source allocations: {native.SourceAllocations}; reuses: {native.ReusedSources}; prepared bytes: {native.PreparedClipBytes}\n" +
            $"Measured elapsed milliseconds: {timer.Elapsed.TotalMilliseconds:F2}; managed bytes allocated: {GC.GetTotalAllocatedBytes(true) - before}\n" +
            $"Underruns: {native.Underruns}; failure: {native.LastFailure ?? "none"}\n";
        Console.Write(report);
        if (destination != null) File.WriteAllText(destination, report);
    }
}
