namespace SoundFlow.Providers;

/// <summary>
/// Optional cooperative read contract. Cancellation is terminal for an in-flight native decoder;
/// implementations must not throw across an unmanaged callback or dispose input while a read still owns it.
/// </summary>
public interface ICancellableDataProvider : IDataProvider
{
    /// <summary>Reads complete interleaved frames, observing cancellation during blocking I/O when the input supports it.</summary>
    int ReadSamples(Span<float> buffer, CancellationToken cancellationToken);
}
