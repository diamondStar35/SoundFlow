# Engine and backend architecture

The managed core owns graphs, providers, PCM transport and shared resource contracts. A backend translates those contracts into its own device, source and effect implementation. Optional features describe usable behavior; discovering one is not a request to emulate an unsupported native operation.

## Modules and ownership

| Module | Responsibility |
| --- | --- |
| Engine | Codec registration, device creation/enumeration and backend lifetime |
| Devices | Playback/capture ownership, staged buffers, switching and paired devices |
| Playback | Immutable clips, provider leases, transport, source lifetime and groups |
| Spatial | Listener and positional-source snapshots with explicit units |
| Effects | Typed settings and optional slot/send contracts |
| Features | Rendering, timing, batching, notifications and diagnostics interfaces |

An engine owns its devices; a playback device owns its sources and effect slots. Dispose sources/slots before devices, then the engine. Device cleanup releases remaining children. Source disposal is a lifetime template: an implementation releases its resources through `DisposeSource`, while core always relinquishes its provider lease, even when cleanup throws.

A mutable provider has one consuming source across the process. Use independent providers or create several sources from an immutable `Clip` to play content concurrently. `leaveOpen: true` preserves the provider after disposal; it does not permit simultaneous consumption. Construction failures release the lease. Graph providers own their graphs unless explicitly borrowed.

## Rendering paths

```text
provider / clip / graph
    -> shared transport (loops, speed, reverse)
    -> software modifiers
    -> backend voice (gain, pitch, spatialization, filters, sends)
    -> device output

MasterMixer graph -> one nonspatial backend voice -> same device output
```

`CreateSource` creates nonspatial playback. `CreateSpatialSource` preserves the input layout and returns positional controls directly. Backends without native spatial support throw `NotSupportedException`; MiniAudio does not substitute another spatial renderer.

A graph is combined before entering one voice. Its children therefore share a position and send routing. Create separate voices for sounds needing independent locations. `MasterMixer.Volume` controls the software graph feed, whereas `PlaybackDevice.MasterGain` controls the complete native mix.

Software-source conversion uses the device mix format. OpenAL retains source channels/rate and lets its native renderer resample. Channel-dependent modifiers must match the format actually processed, which is also supplied to source-buffer callbacks.

## Transport and time

`ReadSamples` is synchronous and returns an interleaved sample count, not bytes or frames. Complete frames contain `Channels` samples. Loop ends are exclusive; `-1` means the natural end. `Transport.Read` returns the produced count and Data, Starved or Completed status. A live provider returning zero does not end playback until completion is signalled.

Positive `PlaybackSpeed` uses pitch-preserving WSOLA. Reverse playback requires seeking and changes pitch through backwards interpolation. Native `ISourcePitch` changes rate and pitch independently after software processing.

Decoded position can lead playback by the queued PCM. OpenAL source `Time` maps the native queue cursor back to content position, excluding decode-ahead. Completion waits for queued PCM to drain; effect tails can continue after `PlaybackEnded`. Device-clock nanoseconds are a separate monotonic domain, distinct from wall time and content seconds.

## Buffer access and threads

| Stage | Meaning |
| --- | --- |
| SourceInput | Source PCM after software modifiers, before native effects/spatial rendering |
| GraphOutput | Complete software graph before native rendering |
| Capture | Converted microphone/input samples |
| FinalOutput | Complete device mix, including native effects when available |

OpenAL physical output exposes GraphOutput, not its final mix. Offline output exposes graph and final buffers. MiniAudio exposes its final software mix. The legacy `OnAudioProcessed` callback runs only for Capture/FinalOutput; graph PCM must not masquerade as a final echo-cancellation reference.

Spans are borrowed only for the callback. Do not retain them, block, dispose resources or issue synchronous native controls from PCM callbacks. OpenAL rejects these control cycles before they enter its worker queue. Final offline callbacks run on the rendering caller; source callbacks run on decode workers; capture callbacks run on the capture worker.

Each OpenAL context has one native worker. Each voice has a bounded reusable PCM pool and decoder worker. Providers must eventually return from reads for their worker to join during disposal. No thread is forcibly aborted. Offline render calls and final-buffer callbacks are serialized together.

OpenAL completion and backend notifications run on a managed dispatcher outside native callbacks and transport locks. Handlers may issue controls and should return promptly. Diagnostic notifications have a bounded queue and overflow counter. Completion is important delivery and is not dropped under diagnostic pressure; pending deliveries are cancelled when their owner is disposed.

## Failures and switching

Invalid settings and unsupported requests are rejected before native changes. Processing failures stop the affected voice, record diagnostics and post a notification. They do not disable cleanup or unrelated voices. Faulted voices must be recreated.

OpenAL reopens physical playback in place, preserving context-owned sources, queues, listener state and effects. Capture replacement preserves subscriptions. MiniAudio prepares replacement devices before transferring software sources/graphs and rolls back on startup failure. Full-duplex clocks remain independent; cleanup attempts both halves even when one fails.

Interop remains private to each backend. Core grants no backend friend access. The independent backend contract fixture compiles and exercises its public/protected extension interface.
