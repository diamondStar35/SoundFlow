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
        try { Play(); FillQueue(true, false); }
        catch { PauseAfterNativeCommand(); throw; }
        finally { _preparing = false; }
    }

    internal void PauseAfterNativeCommand()
    {
        _state = PlaybackState.Paused;
        _pump.Enable(false);
        _pump.Change(_transport.Pause, false);
        ScheduledAt = null;
    }
}
