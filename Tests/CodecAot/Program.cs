using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using SoundFlow.Codecs.FFMpeg;
using SoundFlow.Enums;
using SoundFlow.Structs;

// Run the published executable to exercise the generated native interop, rather than the test runner.
var format = new AudioFormat
{
    Format = SampleFormat.F32,
    Channels = 2,
    SampleRate = 48000,
    Layout = ChannelLayout.Stereo
};
var capabilities = new Capabilities();
Require(capabilities.NonAudioCodecCount == 0, "Unexpected non-audio codecs.");
Require(capabilities.Decoders.Contains("adpcm_ima_wav"), "Missing native IMA ADPCM.");
Require(capabilities.InputProtocols.Contains("https"), "Missing HTTPS.");
var factory = new FFmpegCodecFactory();
var tone = Enumerable.Range(0, 9600).Select(i => .2f * MathF.Sin((i / 2) * .13f)).ToArray();
byte[] wave = [];
foreach (var kind in new[] { "wav", "mp3", "aac", "m4a", "flac", "ogg", "opus" })
{
    using var encoded = new MemoryStream();
    using (var encoder = factory.CreateEncoder(encoded, kind, format))
    {
        Require(encoder != null && encoder.Encode(tone) == tone.Length, $"Encoding failed: {kind}.");
    }
    if (kind == "wav")
        wave = encoded.ToArray();
    encoded.Position = 0;
    using var decoder = factory.TryCreateDecoder(encoded, out _, format);
    Require(decoder != null, $"Decoder could not open {kind}.");
    var pcm = new float[16384];
    var count = decoder!.Decode(pcm);
    Require(count is >= 7000 and <= 14000, $"Incorrect duration: {kind}.");
    Require(pcm.Take(count).All(float.IsFinite) && pcm.Take(count).Any(value => Math.Abs(value) > .01f),
        $"Invalid or silent PCM: {kind}.");
    Require(decoder.Seek(0), $"Rewind failed: {kind}.");
    Console.WriteLine($"PASS NativeAOT {kind} encode/decode/rewind");
}

using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(15));
using var listener = new TcpListener(IPAddress.Loopback, 0);
listener.Start();
var endpoint = new Uri($"http://127.0.0.1:{((IPEndPoint)listener.LocalEndpoint).Port}/audio");
var serve = Serve();
try
{
    using (var provider = await NetworkDataProvider.OpenAsync(endpoint, format, cancellationToken: stop.Token))
    {
        var pcm = new float[16384];
        var count = provider.ReadSamples(pcm, stop.Token);
        Require(count == tone.Length && pcm.Take(count).All(float.IsFinite) &&
            pcm.Take(count).Any(value => Math.Abs(value) > .01f), "HTTP did not produce the expected PCM.");
    }
    Console.WriteLine("PASS NativeAOT HTTP native input and PCM callbacks");
    using var cancel = new CancellationTokenSource(TimeSpan.FromMilliseconds(250));
    var watch = Stopwatch.StartNew();
    try
    {
        using var unexpected = await NetworkDataProvider.OpenAsync(endpoint, format, cancellationToken: cancel.Token);
        throw new InvalidOperationException("Blocked input unexpectedly opened.");
    }
    catch (OperationCanceledException) when (cancel.IsCancellationRequested)
    {
        Require(watch.Elapsed < TimeSpan.FromSeconds(3), "Native interrupt callback did not cancel promptly.");
    }
    Console.WriteLine("PASS NativeAOT cancellation of blocked native I/O");
}
finally
{
    stop.Cancel();
    listener.Stop();
    await serve;
}
Console.WriteLine($"PASS NativeAOT FFmpeg {capabilities.Version}, audio-only profile");

async Task Serve()
{
    try
    {
        for (var request = 0; request < 2; request++)
        {
            using var client = await listener.AcceptTcpClientAsync(stop.Token);
            var stream = client.GetStream();
            using var reader = new StreamReader(stream, Encoding.ASCII, false, 1024, leaveOpen: true);
            while (!string.IsNullOrEmpty(await reader.ReadLineAsync(stop.Token)))
            { }
            if (request == 0)
            {
                var header = Encoding.ASCII.GetBytes($"HTTP/1.1 200 OK\r\nContent-Type: audio/wav\r\nContent-Length: {wave.Length}\r\nConnection: close\r\n\r\n");
                await stream.WriteAsync(header, stop.Token);
                await stream.WriteAsync(wave, stop.Token);
            }
            else
            {
                await stream.WriteAsync("HTTP/1.1 200 OK\r\nConnection: close\r\n\r\n"u8.ToArray(), stop.Token);
                await Task.Delay(Timeout.Infinite, stop.Token);
            }
        }
    }
    catch (OperationCanceledException) when (stop.IsCancellationRequested) { }
}

static void Require(bool condition, string message)
{
    if (!condition)
        throw new InvalidOperationException(message);
}
