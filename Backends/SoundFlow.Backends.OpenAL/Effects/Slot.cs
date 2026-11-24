using Context = SoundFlow.Backends.OpenAL.Devices.Context;
using CoreSlot = SoundFlow.Effects.Slot;
using PlaybackDevice = SoundFlow.Devices.PlaybackDevice;
using Settings = SoundFlow.Effects.Settings;
using SoundFlow.Devices;
using SoundFlow.Effects;

namespace SoundFlow.Backends.OpenAL.Effects;

/// <summary>Owns the native effect snapshot and auxiliary slot as one resource, simplifying updates and cleanup.</summary>
internal sealed unsafe class Slot : CoreSlot
{
    internal readonly Context Owner;
    internal uint Id;
    private uint _effect;
    private Settings _settings;
    private Slot? _target;
    private float _gain = 1;
    public override PlaybackDevice Device => Owner.Owner;
    public override bool IsDisposed => _disposed;
    private bool _disposed;
    public override Settings Settings => _settings;
    public override CoreSlot? Target => _target;

    internal Slot(Context owner, Settings settings)
    {
        Owner = owner; _settings = settings;
        var al = owner.Al;
        uint id;
        al.alGenAuxiliaryEffectSlots(1, &id); Id = id;
        try
        {
            al.CheckAl("Allocate auxiliary effect slot");
            UpdateCore(settings);
        }
        catch { if (Id != 0) al.alDeleteAuxiliaryEffectSlots(1, &id); throw; }
    }
    public override float Gain
    {
        get => _gain;
        set
        {
            if (!float.IsFinite(value) || value < 0 || value > 1) throw new ArgumentOutOfRangeException(nameof(value));
            Control(() => { Owner.Al.alAuxiliaryEffectSlotf(Id, 2, value); Owner.Al.CheckAl("Set slot gain"); _gain = value; });
        }
    }
    public override void Update(Settings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        settings.Validate();
        Control(() => UpdateCore(settings));
    }
    private void UpdateCore(Settings settings)
    {
        if (!Owner.SupportedEffects.Contains(settings.GetType())) throw new NotSupportedException("This effect is unavailable on the current device.");
        var al = Owner.Al;
        uint effect; al.alGenEffects(1, &effect); al.CheckAl("Allocate effect snapshot");
        try
        {
            SettingsBinding.Apply(al, effect, settings);
            al.alAuxiliaryEffectSloti(Id, 1, (int)effect);
            al.CheckAl("Attach effect snapshot to slot");
        }
        catch { al.alDeleteEffects(1, &effect); throw; }
        if (_effect != 0) { var old = _effect; al.alDeleteEffects(1, &old); }
        _effect = effect; _settings = settings;
    }
    private void Control(Action action)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        Owner.Worker.Invoke(action);
    }
    public override void SetTarget(CoreSlot? target) => Control(() =>
    {
        if (!Owner.SlotChaining) throw new NotSupportedException("AL_SOFT_effect_target is unavailable on this device.");
        if (target is not null && (target is not Slot native || native.Owner != Owner || native._disposed))
            throw new ArgumentException("Target must be a live slot on the same device.", nameof(target));
        for (var next = (Slot?)target; next != null; next = next._target)
            if (next == this) throw new ArgumentException("Effect-slot routing cannot contain a cycle.", nameof(target));
        Owner.Al.alAuxiliaryEffectSloti(Id, 0x199C, (int)(((Slot?)target)?.Id ?? 0));
        Owner.Al.CheckAl("Route effect-slot output");
        _target = (Slot?)target;
    });
    public override void Dispose()
    {
        if (_disposed) return;
        Owner.Worker.Invoke(() =>
        {
            foreach (var source in Owner.Sources) source.DetachSlot(this);
            foreach (var slot in Owner.Slots)
                if (slot._target == this) slot.SetTarget(null);
            if (_target != null) SetTarget(null);
            var id = Id; Owner.Al.alDeleteAuxiliaryEffectSlots(1, &id);
            var effect = _effect; Owner.Al.alDeleteEffects(1, &effect);
            Owner.Al.CheckAl("Release effect slot");
            _disposed = true;
            Owner.Slots.Remove(this);
        });
    }
}
