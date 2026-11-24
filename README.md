# SoundFlow

This fork provides a managed .NET 8 audio engine with independent MiniAudio and OpenAL Soft backends, software graphs/DSP and pluggable codecs. OpenAL registers FFmpeg automatically and has no MiniAudio dependency.

| Package | Role | Default codecs |
| --- | --- | --- |
| SoundFlow | Managed engine, transport, graphs and shared contracts | None |
| SoundFlow.Backends.OpenAL | Native 3D voices, EFX, HRTF, scheduling, capture and offline output | FFmpeg |
| SoundFlow.Backends.MiniAudio | MiniAudio devices and software graph playback | MiniAudio |
| SoundFlow.Codecs.FFMpeg | Explicit codec registration for other engines | None until registered |

Reference a backend explicitly. Construct `SoundFlow.Backends.OpenAL.Engine` or `SoundFlow.Backends.MiniAudio.Engine`. Native assets belong to backend/codec packages, not core.

`CreateSource` is explicitly nonspatial, including mono input. `CreateSpatialSource` returns typed positional controls and preserves source channels. Unsupported layouts/backends throw rather than silently downmixing or substituting another renderer.

## Guides

- [Architecture, ownership and processing stages](Docs/Architecture.md)
- [OpenAL Soft features and runnable examples](Docs/Backends/OpenAL.md)
- [MiniAudio backend](Docs/Backends/MiniAudio.md)
- [Writing an independent backend](Docs/BackendAuthoring.md)
- [Migrating renamed interfaces](Docs/Migration.md)

The OpenAL backend includes all 13 standard EFX effects, 113 reverb presets, shared slots/chaining, per-source rendering controls, HRTF, synchronized groups, device-clock scheduling, latency queries, batching and managed notifications. Capabilities are discovered through typed interfaces on their actual owner.

## Validation and native delivery

```powershell
dotnet build SoundFlow.sln -c Release
dotnet test Tests/SoundFlow.Tests/SoundFlow.Tests.csproj -c Release
dotnet run --project Samples/SoundFlow.Samples.OpenAL -- --scheduled-offline rendered.wav
```

These commands consume precompiled libraries. OpenAL native compilation is restricted to its GitHub workflow; the helper rejects local invocation. The sample's other commands explicitly access physical devices and are unsuitable for unattended tests.

Retained targets are Windows x64/ARM64, Linux x64/ARM64, macOS x64/ARM64, iOS ARM64 and Android ARM64. Only the supplied Windows x64 OpenAL runtime is present locally. Other native artifacts require workflow generation and validation before distribution is complete. MIDI backend files deleted by this fork remain deleted.

This repository is based on [LSXPrime/SoundFlow](https://github.com/LSXPrime/SoundFlow). Preserve the repository license and packaged native-library notices when distributing binaries.
