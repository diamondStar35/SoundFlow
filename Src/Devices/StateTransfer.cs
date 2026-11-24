using SoundFlow.Abstracts;
using SoundFlow.Devices;

namespace SoundFlow.Devices;

/// <summary>
/// Backend switching helpers for software graph snapshots and processing subscriptions. These helpers do not transfer source leases or dispose either device.
/// </summary>
/// <remarks>Stop rendering and detach graph children from the old mixer before restoring them. TransferSourcesTo handles source ownership separately.</remarks>
public static class StateTransfer
{
    /// <summary>
    /// Preserves the state of a playback device by extracting its sound components.
    /// </summary>
    /// <param name="device">The playback device to preserve.</param>
    /// <returns>A read-only collection of sound components.</returns>
    public static IReadOnlyCollection<SoundComponent> PreservePlaybackState(PlaybackDevice device)
    {
        // Return a copy of the list of components from the master mixer.
        return device.MasterMixer.Components;
    }

    /// <summary>
    /// Restores the state to a new playback device by re-adding the preserved components.
    /// </summary>
    /// <param name="device">The new playback device.</param>
    /// <param name="components">The preserved sound components.</param>
    public static void RestorePlaybackState(PlaybackDevice device, IReadOnlyCollection<SoundComponent> components)
    {
        foreach (var component in components)
        {
            device.MasterMixer.AddComponent(component);
        }
    }

    /// <summary>
    /// Preserves the state of any audio device by extracting its event subscribers.
    /// </summary>
    /// <param name="device">The device to preserve.</param>
    /// <returns>An array of delegates subscribed to the device's processing event.</returns>
    public static Delegate[] PreserveEventSubscribers(Device device)
    {
        return [.. device.GetEventSubscribers(), .. device.GetBufferSubscribers()];
    }

    /// <summary>
    /// Restores the state to a new audio device by re-adding the preserved event subscribers.
    /// </summary>
    /// <param name="device">The new device.</param>
    /// <param name="subscribers">The preserved event subscribers.</param>
    public static void RestoreEventSubscribers(Device device, Delegate[] subscribers)
    {
        foreach (var subscriber in subscribers)
        {
            if (subscriber is Features.BufferCallback bufferCallback) device.AudioBufferProcessed += bufferCallback;
            if (subscriber is AudioProcessCallback callback)
            {
                device.OnAudioProcessed += callback;
            }
        }
    }
}
