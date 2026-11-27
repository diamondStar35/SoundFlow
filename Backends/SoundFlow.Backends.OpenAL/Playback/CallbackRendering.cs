using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using SoundFlow.Enums;
using SoundFlow.Playback;
using SoundFlow.Processing;
using SoundFlow.Structs;

namespace SoundFlow.Backends.OpenAL.Playback;

internal sealed unsafe partial class Voice
{
    private RenderSession? _render;
    private CallbackEndpoint? _callback;
    private Exception? _callbackFailure;
    private int _renderFrames;
    private GraphProvider? _callbackGraph;

    private void InitializeCallback(SourceOptions options)
    {
        if (!_owner.HasExtension("AL_SOFT_callback_buffer") || Al.alBufferCallbackSOFT == null)
            throw new NotSupportedException("On-demand rendering requires AL_SOFT_callback_buffer. Queued playback is not substituted.");
        _renderFrames = options.RenderBlockFrames;
        _callbackGraph = _transport.DataProvider as GraphProvider;
        var runner = options.RenderProcessing!.CreateRunner(_pump.OutputFormat, _renderFrames);
        try { _render = new(_pump, runner, _renderFrames, renderDomain: _owner.Owner); }
        catch { runner.Dispose(); throw; }
        if (_render.OutputFormat.Ambisonics != null)
            throw new NotSupportedException("Decode Ambisonics before the native speaker endpoint.");
        _nativeFormat = FormatToken(_render.OutputFormat.Channels);
        _callback = new(this, _buffers[0], _render.OutputFormat, 0, null, 0);
        Al.alSourcei(Id, 0x1009, (int)_buffers[0]);
        Al.CheckAl("Attach on-demand source buffer");
    }

    internal void ConfigureRenderProcessing(Chain? chain)
    {
        RequireStoppedProcessing();
        if (_render == null) throw new NotSupportedException("Create the source with a render chain to reserve callback rendering.");
        var runner = (chain ?? Chain.Empty).CreateRunner(_pump.OutputFormat, _renderFrames);
        RenderSession replacement;
        try
        {
            if (runner.OutputFormat != _render.OutputFormat)
                throw new ArgumentException("Recreate the source to change its native callback format.");
            replacement = new(_pump, runner, _renderFrames, renderDomain: _owner.Owner);
        }
        catch { runner.Dispose(); throw; }
        Control(() =>
        {
            FlushQueue();
            ClearProcessingRoutes();
            var previous = _render;
            _render = replacement;
            previous.Dispose();
        });
    }

    private void PollCallback(bool wait, bool running)
    {
        if (_callbackFailure != null) { Fail(_callbackFailure); return; }
        if (_state != PlaybackState.Playing) return;
        if (wait)
        {
            if (_callbackGraph != null) _callbackGraph.PrepareForRender();
            else _pump.Prepare();
        }
        if (_render!.Completed && NativeIds.All(id => Al.SourceInteger(id, 0x1010) != 0x1012))
        {
            _state = PlaybackState.Stopped;
            _pump.Enable(false);
            PostNaturalCompletion();
        }
        else if (running && !_preparing && !ScheduledAt.HasValue && !_hasStarted)
        {
            PlayNativeEndpoints();
            _hasStarted = true;
        }
    }

    private void DisposeCallback()
    {
        _render?.Suspend();
        _callback?.Dispose();
        _callback = null;
        _render?.Dispose();
        _render = null;
    }

    /// <summary>
    /// Native callback registration and lifetime. Buffer replacement after source detachment synchronizes
    /// with OpenAL's mixer before the managed handle can be released. Exceptions never cross native code.
    /// </summary>
    private sealed class CallbackEndpoint : IDisposable
    {
        internal readonly Voice Voice;
        private readonly uint _buffer;
        private readonly AudioFormat _format;
        private readonly int _endpoint;
        private readonly Point? _point;
        private readonly DelayLine _delay;
        private readonly float[] _scratch;
        private readonly int _delayFrames;
        private int _delayRemaining;
        private GCHandle _handle;

        internal CallbackEndpoint(Voice voice, uint buffer, AudioFormat format, int endpoint, Point? point, int delay)
        {
            Voice = voice;
            _buffer = buffer;
            _format = format;
            _endpoint = endpoint;
            _point = point;
            _delay = new(delay, format.Channels);
            _delayFrames = _delayRemaining = delay;
            _scratch = new float[voice._renderFrames * format.Channels];
            _handle = GCHandle.Alloc(this);
            try
            {
                voice.Al.alBufferCallbackSOFT(buffer, FormatToken(format.Channels), format.SampleRate, &ReadCallback, GCHandle.ToIntPtr(_handle));
                voice.Al.CheckAl("Register on-demand PCM callback");
            }
            catch { _handle.Free(); throw; }
        }

        [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
        private static int ReadCallback(nint user, void* samples, int bytes)
        {
            var output = new Span<float>(samples, bytes / sizeof(float));
            output.Clear();
            var endpoint = (CallbackEndpoint)GCHandle.FromIntPtr(user).Target!;
            try
            {
                using var processing = Devices.ProcessingScope.Enter();
                var session = endpoint.Voice._render;
                if (session == null) return 0;
                var written = 0;
                while (written < output.Length)
                {
                    var count = Math.Min(endpoint._scratch.Length, output.Length - written);
                    var scratch = endpoint._scratch.AsSpan(0, count);
                    var frames = endpoint.Voice._callbackGraph is { } graph
                        ? graph.ReadSamples(scratch) / endpoint._format.Channels
                        : session.Read(scratch, endpoint._endpoint, endpoint._point);
                    var valid = frames * endpoint._format.Channels;
                    var outgoing = valid;
                    if (valid < count && endpoint._delayRemaining > 0)
                    {
                        var tail = Math.Min((count - valid) / endpoint._format.Channels, endpoint._delayRemaining);
                        endpoint._delayRemaining -= tail;
                        outgoing += tail * endpoint._format.Channels;
                    }
                    endpoint._delay.Process(scratch[..outgoing], output.Slice(written, outgoing));
                    written += outgoing;
                    if (outgoing != count) break;
                }
                return written * sizeof(float);
            }
            catch (Exception error)
            {
                Interlocked.CompareExchange(ref endpoint.Voice._callbackFailure, error, null);
                return 0;
            }
        }

        internal void Reset() { _delay.Reset(); _delayRemaining = _delayFrames; }

        public void Dispose()
        {
            if (!_handle.IsAllocated) return;
            // Source ownership must be detached before callback storage is replaced.
            Voice.Al.alBufferData(_buffer, FormatToken(_format.Channels), null, 0, _format.SampleRate);
            Voice.Al.CheckAl("Retire on-demand callback buffer");
            _handle.Free();
            Voice._render?.RemoveEndpoint(_endpoint);
        }
    }
}
