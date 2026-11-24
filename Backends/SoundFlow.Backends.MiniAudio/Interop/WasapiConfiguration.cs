using System.Runtime.InteropServices;
using SoundFlow.Backends.MiniAudio.Enums;

namespace SoundFlow.Backends.MiniAudio.Interop;

/// <summary>ABI layout for the wrapper's sf_WasapiConfig record; field order, alignment and one-byte booleans must match Native/miniaudio-backend/library.h.</summary>
[StructLayout(LayoutKind.Sequential, CharSet = CharSet.Ansi)]
internal struct WasapiConfiguration
{
    public WasapiUsage Usage;
    [MarshalAs(UnmanagedType.U1)] public bool NoAutoConvertSRC;
    [MarshalAs(UnmanagedType.U1)] public bool NoDefaultQualitySRC;
    [MarshalAs(UnmanagedType.U1)] public bool NoAutoStreamRouting;
    [MarshalAs(UnmanagedType.U1)] public bool NoHardwareOffloading;
}
