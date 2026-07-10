using Kumori.Core.State;
using Kumori.Tracking;

namespace Kumori.App;

internal sealed class StatePublishingAttemptSink : IAttemptSink
{
    private readonly IAttemptSink _inner;
    private readonly Func<long?> _currentAttemptId;
    private readonly Func<long, bool> _hasReplayData;
    private readonly AppStateStore _store;

    public StatePublishingAttemptSink(
        IAttemptSink inner,
        Func<long?> currentAttemptId,
        Func<long, bool> hasReplayData,
        AppStateStore store)
    {
        _inner = inner;
        _currentAttemptId = currentAttemptId;
        _hasReplayData = hasReplayData;
        _store = store;
    }

    public void StartAttempt(AttemptStart start) => _inner.StartAttempt(start);

    public void Checkpoint(AttemptCheckpoint checkpoint) => _inner.Checkpoint(checkpoint);

    public void DiscardIfEmpty(AttemptDiscard discard) => _inner.DiscardIfEmpty(discard);

    public void Finalize(AttemptFinalization finalization)
    {
        var attemptId = _currentAttemptId();
        _inner.Finalize(finalization);
        if (attemptId is not { } id)
        {
            return;
        }

        _store.Update(s => s with
        {
            Tracking = s.Tracking with
            {
                LatestAttemptId = id,
                LatestReplayAttemptId = _hasReplayData(id) ? id : s.Tracking.LatestReplayAttemptId,
            },
        });
    }
}
