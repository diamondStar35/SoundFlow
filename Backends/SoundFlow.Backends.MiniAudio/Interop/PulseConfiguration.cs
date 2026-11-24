using System.Runtime.InteropServices;

namespace SoundFlow.Backends.MiniAudio.Interop;

/// <summary>ABI layout for the wrapper's sf_PulseConfig record; field order, alignment and one-byte booleans must match Native/miniaudio-backend/library.h.</summary>
[StructLayout(LayoutKind.Sequential, CharSet = CharSet.Ansi)]
internal struct PulseConfiguration
{
    public nint pStreamNamePlayback;
    public nint pStreamNameCapture;
}
