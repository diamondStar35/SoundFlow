# Steam Audio

Steam Audio is an optional part of SoundFlow's core. It provides headphone spatialization, speaker
panning, direct-sound filtering, virtual surround, and Ambisonics rendering through SteamAudio.NET.
The selected backend owns output and native effects. Ordinary sources retain their existing behavior.

## Setup

Initialize the pinned module explicitly, then enable it for the entire source build:

```powershell
git submodule update --init Modules/SteamAudio.NET
dotnet build Src/SoundFlow.csproj -c Release -p:EnableSteamAudio=true
```

`EnableSteamAudio` defaults to false. Disabled builds omit the integration and its project reference;
the module does not need to be initialized. Enabled builds report a missing checkout without fetching
anything. Keep the property consistent across projects referencing SoundFlow in one build.

Use the module's existing native assets. For shared loading, stage its official SDK with
`python Modules/SteamAudio.NET/scripts/build.py sdk`, or supply matching binaries through
`SteamAudioNativeDirectory`. This downloads prebuilt binaries; it does not compile Steam Audio.
Dynamic loading uses the application's `lib` directory, followed by normal native probing.
Delivered Steam Audio targets are Windows x64, Linux x64, macOS x64/ARM64, Android ARM64, and iOS ARM64.
This optional module does not expand that native target set to every runtime supported by SoundFlow.

NativeAOT publishing defaults to static Steam Audio linking. Import `Src/SoundFlow.SteamAudio.targets`
in the application when using project references; packaged builds import the dependency's transitive
targets automatically. Supply the matching workflow-produced `Native/static/<rid>` bundle, including
its `Link.targets` and all required archives. `SteamAudioStaticLink=false` explicitly selects shared
loading. Source compilation of native libraries belongs to GitHub Actions, never normal local builds.
Enabled packages require the matching SteamAudio.NET package, produced together by the integration
workflow; a consumer property cannot add APIs to a previously compiled disabled package.

## Headphone sources

```csharp
using SoundFlow.SteamAudio;

using var renderer = new Renderer();
using var source = renderer.CreateSource(output, provider, inputFormat,
    new SpatializationSettings { Position = new(3, 0, -4) });
source.Play();

renderer.Listener = new() { Position = new(1, 0, 0), Forward = new(0, 0, -1) };
if (source.TryGetFeature<ISpatialization>(out var spatial))
    spatial.Settings = spatial.Settings with { Position = new(-3, 0, -4) };
```

Create a stereo output device first. Input may be mono or stereo F32. Stereo input occupies one position;
full spatial blend combines its channels into a point source. Use independent voices for independent
channel positions. Coordinates use +X right, +Y up, -Z forward. Relative positions use the listener's
local axes. The application converts any different world coordinate convention before assigning poses.

`RendererSettings.BlockFrames` defaults to 256; 64, 128, and 512 are also supported. SOFA loading is
selected with `SofaPath` at construction. Shared HRTFs are cached per processing sample rate. Each voice
has independent convolution/filter history. Dispose sources before the renderer and output device.
Hot HRTF changes require recreation; listener/source pose changes do not reset DSP history.

Distance attenuation is enabled by default: `LinearDb`, minimum 1, maximum 200, rolloff 1. The linear
amplitude curve is mapped through a 33 dB range, with an exact cutoff at maximum distance. It intentionally
does not fade the last decibels down to zero. Inverse, exponential, linear, and explicit `None` curves are
also available. `CurveScaler` changes effective distances: minimum is floored at 0.001 and scaled first;
maximum is then clamped against that effective minimum, scaled, and clamped again. This order matters
when minimum exceeds maximum or the scale is below one. Inverse/exponential models have no maximum cutoff.

## Direct sound and stereo controls

Air absorption is disabled until `AirAbsorptionModel` or manual `AirAbsorption` gains are supplied:

```csharp
var settings = new SpatializationSettings
{
    Position = new(10, 0, -20),
    AirAbsorptionModel = new() { Strength = 1 },
    DirectivityModel = new() { DipoleWeight = .5f, DipolePower = 1 },
    Forward = new(0, 0, 1),
    SpatialBlend = .75f,
    Interpolation = HrtfInterpolation.Bilinear,
    Pan = .5f,
    Width = 1
};
```

Absorption uses physical distance, independently of the selected volume attenuation curve.
`RendererSettings.MetresPerUnit` defines the length of one world unit in metres; it defaults to one.
`Strength` multiplies that distance, and zero disables attenuation from this model. The default atmosphere
uses Steam Audio's low/mid/high exponential rates. To supply your own rates, set
`Coefficients = new AbsorptionCoefficients(low, mid, high)`: the gains are `exp(-rate * distanceMetres * strength)`.
Rates are nonnegative values per metre, rather than amplitude gains. Manual `AirAbsorption = new Bands(...)`
overrides the automatic model and accepts amplitude gains in [0,1].

Automatic directivity uses the source's normalized `Forward`/`Up` orientation and the listener position.
`DipoleWeight` ranges from zero (omnidirectional) to one (dipole); .5 is cardioid.
`DipolePower` is nonnegative and sharpens the pattern. A relative source's pose and orientation both use
listener-local coordinates. At coincident positions the gain is one. Manual `Directivity` overrides the
model. Optional `Occlusion` and three-band `Transmission` remain application-supplied; transmission
requires occlusion. These controls do not create geometry or run simulation.

`SpatialBlend` defaults to one. At zero, headphone rendering preserves stereo or duplicates mono to both
channels, without HRTF coloration; distance and direct-path filtering still apply. Intermediate headphone
values use Steam Audio's native HRTF blending, rather than a linear mix of fully rendered dry/wet signals.
`Interpolation` selects nearest or bilinear HRTF sampling; bilinear remains the default for moving sources.

The default stereo chain applies pan and width after spatialization. Pan ranges from zero (left) to one
(right), with .5 preserving both channel gains. It uses the existing unity-center square-root gain law.
Width uses mid/side processing: zero collapses to mono, one preserves the signal exactly, and two doubles
the side component. Widening can increase peaks; the processor does not clip them. Nonneutral pan/width
are rejected by surround source helpers, including later control updates. Custom chains add
`CreateStereoProcessor` explicitly wherever shaping is required.

Custom chains can select which direct components a stage applies:

```csharp
builder.Add(format => renderer.CreateDirectProcessor(format, controls, DirectEffects.Distance));
var roomInput = builder.Capture("room");
builder.Add(format => renderer.CreateDirectProcessor(format, controls,
    DirectEffects.All & ~DirectEffects.Distance));
builder.Add(format => renderer.CreateBinauralProcessor(format, controls));
```

This captures distance-attenuated sound before dry-path absorption, directivity, occlusion, and HRTF.
Bind `roomInput` with `IProcessingSends` to feed native effects independently. Selected components
are fixed when the processor is created; live settings continue to use the same spatial control object.
The original factory overload applies `DirectEffects.All`. Transmission requires occlusion in its mask.
Latency compensation remains `None` unless explicitly requested on a processor or send.

## Speakers and virtual surround

Select `RenderingMode.Speakers` and a `SpeakerLayout` before creating sources:

```csharp
using var renderer = new Renderer(new()
{
    Mode = RenderingMode.Speakers,
    Speakers = SpeakerLayout.Surround51
});
using var source = renderer.CreateSource(surroundOutput, monoClip,
    new() { Position = new(2, 0, -4) });
```

Speaker helpers accept mono point sources and require an output device with the same channel count.
Panning follows the listener pose without headphone HRTF processing. Fractional blend is a linear mix
with an unpositioned image: mono is duplicated for stereo, and surround uses fixed-front panning.
Stereo pan/width remain available when the selected speaker output is stereo.

`SpeakerLayout` provides mono, stereo, quad, 5.1, and 7.1 layouts. The native channel order is front
left/right, center, LFE, rear left/right, then side left/right as applicable. `Custom(directions)` copies
and normalizes one through 32 speaker directions in the supplied channel order. Custom layouts carry
`ChannelLayout.Unknown`; their channel count must still be supported by the selected physical backend.
Direction arrays and their native storage remain owned for the processor's lifetime.

An authored multichannel mix needs virtual surround, rather than point-source binaural rendering:

```csharp
builder.Add(format => renderer.CreateVirtualSurroundProcessor(format, SpeakerLayout.Quad));
```

The input channel count must match that layout. Each virtual speaker retains its own fixed direction,
and the result is stereo headphones. This effect does not rotate the authored speaker bed with listener
movement. Use an Ambisonics sound field for a world-oriented bed.

## Ambisonics

Ambisonics is explicit format metadata, never inferred from channel count. A four-channel field is not
quad speaker audio. Set both layout and descriptor when supplying recorded Ambisonics:

```csharp
var fieldFormat = new AudioFormat
{
    Format = SampleFormat.F32,
    SampleRate = 48000,
    Channels = 4,
    Layout = ChannelLayout.Ambisonics,
    Ambisonics = new AmbisonicsFormat(1, AmbisonicsConvention.SN3D)
};
var builder = new SoundFlow.Processing.Builder();
builder.Add(format => renderer.CreateAmbisonicsConverter(format, AmbisonicsConvention.N3D));
builder.Add(format => renderer.CreateAmbisonicsDecoder(format));
```

Orders zero through three use 1, 4, 9, or 16 channels. Native effects consume ACN/N3D. Explicit converters
also support ACN/SN3D (AmbiX) and FuMa, retaining order and sample rate. Ordinary channel conversion cannot
change an Ambisonics descriptor; decode to speakers first. Rate conversion may preserve the field
channel by channel. Prepared native clips, speaker devices, native effect sends, and physical source
endpoints reject undecoded fields. Decode a capture before sending it to a backend effect.
Audio file decoding does not infer these tags from a four-channel file: declare its convention yourself.

For a mono input, `CreateAmbisonicsEncoder(format, controls, order)` creates a listener-relative field
using the source's current positional direction. Apply direct filtering before encoding. For world-space
recorded fields, choose one of these paths:

- `CreateAmbisonicsDecoder` applies the current listener rotation once, then decodes to headphones or
  speakers. Pass `binaural: false` and a speaker layout to select speaker output.
- `CreateAmbisonicsRotation` followed by `CreateAmbisonicsBinaural` or `CreateAmbisonicsPanning` keeps
  rotation and decoding separate. Those two decoders consume an already oriented field.

Do not add listener rotation twice. In particular, the positional encoder already produces the listener
frame and normally feeds an unrotated decoder. These processors render existing sound fields; they do
not simulate propagation, geometry, reflections, baking, or pathing.

## Processing and native effects

`SourceOptions.Processing` runs on shared production workers before decoded packets are queued.
`RenderProcessing` runs on demand after that queue. A render processor must not block, allocate, read
files, dispatch events, or issue playback controls. Declare `RequiredBlockFrames` for a fixed native effect;
all fixed effects in one render chain must agree with `RenderBlockFrames`.

Build a custom direct/binaural chain when inserting software effects or native capture points:

```csharp
var controls = new Spatialization(new() { Position = new(3, 0, -4) });
var builder = new SoundFlow.Processing.Builder();
builder.Add(format => renderer.CreateDirectProcessor(format, controls));
var environment = builder.Capture("environment");
builder.Add(format => renderer.CreateBinauralProcessor(format, controls));
var options = new SoundFlow.Playback.SourceOptions
{
    RenderProcessing = builder.Build(),
    RenderBlockFrames = renderer.Settings.BlockFrames
};
using var voice = output.CreateSource(provider, inputFormat, options);
```

On OpenAL, bind `environment` through `IProcessingSends` to an EAX slot before playback. A capture after
the direct processor includes its attenuation/filtering; a capture before it does not. Existing
final-output `IEffectSends` semantics remain unchanged. Native EAX wet output uses OpenAL's mixer, not
Steam Audio's HRTF. Latency compensation remains `None`; select `Automatic` or `ProcessorOptIn` explicitly
on each send that should align declared software buffering. Artistic delays and HRTF ear differences
are not buffering compensation.

OpenAL's render path requires `AL_SOFT_callback_buffer` and uses nonspatial sources with direct channel
mapping. There is no automatic queued fallback. For a headphone-only output, request OpenAL HRTF disabled
when opening the device. If feeding Steam Audio sources through a software master graph, select
`OpenAL.DeviceConfiguration.OnDemandGraph=true`; every graph processor must obey the callback contract.
Otherwise that graph bridge retains its existing native streaming queue. Individually created render
sources use callbacks regardless of the graph setting.
On-demand graph feeds do not advertise a graph PCM observation tap; offline final-output observation
remains available. Apply render processors to buffered graph leaves rather than wrapping a live graph
provider in another render chain.

Processed pool leases accept `RenderProcessingFactory` and `RenderBlockFrames`. Return a definition that
creates independent DSP and spatial controls per lease. Prepared native clips bypass managed processing.
The default helper registers `ISpatialization`; custom chains retain their explicit `Spatialization` owner.

## Timing and lifecycle

Use one `Renderer` per serialized output/render domain. SoundFlow binds its native shared workspaces
to the first device/domain and rejects reuse on a different one, including software device transfers.
Create a new renderer and sources for another device. Manually invoked processors must also be serialized
within their renderer. Independent renderers can run concurrently.

Steam Audio's HRTF database includes mutable interpolation scratch. Source construction and warm-up use
separate HRTFs from live rendering, so constructing another voice cannot overwrite active callback work.
Only construction is serialized by an asset lock; rendering does not acquire that lock. Each native
effect and its planar buffers are allocated and warmed before publication.

Listener and source setters publish complete snapshots atomically. One chain quantum captures its source
controls and listener pose before any processor runs, so direct filtering, spatialization, and stereo
shaping use the same generation even when a setter runs midway through the chain. Orientation bases are
prepared on the control thread. Updating controls does not enqueue work behind decoding or clear DSP history.

Workers decode ahead into bounded input blocks. The render consumer gathers one complete native quantum
only when output is requested, processes it with the latest snapshots, and retains the unconsumed
remainder. Ready input produces output immediately without a block of adapter startup silence. Callback
and decoded-packet lengths need not equal the quantum; normal packet boundaries are never padded.

Listener changes affect the next newly rendered quantum. Already rendered frames, native resampler
lookahead, driver buffers, and device latency remain. A 256-frame quantum is about 5.33 ms at 48 kHz or
5.80 ms at 44.1 kHz; these are not total output-latency guarantees. Lower quanta increase DSP overhead.
Choose device periods independently; OpenAL's backend configuration determines its physical update size.

`RenderProcessingState` separates decoded frames from retained rendered frames and reports quantum size
and starvation counts. `ISpatialization` reports applied source/listener generations. OpenAL diagnostics
aggregate on-demand sources and retained frames; device timing remains available through native features.

At temporary starvation the output is silence/effect decay, not EOF; content position does not advance.
At EOF only the final partial quantum is padded, followed by explicit native tail draining. Natural
completion waits for dry and wet endpoints. Pause retains history; discontinuities clear retained state.
Replace processing structure only while stopped. Producer replacements must preserve the format entering
the render stage; recreate a source to change that handoff or native endpoint format. Existing source PCM observation remains on production workers and describes
the pre-render format; callbacks never invoke application PCM handlers.

Run the sample with `--play` for listener movement or an output WAV path for offline export. Add
`--speakers` to select stereo panning, or `--effects` to enable absorption, partial blend, and stereo shaping.
`--verify` renders without opening a physical device and exercises every adapter, convention round trips,
and bounded Ambisonics tail draining. It also serves as the static NativeAOT and packaged-consumer check.
The unit tests compare actual PCM/tails with native Steam Audio effects and check allocation and lifecycle
behavior. The integration workflow builds native libraries only on GitHub Actions and never publishes packages.
