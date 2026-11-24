using Functions = SoundFlow.Backends.OpenAL.Interop.Functions;
namespace SoundFlow.Backends.OpenAL.Devices;

/// <summary>Narrow device-lifecycle seam for fault injection without opening hardware in tests.</summary>
internal interface IDeviceLifecycle
{
    nint Open(bool offline, byte[]? name);
    nint CreateContext(nint device, int[] attributes);
    bool Reopen(nint device, byte[]? name, int[] attributes);
    void DestroyContext(nint context);
    void Close(nint device);
}

/// <summary>Production ALC lifecycle adapter. All returned objects belong to the loaded implementation.</summary>
internal sealed unsafe class DeviceLifecycle(Functions native) : IDeviceLifecycle
{
    public nint Open(bool offline, byte[]? name)
    {
        if (offline && native.alcLoopbackOpenDeviceSOFT == null) throw new NotSupportedException("ALC_SOFT_loopback is unavailable.");
        fixed (byte* pointer = name) return offline ? native.alcLoopbackOpenDeviceSOFT(null) : native.alcOpenDevice(pointer);
    }
    public nint CreateContext(nint device, int[] attributes)
    {
        fixed (int* pointer = attributes) return native.alcCreateContext(device, pointer);
    }
    public bool Reopen(nint device, byte[]? name, int[] attributes)
    {
        fixed (byte* n = name)
        fixed (int* a = attributes) return native.alcReopenDeviceSOFT(device, n, a) != 0;
    }
    public void DestroyContext(nint context) => native.alcDestroyContext(context);
    public void Close(nint device) => native.alcCloseDevice(device);
}
