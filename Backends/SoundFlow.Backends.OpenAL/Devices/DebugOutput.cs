using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using SoundFlow.Backends.OpenAL.Features;
using CoreSource = SoundFlow.Playback.Source;
using CoreSlot = SoundFlow.Effects.Slot;

namespace SoundFlow.Backends.OpenAL.Devices;

internal sealed unsafe partial class Context : IDebugOutput
{
    private GCHandle _debugHandle;
    private DebugSeverity _debugSeverity;
    internal bool HasDebug => HasExtension("AL_EXT_debug") && Al.alDebugMessageCallbackEXT != null &&
        Al.alDebugMessageControlEXT != null && Al.alDebugMessageInsertEXT != null && Al.alObjectLabelEXT != null;
    public bool Enabled => _debugHandle.IsAllocated;
    public event EventHandler<DebugMessage>? Message;
    public DebugSeverity MinimumSeverity
    {
        get => _debugSeverity;
        set => Worker.Invoke(() => ApplyDebugFilter(value));
    }
    private void ApplyDebugFilter(DebugSeverity value)
    {
        if (!Enum.IsDefined(value)) throw new ArgumentOutOfRangeException(nameof(value));
        for (var i = 0; i <= 3; i++) Al.alDebugMessageControlEXT(2, 2, 0x19C3 + i, 0, null, (byte)(i <= (int)value ? 1 : 0));
        Al.CheckAl("Filter native debug messages"); _debugSeverity = value;
    }
    private void InitializeDebug()
    {
        if (!Config.DebugMessages) return;
        if (!HasDebug) throw new NotSupportedException("This context cannot provide native debug messages.");
        _debugHandle = GCHandle.Alloc(this);
        Al.alDebugMessageCallbackEXT(&OnDebugMessage, GCHandle.ToIntPtr(_debugHandle));
        Al.alEnable(0x19B2);
        ApplyDebugFilter(Config.DebugMinimumSeverity);
        Al.CheckAl("Enable native debug messages");
    }
    private void RemoveDebug()
    {
        if (!_debugHandle.IsAllocated) return;
        Al.alDebugMessageCallbackEXT(null, 0);
        _debugHandle.Free();
    }
    public void Write(string message, DebugSeverity severity = DebugSeverity.Notification)
    {
        ArgumentNullException.ThrowIfNull(message);
        if (!Enum.IsDefined(severity)) throw new ArgumentOutOfRangeException(nameof(severity));
        Worker.Invoke(() =>
        {
            if (!Enabled) throw new InvalidOperationException("Enable DebugMessages when creating the device.");
            var bytes = Encoding.UTF8.GetBytes(message);
            if (bytes.Length >= Al.alGetInteger(0x19C9)) throw new ArgumentOutOfRangeException(nameof(message), "Message exceeds the native debug limit.");
            fixed (byte* text = bytes) Al.alDebugMessageInsertEXT(0x19B8, 0x19BF, 0, 0x19C3 + (int)severity, bytes.Length, text);
            Al.CheckAl("Insert application debug message");
        });
    }
    public void SetLabel(CoreSource source, string label) => Worker.Invoke(() => Label(0x19D0, GetVoice(source).Id, label));
    public void SetLabel(CoreSlot slot, string label) => Worker.Invoke(() =>
    {
        if (slot is not Effects.Slot native || native.Owner != this || native.IsDisposed) throw new ArgumentException("Expected a live slot on this device.", nameof(slot));
        Label(0x19D3, native.Id, label);
    });
    internal void Label(int kind, uint id, string label)
    {
        if (!HasDebug) return;
        ArgumentNullException.ThrowIfNull(label);
        var bytes = Encoding.UTF8.GetBytes(label);
        if (bytes.Length >= Al.alGetInteger(0x19CC)) throw new ArgumentOutOfRangeException(nameof(label), "Label exceeds the native limit.");
        fixed (byte* text = bytes) Al.alObjectLabelEXT(kind, id, bytes.Length, text);
        Al.CheckAl("Label native resource");
    }
    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void OnDebugMessage(int source, int type, uint id, int severity, int length, byte* text, nint userdata)
    {
        try
        {
            var context = (Context?)GCHandle.FromIntPtr(userdata).Target;
            if (context == null) return;
            var message = new DebugMessage { Source = source, Type = type, Id = id, Severity = (DebugSeverity)(severity - 0x19C3),
                Text = Marshal.PtrToStringUTF8((nint)text, Math.Max(0, length)) ?? "" };
            context._engine.Dispatch(() => { if (!context._disposed) context.Message?.Invoke(context.Owner, message); });
        }
        catch { /* Debug callbacks must never unwind into the native renderer. */ }
    }
}
