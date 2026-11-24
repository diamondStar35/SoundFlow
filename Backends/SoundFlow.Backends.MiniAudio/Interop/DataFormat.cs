using System.Runtime.InteropServices;
using SoundFlow.Enums;
namespace SoundFlow.Backends.MiniAudio.Interop;

/// <summary>ABI layout for the wrapper's native_data_format record; field order, alignment and one-byte booleans must match Native/miniaudio-backend/library.h.</summary>
[StructLayout(LayoutKind.Sequential)]
internal struct DataFormat
{
    public SampleFormat Format;
    public uint Channels;
    public uint SampleRate;
    // Required for the native array stride even though public format metadata does not expose backend flags.
    public uint Flags;
}
