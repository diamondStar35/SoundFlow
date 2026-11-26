using PlaybackDevice = SoundFlow.Devices.PlaybackDevice;
using SoundFlow.Enums;
using SoundFlow.Playback;
using CoreSource = SoundFlow.Playback.Source;

namespace SoundFlow.Backends.OpenAL.Playback;

/// <summary>Uses AL vector operations so a group crosses a single native mixing boundary.</summary>
internal sealed unsafe class Group : SourceGroup
{
    internal readonly Devices.Context Context;
    private readonly IReadOnlyList<CoreSource> _sources;
    private readonly uint[] _ids;
    public override PlaybackDevice Device => Context.Owner;
    public override IReadOnlyList<CoreSource> Sources => _sources;
    public override bool IsDisposed => _disposed;
    private bool _disposed;

    internal Group(Devices.Context context, CoreSource[] sources)
    {
        Context = context;
        ArgumentNullException.ThrowIfNull(sources);
        if (sources.Length == 0 || sources.Distinct().Count() != sources.Length) throw new ArgumentException("A group needs distinct source members.", nameof(sources));
        _sources = Array.AsReadOnly(sources.ToArray());
        _ids = sources.Select(source => context.GetVoice(source).Id).ToArray();
    }

    private Voice[] Validate()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        return _sources.Select(Context.GetVoice).ToArray();
    }

    public override void Play() => Start(null);

    internal void Start(long? timestamp)
    {
        var voices = Context.Worker.Invoke(() => { Context.RequireRunningForNativePlayback(); return Validate(); });
        if (!timestamp.HasValue && voices.All(voice => voice.State == PlaybackState.Playing)) return;
        if (voices.Any(voice => voice.State == PlaybackState.Playing)) throw new InvalidOperationException("Pause or stop all members before synchronized playback.");
        // Decode-ahead may advance input cursors, but no member starts before every input is prepared.
        try
        {
            foreach (var voice in voices) voice.PrepareStart();
            Context.Worker.Invoke(() =>
            {
                Validate();
                fixed (uint* ids = _ids)
                {
                    if (timestamp.HasValue) Context.Al.alSourcePlayAtTimevSOFT(_ids.Length, ids, timestamp.Value);
                    else Context.Al.alSourcePlayv(_ids.Length, ids);
                }
                Context.Al.CheckAl("Start source group");
                foreach (var voice in voices) voice.CompleteStart(timestamp);
            });
        }
        catch
        {
            Context.Worker.Invoke(() => { foreach (var voice in voices) { voice.PauseNative(); voice.PauseAfterNativeCommand(); } });
            throw;
        }
    }

    public override void Pause() => Context.Worker.Invoke(() =>
    {
        var voices = Validate();
        fixed (uint* ids = _ids) Context.Al.alSourcePausev(_ids.Length, ids);
        Context.Al.CheckAl("Pause source group");
        foreach (var voice in voices) voice.PauseAfterNativeCommand();
    });

    public override void Stop()
    {
        var voices = Context.Worker.Invoke(() =>
        {
            var members = Validate();
            fixed (uint* ids = _ids) Context.Al.alSourceStopv(_ids.Length, ids);
            Context.Al.CheckAl("Stop source group"); return members;
        });
        foreach (var source in _sources) source.Stop();
    }
    public override void Rewind()
    {
        var voices = Context.Worker.Invoke(Validate);
        if (voices.Any(voice => !voice.DataProvider.CanSeek)) throw new NotSupportedException("Rewinding requires seekable members.");
        Stop();
    }

    public override void Dispose() => _disposed = true;
}
