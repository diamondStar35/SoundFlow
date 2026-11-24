using SoundFlow.Providers;
using BackendException = SoundFlow.Backends.OpenAL.BackendException;
using CoreSource = SoundFlow.Playback.Source;
using DeviceConfiguration = SoundFlow.Backends.OpenAL.DeviceConfiguration;
using Engine = SoundFlow.Backends.OpenAL.Engine;
using Functions = SoundFlow.Backends.OpenAL.Interop.Functions;
using INativeSource = SoundFlow.Backends.OpenAL.Playback.INativeSource;
using CorePlaybackDevice = SoundFlow.Devices.PlaybackDevice;
using Slot = SoundFlow.Backends.OpenAL.Effects.Slot;
using Source = SoundFlow.Backends.OpenAL.Playback.Source;
using SpatialSource = SoundFlow.Backends.OpenAL.Playback.SpatialSource;
using Voice = SoundFlow.Backends.OpenAL.Playback.Voice;
using System.Text;
using SoundFlow.Devices;
using SoundFlow.Effects;
using SoundFlow.Enums;
using SoundFlow.Features;
using SoundFlow.Interfaces;
using SoundFlow.Playback;
using SoundFlow.Structs;

namespace SoundFlow.Backends.OpenAL.Devices;

/// <summary>Owns one native context, its worker, sources and routing. Playback and offline adapters use the same implementation.</summary>
internal sealed unsafe partial class Context : IDisposable, IDiagnostics
{
    internal readonly Functions Al;
    private readonly Engine _engine;
    internal readonly CorePlaybackDevice Owner;
    internal readonly DeviceConfiguration Config;
    internal readonly ContextWorker Worker;
    internal readonly List<Voice> Sources = new();
    internal readonly List<Slot> Slots = new();
    internal nint Device;
    private nint _context;
    private readonly IDeviceLifecycle _lifecycle;
    internal readonly bool Offline;
    internal readonly Action<float[], int>? GraphTap;
    internal int MaximumSends;
    internal bool HasEfx;
    internal bool HasHrtf;
    internal bool SlotChaining;
    private bool _disposed;
    private bool _running;
    private string _version = "";
    private string[] _extensions = [];
    private string _deviceName = "";
    private float _masterGain = 1;
    private HrtfMode _hrtfMode;
    private int? _hrtfDataset;
    private Features.OutputMode? _outputMode;
    private bool? _outputLimiter;

    internal Context(Engine engine, CorePlaybackDevice owner, DeviceInfo? info, DeviceConfiguration config, bool offline, IDeviceLifecycle? lifecycle = null, Action<float[], int>? graphTap = null)
    {
        _engine = engine;
        GraphTap = graphTap;
        _hrtfMode = config.Hrtf; _outputMode = config.OutputMode; _outputLimiter = config.OutputLimiter;
        Al = engine.Native; Owner = owner; Config = config; Offline = offline;
        _lifecycle = lifecycle ?? new DeviceLifecycle(Al);
        if (owner.Format.Format != SampleFormat.F32 || owner.Format.SampleRate <= 0)
            throw new ArgumentException("OpenAL output requires a positive sample rate and interleaved F32 samples.");
        _ = ChannelToken(owner.Format.Channels);
        Worker = new ContextWorker(() => Initialize(info), Tick, Cleanup, ProcessingFailed);
        try
        {
            // The master mixer is one nonspatial graph feed, alongside individually spatialized native sources.
            var graph = CreateSource(new GraphProvider(owner.MasterMixer, true), owner.Format, false);
            ((INativeSource)graph).Voice.IsGraphFeed = true;
            graph.Play();
        }
        catch { Worker.Dispose(); throw; }
    }

    private void Initialize(DeviceInfo? info)
    {
        try
        {
            var name = Engine.DeviceName(info, "playback");
            Device = _lifecycle.Open(Offline, name);
            if (Device == 0) throw new BackendException("Open device", Al.alcGetError(0), Al.LibraryPath);
            if (Config.OutputMode.HasValue && (Offline || !Al.HasAlcExtension(Device, "ALC_SOFT_output_mode"))) throw new NotSupportedException("This device cannot accept a physical output-mode request.");
            if (Config.OutputLimiter.HasValue && !Al.HasAlcExtension(Device, "ALC_SOFT_output_limiter")) throw new NotSupportedException("Output limiter control is unavailable.");
            var attributes = Attributes();
            _context = _lifecycle.CreateContext(Device, attributes);
            if (_context == 0) throw new BackendException("Create context", Al.alcGetError(Device), Al.LibraryPath);
            if (Al.alcSetThreadContext == null || Al.alcSetThreadContext(_context) == 0)
                throw new NotSupportedException("OpenAL Soft must provide ALC_EXT_thread_local_context to isolate concurrent device workers.");
            _version = Functions.Text(Al.alGetString(0xB002));
            if (!_version.Contains("ALSOFT", StringComparison.OrdinalIgnoreCase))
                throw new NotSupportedException("This backend requires OpenAL Soft, not a system OpenAL/router implementation.");
            _deviceName = Offline ? "OpenAL Soft offline renderer" : Functions.Text(Al.alcGetString(Device, 0x1005));
            _extensions = (Functions.Text(Al.alGetString(0xB004)) + " " + Functions.Text(Al.alcGetString(Device, 0x1006)))
                .Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Distinct().Order().ToArray();
            InitializeCapabilities();
            HasEfx = Al.HasAlcExtension(Device, "ALC_EXT_EFX") && Al.alGenEffects != null && Al.alGenAuxiliaryEffectSlots != null;
            HasHrtf = Al.HasAlcExtension(Device, "ALC_SOFT_HRTF") && Al.alcResetDeviceSOFT != null;
            SlotChaining = Al.HasAlExtension("AL_SOFT_effect_target");
            MaximumSends = HasEfx ? Al.DeviceInteger(Device, 0x20003) : 0;
            if (HasExtension("AL_EXT_source_distance_model")) Al.alEnable(0x200);
            InitializeEffects();
            InitializeHrtf();
            InitializeNotifications();
            Al.CheckAl("Initialize context");
        }
        catch { Cleanup(); throw; }
    }

    private int[] Attributes(HrtfMode? hrtfMode = null, int? hrtfDataset = null, DeviceConfiguration? replacement = null)
    {
        var attributes = new List<int> { 0x1007, Owner.Format.SampleRate, 0x20003, Config.AuxiliarySends };
        if (Offline) attributes.AddRange([0x1990, ChannelToken(Owner.Format.Channels), 0x1991, 0x1406]);
        var mode = hrtfMode ?? replacement?.Hrtf ?? _hrtfMode;
        attributes.AddRange([0x1992, mode == HrtfMode.Automatic ? 2 : mode == HrtfMode.Enabled ? 1 : 0]);
        var dataset = hrtfDataset ?? _hrtfDataset;
        if (dataset.HasValue) attributes.AddRange([0x1996, dataset.Value]);
        var output = replacement?.OutputMode ?? _outputMode;
        if (output.HasValue) attributes.AddRange([0x19AC, OutputModeToken(output.Value)]);
        var limiter = replacement?.OutputLimiter ?? _outputLimiter;
        if (limiter.HasValue) attributes.AddRange([0x199A, limiter.Value ? 1 : 0]);
        attributes.Add(0);
        return attributes.ToArray();
    }

    internal static int ChannelToken(int channels) => channels switch
    {
        1 => 0x1500, 2 => 0x1501, 4 => 0x1503, 6 => 0x1504, 7 => 0x1505, 8 => 0x1506,
        _ => throw new NotSupportedException($"OpenAL output layout with {channels} channels is unsupported.")
    };

    internal CoreSource CreateSource(IDataProvider provider, AudioFormat format, bool leaveOpen, bool spatial = false)
        => Worker.Invoke<CoreSource>(() =>
        {
            EnsureOutsideBatch();
            return spatial ? new SpatialSource(this, provider, format, leaveOpen) : new Source(this, provider, format, leaveOpen);
        });

    internal void Start() => Worker.Invoke(() =>
    {
        if (_running) return;
        if (!Offline && Al.alcDeviceResumeSOFT != null) Al.alcDeviceResumeSOFT(Device);
        _running = true;
        foreach (var source in Sources) source.ResumeNative();
    });
    internal void Stop() => Worker.Invoke(() =>
    {
        if (!_running) return;
        foreach (var source in Sources) source.PauseNative();
        if (!Offline && Al.alcDevicePauseSOFT != null) Al.alcDevicePauseSOFT(Device);
        _running = false;
    });
    internal float MasterGain
    {
        get => _masterGain;
        set
        {
            if (!float.IsFinite(value) || value < 0) throw new ArgumentOutOfRangeException(nameof(value));
            Worker.Invoke(() => { Al.alListenerf(0x100A, value); Al.CheckAl("Set master gain"); _masterGain = value; });
        }
    }
    private void Tick()
    {
        if (Offline || !_running) return;
        FillSources(false);
    }
    private void FillSources(bool wait)
    {
        for (var i = Sources.Count - 1; i >= 0; i--)
            if (i < Sources.Count)
            {
                var source = Sources[i];
                try { source.FillQueue(wait, Offline || _running); }
                catch (Exception error) { source.Fail(); ProcessingFailed(error); }
            }
    }

    internal void Render(Span<float> output)
    {
        EnsureOutsideBatch();
        if (!Offline) throw new NotSupportedException("Only an offline device can read OpenAL's final mix.");
        if (output.Length % Owner.Format.Channels != 0) throw new ArgumentException("Output must contain complete interleaved frames.", nameof(output));
        fixed (float* pointer = output)
        {
            var address = (nint)pointer;
            var length = output.Length;
            Worker.Invoke(() =>
            {
                var offset = 0;
                while (offset < length)
                {
                    FillSources(true);
                    var frames = Math.Min(Config.BufferFrames, (length - offset) / Owner.Format.Channels);
                    Al.alcRenderSamplesSOFT(Device, (float*)address + offset, frames);
                    Al.CheckAlc(Device, "Render offline samples");
                    offset += frames * Owner.Format.Channels;
                    FillSources(false);
                }
            });
        }
    }

    internal void Reopen(DeviceInfo info, DeviceConfiguration? config) => Worker.Invoke(() =>
    {
        EnsureOutsideBatch();
        if (Offline || Al.alcReopenDeviceSOFT == null) throw new NotSupportedException("This device cannot reopen its physical endpoint.");
        if (config != null && (config.BufferFrames != Config.BufferFrames || config.QueueBuffers != Config.QueueBuffers || config.AuxiliarySends != Config.AuxiliarySends))
            throw new ArgumentException("Queue sizes and send capacity cannot change during an in-place device switch.", nameof(config));
        var name = Engine.DeviceName(info, "playback");
        if (config?.OutputMode is not null && !Al.HasAlcExtension(Device, "ALC_SOFT_output_mode")) throw new NotSupportedException("Output-mode control is unavailable.");
        if (config?.OutputLimiter is not null && !Al.HasAlcExtension(Device, "ALC_SOFT_output_limiter")) throw new NotSupportedException("Limiter control is unavailable.");
        var attrs = Attributes(replacement: config);
        if (!_lifecycle.Reopen(Device, name, attrs)) throw new BackendException("Reopen device; previous endpoint retained", Al.alcGetError(Device), Al.LibraryPath);
        _deviceName = Functions.Text(Al.alcGetString(Device, 0x1005));
        if (config != null) { _hrtfMode = config.Hrtf; _outputMode = config.OutputMode ?? _outputMode; _outputLimiter = config.OutputLimiter ?? _outputLimiter; }
        MaximumSends = HasEfx ? Al.DeviceInteger(Device, 0x20003) : 0;
        InitializeHrtf();
    });

    public DiagnosticsSnapshot GetSnapshot() => Worker.Invoke(() => new DiagnosticsSnapshot
    {
        Backend = "OpenAL Soft", Library = $"{_version} ({Al.LibraryPath})", Device = _deviceName,
        Format = Owner.Format, Extensions = Array.AsReadOnly(_extensions), AuxiliarySends = MaximumSends,
        Sources = Sources.Count(s => !s.IsGraphFeed), EffectSlots = Slots.Count, Underruns = Sources.Sum(s => s.Underruns),
        Connected = Offline || !Al.HasAlcExtension(Device, "ALC_EXT_disconnect") || Al.DeviceInteger(Device, 0x313) != 0,
        HrtfStatus = HasHrtf ? HrtfStatus() : "Unavailable",
        LastFailure = _lastFailure, DroppedNotifications = DroppedNotifications
    });

    private void Cleanup()
    {
        if (_context != 0)
        {
            RemoveNotifications();
            if (Al.alcSetThreadContext != null) Al.alcSetThreadContext(0);
            _lifecycle.DestroyContext(_context);
            _context = 0;
        }
        if (Device != 0) { _lifecycle.Close(Device); Device = 0; }
    }
    public void Dispose()
    {
        ProcessingScope.ThrowIfProcessing();
        EnsureOutsideBatch();
        if (_disposed) return;
        _disposed = true;
        List<Exception>? failures = null;
        try
        {
            foreach (var source in Sources.ToArray())
                try { source.Dispose(); } catch (Exception error) { (failures ??= []).Add(error); }
            foreach (var slot in Slots.ToArray())
                try { slot.Dispose(); } catch (Exception error) { (failures ??= []).Add(error); }
        }
        finally { Worker.Dispose(); }
        if (failures != null) throw new AggregateException("OpenAL context cleanup encountered errors.", failures);
    }
}
