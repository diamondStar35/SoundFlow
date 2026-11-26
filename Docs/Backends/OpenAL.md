# OpenAL Soft backend

Reference `SoundFlow.Backends.OpenAL` and construct `SoundFlow.Backends.OpenAL.Engine`. FFmpeg is registered automatically. No MiniAudio package, library, codec or fallback is involved. Engine construction loads libraries but opens no physical hardware.

Bindings target OpenAL Soft 1.25.2, commit `b2c48f7718ef3fcf67921a8b6534c4914e328970`. That revision's native source/headers define behavior; Python-wrapper examples are supplementary references.

## Native spatial playback

```csharp
using System.Numerics;
using OpenAL = SoundFlow.Backends.OpenAL;
using SoundFlow.Enums;
using SoundFlow.Effects;
using SoundFlow.Features;
using SoundFlow.Playback;
using SoundFlow.Providers;
using SoundFlow.Spatial;
using SoundFlow.Structs;

using var engine = new OpenAL.Engine();
var mono = new AudioFormat { Format = SampleFormat.F32, Channels = 1, SampleRate = 48000 };
using var output = engine.InitializePlaybackDevice(null, mono with { Channels = 2 });
using var source = output.CreateSpatialSource(
    new StreamDataProvider(engine, mono, File.OpenRead("sound.mp3")), mono);
source.Settings = new SourceSettings { Position = new Vector3(2, 0, -4), RolloffFactor = .5f };
if (output.TryGetFeature<IScene>(out var scene))
    scene.Listener = new Listener { Forward = -Vector3.UnitZ, Up = Vector3.UnitY };
source.Play();
output.Start();
// Keep resources alive while playing.
```

Coordinates are right-handed: +X right, +Y up, -Z forward. Distances use world units (metres by default), velocities units/second and cones degrees. Stereo channel angles are radians. `IScene.MetresPerUnit` controls environmental air-absorption conversion; it does not transform coordinates, attenuation distances or speed of sound. Keep velocity and speed-of-sound units consistent.

`CreateSource` is nonspatial even for mono. Spatial creation preserves channels; unsupported layouts throw without hidden downmixing. Mono represents a point-like source. Native stereo/multichannel spatialization follows OpenAL's channel model and is not equivalent to a mono point source.

`OpenAL.Features.ISourceRendering` exposes gain bounds, radius, distance overrides, stereo angles, direct-channel mapping/remix, resamplers and EFX automatic filter gains. Inspect its capability flags before presenting controls. Unspecified distance models follow scene changes; explicit overrides remain independent. `ISourcePitch` changes native rate/pitch separately from transport speed.

## Feature coverage

| Capability | Interface |
| --- | --- |
| Listener, distance, Doppler and environmental scale | `SoundFlow.Spatial.IScene` |
| Positional source | `CreateSpatialSource`, `SpatialSource.Settings` |
| Optional native voice controls | `OpenAL.Features.ISourceRendering` |
| HRTF datasets and actual status | `IHrtfAudio` |
| Physical mode and limiter | `DeviceConfiguration`, `OpenAL.Features.IDeviceRendering` |
| EFX, filters and shared slots | `IEffects`, `IEffectSends` |
| Earlier software-point EFX sends | `IProcessingSends`, `Processing.Chain` |
| Clock, offset and latency | `IPlaybackTiming` |
| Synchronized vectors | `ISourceGroups` |
| Scheduled source/group starts | `IScheduledPlayback` |
| Coordinated property updates | `IUpdateBatch` |
| State/system events and failures | `INotifications` |
| Native/device/queue diagnostics | `IDiagnostics` |
| Application-only offline mix | `IOfflineRendering` |

Features are registered only when usable. Subfeature flags remain separate from interface availability. Native errors retain operation, error-code and library context.

## Effects and routing

The 13 standard EFX settings cover Reverb, EaxReverb, Chorus, Distortion, Echo, Flanger, FrequencyShifter, VocalMorpher, PitchShifter, RingModulator, Autowah, Compressor and Equalizer. They are immutable records with validated ranges/defaults and documented semantics. Support is probed per context; user configuration can disable individual effects.

`EfxReverbPresets.All` contains all 113 original presets, indexed case-insensitively. Customize records with `with`. This is EFX EAX reverb, not the legacy EAX 1–5 application interface.

```csharp
if (!output.TryGetFeature<IEffects>(out var effects) ||
    !source.TryGetFeature<IEffectSends>(out var sends))
    throw new NotSupportedException("Effect routing is unavailable");
using var room = effects.CreateSlot(EfxReverbPresets.All["ROOM"] with { DecayTime = 1.8f });
using var equalizer = effects.CreateSlot(new EqualizerSettings { HighGain = .5f });
if (effects.SupportsSlotChaining) room.SetTarget(equalizer);
sends.SetSend(0, room, gain: .4f);
sends.SetDirectFilter(new FilterSettings { Kind = FilterKind.LowPass, GainHF = .3f });
```

Slots are device-owned wet buses shared by many voices. Four sends per source are requested by default; `MaximumSends` reports the negotiated limit. Send capacity is not the total slot limit. Slot allocation can fail on native resource exhaustion.

Send gain multiplies its filter gain; slot gain controls that bus's wet output. Direct filtering is independent. Low-pass, high-pass and band-pass routes are supported. Software modifiers precede native processing. `IProcessingSends` can route copied earlier software points into native effects; see [software processing](../Processing.md) for mono-to-stereo adapters, opt-in latency compensation and ownership rules.

Slot targets require `AL_SOFT_effect_target`. Cycles, foreign devices and disposed targets are rejected. Disposing a slot detaches sends and incoming/outgoing links; incoming slots return to the main mix. Updates prepare a replacement before changing the current snapshot.

## Timing and coordinated playback

Groups have fixed, non-owning membership. Disposing one leaves its sources alive. Members must be distinct, live and from one device. Native vectors synchronize play/pause/stop boundaries. Pause or stop mixed playing/nonplaying members before synchronized startup. Rewind requires seekable members.

Start physical output before group or scheduled playback commands. Ordinary single-source Play may precede device Start. Offline devices need no hardware start.

```csharp
if (!output.TryGetFeature<IPlaybackTiming>(out var timing) ||
    !output.TryGetFeature<ISourceGroups>(out var groups) ||
    !output.TryGetFeature<IScheduledPlayback>(out var scheduler))
    throw new NotSupportedException("Native scheduling is unavailable");
source.Stop();
using var second = output.CreateSource(new Clip(new float[48000], mono));
using var group = groups.CreateGroup(source, second);
var when = timing.GetClock().Nanoseconds + 100_000_000; // 100 ms on this device
scheduler.PlayAt(group, when);
```

Scheduling prepares queues before arming the native command. Negative timestamps are invalid; past timestamps start immediately. Scheduling playing members is rejected. Stop cancels pending playback. Never combine device timestamps or use wall-clock ticks.

Clock snapshots include estimated physical output latency. Source timing reports native queue offset in frames and mapped content seconds; WSOLA lookahead is not playback progress. Hardware estimates can change with the endpoint/configuration.

`IUpdateBatch.Apply` executes synchronous property setters on the context worker under native deferred updates. Nested controls share the outer boundary. Exceptions always release deferral; earlier successful setters are not rolled back. Rendering, switching, resource disposal and asynchronous waiting are prohibited inside a batch.

## HRTF, capture and offline output

HRTF defaults to Automatic, respecting native/user configuration. Enabled is a request: inspect actual status and dataset names. Runtime resets and reopening preserve resources and current configuration. Physical output-mode requests are separate from offline output's explicit layout. Nullable limiter requests preserve native defaults when unspecified.

Capture uses an independent device/worker and converts mono/stereo S16 input into F32 callbacks. Full duplex does not promise sample-locked capture/playback. OpenAL loopback renders this application's mix; it cannot capture desktop audio.

Offline clocks advance through Render only. Render additional frames after completion to preserve effect tails. Render calls serialize final-buffer delivery. PCM callbacks must not synchronously issue native controls; completion/backend notifications run outside processing locks and may issue controls.

```powershell
dotnet run --project Samples/SoundFlow.Samples.OpenAL -- --offline rendered.wav
dotnet run --project Samples/SoundFlow.Samples.OpenAL -- --processing-offline processed.wav
dotnet run --project Samples/SoundFlow.Samples.OpenAL -- --scheduled-offline scheduled.wav
dotnet run --project Samples/SoundFlow.Samples.OpenAL -- --play sound.mp3
dotnet run --project Samples/SoundFlow.Samples.OpenAL -- --graph
dotnet run --project Samples/SoundFlow.Samples.OpenAL -- --capture recording.wav
dotnet run --project Samples/SoundFlow.Samples.OpenAL -- --devices
```

Only offline execution is suitable for unattended tests. Listening, microphone quality, hardware latency and hot-plug behavior need manual checks.

## Native delivery and limits

The supplied Windows x64 DLL comes from the official 1.25.2 distribution and loads directly without the OpenAL router. Provenance records its hash. Preserve packaged OpenAL, HRTF and FFT notices when redistributing.

OpenAL compilation is restricted to the GitHub workflow. The helper rejects local execution; normal .NET commands use precompiled assets. The workflow is not dispatched as part of implementation.

The matrix covers Windows x64/ARM64, Linux x64/ARM64, macOS x64/ARM64, iOS ARM64 and Android ARM64. It verifies architecture/Windows imports, runs desktop offline tests and requires the complete matrix for release packaging. Only Windows x64 is supplied in this checkout; other delivery awaits workflow validation.

Provenance records the pin, compiler options and two libc++ patches replacing ambiguous smart-pointer member pointers with equivalent lambdas. Native checkouts and downloaded toolchains are not vendored.

Desktop project references copy the selected RID into lib; NuGet uses runtime assets. iOS transitive targets embed frameworks. Android uses its packaged library search and ARM64/16 KiB-compatible ELF alignment.

Prepared uploads, explicit clip pooling and shared decoding are covered in [Playback](../Playback.md). Native prepared sources support spatialization and wet effects without decoding sessions, with explicit software-processing restrictions.

Dedicated dialogue/LFE settings are available when `ALC_EXT_DEDICATED` and effect probes succeed. They route a wet signal according to the output layout; LFE routing is not a bass-boost filter and requires a suitable output path.

`ConvolutionSettings` uses `AL_SOFTX_convolution_effect` from the pinned runtime. Supply a nonempty mono/stereo impulse-response `Clip` and optional forward/up orientation. Responses are retained in the prepared-PCM cache while attached; gains are not normalized. Slot updates prepare the effect and response before changing the current snapshot and restore the previous snapshot on attachment failure. Large or pinned responses can exhaust the configured native clip budget.

Request `DeviceConfiguration.DebugMessages = true` to create a native debug context. Optional `OpenAL.Features.IDebugOutput` filters severities, labels sources/slots and delivers copied messages through bounded diagnostics outside native callbacks. Unsupported debug requests fail explicitly; debugging is disabled by default. `IDiagnostics` includes shared decoding counts, clip storage, allocation/reuse totals and implemented SoundFlow features separately from native extension names.

Additional sample commands: `--prepared-offline output.wav`, `--convolution-offline output.wav`, `--debug-offline output.wav`, and `--pool-stress report.txt`. All use offline rendering.

Ambisonics/UHJ, callback/mapped-buffer APIs, virtualization, streamed-input pooling, geometry acoustics and legacy EAX remain deferred.
