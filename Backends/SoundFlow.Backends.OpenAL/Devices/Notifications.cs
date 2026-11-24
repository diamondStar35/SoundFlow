using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using SoundFlow.Features;

namespace SoundFlow.Backends.OpenAL.Devices;

internal sealed unsafe partial class Context : INotifications
{
    private GCHandle _eventHandle;
    private string? _lastFailure;
    public event EventHandler<BackendNotification>? Notification;
    public long DroppedNotifications => _engine.DroppedNotifications;

    internal void Publish(NotificationKind kind, string message)
    {
        var notification = new BackendNotification { Kind = kind, Message = message, Device = Owner };
        _engine.Dispatch(() =>
        {
            if (_disposed || Owner.IsDisposed) return;
            Notification?.Invoke(Owner, notification);
            _engine.Publish(notification);
        });
    }

    internal void PostCompletion(Action callback) => _engine.Dispatch(callback, true);

    private void ProcessingFailed(Exception error)
    {
        _lastFailure = error.ToString();
        Publish(NotificationKind.ProcessingFailure, error.Message);
    }

    private void InitializeNotifications()
    {
        if (!HasExtension("AL_SOFT_events") || Al.alEventCallbackSOFT == null || Al.alEventControlSOFT == null) return;
        _eventHandle = GCHandle.Alloc(this);
        Al.alEventCallbackSOFT(&NativeNotification, GCHandle.ToIntPtr(_eventHandle));
        int* events = stackalloc int[2] { 0x19A5, 0x19A6 };
        Al.alEventControlSOFT(2, events, 1);
        Al.CheckAl("Subscribe to native context events");
    }

    private void RemoveNotifications()
    {
        if (!_eventHandle.IsAllocated) return;
        // OpenAL synchronizes callback replacement. Release managed userdata only after native delivery stops.
        Al.alEventCallbackSOFT(null, 0);
        _eventHandle.Free();
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void NativeNotification(int type, uint source, uint parameter, int length, byte* message, nint userdata)
    {
        try
        {
            var context = (Context?)GCHandle.FromIntPtr(userdata).Target;
            if (context == null) return;
            var text = Marshal.PtrToStringUTF8((nint)message, Math.Max(0, length)) ?? "";
            context.Publish(type == 0x19A6 ? NotificationKind.Disconnected : NotificationKind.SourceStateChanged, text);
        }
        catch { /* Managed exceptions must never unwind through a native callback. */ }
    }
}
