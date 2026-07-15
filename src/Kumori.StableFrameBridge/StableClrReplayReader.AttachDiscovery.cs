using System.Diagnostics;
using Kumori.Tracking;
using Microsoft.Diagnostics.Runtime;

namespace Kumori.Native;

public sealed partial class StableClrReplayReader
{
    private enum StableAttachScanState { Pending, Found, Exhausted, Aborted }

    private readonly record struct StableAttachScanResult(StableAttachScanState State, uint RulesetSlot = 0);

    /// <summary>
    /// Carries the executable/JIT-region cursor between bridge polls. Each poll
    /// reads at most 64 KiB, validates at most eight candidates, and spends at
    /// most a few milliseconds scanning before yielding back to the bridge.
    /// </summary>
    private sealed class StableAttachDiscovery : IDisposable
    {
        private const int MaximumBytesPerStep = 64 * 1024;
        private const int MaximumCandidatesPerStep = 8;
        private const int MaximumCandidates = 128;
        private static readonly TimeSpan MaximumScanTimePerStep = TimeSpan.FromMilliseconds(3);
        private readonly Process process;
        private ProcessMemory? memory;
        private readonly IReadOnlyList<byte?> pattern;
        private readonly MemoryRegion[] regions;
        private readonly byte[] buffer;
        private int regionIndex;
        private long regionOffset;
        private int bufferedCount;
        private nint bufferedAddress;
        private int scanOffset;
        private int candidateCount;
        private long totalBytesRead;

        public StableAttachDiscovery(Process process, ProcessMemory memory, IReadOnlyList<byte?> pattern)
        {
            this.process = process;
            this.memory = memory;
            this.pattern = pattern;
            // Include every executable allocation, including private CLR JIT
            // regions; limiting discovery to image modules misses stable builds.
            regions = memory.Regions().Where(region => region.Executable).ToArray();
            buffer = System.Buffers.ArrayPool<byte>.Shared.Rent(MaximumBytesPerStep);
            Diagnostic = $"scanning stable executable/JIT regions for process {process.Id}";
        }

        public int ProcessId => process.Id;
        public string Diagnostic { get; private set; }

        public StableAttachScanResult ScanStep()
        {
            try { if (process.HasExited) return abort("stable process exited during attach discovery"); }
            catch { return abort("stable process became unavailable during attach discovery"); }
            if (memory is null)
                return abort("stable process memory is unavailable");

            var timer = Stopwatch.StartNew();
            int bytesThisStep = 0;
            int candidatesThisStep = 0;
            while (timer.Elapsed < MaximumScanTimePerStep
                   && bytesThisStep < MaximumBytesPerStep
                   && candidatesThisStep < MaximumCandidatesPerStep)
            {
                if (bufferedCount == 0)
                {
                    if (regionIndex >= regions.Length)
                    {
                        Diagnostic = $"stable ruleset signature unavailable after bounded scan ({totalBytesRead} bytes, {candidateCount} candidates)";
                        return new StableAttachScanResult(StableAttachScanState.Exhausted);
                    }

                    MemoryRegion region = regions[regionIndex];
                    long remaining = region.RegionSize - regionOffset;
                    if (remaining <= 0)
                    {
                        regionIndex++;
                        regionOffset = 0;
                        continue;
                    }

                    int readSize = (int)Math.Min(remaining, MaximumBytesPerStep - bytesThisStep);
                    try
                    {
                        memory.ReadBytes(region.BaseAddress + (nint)regionOffset, buffer, readSize);
                    }
                    catch
                    {
                        regionIndex++;
                        regionOffset = 0;
                        continue;
                    }
                    bufferedAddress = region.BaseAddress + (nint)regionOffset;
                    bufferedCount = readSize;
                    scanOffset = 0;
                    bytesThisStep += readSize;
                    totalBytesRead += readSize;
                }

                int lastStart = bufferedCount - pattern.Count;
                while (scanOffset <= lastStart)
                {
                    if ((scanOffset & 0xff) == 0 && timer.Elapsed >= MaximumScanTimePerStep)
                        return pending();
                    if (!matches(buffer, scanOffset, pattern))
                    {
                        scanOffset++;
                        continue;
                    }
                    if (candidatesThisStep >= MaximumCandidatesPerStep
                        || bytesThisStep + sizeof(int) > MaximumBytesPerStep)
                        return pending();
                    if (++candidateCount > MaximumCandidates)
                    {
                        Diagnostic = $"stable ruleset candidate cap reached after {totalBytesRead} bytes";
                        return new StableAttachScanResult(StableAttachScanState.Exhausted);
                    }

                    candidatesThisStep++;
                    bytesThisStep += sizeof(int);
                    nint signature = bufferedAddress + scanOffset++;
                    try
                    {
                        uint rulesetSlot = unchecked((uint)memory.ReadInt32(signature - 0x0b));
                        if (rulesetSlot >= 0x10000)
                            return new StableAttachScanResult(StableAttachScanState.Found, rulesetSlot);
                    }
                    catch { }
                }

                finishBufferedRegionChunk();
            }
            return pending();
        }

        private StableAttachScanResult pending()
        {
            Diagnostic = $"scanning stable executable/JIT regions {regionIndex + 1}/{Math.Max(1, regions.Length)} ({totalBytesRead} bytes, {candidateCount} candidates)";
            return new StableAttachScanResult(StableAttachScanState.Pending);
        }

        private StableAttachScanResult abort(string diagnostic)
        {
            Diagnostic = diagnostic;
            return new StableAttachScanResult(StableAttachScanState.Aborted);
        }

        private void finishBufferedRegionChunk()
        {
            MemoryRegion region = regions[regionIndex];
            if (regionOffset + bufferedCount >= region.RegionSize)
            {
                regionIndex++;
                regionOffset = 0;
            }
            else
            {
                int overlap = Math.Max(0, pattern.Count - 1);
                regionOffset += Math.Max(1, bufferedCount - overlap);
            }
            bufferedCount = 0;
            scanOffset = 0;
        }

        public ProcessMemory TakeMemory()
        {
            ProcessMemory result = memory ?? throw new ObjectDisposedException(nameof(StableAttachDiscovery));
            memory = null;
            return result;
        }

        public void Dispose()
        {
            System.Buffers.ArrayPool<byte>.Shared.Return(buffer);
            memory?.Dispose();
            memory = null;
            process.Dispose();
        }
    }

    private static bool matches(byte[] buffer, int offset, IReadOnlyList<byte?> pattern)
    {
        for (int index = 0; index < pattern.Count; index++)
        {
            if (pattern[index] is { } expected && buffer[offset + index] != expected)
                return false;
        }
        return true;
    }
}
