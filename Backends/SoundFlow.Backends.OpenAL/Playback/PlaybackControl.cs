using SoundFlow.Enums;

namespace SoundFlow.Backends.OpenAL.Playback;

internal sealed unsafe partial class Voice
{
    private bool _preparing;
    internal long? ScheduledAt;

    /// <summary>Fills a source queue without starting it; vector/scheduled commands are issued only after preparation.</summary>
    internal void PrepareStart()
    {
        _preparing = true;
        try
        {
            Play();
            if (!IsPrepared) _pump.Prepare();
            _owner.Worker.Invoke(() => FillQueue(false, false));
        }
        catch { _owner.Worker.Invoke(PauseAfterNativeCommand); throw; }
    }

    /// <summary>Releases preparation only after the native vector/scheduled start has been armed.</summary>
    internal void CompleteStart(long? timestamp)
    {
        ScheduledAt = timestamp; _hasStarted = true; _preparing = false;
    }

    internal void PauseAfterNativeCommand()
    {
        _preparing = false;
        _state = PlaybackState.Paused;
        if (IsPrepared) { ScheduledAt = null; return; }
        _pump.Enable(false);

        ScheduledAt = null;
    }
}
