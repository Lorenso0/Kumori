namespace Kumori.Tracking;

/// <summary>
/// Supplies replay-playback state that is not currently exposed by the
/// official tosu v2 websocket payload.
/// </summary>
public interface IReplayPlaybackDetector
{
    bool IsWatchingReplay(OsuClientKind clientKind);

    /// <summary>
    /// Invalidates a result from the completed gameplay generation. The default
    /// keeps lightweight/custom detectors source-compatible.
    /// </summary>
    void ResetAfterGameplay(OsuClientKind clientKind) { }
}
