using Serilog;

namespace Kumori.Tracking;

/// <summary>
/// Prevents an optional observer (such as replay capture) from blocking the
/// authoritative attempt store when that observer fails.
/// </summary>
public sealed class BestEffortAttemptSink : IAttemptSink
{
    private readonly IAttemptSink _inner;
    private readonly string _name;

    public BestEffortAttemptSink(IAttemptSink inner, string name)
    {
        _inner = inner;
        _name = name;
    }

    public void StartAttempt(AttemptStart start) => Execute("start", () => _inner.StartAttempt(start));
    public void Checkpoint(AttemptCheckpoint checkpoint) => Execute("checkpoint", () => _inner.Checkpoint(checkpoint));
    public void DiscardIfEmpty(AttemptDiscard discard) => Execute("discard", () => _inner.DiscardIfEmpty(discard));
    public void Finalize(AttemptFinalization finalization) => Execute("finalize", () => _inner.Finalize(finalization));

    private void Execute(string operation, Action action)
    {
        try
        {
            action();
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Optional attempt sink {Sink} failed during {Operation}", _name, operation);
        }
    }
}
