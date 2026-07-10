namespace Kumori.Tracking;

public sealed class TrackingReplayRunner
{
    private readonly TosuClient _client = new();
    private readonly AttemptTracker _attemptTracker;
    private readonly SessionTracker? _sessionTracker;
    private TosuSnapshot? _lastSnapshot;

    public TrackingReplayRunner(AttemptTracker attemptTracker, SessionTracker? sessionTracker = null)
    {
        _attemptTracker = attemptTracker;
        _sessionTracker = sessionTracker;
        _client.SnapshotReceived += OnSnapshot;
    }

    public async Task RunAsync(ITosuPacketSource source, CancellationToken cancellationToken = default)
    {
        await _client.RunAsync(source, cancellationToken);
        if (_lastSnapshot is not null && _sessionTracker?.HasSession == true)
        {
            _sessionTracker.EndClean(_lastSnapshot.WallTime, _lastSnapshot.MonoTime);
        }
    }

    private void OnSnapshot(TosuSnapshot snapshot)
    {
        _lastSnapshot = snapshot;
        if (!snapshot.IsStandardMode)
        {
            return;
        }

        _sessionTracker?.Ingest(TrackingFrameMapper.ToSessionFrame(snapshot));
        _attemptTracker.Ingest(TrackingFrameMapper.ToAttemptFrame(snapshot));
    }
}
