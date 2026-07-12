using System.Diagnostics;
using Kumori.Tracking;
using Microsoft.Diagnostics.Runtime;
using Serilog;

namespace Kumori.Native;

/// <summary>
/// Reads osu!stable's own in-progress List&lt;ReplayFrame&gt; from the CLR heap.
/// Stable obfuscates its game types on every update, so discovery deliberately
/// uses the invariant replay-frame shape (time, X, Y and pButtonState) rather
/// than an obfuscated type name. No desktop cursor or keyboard state is used.
/// </summary>
public sealed class StableLiveReplayFrameSource : ILazerReplayFrameSource, ILazerReplayFrameSnapshotSource, IAttemptAwareReplayFrameSource, IFinalizableReplayFrameSource, IAsyncDisposable
{
    private readonly object gate = new();
    private readonly TimeSpan pollInterval;
    private readonly IReplayFrameStatusSink status;
    private AttemptStart? attempt;
    private Process? bridge;
    private List<LazerReplayFrame> latest = [];
    private int emittedCount;

    public StableLiveReplayFrameSource(TimeSpan? pollInterval = null, IReplayFrameStatusSink? status = null)
    {
        this.pollInterval = pollInterval ?? TimeSpan.FromMilliseconds(16);
        this.status = status ?? new DelegatingReplayFrameStatusSink();
    }

    public void StartAttempt(AttemptStart start)
    {
        lock (gate)
        {
            attempt = start;
            latest = [];
            emittedCount = 0;
            stopBridge();
        }
    }

    public void UpdateAttempt(AttemptSnapshot snapshot) { }

    public void EndAttempt()
    {
        lock (gate)
        {
            attempt = null;
            stopBridge();
        }
    }

    public IReadOnlyList<LazerReplayFrame> FinalizeAttemptSnapshot()
    {
        // Give the stdout reader two normal polling intervals to consume frames
        // already emitted by the x86 bridge before it is terminated.
        Thread.Sleep(TimeSpan.FromMilliseconds(Math.Max(32, pollInterval.TotalMilliseconds * 2)));
        lock (gate)
        {
            var snapshot = latest.ToArray();
            attempt = null;
            stopBridge();
            return snapshot;
        }
    }

    public IReadOnlyList<LazerReplayFrame> ReadCurrentFramesSnapshot()
    {
        lock (gate)
        {
            refreshLocked();
            return latest.ToArray();
        }
    }

    public async IAsyncEnumerable<LazerReplayFrame> ReadFramesAsync(
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            Process? currentBridge;
            AttemptStart? currentAttempt;
            lock (gate)
            {
                currentAttempt = attempt;
                if (currentAttempt is not null && (bridge is null || bridge.HasExited))
                {
                    stopBridge();
                    bridge = startBridge(currentAttempt.GameFolder);
                }
                currentBridge = bridge;
            }
            if (currentAttempt is null || currentBridge is null)
            {
                await Task.Delay(pollInterval, cancellationToken);
                continue;
            }

            string? line;
            try { line = await currentBridge.StandardOutput.ReadLineAsync(cancellationToken); }
            catch { line = null; }
            if (line is null)
            {
                lock (gate) { if (ReferenceEquals(bridge, currentBridge)) stopBridge(); }
                await Task.Delay(pollInterval, cancellationToken);
                continue;
            }
            if (!LazerReplayFrameJson.TryParse(line, out LazerReplayFrame frame))
                continue;
            lock (gate)
            {
                if (attempt is null) continue;
                if (latest.Count > 0 && (frame.Sequence ?? 0) <= (latest[^1].Sequence ?? 0))
                    latest.Clear();
                latest.Add(frame);
                emittedCount = latest.Count;
            }
            yield return frame;
        }
    }

    private void refreshLocked()
    {
        if (attempt is null)
            return;

        // Frames are populated by the x86 bridge reader in ReadFramesAsync.
    }

    private Process? startBridge(string? gameFolder)
    {
        string path = Path.Combine(AppContext.BaseDirectory, "stable-frame-bridge", "Kumori.StableFrameBridge.exe");
        if (!File.Exists(path))
            path = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "Kumori.StableFrameBridge", "bin", "Debug", "net8.0-windows10.0.17763.0", "win-x86", "Kumori.StableFrameBridge.exe"));
        if (!File.Exists(path))
        {
            status.Update(s => { s.State = "stable_bridge_missing"; s.Detail = $"Stable frame bridge was not found at {path}."; s.LastError = s.Detail; });
            return null;
        }
        var start = new ProcessStartInfo(path)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        if (!string.IsNullOrWhiteSpace(gameFolder)) start.ArgumentList.Add(gameFolder);
        Process? process = Process.Start(start);
        if (process is not null)
        {
            process.ErrorDataReceived += (_, e) =>
            {
                if (string.IsNullOrWhiteSpace(e.Data)) return;
                Log.Debug("Stable frame bridge: {Diagnostic}", e.Data);
                status.Update(s => { s.State = "stable_memory_diagnostic"; s.Detail = e.Data; s.LastError = null; });
            };
            process.BeginErrorReadLine();
        }
        return process;
    }

    private void stopBridge()
    {
        if (bridge is null) return;
        try { if (!bridge.HasExited) bridge.Kill(entireProcessTree: true); } catch { }
        bridge.Dispose();
        bridge = null;
    }

    public ValueTask DisposeAsync()
    {
        lock (gate) stopBridge();
        return ValueTask.CompletedTask;
    }
}

public sealed class StableClrReplayReader : IDisposable
{
    public static string LastAttachDiagnostic { get; private set; } = "not attempted";
    private readonly DataTarget target;
    private readonly ClrRuntime runtime;
    private readonly ProcessMemory memory;
    private readonly uint rulesetSlot;
    private ulong listAddress;
    private string? timeField;
    private string? xField;
    private string? yField;
    private string? buttonsField;
    private int unchangedCachedReads;
    private int cachedCount;
    private double cachedTailTime = double.NegativeInfinity;
    public string LastDiagnostic { get; private set; } = "not scanned";

    private StableClrReplayReader(DataTarget target, ClrRuntime runtime, ProcessMemory memory, uint rulesetSlot)
    {
        this.target = target;
        this.runtime = runtime;
        this.memory = memory;
        this.rulesetSlot = rulesetSlot;
    }

    public static StableClrReplayReader? TryAttach(string? expectedGameFolder)
    {
        Process? process = findStableProcess(expectedGameFolder);
        if (process is null)
        {
            LastAttachDiagnostic = "osu!stable process not found";
            return null;
        }

        using (process)
        {
            DataTarget? target = null;
            ProcessMemory? memory = null;
            try
            {
                memory = ProcessMemory.Open(process);
                byte?[] rulesetPattern = [0x7d, 0x15, 0xa1, null, null, null, null, 0x85, 0xc0];
                nint signature = memory.Regions()
                    .Where(region => region.Executable)
                    .Select(region => memory.FindPattern(region.BaseAddress, region.RegionSize, rulesetPattern))
                    .FirstOrDefault(address => address != 0);
                if (signature == 0)
                    throw new InvalidOperationException("Stable ruleset signature unavailable.");
                uint rulesetSlot = unchecked((uint)memory.ReadInt32(signature - 0x0b));
                if (rulesetSlot < 0x10000)
                    throw new InvalidOperationException("Stable ruleset slot unavailable.");
                target = DataTarget.AttachToProcess(process.Id, suspend: false);
                ClrInfo? clr = target.ClrVersions.FirstOrDefault();
                if (clr is null)
                {
                    target.Dispose();
                    return null;
                }
                LastAttachDiagnostic = $"attached via stable ruleset slot 0x{rulesetSlot:x8}";
                return new StableClrReplayReader(target, clr.CreateRuntime(), memory, rulesetSlot);
            }
            catch (Exception ex)
            {
                LastAttachDiagnostic = $"{ex.GetType().Name}: {ex.Message}";
                target?.Dispose();
                memory?.Dispose();
                return null;
            }
        }
    }

    public IReadOnlyList<LazerReplayFrame> ReadReplayFrames()
    {
        runtime.FlushCachedData();
        if (listAddress != 0)
        {
            try
            {
                IReadOnlyList<LazerReplayFrame> cached = readList(listAddress, 1_000_000);
                if (cached.Count > 0)
                {
                    double tailTime = cached[^1].MapTimeMs;
                    if (cached.Count != cachedCount || !tailTime.Equals(cachedTailTime))
                    {
                        cachedCount = cached.Count;
                        cachedTailTime = tailTime;
                        unchangedCachedReads = 0;
                        return cached;
                    }

                    // A stable update may replace the List object while leaving
                    // the previous list alive and non-empty. Periodically walk
                    // the live gameplay graph again when the cached tail stalls.
                    if (++unchangedCachedReads < 60)
                        return cached;
                    unchangedCachedReads = 0;
                }
            }
            catch
            {
                listAddress = 0;
            }
        }

        ulong previousListAddress = listAddress;
        double previousTailTime = cachedTailTime;
        var candidates = new List<(ulong Address, int Count, double LastTime, string Time, string X, string Y, string Buttons, string Type, string Layout)>();
        int listsSeen = 0;
        int populatedLists = 0;
        int frameShapedLists = 0;

        uint rulesetAddress;
        try { rulesetAddress = unchecked((uint)memory.ReadInt32((nint)(rulesetSlot + 4))); }
        catch
        {
            LastDiagnostic = "stable ruleset pointer could not be read";
            return [];
        }

        uint gameplayAddress = 0;
        try
        {
            uint candidate = unchecked((uint)memory.ReadInt32((nint)(rulesetAddress + 0x64)));
            ClrObject gameplay = runtime.Heap.GetObject(candidate);
            if (candidate >= 0x10000 && !gameplay.IsNull && gameplay.Type is not null)
                gameplayAddress = candidate;
        }
        catch { }

        IEnumerable<ClrObject> reachable = gameplayAddress != 0
            ? enumerateReachableObjects(gameplayAddress, maxDepth: 12, maxObjects: 150_000)
            : enumerateReachableObjects(rulesetAddress, maxDepth: 8, maxObjects: 150_000);

        int reachableSeen = 0;
        foreach (ClrObject obj in reachable)
        {
            reachableSeen++;
            if (obj.IsNull || obj.Type?.Name?.StartsWith("System.Collections.Generic.List<", StringComparison.Ordinal) != true)
                continue;
            listsSeen++;
            if (!tryRead(obj, "_size", out int size) || size < 2 || size > 1_000_000)
                continue;
            populatedLists++;
            if (!tryReadObject(obj, "_items", out ClrObject items) || items.IsNull || !items.IsArray)
                continue;

            ClrArray array = items.AsArray();
            ulong[] addresses;
            try { addresses = readSampleObjectAddresses(array, size, 64); }
            catch { continue; }
            ClrObject[] samples = addresses.Where(a => a != 0).Select(runtime.Heap.GetObject).Where(o => !o.IsNull).ToArray();
            if (samples.Length < 2 || !tryIdentifyFrameFields(samples, out string time, out string x, out string y, out string buttons))
                continue;
            frameShapedLists++;
            ClrType? frameType = samples[0].Type;
            string fieldLayout = frameType is null
                ? "unknown"
                : string.Join(",", frameType.Fields.Select(field => $"{field.ElementType}@{field.Offset}"));
            double lastTime;
            try { lastTime = runtime.Heap.GetObject(readObjectAddressAt(array, size - 1)).ReadField<int>(time); }
            catch { continue; }
            candidates.Add((obj.Address, size, lastTime, time, x, y, buttons, frameType?.Name ?? "unknown", fieldLayout));
        }

        var best = previousListAddress != 0
            ? candidates.Where(c => c.Address != previousListAddress && c.LastTime > previousTailTime)
                        .OrderByDescending(c => c.LastTime).ThenByDescending(c => c.Count).FirstOrDefault()
            : candidates.OrderByDescending(c => c.Count).FirstOrDefault();
        if (best.Address == 0)
        {
            if (previousListAddress != 0)
                return readList(previousListAddress, 1_000_000);
            LastDiagnostic = $"attached to stable ruleset 0x{rulesetAddress:x8}, gameplay={(gameplayAddress == 0 ? "unavailable" : $"0x{gameplayAddress:x8}")}; replay list not matched (reachable={reachableSeen}, lists={listsSeen}, populated={populatedLists}, frame-shaped={frameShapedLists})";
            return [];
        }

        listAddress = best.Address;
        timeField = best.Time;
        xField = best.X;
        yField = best.Y;
        buttonsField = best.Buttons;
        cachedCount = best.Count;
        cachedTailTime = best.LastTime;
        unchangedCachedReads = 0;
        LastDiagnostic = $"locked stable replay-frame layout (type={best.Type}; fields={best.Layout})";
        return readList(best.Address, best.Count);
    }

    private IEnumerable<ClrObject> enumerateReachableObjects(uint rootAddress, int maxDepth, int maxObjects)
    {
        if (rootAddress < 0x10000)
            yield break;

        var pending = new Queue<(ulong Address, int Depth)>();
        var visited = new HashSet<ulong>();
        pending.Enqueue((rootAddress, 0));
        while (pending.Count > 0 && visited.Count < maxObjects)
        {
            (ulong address, int depth) = pending.Dequeue();
            if (address == 0 || !visited.Add(address))
                continue;
            ClrObject obj;
            try { obj = runtime.Heap.GetObject(address); }
            catch { continue; }
            if (obj.IsNull || obj.Type is null)
                continue;
            yield return obj;
            if (depth >= maxDepth || obj.Type.IsString)
                continue;
            try
            {
                foreach (ClrObject child in obj.EnumerateReferences(carefully: true, considerDependantHandles: false))
                {
                    if (!child.IsNull && !visited.Contains(child.Address))
                        pending.Enqueue((child.Address, depth + 1));
                }
            }
            catch { }
        }
    }

    private IReadOnlyList<LazerReplayFrame> readList(ulong address, int expectedCount)
    {
        ClrObject list = runtime.Heap.GetObject(address);
        if (list.IsNull || !tryRead(list, "_size", out int size) || size < 1 || size > 1_000_000)
            return [];
        if (!tryReadObject(list, "_items", out ClrObject items) || items.IsNull || !items.IsArray)
            return [];

        ulong[] addresses = readObjectAddresses(items.AsArray(), Math.Min(size, expectedCount));
        var result = new List<LazerReplayFrame>(addresses.Length);
        long sequence = 0;
        foreach (ulong frameAddress in addresses)
        {
            if (frameAddress == 0) continue;
            ClrObject frame = runtime.Heap.GetObject(frameAddress);
            if (!tryRead(frame, timeField!, out int time)
                || !tryRead(frame, xField!, out float x)
                || !tryRead(frame, yField!, out float y)
                || !tryRead(frame, buttonsField!, out int buttons)
                || time < -200_000 || time > 86_400_000
                || x is < -10_000 or > 10_000 || y is < -10_000 or > 10_000
                || (buttons & ~0x1f) != 0)
                continue;

            result.Add(new LazerReplayFrame
            {
                MapTimeMs = time,
                MonotonicMs = time,
                X = x,
                Y = y,
                LeftPressed = (buttons & 0x05) != 0,
                RightPressed = (buttons & 0x0a) != 0,
                Focused = true,
                Sequence = ++sequence,
            });
        }
        return removeIsolatedTimestampOutliers(result);
    }

    private static IReadOnlyList<LazerReplayFrame> removeIsolatedTimestampOutliers(List<LazerReplayFrame> frames)
    {
        if (frames.Count < 3)
            return frames;
        var cleaned = new List<LazerReplayFrame>(frames.Count);
        for (int i = 0; i < frames.Count; i++)
        {
            bool isolated = i > 0 && i + 1 < frames.Count
                            && frames[i].MapTimeMs > frames[i - 1].MapTimeMs + 60_000
                            && frames[i].MapTimeMs > frames[i + 1].MapTimeMs + 60_000;
            if (!isolated)
                cleaned.Add(frames[i]);
        }
        return cleaned;
    }

    private ulong[] readObjectAddresses(ClrArray array, int count)
    {
        if (target.DataReader.PointerSize == 4)
            return (array.ReadValues<uint>(0, count) ?? []).Select(value => (ulong)value).ToArray();
        return array.ReadValues<ulong>(0, count) ?? [];
    }

    private ulong[] readSampleObjectAddresses(ClrArray array, int size, int maximum)
    {
        int count = Math.Min(size, maximum);
        if (count == size)
            return readObjectAddresses(array, count);

        var result = new ulong[count];
        for (int i = 0; i < count; i++)
        {
            int index = (int)Math.Round(i * (size - 1d) / (count - 1d));
            result[i] = target.DataReader.PointerSize == 4
                ? array.ReadValues<uint>(index, 1)?[0] ?? 0
                : array.ReadValues<ulong>(index, 1)?[0] ?? 0;
        }
        return result;
    }

    private ulong readObjectAddressAt(ClrArray array, int index)
        => target.DataReader.PointerSize == 4
            ? array.ReadValues<uint>(index, 1)?[0] ?? 0
            : array.ReadValues<ulong>(index, 1)?[0] ?? 0;

    private static bool tryIdentifyFrameFields(
        IReadOnlyList<ClrObject> samples,
        out string timeField,
        out string xField,
        out string yField,
        out string buttonsField)
    {
        timeField = xField = yField = buttonsField = "";
        ClrType? type = samples[0].Type;
        if (type is null || samples.Any(s => s.Type != type)) return false;
        var floats = type.Fields.Where(f => f.ElementType == ClrElementType.Float).ToArray();
        var ints = type.Fields.Where(f => f.ElementType is ClrElementType.Int32 or ClrElementType.UInt32 || f.Type?.IsEnum == true).ToArray();
        int booleans = type.Fields.Count(f => f.ElementType == ClrElementType.Boolean);
        // Stable's replay frame is a small leaf object: X/Y, input flags,
        // button-state enum and timestamp. Requiring a leaf-sized type avoids
        // mistaking large animation/gameplay objects with coincidental fields.
        if (type.Fields.Count() > 12 || floats.Length != 2 || ints.Length < 2 || booleans is < 4 or > 8)
            return false;

        foreach (ClrInstanceField time in ints)
        foreach (ClrInstanceField buttons in ints.Where(f => f != time))
        foreach (ClrInstanceField x in floats)
        foreach (ClrInstanceField y in floats.Where(f => f != x))
        {
            try
            {
                int[] times = samples.Select(s => s.ReadField<int>(time.Name!)).ToArray();
                int[] states = samples.Select(s => s.ReadField<int>(buttons.Name!)).ToArray();
                float[] xs = samples.Select(s => s.ReadField<float>(x.Name!)).ToArray();
                float[] ys = samples.Select(s => s.ReadField<float>(y.Name!)).ToArray();
                if (times.Zip(times.Skip(1)).Count(pair => pair.First <= pair.Second) < times.Length - 2) continue;
                if (times.Max() - times.Min() < 10) continue;
                if (states.Any(v => (v & ~0x1f) != 0)) continue;
                if (xs.Any(v => !float.IsFinite(v) || v is < -10_000 or > 10_000)) continue;
                if (ys.Any(v => !float.IsFinite(v) || v is < -10_000 or > 10_000)) continue;
                timeField = time.Name!; buttonsField = buttons.Name!; xField = x.Name!; yField = y.Name!;
                return true;
            }
            catch { }
        }
        return false;
    }

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
        foreach (Process process in Process.GetProcessesByName(name))
        {
            try
            {
                string? directory = Path.GetDirectoryName(process.MainModule?.FileName);
                bool expected = !string.IsNullOrWhiteSpace(expectedGameFolder) && directory is not null
                    && Path.GetFullPath(directory).Equals(Path.GetFullPath(expectedGameFolder), StringComparison.OrdinalIgnoreCase);
                bool stable = directory is not null && Directory.Exists(Path.Combine(directory, "Songs"));
                if (!process.HasExited && (expected || stable)) return process;
            }
            catch { }
            process.Dispose();
        }
        return null;
    }

    public void Dispose()
    {
        memory.Dispose();
        target.Dispose();
    }
}
