using Kumori.Tracking;
using Xunit;

namespace Kumori.Tracking.Tests;

public class CompositeAttemptSinkTests
{
    [Fact]
    public void Finalize_UnwindsSinksInReverseOrder()
    {
        var calls = new List<string>();
        var first = new RecordingSink("first", calls);
        var second = new RecordingSink("second", calls);
        var composite = new CompositeAttemptSink(first, second);

        composite.StartAttempt(new AttemptStart());
        composite.Finalize(new AttemptFinalization("completed", "results_screen", new AttemptSnapshot(), 1));

        Assert.Equal(
            ["first.Start", "second.Start", "second.Finalize", "first.Finalize"],
            calls);
    }

    private sealed class RecordingSink : IAttemptSink
    {
        private readonly string _name;
        private readonly List<string> _calls;

        public RecordingSink(string name, List<string> calls)
        {
            _name = name;
            _calls = calls;
        }

        public void StartAttempt(AttemptStart start) => _calls.Add($"{_name}.Start");
        public void Checkpoint(AttemptCheckpoint checkpoint) => _calls.Add($"{_name}.Checkpoint");
        public void DiscardIfEmpty(AttemptDiscard discard) => _calls.Add($"{_name}.Discard");
        public void Finalize(AttemptFinalization finalization) => _calls.Add($"{_name}.Finalize");
    }
}
