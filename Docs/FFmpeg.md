# FFmpeg audio

The codec package uses native FFmpeg for decoding and encoding. OpenAL registers this factory by
default. It has no managed ADPCM decoder. The native wrapper, allowlist, dependency versions, and
SHA-256 source hashes live in `Native/ffmpeg-codec` and `Packaging/FFmpeg.json`.

## Audio profile

The build pins FFmpeg 8.0.3, a maintenance update on the existing ABI branch. It starts with
`--disable-everything --disable-autodetect` and explicitly enables:

- MP3/MP2/MP1, AAC/HE-AAC, Vorbis, Opus, AC3/EAC3, DTS, and common speech formats.
- FLAC, ALAC, APE, WavPack, TTA, and other listed lossless audio decoders.
- PCM, G.711 A-law/mu-law, DSD, and the native ADPCM decoder family, including IMA WAV and Microsoft ADPCM.
- WAV, AIFF, CAF, Ogg, MP4/M4A, Matroska/WebM audio, MPEG-TS, and the other listed audio containers.
- HTTP(S), internet radio, audio HLS, RTP/RTSP, RTMP(S), TCP, and UDP input.

Video/subtitle decoders and encoders, image-only demuxers, hardware video features, filters, capture
and output devices, and command-line programs are excluded. Some containers also support video;
the wrapper selects an audio stream and discards other streams. It does not decode video.

Encoding uses LAME for MP3, libopus for Opus, libvorbis for Ogg Vorbis, and native FFmpeg audio
encoders for AAC, ALAC, FLAC, PCM and other enabled formats. M4A produces an MP4 audio container;
AAC produces ADTS. Seekable output streams permit native muxers to finalize headers. Nonseekable
M4A uses fragmented output. Internal managed PCM is always interleaved F32, including when the
encoded source contains integer samples.

`new SoundFlow.Codecs.FFMpeg.Capabilities()` queries the loaded library's actual version, codecs,
containers, and protocols. Distributed runtimes also contain `ffmpeg/profile.json`, native capability
reports on runnable CI platforms, and third-party notices. No runtime feature list is inferred from
filename extensions or a package version.

## Network playback

```csharp
using SoundFlow.Codecs.FFMpeg;
using SoundFlow.Enums;
using SoundFlow.Structs;

var format = new AudioFormat
{
    Format = SampleFormat.F32, Channels = 2, SampleRate = 48000,
    Layout = ChannelLayout.Stereo
};
using var provider = await NetworkDataProvider.OpenAsync(
    new Uri("https://audio.example.net/radio.mp3"), format,
    new NetworkSettings { ReadTimeout = TimeSpan.FromSeconds(5) }, cancellationToken);
using var source = output.CreateSource(provider, format);
source.Play();
```

The same API opens a supported playlist URL, including HLS, or an RTSP endpoint. RTP payloads must
provide enough format information for FFmpeg, for example standard static payload types or an SDP
served over HTTP. RTSP defaults to interleaved TCP; `RtspOverTcp = false` selects UDP.

Startup/probing runs on the thread pool. The opening token cancels startup; later reads use the
engine's shared decoder workers and their own cancellation tokens. No source-specific decoding
thread is created. Input is incremental and bounded by the decoder/engine buffers. A slow connection
can occupy a worker until data, cancellation, or timeout arrives; choose an appropriate timeout for
your workload. There is no automatic reconnect or complete-file download. Reopen explicitly to reconnect.

Network providers do not promise seeking. Duration may be unknown and is then reported as zero.
Remote MP4/M4A needs HTTP range support or a streaming-friendly container layout (fast start or
fragmented MP4) to avoid scanning to the end for metadata before playback can begin.
The integer provider position saturates at the interface maximum for long-running streams. Codec
and network errors propagate as failures; they are not reported as successful end-of-stream.

`NetworkSettings` configures read timeout, probe bytes, media analysis duration, User-Agent, extra HTTP
headers, and RTSP transport. Headers are copied at open. TLS peer verification is enabled for the
initial request and nested HLS playlists, media segments and keys. Windows uses SChannel and Apple
uses Secure Transport with platform trust. Linux and Android statically link OpenSSL 3.5 LTS;
`CertificateAuthorityFile` supplies a PEM root bundle where the platform has no OpenSSL trust store.
This option is rejected on platforms using native trust. The distributed profile uses LGPL v3-or-later
FFmpeg with GPL/nonfree components disabled; dependency licenses are included in runtime notices.
Analysis duration measures media examined during probing; it is not a wall-clock startup deadline.
Use the opening cancellation token for that deadline. Zero selects FFmpeg's automatic analysis limit.

The native network input API handles playback input. Encoding to a managed network `Stream` still
uses the encoder stream API. There is no separate managed API for publishing an RTMP or RTP URL.

## Build and verification

C/C++ compilation runs in `.github/workflows/build-ffmpeg.yml`, not as part of a local .NET build.
The retained targets are Windows x64/ARM64, Linux x64/ARM64, macOS x64/ARM64, Android ARM64, and iOS ARM64.
Debug information is disabled in the native release dependencies. Native CLI programs are not built.

On runnable hosted platforms, `Scripts/verify-ffmpeg.py` loads the just-built library and verifies:

- Actual audio-only codec registration and required formats/protocols.
- Native encode/decode/rewind with WAV, AIFF, MP3, AAC, M4A, ALAC, FLAC, Vorbis, and Opus.
- Native IMA ADPCM decoding independently of the .NET layer.
- Loopback HTTP and HLS audio, plus bounded cancellation and timeout of stalled I/O.

The .NET suite also verifies URL playback through shared workers, HTTP headers, opening/read
cancellation, timeout failures, F32 conversion, and common-container factory round trips. It opens no
physical audio device. The headless AOT consumer can verify encoded game/application assets against
the installed native runtime. Foreign binaries are compiled but are not executed on incompatible hosts.

```powershell
dotnet test Tests/SoundFlow.Tests/SoundFlow.Tests.csproj -c Release -p:EnableSteamAudio=true -m:1
python Scripts/verify-ffmpeg.py --library Codecs/SoundFlow.Codecs.FFMpeg/runtimes/win-x64/native/soundflow-ffmpeg.dll
dotnet publish Tests/CodecAot/CodecAot.csproj -c Release -r win-x64 -o artifacts/codec-aot
./artifacts/codec-aot/CodecAot.exe
```

Keep the matching native runtime with the managed codec package when updating. New URL, capability,
and seekable-encoder exports require the rebuilt wrapper; an older DLL does not provide these APIs.
