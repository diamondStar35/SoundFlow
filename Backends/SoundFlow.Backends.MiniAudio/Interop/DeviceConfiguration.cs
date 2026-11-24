using System.Runtime.InteropServices;

namespace SoundFlow.Backends.MiniAudio.Interop;

/// <summary>ABI layout for the wrapper's sf_DeviceConfig record; field order, alignment and one-byte booleans must match Native/miniaudio-backend/library.h.</summary>
[StructLayout(LayoutKind.Sequential, CharSet = CharSet.Ansi)]
internal struct DeviceConfiguration
{
    public uint PeriodSizeInFrames;
    public uint PeriodSizeInMilliseconds;
    public uint Periods;
    [MarshalAs(UnmanagedType.U1)] public bool NoPreSilencedOutputBuffer;
    [MarshalAs(UnmanagedType.U1)] public bool NoClip;
    [MarshalAs(UnmanagedType.U1)] public bool NoDisableDenormals;
    [MarshalAs(UnmanagedType.U1)] public bool NoFixedSizedCallback;

    public nint Playback;
    public nint Capture;
    public nint Wasapi;
    public nint CoreAudio;
    public nint Alsa;
    public nint Pulse;
    public nint OpenSL;
    public nint AAudio;
}
