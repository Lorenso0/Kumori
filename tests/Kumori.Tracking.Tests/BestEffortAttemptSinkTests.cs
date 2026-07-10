using Kumori.Tracking;
using Xunit;

namespace Kumori.Tracking.Tests;

public class BestEffortAttemptSinkTests
{
    [Fact]
    public void Finalize_SwallowsOptionalSinkFailure()
    {
        var sink = new BestEffortAttemptSink(new ThrowingSink(), "test");

        var exception = Record.Exception(() =>
            sink.Finalize(new AttemptFinalization("completed", "results", new AttemptSnapshot(), 1)));

        Assert.Null(exception);
    }

    private sealed class ThrowingSink : IAttemptSink
    {
        public void StartAttempt(AttemptStart start) => throw new InvalidOperationException();
        public void Checkpoint(AttemptCheckpoint checkpoint) => throw new InvalidOperationException();
        public void DiscardIfEmpty(AttemptDiscard discard) => throw new InvalidOperationException();
        public void Finalize(AttemptFinalization finalization) => throw new InvalidOperationException();
    }
}
