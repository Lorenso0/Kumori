using System.Diagnostics;
using Kumori.Tracking;
using Microsoft.Diagnostics.Runtime;

namespace Kumori.Native;

public sealed partial class StableClrReplayReader
{
    private enum StableHeapScanState { Pending, Found, Exhausted }

    private readonly record struct StableHeapScanResult(
        StableHeapScanState State,
        StableReplayListCandidate Candidate,
        string Diagnostic);

    /// <summary>
    /// Background fallback for stable builds whose replay list is not strongly
    /// reachable from the ruleset object graph. Only List&lt;T&gt; instances whose
    /// T has the invariant replay-frame primitive shape are value-validated.
    /// </summary>
    private sealed class StableHeapDiscovery : IDisposable
    {
        private readonly StableClrReplayReader reader;
        private readonly int graphAttempt;
        private readonly CancellationTokenSource cancellation = new();
        private readonly Task<StableHeapScanResult> scanTask;
        private int objectsSeen;
        private int shapedListsSeen;
        private int populatedListsSeen;

        public StableHeapDiscovery(StableClrReplayReader reader, int graphAttempt)
        {
            this.reader = reader;
            this.graphAttempt = graphAttempt;
            scanTask = Task.Factory.StartNew(
                scan,
                cancellation.Token,
                TaskCreationOptions.LongRunning,
                TaskScheduler.Default);
            Diagnostic = pendingDiagnostic();
        }

        public string Diagnostic { get; private set; }

        public StableHeapScanResult ScanStep()
        {
            if (!scanTask.IsCompleted)
            {
                Diagnostic = pendingDiagnostic();
                return new StableHeapScanResult(StableHeapScanState.Pending, default, Diagnostic);
            }

            try { return scanTask.GetAwaiter().GetResult(); }
            catch (OperationCanceledException) { return exhausted(cancelled: true); }
            catch (Exception ex)
            {
                Diagnostic = $"stable typed heap fallback failed: {ex.GetType().Name}: {ex.Message}";
                return new StableHeapScanResult(StableHeapScanState.Exhausted, default, Diagnostic);
            }
        }

        private StableHeapScanResult scan()
        {
            try { Thread.CurrentThread.Priority = ThreadPriority.BelowNormal; }
            catch { }

            // The bridge attaches and prewarms in stable's menus. ClrMD caches
            // heap segments at that point, so a later whole-heap enumeration
            // otherwise walks the pre-attempt object map and cannot see the
            // replay list allocated for gameplay. Refresh only here, on the
            // bridge's isolated below-normal worker, after the bounded graph
            // search has failed and before no other CLR operation is active.
            reader.runtime.FlushCachedData();
            cancellation.Token.ThrowIfCancellationRequested();

            var matches = new List<StableReplayListCandidate>();
            var metadataRejectedListTypes = new HashSet<ulong>();
            using IEnumerator<ClrObject> objects = reader.runtime.Heap.EnumerateObjects().GetEnumerator();
            while (!cancellation.IsCancellationRequested
                   && objectsSeen < StableGraphDiscoveryPolicy.MaximumHeapObjects)
            {
                bool moved;
                ClrObject obj = default;
                try
                {
                    moved = objects.MoveNext();
                    if (moved)
                        obj = objects.Current;
                }
                catch
                {
                    moved = false;
                }
                if (!moved)
                    break;

                Interlocked.Increment(ref objectsSeen);
                ClrType? type;
                try { type = obj.IsNull ? null : obj.Type; }
                catch { continue; }
                if (!isGenericList(type)
                    || metadataRejectedListTypes.Contains(type!.MethodTable))
                    continue;

                Interlocked.Increment(ref shapedListsSeen);
                var candidate = new StableListCandidateDiscovery(reader, obj, depth: 0);
                StableListCandidateStep step;
                do
                {
                    cancellation.Token.ThrowIfCancellationRequested();
                    var timer = Stopwatch.StartNew();
                    int operations = 0;
                    step = candidate.ScanStep(timer, ref operations);
                }
                while (step == StableListCandidateStep.Pending);

                if (candidate.Populated)
                    Interlocked.Increment(ref populatedListsSeen);
                if (candidate.Populated && !candidate.MetadataShaped)
                    metadataRejectedListTypes.Add(type.MethodTable);
                if (step == StableListCandidateStep.Matched)
                    matches.Add(candidate.Result);
            }

            cancellation.Token.ThrowIfCancellationRequested();
            if (matches.Count == 0)
                return exhausted(cancelled: false);

            // Active recording is the replay-frame list that continues growing.
            // Recheck all exact-shape matches after a short observation window
            // instead of accidentally locking a cached, already-complete replay.
            Thread.Sleep(100);
            StableReplayListCandidate best = matches
                .Select(candidate => (Candidate: candidate, Growth: currentCount(candidate) - candidate.Count))
                .OrderByDescending(item => item.Growth > 0)
                .ThenByDescending(item => item.Growth)
                .ThenByDescending(item => item.Candidate.Address)
                .First().Candidate;
            Diagnostic = $"stable typed heap fallback matched replay list after {objectsSeen} objects (scan attempt {graphAttempt}/{StableGraphDiscoveryPolicy.MaximumAttempts}, shaped-lists={shapedListsSeen}, populated={populatedListsSeen}, matches={matches.Count})";
            return new StableHeapScanResult(StableHeapScanState.Found, best, Diagnostic);
        }

        private int currentCount(StableReplayListCandidate candidate)
        {
            try
            {
                ClrObject list = reader.runtime.Heap.GetObject(candidate.Address);
                return tryRead(list, "_size", out int size) ? size : candidate.Count;
            }
            catch { return candidate.Count; }
        }

        private StableHeapScanResult exhausted(bool cancelled)
        {
            string reason = cancelled
                ? "was cancelled"
                : objectsSeen >= StableGraphDiscoveryPolicy.MaximumHeapObjects
                    ? $"reached object cap {StableGraphDiscoveryPolicy.MaximumHeapObjects}"
                    : "did not find a populated replay list";
            Diagnostic = $"stable typed heap fallback {reason} (scan attempt {graphAttempt}/{StableGraphDiscoveryPolicy.MaximumAttempts}, objects={objectsSeen}, shaped-lists={shapedListsSeen}, populated={populatedListsSeen})";
            return new StableHeapScanResult(StableHeapScanState.Exhausted, default, Diagnostic);
        }

        private string pendingDiagnostic()
            => $"stable typed heap fallback in background (scan attempt {graphAttempt}/{StableGraphDiscoveryPolicy.MaximumAttempts}, objects={Volatile.Read(ref objectsSeen)}, shaped-lists={Volatile.Read(ref shapedListsSeen)}, populated={Volatile.Read(ref populatedListsSeen)})";

        public void Dispose()
        {
            try { cancellation.Cancel(); }
            catch { }
        }
    }
}
