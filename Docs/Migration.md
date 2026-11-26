# Migrating the engine and backend interfaces

This is a source-breaking revision with no compatibility aliases. Update references and type names together. Package identities remain unchanged.

| Previous | Current |
| --- | --- |
| SoundFlow.Abstracts.AudioEngine | SoundFlow.Engine |
| SoundFlow.Abstracts.Devices | SoundFlow.Devices |
| AudioDevice, AudioPlaybackDevice, AudioCaptureDevice, DeviceConfig | Device, PlaybackDevice, CaptureDevice, DeviceConfiguration |
| SoundFlow.Structs.DeviceInfo | SoundFlow.Devices.DeviceInfo |
| OpenAlEngine, OpenAlDeviceConfig, OpenAlException | SoundFlow.Backends.OpenAL.Engine, DeviceConfiguration, BackendException |
| MiniAudioEngine, MiniAudioDeviceConfig | SoundFlow.Backends.MiniAudio.Engine, DeviceConfiguration |
| MiniAudioBackend, MiniAudioResult | SoundFlow.Backends.MiniAudio.Enums.Backend, Result |
| AudioSource, AudioClip, internal PcmTransport | SoundFlow.Playback.Source, Clip, public Transport |
| AudioEffectSettings, AudioEffectSlot, IAudioEffects, IAudioEffectSends | SoundFlow.Effects.Settings, Slot, IEffects, IEffectSends |
| AudioFilterSettings, AudioFilterKind | SoundFlow.Effects.FilterSettings, FilterKind |
| ISpatialAudio, AudioListener, AudioDistanceModel, SpatialSourceSettings | SoundFlow.Spatial.IScene, Listener, DistanceModel, SourceSettings |
| AudioProcessingStage, AudioBufferInfo, AudioBufferCallback | SoundFlow.Features.ProcessingStage, BufferInfo, BufferCallback |
| IAudioDiagnostics, AudioDiagnostics, AudioRenderDevice | IDiagnostics, DiagnosticsSnapshot, SoundFlow.Devices.RenderDevice |
| ISoundDataProvider, ISoundPlayer | SoundFlow.Providers.IDataProvider, SoundFlow.Playback.IPlayer |
| Provider ReadBytes(Span<float>) | ReadSamples(Span<float>) |

Use namespace aliases where names overlap: `using OpenAL = SoundFlow.Backends.OpenAL;` followed by `new OpenAL.Engine()`.

## Explicit spatial intent

CreateSource now means nonspatial playback for every layout. Replace positional mono creation with CreateSpatialSource and assign Settings directly. The previous ISpatialSource feature query is removed. Input channels are preserved; request mono decoding explicitly for a point source.

A graph fed into a spatial source is one acoustic object. Children needing independent locations must use separate sources.

## Timing and callbacks

Native completion is dispatched after queues drain. Await the event instead of expecting it synchronously during Render. Buffer callbacks remain synchronous and borrowed: do not issue native controls or dispose resources inside them. Queue those controls on your application thread.

Source time is content time; device timestamps are monotonic nanoseconds. Check timing/scheduling features and use the target device's clock. Start physical output before group/scheduled commands. Stop cancels pending playback.

## Packaging and applications

Add an explicit MiniAudio reference where previously only core was referenced. OpenAL includes/registers FFmpeg without MiniAudio. Migrate hard-coded copy paths out of the old core backend directory.

Source target files are flat but packaged at standard buildTransitive paths. Consumers should use native runtime assets rather than manually importing legacy package-root targets.

Galactic Voyage consumer migration remains outside this repository change. Its old interface references and copy rules need migration before rebuilding. Original fork MIDI deletions remain intact.

## Shared decoding and prepared clip phase

Existing source/graph creation entry points remain. Set the new engine `PlaybackConfiguration` before creating devices or players. Software player input is now buffered: providers run on shared workers, playback time measures consumed content, and natural completion is dispatched after buffered PCM drains. Code manually driving `SoundComponent.Process` offline must prepare buffered leaves with `PrepareForRender` before each bounded chunk. Live audio callbacks must never call preparation.

Use optional cancellation contracts for custom blocking inputs. Do not assume zero means EOF for a live provider, or that an arbitrary synchronous stream can be forcibly canceled. PCM callbacks reject disposal and blocking playback controls before state changes.

Prepared clips and clip pools are explicit additions. Ordinary clip sources retain software processing; native prepared sources reject software modifiers, PCM taps, reverse/time-stretched transport and authored loop subranges. Pool leases automatically release on termination and must not be reused for a later playback. Native effects and diagnostics are discovered through registered features and effect-type support, not extension-name strings alone.
