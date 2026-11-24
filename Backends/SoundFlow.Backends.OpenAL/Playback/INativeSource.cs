namespace SoundFlow.Backends.OpenAL.Playback;

/// <summary>Associates a managed source adapter with its voice without exposing AL identifiers to callers.</summary>
internal interface INativeSource
{
    Voice Voice { get; }
}
