namespace Kumori.Tracking;

public sealed class CompositeAttemptSink : IAttemptSink
{
    private readonly IReadOnlyList<IAttemptSink> _sinks;

    public CompositeAttemptSink(params IAttemptSink[] sinks)
    {
        _sinks = sinks;
    }

    public void StartAttempt(AttemptStart start)
    {
        foreach (var sink in _sinks)
        {
            sink.StartAttempt(start);
        }
    }

    public void Checkpoint(AttemptCheckpoint checkpoint)
    {
        foreach (var sink in _sinks)
        {
            sink.Checkpoint(checkpoint);
        }
    }

    public void DiscardIfEmpty(AttemptDiscard discard)
    {
        foreach (var sink in _sinks)
        {
            sink.DiscardIfEmpty(discard);
        }
    }

    public void Finalize(AttemptFinalization finalization)
    {
        for (var i = _sinks.Count - 1; i >= 0; i--)
        {
            _sinks[i].Finalize(finalization);
        }
    }
}
