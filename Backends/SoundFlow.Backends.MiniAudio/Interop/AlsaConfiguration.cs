using System.Runtime.InteropServices;

namespace SoundFlow.Backends.MiniAudio.Interop;

/// <summary>ABI layout for the wrapper's sf_AlsaConfig record; field order, alignment and one-byte booleans must match Native/miniaudio-backend/library.h.</summary>
[StructLayout(LayoutKind.Sequential, CharSet = CharSet.Ansi)]
internal struct AlsaConfiguration
{
    [MarshalAs(UnmanagedType.U4)] public uint NoMMap;
    [MarshalAs(UnmanagedType.U4)] public uint NoAutoFormat;
    [MarshalAs(UnmanagedType.U4)] public uint NoAutoChannels;
    [MarshalAs(UnmanagedType.U4)] public uint NoAutoResample;
}
