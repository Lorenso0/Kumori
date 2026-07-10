using Kumori.Core.State;
using Kumori.Storage;
using Kumori.Tracking;

namespace Kumori.App;

internal sealed class StatePublishingSessionSink : ISessionSink
{
    private readonly AttemptSqliteSink _inner;
    private readonly AppStateStore _store;

    public StatePublishingSessionSink(AttemptSqliteSink inner, AppStateStore store)
    {
        _inner = inner;
        _store = store;
    }

    public void StartSession(SessionStart start)
    {
        _inner.StartSession(start);
        if (_inner.CurrentSessionId is not { } sessionId)
        {
            return;
        }

        _store.Update(s => s with
        {
            ActiveSession = new ActiveSessionInfo
            {
                SessionId = sessionId,
                StartedAt = DateTimeOffset.FromUnixTimeMilliseconds((long)(start.WallTime * 1000)),
            },
        });
    }

    public void AddActiveSeconds(double seconds) => _inner.AddActiveSeconds(seconds);

    public void PromptOsuClosed(SessionClosePrompt prompt) => _inner.PromptOsuClosed(prompt);

    public void EndSession(SessionEnd end)
    {
        var sessionId = _inner.CurrentSessionId;
        _inner.EndSession(end);
        _store.Update(s => s.ActiveSession?.SessionId == sessionId
            ? s with { ActiveSession = null }
            : s);
    }
}
