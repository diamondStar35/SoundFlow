using System.Runtime.InteropServices;
using SoundFlow.Codecs.FFMpeg.Native;

namespace SoundFlow.Codecs.FFMpeg;

/// <summary>Queries the components registered in the loaded native library.</summary>
public sealed class Capabilities
{
    /// <summary>Actual FFmpeg release, not the managed package version.</summary>
    public string Version { get; }
    /// <summary>Registered audio decoders.</summary>
    public IReadOnlyList<string> Decoders { get; }
    /// <summary>Registered audio encoders.</summary>
    public IReadOnlyList<string> Encoders { get; }
    /// <summary>Input containers and playlist formats.</summary>
    public IReadOnlyList<string> Demuxers { get; }
    /// <summary>Output containers.</summary>
    public IReadOnlyList<string> Muxers { get; }
    /// <summary>Protocols available for input.</summary>
    public IReadOnlyList<string> InputProtocols { get; }
    /// <summary>Protocols available to native output.</summary>
    public IReadOnlyList<string> OutputProtocols { get; }
    /// <summary>Registered video/subtitle codec count; zero for the distributed audio profile.</summary>
    public int NonAudioCodecCount { get; }

    /// <summary>Loads the runtime if needed and reads its capability tables without reflection.</summary>
    public Capabilities()
    {
        Version = Marshal.PtrToStringUTF8(FFmpeg.Version()) ?? "unknown";
        Decoders = Read(0);
        Encoders = Read(1);
        Demuxers = Read(2);
        Muxers = Read(3);
        InputProtocols = Read(4);
        OutputProtocols = Read(5);
        NonAudioCodecCount = FFmpeg.NonAudioCodecCount();
    }

    private static IReadOnlyList<string> Read(int category)
    {
        var values = new SortedSet<string>(StringComparer.Ordinal);
        for (var index = 0; ; index++)
        {
            var name = Marshal.PtrToStringUTF8(FFmpeg.FeatureName(category, index));
            if (name == null)
                break;
            foreach (var alias in name.Split(','))
                values.Add(alias);
        }
        return Array.AsReadOnly(values.ToArray());
    }
}
