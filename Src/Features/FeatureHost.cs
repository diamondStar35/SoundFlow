using System.Diagnostics.CodeAnalysis;

namespace SoundFlow.Features;

/// <summary>Exposes optional, explicitly registered backend features without reflection or backend casts.</summary>
/// <remarks>Backends register features during construction. Each feature is owned by this host and must not outlive it.</remarks>
public abstract class FeatureHost
{
    private readonly Dictionary<Type, object> _features = new();

    /// <summary>Returns an available feature, or false when the host does not implement it.</summary>
    public bool TryGetFeature<T>([NotNullWhen(true)] out T? feature) where T : class
    {
        if (_features.TryGetValue(typeof(T), out var value)) { feature = (T)value; return true; }
        feature = null;
        return false;
    }

    /// <summary>Registers a feature under its public interface. Call only while constructing the host.</summary>
    protected void RegisterFeature<T>(T feature) where T : class => _features.Add(typeof(T), feature);
}
