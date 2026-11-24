using System.Runtime.InteropServices;
using SoundFlow.Backends.MiniAudio.Enums;

namespace SoundFlow.Backends.MiniAudio.Interop;

/// <summary>ABI layout for the wrapper's sf_AAudioConfig record; field order, alignment and one-byte booleans must match Native/miniaudio-backend/library.h.</summary>
[StructLayout(LayoutKind.Sequential, CharSet = CharSet.Ansi)]
internal struct AAudioConfiguration
{
    public AAudioUsage Usage;
    public AAudioContentType ContentType;
    public AAudioInputPreset InputPreset;
    public AAudioAllowedCapturePolicy AllowedCapturePolicy;
}
