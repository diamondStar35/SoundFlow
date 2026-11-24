using System.Runtime.InteropServices;
using SoundFlow.Backends.MiniAudio.Enums;
using SoundFlow.Enums;

namespace SoundFlow.Backends.MiniAudio.Interop;

/// <summary>ABI layout for the wrapper's sf_DeviceSubConfig record; field order, alignment and one-byte booleans must match Native/miniaudio-backend/library.h.</summary>
[StructLayout(LayoutKind.Sequential, CharSet = CharSet.Ansi)]
internal struct EndpointConfiguration
{
    public SampleFormat Format;
    public uint Channels;
    public nint pDeviceID;
    public ShareMode ShareMode;
}
