using System.Runtime.InteropServices;

namespace SoundFlow.Backends.MiniAudio.Interop;

/// <summary>ABI layout for the wrapper's sf_device_info record; field order, alignment and one-byte booleans must match Native/miniaudio-backend/library.h.</summary>
[StructLayout(LayoutKind.Sequential, CharSet = CharSet.Ansi)]
internal struct DeviceInfo
{
    // Borrowed from the context enumeration, not freed by sf_free_device_infos. Managed IDs resolve it only for this enumeration session.
    public IntPtr Id;

    [MarshalAs(UnmanagedType.ByValArray, SizeConst = 256)]
    public byte[] NameBytes;

    [MarshalAs(UnmanagedType.U1)]
    public bool IsDefault;

    public uint NativeDataFormatCount;

    public IntPtr NativeDataFormats;
}
