using Kumori.Core.State;
using Kumori.Storage;
using Kumori.Tracking;

namespace Kumori.App;

internal sealed class StatePublishingAttemptSink : IAttemptSink
{
    private readonly AttemptSqliteSink _inner;
    private readonly AppStateStore _store;

    public StatePublishingAttemptSink(
        AttemptSqliteSink inner,
        AppStateStore store)
    {
        _inner = inner;
        _store = store;
        _inner.AttemptPersisted += PublishPersistedAttempt;
    }

    public void StartAttempt(AttemptStart start) => _inner.StartAttempt(start);

    public void Checkpoint(AttemptCheckpoint checkpoint) => _inner.Checkpoint(checkpoint);

    public void DiscardIfEmpty(AttemptDiscard discard) => _inner.DiscardIfEmpty(discard);

    public void Finalize(AttemptFinalization finalization) => _inner.Finalize(finalization);

    private void PublishPersistedAttempt(long attemptId)
    {
        _store.Update(s => s with
        {
            Tracking = s.Tracking with
            {
                LatestAttemptId = attemptId,
            },
        });
    }
}
