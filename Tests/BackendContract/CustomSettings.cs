using PlaybackDevice = SoundFlow.Devices.PlaybackDevice;
using SoundFlow.Effects;

namespace BackendContract;

/// <summary>A backend-defined effect unrelated to EFX. Core does not need to know this record exists.</summary>
public sealed record CustomSettings(float Amount = .5f) : Settings
{
    public override void Validate() => Range(Amount, 0, 1, nameof(Amount));
}

internal sealed class CustomSlot(Device owner, CustomSettings settings) : Slot
{
    private CustomSettings _settings = settings;
    public override PlaybackDevice Device => owner;
    public override bool IsDisposed => _disposed;
    private bool _disposed;
    public override Settings Settings => _settings;
    public override float Gain { get; set; } = 1;
    public override Slot? Target => null;
    public override void Update(Settings settings)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (settings is not CustomSettings custom) throw new NotSupportedException();
        custom.Validate(); _settings = custom;
    }
    public override void SetTarget(Slot? target) => throw new NotSupportedException();
    public override void Dispose() => _disposed = true;
}
