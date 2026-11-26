using CoreSlot = SoundFlow.Effects.Slot;
using FilterKind = SoundFlow.Effects.FilterKind;
using FilterSettings = SoundFlow.Effects.FilterSettings;
using Slot = SoundFlow.Backends.OpenAL.Effects.Slot;
using SoundFlow.Devices;
using SoundFlow.Effects;

namespace SoundFlow.Backends.OpenAL.Playback;

internal sealed unsafe partial class Voice
{
    private uint _directFilter;
    private readonly Dictionary<int, (Slot Slot, uint Filter)> _sends = new();
    public void SetDirectFilter(FilterSettings? filter)
    {
        filter?.Validate();
        Control(() =>
        {
            var next = CreateFilter(filter, 1);
            Al.alSourcei(Id, 0x20005, (int)next);
            try { Al.CheckAl("Apply direct filter"); }
            catch { if (next != 0) Al.alDeleteFilters(1, &next); throw; }
            if (_directFilter != 0) { var old = _directFilter; Al.alDeleteFilters(1, &old); }
            _directFilter = next;
        });
    }
    public void SetSend(int index, CoreSlot? slot, float gain = 1, FilterSettings? filter = null)
    {
        if (index < 0 || index >= _owner.MaximumSends) throw new ArgumentOutOfRangeException(nameof(index), $"Device negotiated {_owner.MaximumSends} sends per source.");
        if (!float.IsFinite(gain) || gain < 0 || gain > 1) throw new ArgumentOutOfRangeException(nameof(gain));
        filter?.Validate();
        Control(() =>
        {
            if (slot is not null && (slot is not Slot native || native.Owner != _owner || native.IsDisposed))
                throw new ArgumentException("Send target must be a live slot on the source's device.", nameof(slot));
            var next = slot == null ? 0 : CreateFilter(filter, gain);
            Al.alSource3i(Id, 0x20006, (int)(((Slot?)slot)?.Id ?? 0), index, (int)next);
            try { Al.CheckAl("Attach source effect send"); }
            catch { if (next != 0) Al.alDeleteFilters(1, &next); throw; }
            RemoveProcessingSend(index);
            if (_sends.Remove(index, out var old) && old.Filter != 0) { var id = old.Filter; Al.alDeleteFilters(1, &id); }
            if (slot != null) _sends[index] = ((Slot)slot, next);
        });
    }
    private uint CreateFilter(FilterSettings? filter, float gain)
    {
        if (filter == null && gain == 1) return 0;
        filter ??= new FilterSettings();
        uint id; Al.alGenFilters(1, &id); Al.CheckAl("Allocate route filter");
        try
        {
            Al.alFilteri(id, 0x8001, (int)filter.Kind + 1);
            Al.alFilterf(id, 1, filter.Gain * gain);
            if (filter.Kind == FilterKind.LowPass) Al.alFilterf(id, 2, filter.GainHF);
            else Al.alFilterf(id, 2, filter.GainLF);
            if (filter.Kind == FilterKind.BandPass) Al.alFilterf(id, 3, filter.GainHF);
            Al.CheckAl("Configure route filter");
            return id;
        }
        catch { Al.alDeleteFilters(1, &id); throw; }
    }
    internal void DetachSlot(Slot slot)
    {
        foreach (var pair in _processingSends.ToArray())
            if (pair.Value.Sends.TryGetValue(pair.Key, out var route) && route.Slot == slot) RemoveProcessingSend(pair.Key);
        for (var i = 0; i < _owner.MaximumSends; i++)
            if (_sends.TryGetValue(i, out var send) && send.Slot == slot) SetSend(i, null);
    }
    private void DisposeFilters()
    {
        Al.alSourcei(Id, 0x20005, 0);
        if (_directFilter != 0) { var id = _directFilter; Al.alDeleteFilters(1, &id); }
        for (var i = 0; i < _owner.MaximumSends; i++)
            if (_sends.ContainsKey(i)) SetSend(i, null);
    }
}
