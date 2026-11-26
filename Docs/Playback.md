# Streaming, prepared clips and playback pools

Choose the input path according to the sound's lifetime and processing requirements:

| Input | Memory and processing | Typical use |
| --- | --- | --- |
| `StreamDataProvider` over a file | Incremental decoding with bounded PCM queues | Music, long recordings and dialogue |
| Ordinary `Clip` source | Complete decoded PCM in managed memory; shared workers feed playback | Short sounds needing software modifiers or PCM callbacks |
| `IPreparedClips.Prepare(clip)` | Complete native upload shared by independent sources; no decoding session per source | Frequently reused sounds using native spatialization/effects |
| `PlaybackPool` | Explicitly limited transient clip playback, with priority admission and resource reuse | Bursts of game/UI/world effects |

OpenAL is a renderer, not a compressed-file decoder. Its backend registers FFmpeg by default. MiniAudio selects from the codec factories registered on its engine. Neither backend needs to decode an entire streamed file into PCM memory. Supplying a `MemoryStream` still keeps that stream's encoded bytes in memory.

## Shared decoding

Both backends use the engine's `DecodeScheduler`. It creates a fixed number of decoding workers, not one thread per source. Configure it before creating devices or players:

```csharp
using var engine = new SoundFlow.Backends.OpenAL.Engine
{
    PlaybackConfiguration = new SoundFlow.Playback.PlaybackConfiguration
    {
        WorkerCount = 2,
        BufferFrames = 512,
        QueueBuffers = 4,
        ReadTimeout = TimeSpan.FromSeconds(10) // Optional; default is no additional timeout.
    }
};
```

The default worker count is `Clamp(ProcessorCount - 1, 2, 4)`. Each software player retains at most four 512-frame decoded blocks, including any currently checked out by its consumer. OpenAL's device configuration independently controls its native streaming queues and source-session block sizes. Native queue storage and decoder/converter lookahead are additional bounded storage; `DecodingSnapshot.BufferedFrames` measures ready session PCM only.

A worker produces one block per turn. Empty queues receive priority, with regular service for partially buffered queues. Refill requests coalesce into one pending turn per session. One shared timer retries temporarily starved live inputs. Built-in graph generation does not occupy a decoding worker while waiting for another worker's input.

MiniAudio's callback reads prepared player PCM and runs the software graph; it does not perform provider reads or file decoding. OpenAL's context worker uploads ready PCM and generates graph feeds. Slow synchronous code inside a custom generator, modifier, analyzer or PCM callback can still delay processing: these are real-time application responsibilities. CLR stream implementations and native libraries can have their own threads beyond SoundFlow's configured workers.

Smaller queues reduce lookahead and tolerance for input delays; larger queues trade memory and software-processing latency for resilience. A fixed pool cannot guarantee progress if every worker is blocked in custom input code.

## Cancellation, controls and completion

`ICancellableDataProvider` and `ICancellableDecoder` are optional cooperative contracts. Built-in stream decoding forwards cancellation through both codecs and format conversion. Managed I/O exceptions are captured inside native callbacks and rethrown after the codec returns. They are not natural EOF notifications.

Shutdown cancels all sessions before waiting for individual inputs. A configured read timeout also requests cancellation. An interrupted decoder/session is terminal and must be recreated. A custom provider or stream that ignores cancellation must eventually return; there is no forced thread abort, replacement worker per stalled source, or hard deadline for arbitrary CPU work.

Pause suspends consumption and preserves prepared PCM. Stop prevents playback before serializing any required provider reset. Seek and transport changes can wait for an in-progress read on the calling control thread, but do not hold OpenAL's context worker across that read. Avoid these operations in frame-critical application code when input latency is unpredictable.

Seeking invalidates stale decoded blocks. Playback time follows consumed source content rather than decoder lookahead; MiniAudio's software time does not include hardware latency. Natural completion waits for the source queue to drain. Wet effect tails can continue afterward.

`PlaybackEnded` remains natural completion only. `PlaybackTerminated` also reports stop, failure and disposal. Completion/failure handlers run outside processing locks and may issue controls; handlers should return promptly. Synchronous PCM callbacks must not seek, wait for decoding, dispose playback resources or issue OpenAL controls. Borrowed spans are valid only during their callback.

For explicitly driven offline software graphs, call `PrepareForRender()` before each processing chunk. Keep chunks no larger than the configured producer block size; use repeated chunks for larger renders. OpenAL render devices prepare built-in graph leaves automatically. Live callbacks must never call this preparation method.

## Prepared OpenAL clips

```csharp
if (!output.TryGetFeature<SoundFlow.Playback.IPreparedClips>(out var clips))
    throw new NotSupportedException("Native clip preparation is unavailable.");

using var prepared = clips.Prepare(clip);
using var left = prepared.CreateSpatialSource();
using var right = prepared.CreateSpatialSource();
left.Settings = new() { Position = new(-1, 0, -2) };
right.Settings = new() { Position = new(1, 0, -2) };
left.Play(); right.Play();
```

These sources share one native PCM buffer and have independent playback offsets, native pitch, positions, gain, filters and sends. They need no decoding sessions. Releasing the preparation handle leaves existing sources alive because each retains its own upload reference.

Prepared sources support whole-clip looping and native timing/group/scheduled controls. They reject software modifiers, source PCM callback subscriptions, reverse playback, authored subrange loops and transport speed changes other than one. Use `ISourcePitch` for native rate/pitch changes, or an ordinary clip source for the complete software processing path. The backend never switches paths implicitly.

The default native prepared-PCM budget is 64 MiB per OpenAL context, configured with `DeviceConfiguration.PreparedClipBytes`. Clips and convolution responses share this budget. Uploads are keyed by clip identity, not sample-content hashing. Least-recently-used entries without live references can be evicted; referenced buffers cannot. An oversized clip or exhausted pinned budget is rejected. Managed clip storage and codec buffers are outside this budget.

Up to 32 idle prepared-source identifiers are cached per context by default (`MaximumIdleSources`). Returning one detaches its buffer, filters and sends. A fresh logical source reapplies spatial, rendering, pitch, gain and looping defaults before reusing the identifier. Context destruction releases all cached uploads and idle identifiers.

## Explicit transient pools

```csharp
using var pool = output.CreatePool(new() { Capacity = 32 });
using var warning = pool.Play(warningClip, new() { Priority = 100, Protected = true });

if (pool.TryPlay(impactClip, out var impact, new()
{
    Priority = 10,
    Spatial = new() { Position = new(2, 0, -4) }
}))
{
    impact!.Ended += (_, result) => Console.WriteLine(result.Reason);
    // Retain the lease while application controls need it; Dispose/Stop returns its slot immediately.
}
```

Pools accept clips only and start playback on admission. They use prepared clips on OpenAL and ordinary software clip sources on MiniAudio. OpenAL pooled sources inherit prepared-clip processing restrictions. Native spatial/pitch requests remain optional backend capabilities.

Capacity counts occupied leases, including paused playbacks. Higher numeric priority wins. If full, the pool steals the oldest voice among the lowest-priority unprotected voices only when its priority is strictly lower than the incoming request. Equal priorities are rejected. Protected voices, ordinary sources and voices in other pools are never stolen.

Stealing stops the selected playback immediately. No automatic fade or virtualization is applied; shared wet tails may continue. Preparing an incoming upload happens before stealing, so unsupported layouts or an exhausted upload budget leave the existing playback intact. Later native allocation/start failure can still reject admission after a victim has been stopped.

`TryPlay` returns false for capacity/native resource exhaustion. Invalid arguments and unsupported features throw. `Play` throws when admission fails. `GetSnapshot()` reports occupancy, admissions, steals and rejections.

Each lease has a fresh `Source`. Its native features are valid only while the lease is occupied; retain neither them nor its provider for another playback. Stop controls through the lease or its source release the slot. Natural completion and source disposal release it automatically. Returning an old lease cannot affect a reused native identifier.

`Outcome` remains available after release and contains Completed, Stopped, Stolen, Failed, Disposed or PoolDisposed. `Ended` delivers that copied outcome once, after resource release. Inspect `Outcome` if subscribing after a very short clip may already have finished. Device disposal closes its pools; validated software-device transfers move pools together with their sources.

## Offline examples and measurements

The OpenAL sample adds `--prepared-offline`, `--convolution-offline`, `--debug-offline` and `--pool-stress`. None opens physical hardware. The stress mode measures 256 managed producers, a 32-voice priority burst and 1,000 warmed admissions. It reports worker counts, managed allocations, native allocations/reuses, elapsed time and underruns. Timing is a measurement, not a portable performance guarantee.

Use `Engine.Decoding.GetSnapshot()`, each pool's snapshot and `IDiagnostics` together. Native `Extensions` describes the library; `ImplementedFeatures` and `AvailableFeatures` describe callable SoundFlow interfaces. Continue to inspect capability flags for subfeatures.
