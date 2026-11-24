using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using SoundFlow.Features;

namespace SoundFlow.Backends.OpenAL.Interop;

/// <summary>Multiplexes OpenAL's one library-wide system callback without letting one engine replace another's subscription.</summary>
internal static unsafe class SystemEvents
{
    private static readonly object Gate = new();
    private static readonly Dictionary<nint, Entry> Entries = new();

    internal static IDisposable? Subscribe(Functions functions, Action<BackendNotification> subscriber)
    {
        if (functions.alcEventCallbackSOFT == null || functions.alcEventControlSOFT == null || functions.alcEventIsSupportedSOFT == null ||
            !functions.HasAlcExtension(0, "ALC_SOFT_system_events")) return null;
        lock (Gate)
        {
            if (!Entries.TryGetValue(functions.LibraryHandle, out var entry))
            {
                entry = new Entry(functions);
                Entries.Add(functions.LibraryHandle, entry);
            }
            entry.Add(subscriber);
            return new Subscription(functions.LibraryHandle, subscriber);
        }
    }

    private sealed class Entry
    {
        internal readonly Functions Functions;
        internal Action<BackendNotification>[] Subscribers = [];
        private GCHandle _handle;

        internal Entry(Functions functions)
        {
            Functions = functions;
            _handle = GCHandle.Alloc(this);
            functions.alcEventCallbackSOFT(&Callback, GCHandle.ToIntPtr(_handle));
            int* events = stackalloc int[3]; var count = 0;
            for (var type = 0x19D6; type <= 0x19D8; type++)
                if (functions.alcEventIsSupportedSOFT(type, 0x19D4) == 0x19D9 || functions.alcEventIsSupportedSOFT(type, 0x19D5) == 0x19D9) events[count++] = type;
            if (count != 0) functions.alcEventControlSOFT(count, events, 1);
        }

        internal void Add(Action<BackendNotification> subscriber) => Volatile.Write(ref Subscribers, [.. Subscribers, subscriber]);
        internal void Remove(Action<BackendNotification> subscriber) => Volatile.Write(ref Subscribers, Subscribers.Where(item => item != subscriber).ToArray());
        internal void Close()
        {
            Functions.alcEventCallbackSOFT(null, 0);
            _handle.Free();
        }
    }

    private sealed class Subscription(nint library, Action<BackendNotification> subscriber) : IDisposable
    {
        private int _disposed;
        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
            lock (Gate)
            {
                var entry = Entries[library]; entry.Remove(subscriber);
                if (entry.Subscribers.Length != 0) return;
                entry.Close(); Entries.Remove(library);
            }
        }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void Callback(int type, int deviceType, nint device, int length, byte* message, nint userdata)
    {
        try
        {
            var entry = (Entry?)GCHandle.FromIntPtr(userdata).Target;
            if (entry == null) return;
            var notification = new BackendNotification
            {
                Kind = type switch { 0x19D6 => NotificationKind.DefaultDeviceChanged, 0x19D7 => NotificationKind.DeviceAdded, _ => NotificationKind.DeviceRemoved },
                Message = Marshal.PtrToStringUTF8((nint)message, Math.Max(0, length)) ?? ""
            };
            // Immutable subscriber snapshots avoid taking Gate inside callbacks; unregistration may wait for this function.
            foreach (var subscriber in Volatile.Read(ref entry.Subscribers)) subscriber(notification);
        }
        catch { /* No managed exception may cross the native ABI. */ }
    }
}
