# SoundFlow FFmpeg codecs

Native audio decoding, encoding, and URL streaming for SoundFlow. The OpenAL backend registers the
factory automatically; other backends can register `new FFmpegCodecFactory()` explicitly.

The audio-only build supports MP3, AAC, WAV/PCM/ADPCM, AIFF, Ogg Vorbis, Opus, FLAC, ALAC, M4A,
Matroska/WebM audio and other formats listed in the build profile. Video codecs, hardware video,
devices, filters, and command-line tools are excluded.

Use `NetworkDataProvider.OpenAsync` for HTTP(S), radio, HLS, RTP/RTSP and RTMP(S) input with bounded
probing, timeouts and cancellation. Playback uses the engine's shared decoder workers. Query
`Capabilities` for the features registered in the loaded native runtime.

See [the FFmpeg guide](../../Docs/FFmpeg.md) for examples, codec/container distinctions, TLS trust,
streaming behavior, encoding, workflow builds, and verification.
