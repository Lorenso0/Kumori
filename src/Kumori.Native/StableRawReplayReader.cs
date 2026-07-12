using System.Buffers.Binary;
using System.Diagnostics;
using Kumori.Tracking;

namespace Kumori.Native;

/// <summary>Locates stable's 32-bit managed List&lt;ReplayFrame&gt; without walking the CLR heap.</summary>
public sealed class StableRawReplayReader : IDisposable
{
    private readonly Process process;
    private readonly ProcessMemory memory;
    private readonly string snapshotPath;
    private uint list;
    private FrameLayout layout;
    private ListLayout listLayout;
    public string LastDiagnostic { get; private set; } = "not scanned";

    private StableRawReplayReader(Process process, ProcessMemory memory, string snapshotPath)
    {
        this.process = process;
        this.memory = memory;
        this.snapshotPath = snapshotPath;
    }

    public static StableRawReplayReader? TryAttach(string? gameFolder, string? snapshotPath = null)
    {
        Process? process = find(gameFolder);
        if (process is null) return null;
        try
        {
            snapshotPath ??= Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "Kumori", "runtime", "debug", "stable-memory-latest.bin");
            return new StableRawReplayReader(process, ProcessMemory.Open(process), snapshotPath);
        }
        catch { process.Dispose(); return null; }
    }

    public IReadOnlyList<LazerReplayFrame> ReadReplayFrames()
    {
        if (list != 0)
        {
            var cached = readList(list, listLayout, layout);
            if (cached.Count > 0) return cached;
            list = 0;
        }

        StableMemorySnapshot snapshot = StableMemorySnapshot.Capture(memory);
        Candidate? best = null;
        long listShapes = 0;
        long frameShapes = 0;
        foreach (SnapshotRegion region in snapshot.Regions)
        {
            byte[] bytes = region.Data;
            for (int i = 0; i <= bytes.Length - 20; i += 4)
            {
                uint items = u32(bytes, i + 4);
                if (items < 0x10000 || items > 0x7fff0000) continue;
                foreach (ListLayout candidateLayout in ListLayouts)
                {
                    int size = i32(bytes, i + candidateLayout.Size);
                    int version = i32(bytes, i + candidateLayout.Version);
                    if (size < 2 || size > 200_000 || version < 0 || version > 1_000_000) continue;
                    listShapes++;
                    uint address = region.BaseAddress + (uint)i;
                    Candidate? candidate = validateList(snapshot, address, items, size, candidateLayout);
                    if (candidate is null) continue;
                    frameShapes++;
                    if (best is null || candidate.Value.Score > best.Value.Score) best = candidate;
                }
            }
        }

        if (best is null)
        {
            snapshot.Save(snapshotPath);
            LastDiagnostic = $"no replay list matched; captured {snapshot.TotalBytes / 1048576d:0.0} MiB to {snapshotPath}; list candidates={listShapes}, frame candidates={frameShapes}";
            return [];
        }
        list = best.Value.List;
        listLayout = best.Value.ListLayout;
        layout = best.Value.Layout;
        LastDiagnostic = $"matched stable replay list with {best.Value.Count} frames";
        return readList(list, listLayout, layout);
    }

    public string CaptureDiagnosticSnapshot()
    {
        StableMemorySnapshot snapshot = StableMemorySnapshot.Capture(memory);
        snapshot.Save(snapshotPath);
        LastDiagnostic = $"captured {snapshot.TotalBytes / 1048576d:0.0} MiB of stable private memory for offline replay analysis: {snapshotPath}";
        return LastDiagnostic;
    }

    private static Candidate? validateList(StableMemorySnapshot snapshot, uint address, uint items, int size, ListLayout candidateLayout)
    {
        try
        {
            int capacity = snapshot.ReadInt32(items + 4);
            if (capacity < size || capacity > 1_000_000) return null;
            int[] indexes = [0, Math.Min(1, size - 1), size / 2, size - 1];
            uint[] refs = indexes.Select(index => unchecked((uint)snapshot.ReadInt32(items + 8u + (uint)(index * 4)))).ToArray();
            if (refs.Any(value => value < 0x10000 || value > 0x7fff0000)) return null;
            byte[][] objects = refs.Select(value => snapshot.ReadBytes(value, 96)).ToArray();
            FrameLayout? found = findLayout(objects);
            if (found is null) return null;
            int lastTime = i32(objects[^1], found.Value.Time);
            int score = size + Math.Max(0, lastTime / 100);
            return new Candidate(address, size, candidateLayout, found.Value, score);
        }
        catch { return null; }
    }

    private static FrameLayout? findLayout(IReadOnlyList<byte[]> objects)
    {
        // Current stable's ReplayFrame declaration is float X, float Y,
        // six booleans, pButtonState, int Time. Auto-layout places these at
        // 4, 8, 20 and 24 respectively on the 32-bit CLR.
        var stableLayout = new FrameLayout(24, 4, 8, 20);
        if (layoutMatches(objects, stableLayout)) return stableLayout;

        for (int time = 4; time <= 88; time += 4)
        {
            int[] times = objects.Select(o => i32(o, time)).ToArray();
            if (times[^1] < -1000 || times[^1] > 86_400_000 || times.Zip(times.Skip(1)).Any(p => p.First > p.Second)) continue;
            if (times[^1] - times[0] < 10) continue;
            for (int buttons = 4; buttons <= 88; buttons += 4)
            {
                if (buttons == time || objects.Any(o => (i32(o, buttons) & ~0x1f) != 0)) continue;
                for (int x = 4; x <= 88; x += 4)
                for (int y = 4; y <= 88; y += 4)
                {
                    if (x == y || x == time || y == time || x == buttons || y == buttons) continue;
                    float[] xs = objects.Select(o => f32(o, x)).ToArray();
                    float[] ys = objects.Select(o => f32(o, y)).ToArray();
                    if (xs.All(saneCoordinate) && ys.All(saneCoordinate)
                        && xs.Any(value => Math.Abs(value) > 1)
                        && ys.Any(value => Math.Abs(value) > 1))
                        return new FrameLayout(time, x, y, buttons);
                }
            }
        }
        return null;
    }

    private static bool layoutMatches(IReadOnlyList<byte[]> objects, FrameLayout layout)
    {
        int[] times = objects.Select(o => i32(o, layout.Time)).ToArray();
        float[] xs = objects.Select(o => f32(o, layout.X)).ToArray();
        float[] ys = objects.Select(o => f32(o, layout.Y)).ToArray();
        return times[^1] is >= -1000 and <= 86_400_000
               && !times.Zip(times.Skip(1)).Any(p => p.First > p.Second)
               && times[^1] - times[0] >= 10
               && objects.All(o => (i32(o, layout.Buttons) & ~0x1f) == 0)
               && xs.All(saneCoordinate) && ys.All(saneCoordinate)
               && xs.Any(value => Math.Abs(value) > 1) && ys.Any(value => Math.Abs(value) > 1);
    }

    private IReadOnlyList<LazerReplayFrame> readList(uint address, ListLayout listFields, FrameLayout fields)
    {
        try
        {
            uint items = unchecked((uint)memory.ReadInt32((nint)(address + 4)));
            int size = memory.ReadInt32((nint)(address + (uint)listFields.Size));
            if (size < 1 || size > 200_000) return [];
            var result = new List<LazerReplayFrame>(size);
            int rejected = 0;
            for (int i = 0; i < size; i++)
            {
                uint frame; byte[] data;
                try
                {
                    frame = unchecked((uint)memory.ReadInt32((nint)(items + 8u + (uint)(i * 4))));
                    data = memory.ReadBytes((nint)frame, 96);
                }
                catch { rejected++; continue; }
                int time = i32(data, fields.Time); float x = f32(data, fields.X); float y = f32(data, fields.Y); int buttons = i32(data, fields.Buttons);
                if (!saneCoordinate(x) || !saneCoordinate(y) || (buttons & ~0x1f) != 0
                    || (result.Count > 0 && time < result[^1].MapTimeMs))
                { rejected++; continue; }
                result.Add(new LazerReplayFrame { MapTimeMs = time, MonotonicMs = time, X = x, Y = y, LeftPressed = (buttons & 5) != 0, RightPressed = (buttons & 10) != 0, Focused = true, Sequence = i + 1 });
            }
            return result.Count >= 2 && rejected <= Math.Max(2, size / 5) ? result : [];
        }
        catch { return []; }
    }

    private static bool saneCoordinate(float value) => float.IsFinite(value) && value is > -10_000 and < 10_000;
    private static int i32(byte[] data, int offset) => BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(offset, 4));
    private static uint u32(byte[] data, int offset) => BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(offset, 4));
    private static float f32(byte[] data, int offset) => BitConverter.Int32BitsToSingle(i32(data, offset));

    private static Process? find(string? folder)
    {
        foreach (string name in new[] { "osu!", "osu" }) foreach (Process p in Process.GetProcessesByName(name))
        {
            try { string? dir = Path.GetDirectoryName(p.MainModule?.FileName); if (!p.HasExited && dir is not null && ((folder is not null && Path.GetFullPath(dir).Equals(Path.GetFullPath(folder), StringComparison.OrdinalIgnoreCase)) || Directory.Exists(Path.Combine(dir, "Songs")))) return p; }
            catch { }
            p.Dispose();
        }
        return null;
    }

    public void Dispose() { memory.Dispose(); process.Dispose(); }
    private static readonly ListLayout[] ListLayouts = [new(8, 12), new(12, 16)];
    private readonly record struct ListLayout(int Size, int Version);
    private readonly record struct FrameLayout(int Time, int X, int Y, int Buttons);
    private readonly record struct Candidate(uint List, int Count, ListLayout ListLayout, FrameLayout Layout, int Score);
}

internal sealed class StableMemorySnapshot
{
    private const long max_bytes = 384L * 1024 * 1024;
    public IReadOnlyList<SnapshotRegion> Regions { get; }
    public long TotalBytes { get; }
    private readonly Dictionary<uint, SnapshotRegion> pages;

    private StableMemorySnapshot(IReadOnlyList<SnapshotRegion> regions)
    {
        Regions = regions;
        TotalBytes = regions.Sum(region => (long)region.Data.Length);
        pages = new Dictionary<uint, SnapshotRegion>();
        foreach (SnapshotRegion region in regions)
        {
            uint first = region.BaseAddress >> 12;
            uint last = (region.BaseAddress + (uint)Math.Max(0, region.Data.Length - 1)) >> 12;
            for (uint page = first; page <= last; page++) pages[page] = region;
        }
    }

    public static StableMemorySnapshot Capture(ProcessMemory memory)
    {
        var regions = new List<SnapshotRegion>();
        long total = 0;
        foreach (MemoryRegion region in memory.Regions().Where(region => region.Writable && region.Type == 0x20000))
        {
            if (region.RegionSize <= 0 || region.RegionSize > max_bytes || total + region.RegionSize > max_bytes) continue;
            try
            {
                byte[] data = memory.ReadBytes(region.BaseAddress, checked((int)region.RegionSize));
                regions.Add(new SnapshotRegion(checked((uint)region.BaseAddress.ToInt64()), data));
                total += data.Length;
            }
            catch { }
        }
        return new StableMemorySnapshot(regions.OrderBy(region => region.BaseAddress).ToArray());
    }

    public int ReadInt32(uint address) => BinaryPrimitives.ReadInt32LittleEndian(resolve(address, 4));
    public byte[] ReadBytes(uint address, int count) => resolve(address, count).ToArray();

    private ReadOnlySpan<byte> resolve(uint address, int count)
    {
        if (pages.TryGetValue(address >> 12, out SnapshotRegion region))
        {
            ulong offset = (ulong)address - region.BaseAddress;
            if (address >= region.BaseAddress && offset + (uint)count <= (uint)region.Data.Length)
                return region.Data.AsSpan((int)offset, count);
        }
        throw new InvalidDataException($"Address 0x{address:x8} is outside the stable memory snapshot.");
    }

    public void Save(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        string temp = path + ".new";
        using (var stream = File.Create(temp))
        using (var writer = new BinaryWriter(stream))
        {
            writer.Write(0x4b534d53); // SMSK
            writer.Write(1);
            writer.Write(Regions.Count);
            foreach (SnapshotRegion region in Regions)
            {
                writer.Write(region.BaseAddress);
                writer.Write(region.Data.Length);
                writer.Write(region.Data);
            }
        }
        File.Move(temp, path, overwrite: true);
    }
}

internal readonly record struct SnapshotRegion(uint BaseAddress, byte[] Data);
