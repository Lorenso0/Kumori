namespace Kumori.Tracking;

/// <summary>
/// Supplies replay-playback state that is not currently exposed by the
/// official tosu v2 websocket payload.
/// </summary>
public interface IReplayPlaybackDetector
{
    bool IsWatchingReplay(OsuClientKind clientKind);
}
