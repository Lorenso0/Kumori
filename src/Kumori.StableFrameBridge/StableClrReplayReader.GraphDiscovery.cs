using System.Diagnostics;
using Kumori.Tracking;
using Microsoft.Diagnostics.Runtime;

namespace Kumori.Native;

public sealed partial class StableClrReplayReader
{
    /// <summary>
    /// Resumable breadth-first CLR graph traversal. Both reference enumeration
    /// and replay-list validation retain their cursor across calls, so each
    /// bridge poll cooperatively stops at the same three-millisecond deadline.
    /// </summary>
    private sealed class StableGraphDiscovery : IDisposable
    {
        private readonly StableClrReplayReader reader;
        private readonly uint rulesetAddress;
        private readonly uint gameplayAddress;
        private readonly int attempt;
        private readonly LinkedList<(ulong Address, int Depth)> pending = new();
        private readonly HashSet<ulong> scheduled = new();
        private IEnumerator<ClrObject>? referenceEnumerator;
        private int referenceDepth;
        private StableListCandidateDiscovery? candidateDiscovery;
        private StableReplayListCandidate best;
        private int reachableSeen;
        private int listsSeen;
        private int populatedLists;
        private int frameShapedLists;
        private int metadataFilteredLists;
        private int metadataPrioritizedLists;
        private long referencesSeen;
        private bool objectCapReached;

        public StableGraphDiscovery(
            StableClrReplayReader reader,
            uint rulesetAddress,
            uint gameplayAddress,
            int attempt)
        {
            this.reader = reader;
            this.rulesetAddress = rulesetAddress;
            this.gameplayAddress = gameplayAddress;
            this.attempt = attempt;
            foreach (uint root in StableGraphDiscoveryPolicy.DiscoveryRoots(rulesetAddress, gameplayAddress))
            {
                pending.AddLast((root, 0));
                scheduled.Add(root);
            }
            Diagnostic = pendingDiagnostic();
        }

        public string Diagnostic { get; private set; }

        public StableGraphScanResult ScanStep()
        {
            var timer = Stopwatch.StartNew();
            int objectsThisPoll = 0;
            int referencesThisPoll = 0;
            int candidateOperationsThisPoll = 0;
            while (StableGraphDiscoveryPolicy.HasBudget(
                       timer.Elapsed,
                       objectsThisPoll,
                       referencesThisPoll,
                       candidateOperationsThisPoll))
            {
                if (candidateDiscovery is not null)
                {
                    StableListCandidateStep candidateStep = candidateDiscovery.ScanStep(
                        timer,
                        ref candidateOperationsThisPoll);
                    if (candidateStep == StableListCandidateStep.Pending)
                        return pendingResult();

                    StableListCandidateDiscovery completed = candidateDiscovery;
                    candidateDiscovery = null;
                    if (completed.Populated)
                        populatedLists++;
                    if (completed.FrameShaped)
                        frameShapedLists++;
                    if (candidateStep == StableListCandidateStep.Matched)
                    {
                        // Breadth-first traversal reaches the replay list closest
                        // to the live gameplay root first. Its shape validation is
                        // deliberately strict; locking it immediately avoids
                        // spending the first half of a short map searching for a
                        // merely larger duplicate elsewhere in the object graph.
                        best = completed.Result;
                        return complete();
                    }
                    beginReferences(completed.Object, completed.Depth);
                    continue;
                }

                if (referenceEnumerator is not null)
                {
                    if (scheduled.Count >= StableGraphDiscoveryPolicy.MaximumObjects)
                    {
                        objectCapReached = true;
                        endReferences();
                        continue;
                    }
                    bool moved;
                    ClrObject child = default;
                    try
                    {
                        moved = referenceEnumerator.MoveNext();
                        if (moved)
                            child = referenceEnumerator.Current;
                    }
                    catch
                    {
                        moved = false;
                    }
                    referencesThisPoll++;
                    referencesSeen++;
                    if (!moved)
                    {
                        endReferences();
                        continue;
                    }
                    if (child.IsNull || child.Address == 0 || scheduled.Contains(child.Address))
                        continue;
                    scheduled.Add(child.Address);
                    if (isStructurallyReplayList(child.Type))
                    {
                        // A List<T> exposes T through its _items field metadata.
                        // Put replay-frame-shaped lists at the front before the
                        // broad gameplay graph can bury them behind thousands of
                        // unrelated UI and ruleset objects.
                        pending.AddFirst((child.Address, referenceDepth));
                        metadataPrioritizedLists++;
                    }
                    else
                    {
                        pending.AddLast((child.Address, referenceDepth));
                    }
                    continue;
                }

                if (pending.Count == 0)
                    return complete();

                (ulong address, int depth) = pending.First!.Value;
                pending.RemoveFirst();
                objectsThisPoll++;
                ClrObject obj;
                try { obj = reader.runtime.Heap.GetObject(address); }
                catch { continue; }
                if (obj.IsNull || obj.Type is null)
                    continue;

                reachableSeen++;
                if (obj.Type.Name?.StartsWith("System.Collections.Generic.List<", StringComparison.Ordinal) == true)
                {
                    listsSeen++;
                    if (tryGetReplayFrameShape(obj.Type, out bool replayFrameShape) && !replayFrameShape)
                    {
                        // The generic element metadata is authoritative for the
                        // field kinds. Skip the expensive 64-item value sample
                        // for lists that cannot possibly contain replay frames,
                        // while still traversing them for nested references.
                        metadataFilteredLists++;
                        beginReferences(obj, depth);
                        continue;
                    }
                    candidateDiscovery = new StableListCandidateDiscovery(reader, obj, depth);
                    continue;
                }
                beginReferences(obj, depth);
            }
            return pendingResult();
        }

        private void beginReferences(ClrObject obj, int depth)
        {
            if (depth >= StableGraphDiscoveryPolicy.MaximumDepth || obj.Type is null || obj.Type.IsString)
                return;
            try
            {
                referenceEnumerator = obj
                    .EnumerateReferences(carefully: true, considerDependantHandles: false)
                    .GetEnumerator();
                referenceDepth = depth + 1;
            }
            catch
            {
                referenceEnumerator = null;
            }
        }

        private void endReferences()
        {
            try { referenceEnumerator?.Dispose(); }
            catch { }
            referenceEnumerator = null;
        }

        private StableGraphScanResult pendingResult()
        {
            Diagnostic = pendingDiagnostic();
            return new StableGraphScanResult(StableGraphScanState.Pending, default, Diagnostic);
        }

        private StableGraphScanResult complete()
        {
            string cap = objectCapReached ? $", object-cap={StableGraphDiscoveryPolicy.MaximumObjects}" : "";
            string metadata = $", metadata-filtered={metadataFilteredLists}, metadata-prioritized={metadataPrioritizedLists}";
            if (best.Address != 0)
            {
                Diagnostic = $"stable replay graph discovery completed in bounded slices (attempt {attempt}/{StableGraphDiscoveryPolicy.MaximumAttempts}, roots={scheduledRootDescription()}, reachable={reachableSeen}, references={referencesSeen}, lists={listsSeen}, populated={populatedLists}, frame-shaped={frameShapedLists}{metadata}{cap})";
                return new StableGraphScanResult(StableGraphScanState.Found, best, Diagnostic);
            }

            Diagnostic = $"attached to stable ruleset 0x{rulesetAddress:x8}, gameplay=0x{gameplayAddress:x8}; replay list not matched in bounded discovery attempt {attempt}/{StableGraphDiscoveryPolicy.MaximumAttempts} (roots={scheduledRootDescription()}, reachable={reachableSeen}, references={referencesSeen}, lists={listsSeen}, populated={populatedLists}, frame-shaped={frameShapedLists}{metadata}{cap})";
            return new StableGraphScanResult(StableGraphScanState.Exhausted, default, Diagnostic);
        }

        private string pendingDiagnostic()
            => $"stable replay graph discovery {attempt}/{StableGraphDiscoveryPolicy.MaximumAttempts} in progress (roots={scheduledRootDescription()}, reachable={reachableSeen}, queued={pending.Count}, references={referencesSeen}, lists={listsSeen}, populated={populatedLists}, frame-shaped={frameShapedLists}, metadata-filtered={metadataFilteredLists}, metadata-prioritized={metadataPrioritizedLists}; slice<={StableGraphDiscoveryPolicy.MaximumPollDuration.TotalMilliseconds:0}ms/{StableGraphDiscoveryPolicy.MaximumObjectsPerPoll} objects)";

        private string scheduledRootDescription()
            => gameplayAddress >= 0x10000 && gameplayAddress != rulesetAddress
                ? "ruleset+child"
                : "ruleset";

        public void Dispose()
        {
            endReferences();
            candidateDiscovery = null;
            pending.Clear();
            scheduled.Clear();
        }
    }
}
