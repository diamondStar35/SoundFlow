using SoundFlow.Structs;
using Native = global::SteamAudio.Interop;

namespace SoundFlow.SteamAudio.Ambisonics;

internal static class Format
{
    internal static AmbisonicsFormat Require(AudioFormat input, bool native = true)
    {
        input.ValidateChannelLayout();
        var value = input.Ambisonics ?? throw new ArgumentException("Explicit Ambisonics input metadata is required.");
        if (native && value.Convention != AmbisonicsConvention.N3D)
            throw new ArgumentException("Convert Ambisonics to ACN/N3D before native effects.");
        return value;
    }
    internal static AudioFormat Encode(AudioFormat input, int order)
    {
        if (input.Channels != 1 || input.Ambisonics != null)
            throw new ArgumentException("Ambisonics encoding requires mono speaker PCM.");
        var value = new AmbisonicsFormat(order);
        value.Validate();
        return input with
        {
            Channels = value.Channels,
            Layout = ChannelLayout.Ambisonics,
            Ambisonics = value
        };
    }
    internal static AudioFormat Convert(AudioFormat input, AmbisonicsConvention convention)
    {
        var value = Require(input, false) with
        {
            Convention = convention
        };
        value.Validate();
        return input with
        {
            Ambisonics = value
        };
    }
    internal static Native.AmbisonicsType Convention(AmbisonicsConvention convention) => convention switch
    {
        AmbisonicsConvention.N3D => Native.AmbisonicsType.N3D,
        AmbisonicsConvention.SN3D => Native.AmbisonicsType.Sn3d,
        AmbisonicsConvention.FuMa => Native.AmbisonicsType.Fuma,
        _ => throw new ArgumentOutOfRangeException(nameof(convention))
    };
}
