using System.Diagnostics;
using Kumori.Tracking;
using Microsoft.Diagnostics.Runtime;

namespace Kumori.Native;

public sealed partial class StableClrReplayReader
{
    private enum StableGraphScanState { Pending, Found, Exhausted }

    private readonly record struct StableReplayListCandidate(
        ulong Address,
        int Count,
        double LastTime,
        string Time,
        string X,
        string Y,
        string Buttons,
        string Type,
        string Layout);

    private readonly record struct StableGraphScanResult(
        StableGraphScanState State,
        StableReplayListCandidate Candidate,
        string Diagnostic);

    public IReadOnlyList<LazerReplayFrame> ReadReplayFrames()
    {
        if (listAddress != 0)
        {
            try
            {
                return readNewFrames(listAddress);
            }
            catch
            {
                ScheduleRediscovery("stable replay list became unreadable");
                return [];
            }
        }

        if (heapDiscovery is not null)
            return advanceHeapDiscovery();
        if (graphDiscovery is not null)
            return advanceGraphDiscovery();
        var now = DateTimeOffset.UtcNow;
        if (rediscoveryDisabled)
        {
            if (!StableGraphDiscoveryPolicy.CanRetryNegativeCache(now, nextDiscoveryAt))
                return [];
            rediscoveryDisabled = false;
            discoveryAttempts = 0;
            LastDiagnostic = "stable replay discovery cooldown elapsed; bounded discovery resumed";
        }
        if (StableGraphDiscoveryPolicy.AttemptsExhausted(discoveryAttempts)
            || now < nextDiscoveryAt)
            return [];

        uint rulesetAddress;
        try { rulesetAddress = unchecked((uint)memory.ReadInt32((nint)(rulesetSlot + 4))); }
        catch
        {
            LastDiagnostic = "stable ruleset pointer could not be read";
            return [];
        }
        if (rulesetAddress < 0x10000)
        {
            LastDiagnostic = "waiting for stable gameplay ruleset";
            return [];
        }

        // The bridge process, CLR attachment and ruleset signature scan are
        // prewarmed while stable is in its menus. Stable's current replay list
        // is weak/native-owned and is not reliably reachable from the ruleset;
        // traversing that graph also races compacting GC. Refresh and scan the
        // typed heap directly on the isolated bridge worker instead.
        discoveryAttempts++;
        heapDiscovery = new StableHeapDiscovery(this, discoveryAttempts);
        LastDiagnostic = heapDiscovery.Diagnostic;
        return [];
    }

    private IReadOnlyList<LazerReplayFrame> advanceGraphDiscovery()
    {
        StableGraphDiscovery discovery = graphDiscovery!;
        StableGraphScanResult scan = discovery.ScanStep();
        LastDiagnostic = scan.Diagnostic;
        if (scan.State == StableGraphScanState.Pending)
            return [];

        graphDiscovery = null;
        discovery.Dispose();
        if (scan.State == StableGraphScanState.Exhausted)
        {
            // Some stable builds keep the active replay list behind a weak or
            // native-owned reference that is not reachable from the ruleset
            // roots. Fall back to a typed heap walk on the bridge's dedicated,
            // below-normal worker. The scan never runs on Kumori or osu!'s
            // gameplay threads and retains the same strict type/value checks.
            heapDiscovery = new StableHeapDiscovery(this, discoveryAttempts);
            LastDiagnostic += "; starting bounded typed heap fallback";
            return [];
        }

        return lockCandidate(scan.Candidate, "graph");
    }

    private IReadOnlyList<LazerReplayFrame> advanceHeapDiscovery()
    {
        StableHeapDiscovery discovery = heapDiscovery!;
        StableHeapScanResult scan = discovery.ScanStep();
        LastDiagnostic = scan.Diagnostic;
        if (scan.State == StableHeapScanState.Pending)
            return [];

        heapDiscovery = null;
        discovery.Dispose();
        if (scan.State == StableHeapScanState.Exhausted)
        {
            if (StableGraphDiscoveryPolicy.AttemptsExhausted(discoveryAttempts))
            {
                rediscoveryDisabled = true;
                nextDiscoveryAt = DateTimeOffset.UtcNow + StableGraphDiscoveryPolicy.RediscoveryCooldown;
                LastDiagnostic += "; cooling down before bounded rediscovery";
            }
            else
            {
                nextDiscoveryAt = DateTimeOffset.UtcNow + TimeSpan.FromMilliseconds(250);
            }
            return [];
        }

        return lockCandidate(scan.Candidate, "typed heap fallback");
    }

    private IReadOnlyList<LazerReplayFrame> lockCandidate(
        StableReplayListCandidate best,
        string source)
    {
        listAddress = best.Address;
        timeField = best.Time;
        xField = best.X;
        yField = best.Y;
        buttonsField = best.Buttons;
        emittedListCount = 0;
        unreadableFrameIndex = -1;
        unreadableFrameRetries = 0;
        lastEmittedTime = double.NegativeInfinity;
        LastDiagnostic = $"locked stable replay-frame layout via {source} (type={best.Type}; fields={best.Layout})";
        return readNewFrames(best.Address);
    }
}
