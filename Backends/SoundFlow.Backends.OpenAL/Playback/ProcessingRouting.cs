using SoundFlow.Effects;
using SoundFlow.Features;
using SoundFlow.Processing;
using SoundFlow.Structs;
using NativeSlot = SoundFlow.Backends.OpenAL.Effects.Slot;

namespace SoundFlow.Backends.OpenAL.Playback;

internal sealed unsafe partial class Voice
{
    // A logical voice owns one decoder and packet stream. Relays consume copied points from those same
    // packets, with a zero-gain direct filter so only their auxiliary sends reach the listener.
    private readonly List<Relay> _relays = [];
    private readonly Dictionary<int, Relay> _processingSends = new();
    private uint[] _nativeIds = [];
    internal uint[] NativeIds => _nativeIds;
    internal int ProcessingEndpoints => _relays.Count;
    private void RefreshNativeIds() => _nativeIds = [Id, .. _relays.Select(relay => relay.Id)];
    private void PlayNativeEndpoints()
    {
        fixed (uint* ids = NativeIds) Al.alSourcePlayv(NativeIds.Length, ids);
    }

    public void SetSend(int index, Point point, Slot? slot, SendSettings? settings = null)
    {
        ArgumentNullException.ThrowIfNull(point); settings ??= new(); settings.Validate();
        ValidateTransportControl();
        if (IsPrepared) throw new NotSupportedException("Prepared clips have no software capture points.");
        if (index < 0 || index >= _owner.MaximumSends) throw new ArgumentOutOfRangeException(nameof(index));
        // Validate foreign points even when disconnecting, before touching either native path.
        var format = _render?.GetFormat(point) ?? _pump.Processing.GetFormat(point);
        if (format.Ambisonics != null)
            throw new NotSupportedException("Decode captured Ambisonics before routing it to a native speaker effect.");
        var delay = _render?.GetCompensationFrames(point, settings.Compensation) ?? _pump.Processing.GetCompensationFrames(point, settings.Compensation);
        _ = FormatToken(format.Channels);
        Control(() =>
        {
            if (slot != null && (slot is not NativeSlot native || native.Owner != _owner || native.IsDisposed))
                throw new ArgumentException("Send target must be a live slot on the source's device.", nameof(slot));
            _processingSends.TryGetValue(index, out var previous);
            var relay = _relays.FirstOrDefault(item => item.Point == point && item.Compensation == settings.Compensation);
            if (slot != null && previous != relay && _state != Enums.PlaybackState.Stopped)
                throw new InvalidOperationException("Stop playback before changing a send's point or compensation policy.");
            if (slot == null) { RemoveProcessingSend(index); return; }
            var created = relay == null;
            relay ??= new Relay(this, point, format, settings.Compensation, delay);
            try
            {
                relay.Bind(index, (NativeSlot)slot, settings);
                // A send index has exactly one owner across legacy final-output and captured-point routes.
                if (previous != relay) SetSend(index, null);
                if (created) { _relays.Add(relay); RefreshNativeIds(); ApplySpatial(_spatial); ApplyRendering(_rendering); }
                _processingSends[index] = relay;
            }
            catch { if (created) { _relays.Remove(relay); RefreshNativeIds(); relay.Dispose(); } throw; }
        });
    }

    private void RemoveProcessingSend(int index)
    {
        if (!_processingSends.Remove(index, out var relay)) return;
        relay.Unbind(index);
        if (relay.Sends.Count != 0) return;
        _relays.Remove(relay); RefreshNativeIds(); relay.Dispose();
    }
    private void ClearProcessingRoutes()
    {
        foreach (var index in _processingSends.Keys.ToArray()) RemoveProcessingSend(index);
    }

    /// <summary>
    /// Native wet-only endpoint. Buffer identity follows the primary endpoint so no packet can be reused
    /// until every endpoint has finished it. Delay storage is allocated only for an explicitly compensated send.
    /// It has no provider, transport, decoding session, worker or independent playback lifecycle.
    /// </summary>
    private sealed class Relay : IDisposable
    {
        private readonly Voice _voice;
        internal uint Id;
        internal readonly Point Point;
        internal readonly AudioFormat Format;
        internal readonly LatencyCompensation Compensation;
        internal readonly Dictionary<int, (NativeSlot Slot, uint Filter)> Sends = new();
        private readonly Dictionary<uint, uint> _buffers = new();
        private readonly DelayLine _delay;
        private readonly float[] _scratch;
        private uint _mute;
        private CallbackEndpoint? _callback;
        private uint _callbackBuffer;
        private int _queued;
        internal Relay(Voice voice, Point point, AudioFormat format, LatencyCompensation compensation, int delay)
        {
            _voice = voice; Point = point; Format = format; Compensation = compensation;
            _delay = new DelayLine(delay, format.Channels);
            _scratch = new float[checked(voice._owner.Config.BufferFrames * format.Channels)];
            var al = voice.Al;
            try
            {
                uint id; al.alGenSources(1, &id); Id = id; al.CheckAl("Allocate wet endpoint"); voice._owner.SourceAllocations++;
                _mute = voice.CreateFilter(new FilterSettings { Gain = 0 }, 1);
                al.alSourcei(Id, 0x20005, (int)_mute);
                al.alSourcef(Id, 0x100A, voice._gain); al.alSourcef(Id, 0x1003, voice._pitch);
                if (voice._render != null)
                {
                    uint callbackBuffer;
                    al.alGenBuffers(1, &callbackBuffer);
                    _callbackBuffer = callbackBuffer;
                    _callback = new(voice, callbackBuffer, format, voice._render.AddEndpoint(), point, delay);
                    al.alSourcei(Id, 0x1009, (int)callbackBuffer);
                    voice._owner.BufferAllocations++;
                }
                else foreach (var primary in voice._buffers)
                {
                    uint buffer; al.alGenBuffers(1, &buffer); al.CheckAl("Allocate wet queue buffer");
                    _buffers.Add(primary, buffer); voice._owner.BufferAllocations++;
                }
                al.CheckAl("Configure wet endpoint");
            }
            catch { Dispose(); throw; }
        }
        internal void Bind(int index, NativeSlot slot, SendSettings settings)
        {
            var al = _voice.Al; var filter = _voice.CreateFilter(settings.Filter, settings.Gain);
            al.alSource3i(Id, 0x20006, (int)slot.Id, index, (int)filter);
            try { al.CheckAl("Bind captured effect send"); }
            catch { if (filter != 0) al.alDeleteFilters(1, &filter); throw; }
            if (Sends.Remove(index, out var old) && old.Filter != 0) { var id = old.Filter; al.alDeleteFilters(1, &id); }
            Sends[index] = (slot, filter);
        }
        internal void Unbind(int index)
        {
            var al = _voice.Al; al.alSource3i(Id, 0x20006, 0, index, 0); al.CheckAl("Detach captured effect send");
            if (Sends.Remove(index, out var old) && old.Filter != 0) { var id = old.Filter; al.alDeleteFilters(1, &id); }
        }
        internal void Upload(uint primary, ReadOnlySpan<float> samples)
        {
            _delay.Process(samples, _scratch);
            fixed (float* data = _scratch) _voice.Al.alBufferData(_buffers[primary], FormatToken(Format.Channels), data, samples.Length * sizeof(float), Format.SampleRate);
            _voice.Al.CheckAl("Upload captured PCM");
        }
        internal void Queue(uint primary)
        {
            var buffer = _buffers[primary]; _voice.Al.alSourceQueueBuffers(Id, 1, &buffer);
            _voice.Al.CheckAl("Queue captured PCM"); _queued++;
        }
        internal void Reclaim()
        {
            uint buffer; _voice.Al.alSourceUnqueueBuffers(Id, 1, &buffer); _voice.Al.CheckAl("Reclaim wet buffer"); _queued--;
        }
        internal void Flush()
        {
            _voice.Al.alSourceStop(Id);
            while (_queued > 0) Reclaim();
            _delay.Reset();
            _callback?.Reset();
        }
        public void Dispose()
        {
            var al = _voice.Al;
            if (Id != 0)
            {
                Flush(); foreach (var index in Sends.Keys.ToArray()) Unbind(index);
                al.alSourcei(Id, 0x20005, 0); var id = Id; al.alDeleteSources(1, &id); Id = 0;
            }
            _callback?.Dispose();
            _callback = null;
            if (_callbackBuffer != 0) { var buffer = _callbackBuffer; al.alDeleteBuffers(1, &buffer); _callbackBuffer = 0; }
            foreach (var item in _buffers.Values) { var buffer = item; al.alDeleteBuffers(1, &buffer); }
            _buffers.Clear(); if (_mute != 0) { var filter = _mute; al.alDeleteFilters(1, &filter); _mute = 0; }
            al.CheckAl("Release wet endpoint");
        }
    }
}
