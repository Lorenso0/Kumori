namespace Kumori.ReplayViewer;

internal sealed class AdvancedAnalyzerRuntime
{
    private readonly Func<KumoriReplayPlayer?> getPlayer;

    public double Time => getPlayer()?.GameplayTime ?? 0;
    public bool IsPaused => getPlayer()?.IsGameplayPaused ?? true;
    public double Rate => getPlayer()?.PlaybackRate ?? 1;

    public AdvancedAnalyzerRuntime(Func<KumoriReplayPlayer?> getPlayer)
    {
        this.getPlayer = getPlayer;
    }

    public bool Enter()
    {
        if (getPlayer() is not { IsLoaded: true } player)
            return false;
        player.EnterAnalysisMode();
        return true;
    }

    public void Exit()
    {
        getPlayer()?.ExitAnalysisMode();
    }

    public void Focus(double time)
    {
        if (getPlayer() is not { IsLoaded: true } player)
            return;
        player.PauseGameplay();
        player.Seek(Math.Max(0, time));
    }

    public void Play(double startTime)
    {
        if (getPlayer() is not { IsLoaded: true } player)
            return;
        player.Seek(Math.Max(0, startTime));
        player.StartReplayPlayback();
    }

    public void Seek(double time) => getPlayer()?.Seek(Math.Max(0, time));
    public void Pause() => getPlayer()?.PauseGameplay();
    public void Start() => getPlayer()?.StartReplayPlayback();
    public void StepFrame(int direction) => getPlayer()?.StepFrame(direction);
    public void SetRate(double rate) => getPlayer()?.SetPlaybackRate(rate);
    public void SetSelectedAnalysisMarkers(
        MissAnalysisEntry? entry,
        bool showClickMarker,
        bool recolourNote,
        bool showNoteIndicator,
        osu.Framework.Graphics.Colour4 colour)
        => getPlayer()?.SetSelectedAnalysisMarkers(entry, showClickMarker, recolourNote, showNoteIndicator, colour);
}
