using SoundFlow.Backends.OpenAL.Features;
using SoundFlow.Spatial;

namespace SoundFlow.Backends.OpenAL.Devices;

internal sealed unsafe partial class Context
{
    private HashSet<string> _extensionSet = new(StringComparer.Ordinal);
    internal bool HasExtension(string extension) => _extensionSet.Contains(extension);
    internal bool HasDeferredUpdates => HasExtension("AL_SOFT_deferred_updates") && Al.alDeferUpdatesSOFT != null && Al.alProcessUpdatesSOFT != null;
    internal bool HasTiming => HasExtension("ALC_SOFT_device_clock") && HasExtension("AL_SOFT_source_latency") && Al.alcGetInteger64vSOFT != null && Al.alGetSourcei64vSOFT != null;
    internal bool HasScheduling => HasTiming && HasExtension("AL_SOFT_source_start_delay") && Al.alSourcePlayAtTimeSOFT != null && Al.alSourcePlayAtTimevSOFT != null;
    internal RenderingCapabilities RenderingSupport { get; private set; }
    internal IReadOnlyList<string> Resamplers { get; private set; } = Array.Empty<string>();
    internal int DefaultResampler { get; private set; }
    private int _updateDepth;

    /// <summary>Captures immutable context support once, rather than allocating extension strings during source updates.</summary>
    private void InitializeCapabilities()
    {
        _extensionSet = new HashSet<string>(_extensions, StringComparer.Ordinal);
        if (HasExtension("AL_EXT_source_distance_model")) RenderingSupport |= RenderingCapabilities.DistanceModel;
        if (HasExtension("AL_EXT_SOURCE_RADIUS")) RenderingSupport |= RenderingCapabilities.Radius;
        if (HasExtension("AL_EXT_STEREO_ANGLES")) RenderingSupport |= RenderingCapabilities.StereoAngles;
        if (HasExtension("AL_SOFT_direct_channels")) RenderingSupport |= RenderingCapabilities.DirectChannels;
        if (HasExtension("AL_SOFT_direct_channels_remix")) RenderingSupport |= RenderingCapabilities.ChannelRemix;
        if (HasExtension("ALC_EXT_EFX")) RenderingSupport |= RenderingCapabilities.AutomaticFilterGain;
        if (HasExtension("AL_SOFT_source_resampler") && Al.alGetStringiSOFT != null)
        {
            RenderingSupport |= RenderingCapabilities.Resampler;
            var names = new string[Al.alGetInteger(0x1210)];
            for (var i = 0; i < names.Length; i++) names[i] = Interop.Functions.Text(Al.alGetStringiSOFT(0x1213, i));
            Resamplers = Array.AsReadOnly(names);
            DefaultResampler = Al.alGetInteger(0x1211);
        }
    }

    internal void EnsureOutsideBatch()
    {
        if (_updateDepth > 0 && Worker.IsCurrentThread) throw new InvalidOperationException("Rendering, switching and resource disposal cannot run inside an update batch.");
    }

    internal static int DistanceToken(DistanceModel value) => value == DistanceModel.None ? 0 : 0xD000 + (int)value;

    /// <summary>Nested controls share one native update boundary. Always release deferral, including when user code throws.</summary>
    internal void ApplyNativeUpdates(Action action)
    {
        var outer = _updateDepth++ == 0;
        try
        {
            if (outer && HasDeferredUpdates) Al.alDeferUpdatesSOFT();
            action();
        }
        finally
        {
            _updateDepth--;
            if (outer && HasDeferredUpdates) Al.alProcessUpdatesSOFT();
        }
    }
}
