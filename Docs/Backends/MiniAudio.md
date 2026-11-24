# MiniAudio backend

Reference `SoundFlow.Backends.MiniAudio` explicitly and construct `SoundFlow.Backends.MiniAudio.Engine`. The core no longer loads or packages MiniAudio. Its built-in codec factory is registered by default; register FFmpeg explicitly when needed.

Playback, capture, duplex and Windows loopback retain their native implementations. Shared `CreateSource` feeds the software mixer. Differing input channels/rates are converted before modifiers, so configure channel-dependent processors for the output mix format.

Native spatial sources are unsupported. `CreateSpatialSource` throws rather than silently choosing another renderer. Existing software panning/surround graph processors remain available; they do not claim equivalent OpenAL HRTF behavior.

## Configuration and switching

`DeviceConfiguration` exposes periods and platform settings. PeriodSizeInFrames counts frames per channel: 480 stereo frames contain 960 interleaved samples, not 960 frames.

Playback replacement is prepared before stopping/transferring the old device. Owned sources, graph children and processing subscriptions move to it; startup failure rolls them back. Capture replacement is prepared before releasing the old input. Paired switching preserves both processing paths and source ownership. Physical behavior still needs manual testing.

Device IDs are opaque strings resolved through the engine's current enumeration. Re-enumerate before selection; do not persist native pointers or treat array indices as permanent identities.

## Packaging

Normal .NET builds copy precompiled libraries for the selected RID. Standard native runtime paths and a transitive iOS framework target handle package consumers. Source target files sit beside their projects; package metadata places them in NuGet's required buildTransitive directory.

Retained platforms are Windows x64/ARM64, Linux x64/ARM64, macOS x64/ARM64, iOS ARM64 and Android ARM64. Removed architectures are absent from assets and build matrices.

See [architecture](../Architecture.md) for ownership/stages and [migration](../Migration.md) for renamed types.
