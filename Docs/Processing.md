# Software processing and captured native sends

A processing chain describes the audio between a source's transport and its backend. Each processor declares its input and output formats. Interleaved F32 and sample rate stay constant within the chain; channel count can change. A mono-to-stereo spatializer therefore receives mono frames and produces stereo frames without changing seek or loop units.

For controls that must respond after decode-ahead, assign a separate `SourceOptions.RenderProcessing`
chain. It runs only when the endpoint requests audio. `RenderBlockFrames` defaults to 256, and fixed
processors declare matching `RequiredBlockFrames`. Native callbacks require bounded preallocated DSP;
provider I/O and ordinary processing stay on shared workers. See [Steam Audio](SteamAudio.md) for the
on-demand path, captured native sends, diagnostics, and complete lifecycle requirements.

`Source.Format` describes the original provider. `Source.RenderedFormat` describes the PCM delivered to the native endpoint or software mixer. `AudioBufferProcessed` reports the actual rendered format and runs on the producer, outside the physical audio callback. Its span is borrowed for that synchronous handler only. Do not issue playback controls or dispose resources from a processor or PCM handler.

## Definitions, instances and ownership

```csharp
using SoundFlow.Playback;
using SoundFlow.Processing;

var builder = new Builder();
var environment = builder.Capture("environment");
builder.Add(format => new MySpatialProcessor(format));
var definition = builder.Build();
using var source = output.CreateSource(provider, inputFormat,
    new SourceOptions { Processing = definition });
```

`MySpatialProcessor` is an application implementation of `Processor` or `FixedBlockProcessor`. A factory receives the preceding output format. Reuse the immutable definition across sources; return fresh mutable processor state from each factory. A live processor cannot bind to two runners. The source owns factory-created processors and disposes them after production has quiesced. Immutable lookup tables may be shared separately.

`Process` must write exactly the requested frame count. Output storage is cleared before the call. Frames, rather than interleaved sample counts, remain consistent through channel conversion. Allocate scratch storage at construction, not inside processing. Mid-chain sample-rate changes are rejected at binding; rate conversion belongs at the provider/endpoint boundary. Software mixer sources convert to the device rate before binding and to the device channels after processing. OpenAL accepts source rates directly and resamples natively.

Stop a source before `ConfigureProcessing`, `AddModifier`, or `RemoveModifier`. Pause retains live DSP history and does not permit structural edits. Chain replacement creates fresh state. OpenAL removes captured-point bindings when processor structure changes, so rebind the new chain's points before Play; legacy final-output sends remain attached. Parameter-only changes must be synchronized by the processor implementation, for example by atomically replacing immutable settings snapshots.

Legacy `AddModifier(instance)` borrows the caller's format-preserving modifier and never disposes it. `Builder.AddModifier(factory)` owns the factory result. Legacy modifiers run after the declared processors, before endpoint channel conversion. Do not share one mutable legacy modifier between sources. Existing custom modifiers work without new interfaces; implement `IModifierState` to declare buffering latency, a finite silence-drain bound and history reset. Without that interface their declared latency and tail are zero. Delay, chorus, algorithmic reverb, low-pass and high-pass modifiers implement this lifecycle. Artistic echo/reverb decay is a tail, not buffering latency.

## Fixed blocks and completion

Derive from `FixedBlockProcessor` for APIs requiring a fixed frame count. It accumulates partial producer chunks and adds one full processing block of constant buffering latency. Normal packets are never individually padded. At EOF, Drain feeds silence to release the last partial block and any declared effect tail. Override `EffectTailFrames` for additional finite state; call the base Reset from overrides.

For example, a 256-frame adapter at 48 kHz adds 5.33 ms to its processed path. A 100-frame final input chunk still has the same 256-frame delay; it does not acquire an extra packet-sized silence gap. An integration can instead implement `Processor` directly if it can preserve frame count without this buffering.

Runner drains upstream tails through downstream processors, then drains those processors' own remaining state. Earlier capture points receive silence during tails originating after them. Seek, stop, loop/speed reconfiguration and restart reset processor and compensation history; pause/resume retains it. Positions travel with PCM and declared buffering so decode-ahead and adapter silence are not reported as audible content progress. Completion waits for software tails and every queued endpoint. It does not wait for shared native effect slots to become silent: render or keep output running afterward to hear their decay.

## Earlier native effects

Backends may expose `IProcessingSends`. OpenAL exposes it on ordinary streaming/clip sources when EFX is available. Captures are copied before later processors overwrite their scratch storage. A captured send can feed native reverb from mono audio before an external spatializer produces the headphone stereo signal:

```csharp
using SoundFlow.Effects;
using SoundFlow.Features;

if (!output.TryGetFeature<IEffects>(out var effects) ||
    !source.TryGetFeature<IProcessingSends>(out var sends))
    throw new NotSupportedException("Captured sends unavailable");
using var room = effects.CreateSlot(new EaxReverbSettings());
sends.SetSend(0, environment, room, new SendSettings { Gain = .4f });
```

`IProcessingSends` and `IEffectSends` share send indices and the device's negotiated capacity. Binding index 0 through either interface replaces the other route at index 0. Old `IEffectSends` calls always send the final processed signal. Foreign points, foreign/disposed slots and invalid settings are rejected. Bind a point and compensation policy while stopped; an existing route's gain, filter or target may change during playback. Pass a null slot to disconnect.

OpenAL uses a wet-only native endpoint per distinct point/policy pair, shared by sends with that pair. Its direct filter has zero gain. It consumes the same bounded decoded packets and has no provider, decoder or thread of its own. All endpoints share logical volume, pitch, spatial controls and vector play/pause/stop/group/scheduled boundaries. Diagnostics report logical `Sources` separately from `ProcessingEndpoints`; native allocation totals include both. Native source/buffer consumption grows with distinct point/policy pairs, not with duplicate sends from the same pair.

## Latency compensation is opt-in

The default is **None**. Declaring processor latency never silently delays an earlier send, including when a processor sets `CompensateLatency = true`.

| Send policy | Added delay |
| --- | --- |
| `None` (default) | Zero; independent dry and wet timing is preserved |
| `Automatic` | Sum of all later processors' declared buffering latency |
| `ProcessorOptIn` | Sum only of later processors with `CompensateLatency = true` |

Set `SendSettings.Compensation` explicitly on each send that should be delayed. Processor opt-in applies only with the `ProcessorOptIn` send policy. No compensation is global. Storage is a bounded resettable delay allocated when the route is bound. Compensation excludes device queues, hardware latency, artistic predelay and HRTF interaural delays. It aligns declared software buffering, not the phase or acoustic timing of unrelated effects.

For an external headphone spatializer, request disabled OpenAL HRTF, create a nonspatial source and select `ISourceRendering.Settings.ChannelMapping = ChannelMapping.Direct`. This preserves the spatializer's final left/right signal. A native EAX send still renders through OpenAL's effect mixer: it does not pass through the earlier external HRTF. Reflection/late reverb panning remains native EAX panning, not external binaural localization. Use a managed wet branch through the external spatializer when that exact wet rendering is required.

## Processed pooled playback

```csharp
using var pool = output.CreatePool(new PlaybackPoolOptions { Capacity = 32 });
using var lease = pool.Play(clip, new PlaybackOptions
{
    Mode = PlaybackMode.Processed,
    ProcessingFactory = () => definition,
    Configure = voice =>
    {
        if (voice.TryGetFeature<IProcessingSends>(out var routing))
            routing.SetSend(0, environment, room);
    }
});
```

Configure runs before Play, so the first packet has its intended routes. Each processed lease binds fresh processor state even when definitions are shared. Prepared mode remains the default and bypasses managed processing; supplying a ProcessingFactory in prepared mode is rejected. Priority/protection/stealing and terminal lease outcomes apply to both modes.

`Samples/SoundFlow.Samples.OpenAL --processing-offline output.wav` is a complete deterministic adapter/capture/direct-channel example. Its stereo processor demonstrates the contract and is not an HRTF algorithm or a dependency on an external spatial library.
