using System.Diagnostics;
using Kumori.Tracking;
using Microsoft.Diagnostics.Runtime;

namespace Kumori.Native;

public sealed partial class StableClrReplayReader : IDisposable
{

    private static readonly object AttachGate = new();
    private static readonly byte?[] RulesetPattern = [0x7d, 0x15, 0xa1, null, null, null, null, 0x85, 0xc0];
    private static StableAttachDiscovery? attachDiscovery;
    private static int? unsupportedAttachProcessId;
    private static DateTimeOffset unsupportedAttachUntil;
    private static DateTimeOffset nextAttachSearchAt;
    public static string LastAttachDiagnostic { get; private set; } = "not attempted";
    public static TimeSpan AttachPollInterval
    {
        get
        {
            lock (AttachGate)
                return attachDiscovery is null ? TimeSpan.FromMilliseconds(250) : TimeSpan.FromMilliseconds(5);
        }
    }
    private readonly DataTarget target;
    private readonly ClrRuntime runtime;
    private readonly ProcessMemory memory;
    private readonly uint rulesetSlot;
    private ulong listAddress;
    private string? timeField;
    private string? xField;
    private string? yField;
    private string? buttonsField;
    private const int MaximumUnreadableFrameRetries = 3;
    private int discoveryAttempts;
    private DateTimeOffset nextDiscoveryAt;
    private bool rediscoveryDisabled;
    private StableGraphDiscovery? graphDiscovery;
    private StableHeapDiscovery? heapDiscovery;
    private int emittedListCount;
    private int unreadableFrameIndex = -1;
    private int unreadableFrameRetries;
    private double lastEmittedTime = double.NegativeInfinity;
    public string LastDiagnostic { get; private set; } = "not scanned";
    public TimeSpan PollInterval => StableGraphDiscoveryPolicy.PollInterval(
        graphDiscovery is not null || heapDiscovery is not null,
        listAddress != 0);

    private StableClrReplayReader(DataTarget target, ClrRuntime runtime, ProcessMemory memory, uint rulesetSlot)
    {
        this.target = target;
        this.runtime = runtime;
        this.memory = memory;
        this.rulesetSlot = rulesetSlot;
    }

    public static StableClrReplayReader? TryAttach(string? expectedGameFolder)
    {
        lock (AttachGate)
        {
            if (attachDiscovery is null)
            {
                var now = DateTimeOffset.UtcNow;
                if (now < nextAttachSearchAt)
                    return null;

                Process? process = findStableProcess(expectedGameFolder);
                if (process is null)
                {
                    LastAttachDiagnostic = "osu!stable process not found";
                    nextAttachSearchAt = now + TimeSpan.FromSeconds(1);
                    return null;
                }

                if (unsupportedAttachProcessId == process.Id && now < unsupportedAttachUntil)
                {
                    LastAttachDiagnostic = $"stable ruleset signature negative-cached for process {process.Id}";
                    process.Dispose();
                    nextAttachSearchAt = now + TimeSpan.FromSeconds(1);
                    return null;
                }
                if (unsupportedAttachProcessId != process.Id || now >= unsupportedAttachUntil)
                {
                    unsupportedAttachProcessId = null;
                    unsupportedAttachUntil = DateTimeOffset.MinValue;
                }

                ProcessMemory? memory = null;
                try
                {
                    memory = ProcessMemory.Open(process);
                    attachDiscovery = new StableAttachDiscovery(process, memory, RulesetPattern);
                    memory = null;
                }
                catch (Exception ex)
                {
                    memory?.Dispose();
                    process.Dispose();
                    LastAttachDiagnostic = $"{ex.GetType().Name}: {ex.Message}";
                    nextAttachSearchAt = now + TimeSpan.FromSeconds(1);
                    return null;
                }
            }

            StableAttachScanResult scan = attachDiscovery.ScanStep();
            LastAttachDiagnostic = attachDiscovery.Diagnostic;
            if (scan.State == StableAttachScanState.Pending)
                return null;

            int processId = attachDiscovery.ProcessId;
            if (scan.State == StableAttachScanState.Exhausted)
            {
                unsupportedAttachProcessId = processId;
                // Stable is managed: a method not yet JITted can introduce the
                // signature later, so this PID-level negative cache expires.
                unsupportedAttachUntil = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(30);
                attachDiscovery.Dispose();
                attachDiscovery = null;
                nextAttachSearchAt = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(1);
                return null;
            }
            if (scan.State == StableAttachScanState.Aborted)
            {
                attachDiscovery.Dispose();
                attachDiscovery = null;
                nextAttachSearchAt = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(1);
                return null;
            }

            ProcessMemory ownedMemory = attachDiscovery.TakeMemory();
            attachDiscovery.Dispose();
            attachDiscovery = null;
            DataTarget? target = null;
            try
            {
                target = DataTarget.AttachToProcess(processId, suspend: false);
                ClrInfo? clr = target.ClrVersions.FirstOrDefault();
                if (clr is null)
                    throw new InvalidOperationException("Stable CLR runtime unavailable.");
                ClrRuntime runtime = clr.CreateRuntime();
                // The bridge is normally attached in stable's menus. Flush the
                // DAC cache once as part of that prewarm. The normal gameplay
                // graph path never flushes it; only the isolated typed-heap
                // fallback refreshes after proving the cached graph insufficient.
                runtime.FlushCachedData();
                LastAttachDiagnostic = $"attached via stable ruleset slot 0x{scan.RulesetSlot:x8}";
                return new StableClrReplayReader(target, runtime, ownedMemory, scan.RulesetSlot);
            }
            catch (Exception ex)
            {
                LastAttachDiagnostic = $"{ex.GetType().Name}: {ex.Message}";
                target?.Dispose();
                ownedMemory.Dispose();
                nextAttachSearchAt = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(1);
                return null;
            }
        }
    }

    private void ScheduleRediscovery(string reason)
    {
        graphDiscovery?.Dispose();
        graphDiscovery = null;
        heapDiscovery?.Dispose();
        heapDiscovery = null;
        listAddress = 0;
        timeField = null;
        xField = null;
        yField = null;
        buttonsField = null;
        emittedListCount = 0;
        unreadableFrameIndex = -1;
        unreadableFrameRetries = 0;
        lastEmittedTime = double.NegativeInfinity;
        rediscoveryDisabled = true;
        nextDiscoveryAt = DateTimeOffset.UtcNow + StableGraphDiscoveryPolicy.RediscoveryCooldown;
        LastDiagnostic = $"{reason}; cooling down before bounded rediscovery";
    }

    private IReadOnlyList<LazerReplayFrame> readNewFrames(ulong address)
    {
        var timer = Stopwatch.StartNew();
        ClrObject list = runtime.Heap.GetObject(address);
        if (list.IsNull || !tryRead(list, "_size", out int size) || size < 0 || size > 1_000_000)
            return [];
        if (size < emittedListCount)
        {
            ScheduleRediscovery("stable replay list rotated");
            return [];
        }
        if (size == emittedListCount)
            return [];
        if (!tryReadObject(list, "_items", out ClrObject items) || items.IsNull || !items.IsArray)
            return [];

        var startIndex = emittedListCount;
        var batchCount = Math.Min(size - startIndex, StableGraphDiscoveryPolicy.MaximumTailFramesPerPoll);
        ulong[] addresses = readObjectAddresses(items.AsArray(), startIndex, batchCount);
        if (addresses.Length != batchCount)
            return [];
        var result = new List<LazerReplayFrame>(addresses.Length);
        var consumedCount = 0;
        for (var offset = 0; offset < addresses.Length; offset++)
        {
            // emittedListCount is the resumable cursor. Always allow one frame
            // to make progress, then yield once this poll's live-read budget is
            // consumed; the remaining tail is picked up on the next poll.
            if (offset > 0 && timer.Elapsed >= StableGraphDiscoveryPolicy.MaximumPollDuration)
                break;
            ulong frameAddress = addresses[offset];
            ClrObject frame = frameAddress == 0 ? default : runtime.Heap.GetObject(frameAddress);
            var time = 0;
            var x = 0f;
            var y = 0f;
            var buttons = 0;
            var readable = frameAddress != 0
                && !frame.IsNull
                && tryRead(frame, timeField!, out time)
                && tryRead(frame, xField!, out x)
                && tryRead(frame, yField!, out y)
                && tryRead(frame, buttonsField!, out buttons);
            if (!readable)
            {
                var absoluteIndex = startIndex + offset;
                if (unreadableFrameIndex != absoluteIndex)
                {
                    unreadableFrameIndex = absoluteIndex;
                    unreadableFrameRetries = 1;
                    break;
                }
                if (++unreadableFrameRetries < MaximumUnreadableFrameRetries)
                    break;

                // A permanently invalid slot must not stall the entire tail.
                unreadableFrameIndex = -1;
                unreadableFrameRetries = 0;
                consumedCount++;
                continue;
            }

            unreadableFrameIndex = -1;
            unreadableFrameRetries = 0;
            consumedCount++;
            if (time < -200_000 || time > 86_400_000
                || x is < -10_000 or > 10_000 || y is < -10_000 or > 10_000
                || (buttons & ~0x1f) != 0
                || time < lastEmittedTime)
                continue;

            lastEmittedTime = time;
            result.Add(new LazerReplayFrame
            {
                MapTimeMs = time,
                MonotonicMs = time,
                X = x,
                Y = y,
                LeftPressed = (buttons & 0x05) != 0,
                RightPressed = (buttons & 0x0a) != 0,
                Focused = true,
                Sequence = startIndex + offset + 1,
            });
        }
        emittedListCount = startIndex + consumedCount;
        return result;
    }

    private ulong[] readObjectAddresses(ClrArray array, int start, int count)
    {
        if (target.DataReader.PointerSize == 4)
            return (array.ReadValues<uint>(start, count) ?? []).Select(value => (ulong)value).ToArray();
        return array.ReadValues<ulong>(start, count) ?? [];
    }

    private ulong readObjectAddressAt(ClrArray array, int index)
        => target.DataReader.PointerSize == 4
            ? array.ReadValues<uint>(index, 1)?[0] ?? 0
            : array.ReadValues<ulong>(index, 1)?[0] ?? 0;

    private static bool tryRead<T>(ClrObject obj, string field, out T value) where T : unmanaged
    {
        try { value = obj.ReadField<T>(field); return true; }
        catch { value = default; return false; }
    }

    private static bool tryReadObject(ClrObject obj, string field, out ClrObject value)
    {
        try { value = obj.ReadObjectField(field); return true; }
        catch { value = default; return false; }
    }

    private static Process? findStableProcess(string? expectedGameFolder)
    {
        foreach (string name in new[] { "osu!", "osu" })
        {
            Process[] processes = Process.GetProcessesByName(name);
            Process? match = null;
            foreach (Process process in processes)
            {
                try
                {
                    string? directory = Path.GetDirectoryName(process.MainModule?.FileName);
                    bool expected = !string.IsNullOrWhiteSpace(expectedGameFolder) && directory is not null
                        && Path.GetFullPath(directory).Equals(Path.GetFullPath(expectedGameFolder), StringComparison.OrdinalIgnoreCase);
                    bool stable = directory is not null && Directory.Exists(Path.Combine(directory, "Songs"));
                    if (!process.HasExited && (expected || stable))
                    {
                        match = process;
                        break;
                    }
                }
                catch { }
            }
            foreach (Process process in processes)
                if (!ReferenceEquals(process, match)) process.Dispose();
            if (match is not null)
                return match;
        }
        return null;
    }

    public void Dispose()
    {
        graphDiscovery?.Dispose();
        graphDiscovery = null;
        heapDiscovery?.Dispose();
        heapDiscovery = null;
        memory.Dispose();
        target.Dispose();
    }
}
