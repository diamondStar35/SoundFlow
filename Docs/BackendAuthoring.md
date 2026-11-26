# Implementing an independent backend

A backend references managed SoundFlow core. It does not need friend access, another backend, reflection scans or a global feature singleton. `Tests/BackendContract` is a compiled example whose only project dependency is core.

## Engine and devices

Derive from `SoundFlow.Engine`. Implement enumeration, device creation, switching and CleanupBackend. Register codec factories explicitly; defaults belong to the backend constructor. Creating an engine should not open physical hardware merely to discover optional features.

Derive devices from the appropriate shared type. Register optional interfaces during construction with `RegisterFeature<T>`. Use `PublishBuffer` with the actual processing stage and `NotifyFramesRendered` with complete frame counts. A source feed is not a final-output echo-cancellation reference. Keep native identities private and publish managed device IDs.

Dispose child sources before destroying the context. `DisposeSources` attempts every source and aggregates errors. Preparing a replacement before releasing an endpoint makes switch failure recoverable.

## Sources and transport

Derive from `Playback.Source`, or `Playback.SpatialSource` for native positional rendering. The base constructor reserves the provider exclusively. Override `DisposeSource` for backend resources; the nonvirtual template releases the lease. If a constructor fails, call `ReleaseProviderOwnership` before rethrowing. Use `ValidateDisposal` to reject invalid callback/thread contexts before lifetime state changes.

Use public `Playback.Transport` for seeking, loops, reverse reads and WSOLA. `Read` returns a sample count plus Data/Starved/Completed. Serialize controls with reads. When native queues decode ahead, maintain a separate audible cursor and signal completion only after queued input drains. Completion does not imply effect tails are silent.

`Providers.IDataProvider` returns interleaved floats through synchronous `ReadSamples` and must preserve frames. Temporary empty reads are not EOF. Implement optional `ICancellableDataProvider`/`ICancellableDecoder` contracts for blocking input. Forward the token through adapters and use `StreamInput` to capture I/O failures inside native callbacks. A canceled native decoder is terminal; noncooperative reads must eventually return.

Use `engine.Decoding` and public `DecodeSession` for bounded producer storage and serialized transport processing. `TryTake(false)` consumes only ready PCM; return borrowed blocks exactly once with `Release`. Never read providers in a physical audio callback. Offline preparation may wait on its caller; a decoding worker must not wait for another decoding job. Inline sessions are reserved for nonblocking graph generation, with graph leaves prepared outside live processing.

Mark synchronous graph/PCM work with `Playback.ProcessingScope` and reject controls/disposal before modifying lifetime state. Backend engine overrides should call `ValidatePlaybackDisposal` before starting teardown. Call `BeginPlayback` for each new source playback, `RaisePlaybackEnded` after the audible queue drains, and `RaisePlaybackTerminated` for explicit stop/failure.

`Clip` supplies independent cursors over copied immutable samples. Preserve input channels unless conversion is explicit. A backend without spatial rendering should retain the default spatial-creation rejection.

For replacement-device transfers, explicitly opt in through CanTransferTo/RebindDevice. Core validates every member before moving ownership; context-bound native sources reject transfer and should reopen their endpoint instead.

## Optional features and effects

Feature interfaces describe behavior rather than native tokens. Register only usable implementations, on their actual owner. Use capability flags for optional subfeatures. Reject unsupported requests before native mutation rather than registering placeholders that ignore controls.

Define backend-specific immutable records deriving from `Effects.Settings`. Validate their parameters and document units/ranges. Implement `IEffects` and `Slot` for those records without editing core. Dispatch known settings types explicitly for NativeAOT compatibility.

Send routing is a separate `IEffectSends` interface. A backend may expose effects, advertise zero sends and reject chaining. Do not imitate EFX topology if the backend has another model. OpenAL uses explicit bindings for supported settings; it does not offer raw extension handles or runtime registration of arbitrary native effect bindings.

Slots belong to one device. Reject foreign routes, disposed resources and cycles. Allocate a replacement effect before changing an existing snapshot. Detach links before deleting native resources.

For an optimized immutable clip path, implement `IPreparedClips` and device-owned `PreparedClip`. Retain an independent upload reference for every created source, expose actual processing restrictions through `Source.SupportsSoftwareProcessing`, and reject unavailable software operations. `PlaybackPool` discovers this feature automatically; backends without it use software clips. Reuse native resources rather than public source objects, and reset every native property before reuse.

## Threading and verification

Document serialization, callback threads, borrowed-buffer lifetime and disposal blocking as part of the interface. Copy native callback data and deliver user notifications outside native/transport locks. Keep callbacks/userdata rooted until native delivery is unregistered safely.

A voice failure must remain observable without poisoning context cleanup. Test through the same source/device/effect interface used by callers; keep any fault-injection seam narrow and backend-internal.

The contract fixture verifies transport consumption, provider exclusivity/release, staged buffers and a custom effect. OpenAL integration tests use real precompiled offline rendering. Neither suite opens physical hardware or compiles native libraries.
