using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Net.Security;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using SoundFlow.Codecs.FFMpeg;
using SoundFlow.Codecs.FFMpeg.Exceptions;
using SoundFlow.Enums;
using SoundFlow.Structs;
using Xunit;

namespace SoundFlow.Tests;

public sealed class NetworkAudioTests
{
    private static readonly AudioFormat Format = new()
    {
        Format = SampleFormat.F32,
        Channels = 2,
        Layout = ChannelLayout.Stereo,
        SampleRate = 48000
    };

    [Fact]
    public async Task HttpAudioRunsThroughTheSharedPlaybackWorkers()
    {
        await using var server = new Server();
        var settings = new NetworkSettings { Headers = new Dictionary<string, string> { ["X-Audio-Test"] = "native" } };
        using var provider = await NetworkDataProvider.OpenAsync(server.Url("/audio"), Format, settings);
        Assert.False(provider.CanSeek);
        Assert.Equal(SampleFormat.F32, provider.SampleFormat);
        using var engine = new Backends.OpenAL.Engine();
        using var output = engine.CreateRenderDevice(Format);
        using var source = output.CreateSource(provider, Format);
        source.Play();
        var pcm = new float[8192];
        output.Render(pcm);
        Assert.All(pcm, sample => Assert.True(float.IsFinite(sample)));
        Assert.Contains(pcm, sample => Math.Abs(sample) > .01f);
        Assert.Contains(server.Requests, request => request.Contains("X-Audio-Test: native", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task CancellationInterruptsBlockedNativeStartup()
    {
        await using var server = new Server();
        using var cancel = new CancellationTokenSource(TimeSpan.FromMilliseconds(250));
        var watch = System.Diagnostics.Stopwatch.StartNew();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            NetworkDataProvider.OpenAsync(server.Url("/stall"), Format, cancellationToken: cancel.Token));
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(3));
    }

    [Fact]
    public async Task CancellationInterruptsAReadAfterConnection()
    {
        await using var server = new Server();
        using var startup = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        using var provider = await NetworkDataProvider.OpenAsync(server.Url("/partial"), Format,
            new NetworkSettings { ReadTimeout = TimeSpan.FromSeconds(4), ProbeBytes = 2048, AnalyzeDuration = TimeSpan.FromMilliseconds(1) }, startup.Token);
        using var cancel = new CancellationTokenSource(TimeSpan.FromMilliseconds(250));
        var watch = System.Diagnostics.Stopwatch.StartNew();
        Assert.ThrowsAny<OperationCanceledException>(() => provider.ReadSamples(new float[960000], cancel.Token));
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(3));
    }

    [Fact]
    public async Task TimeoutIsAnErrorInsteadOfAnEmptySuccessfulStream()
    {
        await using var server = new Server();
        var watch = System.Diagnostics.Stopwatch.StartNew();
        await Assert.ThrowsAsync<FFmpegException>(() => NetworkDataProvider.OpenAsync(server.Url("/stall"), Format,
            new NetworkSettings { ReadTimeout = TimeSpan.FromMilliseconds(300) }));
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(3));
    }

    [Fact]
    public void InvalidNetworkSettingsFailBeforeConnecting()
    {
        Assert.Throws<ArgumentException>(() => { _ = NetworkDataProvider.OpenAsync(new Uri("file:///test.wav"), Format); });
        Assert.Throws<ArgumentOutOfRangeException>(() =>
        {
            _ = NetworkDataProvider.OpenAsync(new Uri("http://127.0.0.1"), Format,
            new NetworkSettings { ReadTimeout = TimeSpan.Zero });
        });
        Assert.Throws<ArgumentException>(() =>
        {
            _ = NetworkDataProvider.OpenAsync(new Uri("http://127.0.0.1"), Format,
            new NetworkSettings { Headers = new Dictionary<string, string> { ["X-Test"] = "a\r\nb" } });
        });
    }

    [Fact]
    public async Task HttpsRejectsAnUntrustedCertificate()
    {
        await using var server = new Server(tls: true);
        using var cancel = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        // Prove the fixture can serve TLS audio before asking FFmpeg to reject its untrusted issuer.
        using var handler = new HttpClientHandler
        {
            ServerCertificateCustomValidationCallback = HttpClientHandler.DangerousAcceptAnyServerCertificateValidator
        };
        using var control = new HttpClient(handler);
        var wave = await control.GetByteArrayAsync(server.Url("/audio"), cancel.Token);
        Assert.Equal("RIFF", Encoding.ASCII.GetString(wave, 0, 4));
        await Assert.ThrowsAsync<FFmpegException>(() => NetworkDataProvider.OpenAsync(server.Url("/audio"), Format,
            new NetworkSettings { ReadTimeout = TimeSpan.FromSeconds(1) }, cancel.Token));
    }

    /// <summary>A bounded loopback fixture with finite, stalled, and interrupted audio responses.</summary>
    private sealed class Server : IAsyncDisposable
    {
        private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
        private readonly CancellationTokenSource _stop = new();
        private readonly Task _accept;
        private readonly List<Task> _clients = [];
        private readonly X509Certificate2? _certificate;
        internal ConcurrentQueue<string> Requests { get; } = new();
        internal Server(bool tls = false)
        {
            if (tls)
            {
                using var key = RSA.Create(2048);
                var request = new CertificateRequest("CN=SoundFlow loopback test", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
                var names = new SubjectAlternativeNameBuilder();
                names.AddIpAddress(IPAddress.Loopback);
                request.CertificateExtensions.Add(names.Build());
                using var signed = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-1), DateTimeOffset.UtcNow.AddHours(1));
                // Import a PFX so Windows SChannel can acquire the server's private key.
                _certificate = new X509Certificate2(signed.Export(X509ContentType.Pfx));
            }
            _listener.Start();
            _accept = Accept();
        }
        internal Uri Url(string path) => new($"{(_certificate == null ? "http" : "https")}://127.0.0.1:{((IPEndPoint)_listener.LocalEndpoint).Port}{path}");
        private async Task Accept()
        {
            try
            {
                while (!_stop.IsCancellationRequested)
                {
                    var client = await _listener.AcceptTcpClientAsync(_stop.Token);
                    _clients.Add(Serve(client));
                }
            }
            catch (OperationCanceledException) { }
        }
        private async Task Serve(TcpClient client)
        {
            using (client)
            {
                try
                {
                    Stream stream = client.GetStream();
                    using var secure = _certificate == null ? null : new SslStream(stream, leaveInnerStreamOpen: true);
                    if (secure != null)
                    {
                        await secure.AuthenticateAsServerAsync(new SslServerAuthenticationOptions { ServerCertificate = _certificate }, _stop.Token);
                        stream = secure;
                    }
                    using var reader = new StreamReader(stream, Encoding.ASCII, false, 1024, leaveOpen: true);
                    var request = new StringBuilder();
                    string? line;
                    while (!string.IsNullOrEmpty(line = await reader.ReadLineAsync(_stop.Token)))
                        request.AppendLine(line);
                    Requests.Enqueue(request.ToString());
                    if (request.ToString().Contains("/stall "))
                    {
                        await stream.WriteAsync("HTTP/1.1 200 OK\r\nContent-Type: audio/wav\r\nConnection: close\r\n\r\n"u8.ToArray(), _stop.Token);
                        await Task.Delay(Timeout.Infinite, _stop.Token);
                        return;
                    }
                    var partial = request.ToString().Contains("/partial ");
                    var wave = Wave(partial ? 480000 : 12000);
                    var header = Encoding.ASCII.GetBytes(partial
                        ? "HTTP/1.1 200 OK\r\nContent-Type: audio/wav\r\nConnection: close\r\n\r\n"
                        : $"HTTP/1.1 200 OK\r\nContent-Type: audio/wav\r\nContent-Length: {wave.Length}\r\nConnection: close\r\n\r\n");
                    await stream.WriteAsync(header, _stop.Token);
                    await stream.WriteAsync(wave.AsMemory(0, partial ? 131072 : wave.Length), _stop.Token);
                    if (partial)
                        await Task.Delay(Timeout.Infinite, _stop.Token);
                }
                catch (OperationCanceledException) { }
                catch (IOException) { }
                catch (AuthenticationException) { }
            }
        }
        private static byte[] Wave(int frames)
        {
            using var stream = new MemoryStream();
            using var writer = new BinaryWriter(stream);
            writer.Write("RIFF"u8);
            writer.Write(36 + frames * 4);
            writer.Write("WAVEfmt "u8);
            writer.Write(16);
            writer.Write((short)1);
            writer.Write((short)2);
            writer.Write(48000);
            writer.Write(192000);
            writer.Write((short)4);
            writer.Write((short)16);
            writer.Write("data"u8);
            writer.Write(frames * 4);
            for (var i = 0; i < frames * 2; i++)
                writer.Write((short)(6000 * Math.Sin((i / 2) * .13)));
            return stream.ToArray();
        }
        public async ValueTask DisposeAsync()
        {
            _stop.Cancel();
            _listener.Stop();
            await _accept;
            await Task.WhenAll(_clients);
            _certificate?.Dispose();
            _stop.Dispose();
        }
    }
}
