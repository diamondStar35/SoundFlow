namespace SoundFlow.Interfaces;

/// <summary>Optional decoder contract for cooperative cancellation of a serialized decode operation.</summary>
public interface ICancellableDecoder : ISoundDecoder
{
    /// <summary>Decodes into borrowed F32 storage. Cancellation may make the decoder unusable until recreated.</summary>
    int Decode(Span<float> samples, CancellationToken cancellationToken);
}
