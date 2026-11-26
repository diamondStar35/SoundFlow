using SoundFlow.Playback;
using CoreSource = SoundFlow.Playback.Source;
using CoreSpatialSource = SoundFlow.Playback.SpatialSource;

namespace SoundFlow.Backends.OpenAL.Devices;

/// <summary>A reference-counted context upload. All references and native identifiers are touched only by the context worker.</summary>
internal sealed class PreparedBuffer(Clip clip, uint id, long age)
{
    internal readonly Clip Clip = clip;
    internal readonly uint Id = id;
    internal long Age = age;
    internal int References;
    internal long Bytes => (long)Clip.Frames * Clip.Format.Channels * sizeof(float);
}

internal sealed unsafe partial class Context : IPreparedClips
{
    private readonly Dictionary<Clip, PreparedBuffer> _clips = new(ReferenceEqualityComparer.Instance);
    private readonly Stack<uint> _idleSources = new();
    private long _clipBytes;
    private long _clipAge;
    internal long SourceAllocations;
    internal long BufferAllocations;
    internal long ReusedSources;

    public PreparedClip Prepare(Clip clip)
    {
        ArgumentNullException.ThrowIfNull(clip);
        return Worker.Invoke<PreparedClip>(() =>
        {
            EnsureOutsideBatch();
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (clip.Frames == 0) throw new ArgumentException("A prepared clip requires at least one complete frame.", nameof(clip));
            if (!_clips.TryGetValue(clip, out var entry))
            {
                var token = Playback.Voice.FormatToken(clip.Format.Channels);
                var bytes = checked((long)clip.Frames * clip.Format.Channels * sizeof(float));
                if (bytes > int.MaxValue || bytes > Config.PreparedClipBytes) throw new InvalidOperationException("The clip exceeds this device's prepared PCM budget.");
                foreach (var unused in _clips.Values.Where(c => c.References == 0).OrderBy(c => c.Age).ToArray())
                {
                    if (_clipBytes + bytes <= Config.PreparedClipBytes) break;
                    var old = unused.Id; Al.alDeleteBuffers(1, &old); Al.CheckAl("Evict unused prepared clip");
                    _clips.Remove(unused.Clip); _clipBytes -= unused.Bytes;
                }
                if (_clipBytes + bytes > Config.PreparedClipBytes) throw new InvalidOperationException("Prepared PCM is in use and the device's memory budget is exhausted.");
                uint buffer; Al.alGenBuffers(1, &buffer); Al.CheckAl("Allocate prepared clip");
                try
                {
                    fixed (float* samples = clip.Samples) Al.alBufferData(buffer, token, samples, (int)bytes, clip.Format.SampleRate);
                    Al.CheckAl("Upload prepared clip");
                    Label(0x1009, buffer, "SoundFlow prepared clip");
                }
                catch { Al.alDeleteBuffers(1, &buffer); throw; }
                entry = new PreparedBuffer(clip, buffer, ++_clipAge);
                _clips.Add(clip, entry); _clipBytes += bytes; BufferAllocations++;
            }
            entry.References++; entry.Age = ++_clipAge;
            return new PreparedHandle(this, entry);
        });
    }

    internal uint RentPreparedSource()
    {
        if (_idleSources.TryPop(out var existing)) { ReusedSources++; return existing; }
        uint source; Al.alGenSources(1, &source); Al.CheckAl("Allocate prepared source"); SourceAllocations++;
        Label(0x1000, source, "SoundFlow prepared source");
        return source;
    }
    internal void ReturnPreparedSource(uint source)
    {
        if (_idleSources.Count < Config.MaximumIdleSources) _idleSources.Push(source);
        else Al.alDeleteSources(1, &source);
    }
    internal void RetainClip(PreparedBuffer entry) { entry.References++; entry.Age = ++_clipAge; }
    internal void ReleaseClip(PreparedBuffer entry) { entry.References--; entry.Age = ++_clipAge; }
    private void DisposePreparedBuffers()
    {
        while (_idleSources.TryPop(out var source)) Al.alDeleteSources(1, &source);
        foreach (var entry in _clips.Values) { var buffer = entry.Id; Al.alDeleteBuffers(1, &buffer); }
        _clips.Clear(); _clipBytes = 0;
    }

    /// <summary>One retained upload reference. Sources take an additional reference before this handle can be released.</summary>
    private sealed class PreparedHandle(Context context, PreparedBuffer entry) : PreparedClip
    {
        private bool _released;
        public override SoundFlow.Devices.PlaybackDevice Device => context.Owner;
        public override Clip Clip => entry.Clip;
        private CoreSource Create(bool spatial) => context.Worker.Invoke<CoreSource>(() =>
        {
            context.EnsureOutsideBatch();
            ObjectDisposedException.ThrowIf(_released || context._disposed, this);
            var provider = Clip.CreateProvider();
            try
            {
                return spatial ? new Playback.SpatialSource(context, provider, Clip.Format, false, entry)
                    : new Playback.Source(context, provider, Clip.Format, false, entry);
            }
            catch { provider.Dispose(); throw; }
        });
        public override CoreSource CreateSource() => Create(false);
        public override CoreSpatialSource CreateSpatialSource() => (CoreSpatialSource)Create(true);
        public override void Dispose()
        {
            if (context._disposed) { _released = true; return; }
            context.Worker.Invoke(() => { if (_released) return; _released = true; context.ReleaseClip(entry); });
        }
    }
}
