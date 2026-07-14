using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace Kumori.Native;

public sealed class ProcessMemory : IDisposable
{
    private const int ProcessVmRead = 0x0010;
    private const int ProcessQueryInformation = 0x0400;
    private readonly nint _handle;

    private ProcessMemory(nint handle) => _handle = handle;

    public static ProcessMemory Open(Process process)
    {
        var handle = OpenProcess(ProcessVmRead | ProcessQueryInformation, false, process.Id);
        if (handle == 0) throw new Win32Exception(Marshal.GetLastWin32Error());
        return new ProcessMemory(handle);
    }

    public int ReadInt32(nint address) => BitConverter.ToInt32(Read(address, 4));
    public long ReadInt64(nint address) => BitConverter.ToInt64(Read(address, 8));
    public float ReadFloat(nint address) => BitConverter.ToSingle(Read(address, 4));
    public double ReadDouble(nint address) => BitConverter.ToDouble(Read(address, 8));
    public nint ReadIntPtr(nint address) => (nint)BitConverter.ToInt64(Read(address, 8));
    public byte[] ReadBytes(nint address, int count) => Read(address, count);

    public void ReadBytes(nint address, byte[] buffer, int count)
    {
        ArgumentNullException.ThrowIfNull(buffer);
        if ((uint)count > (uint)buffer.Length)
            throw new ArgumentOutOfRangeException(nameof(count));
        if (!ReadProcessMemory(_handle, address, buffer, count, out var bytesRead) || bytesRead != count)
            throw new Win32Exception(Marshal.GetLastWin32Error());
    }

    public IEnumerable<MemoryRegion> Regions()
    {
        nint address = 0x10000;
        while (VirtualQueryEx(_handle, address, out var info, (nuint)Marshal.SizeOf<MemoryBasicInformation>()) != 0)
        {
            if (info.State == 0x1000 && (info.Protect & 0x100) == 0 && (info.Protect & 0x01) == 0)
            {
                bool writable = (info.Protect & (0x04 | 0x08 | 0x40 | 0x80)) != 0;
                bool executable = (info.Protect & (0x10 | 0x20 | 0x40 | 0x80)) != 0;
                yield return new MemoryRegion(info.BaseAddress, (long)info.RegionSize, writable, executable, unchecked((uint)info.Type));
            }

            var next = info.BaseAddress.ToInt64() + (long)info.RegionSize;
            if (next <= address.ToInt64()) yield break;
            address = (nint)next;
        }
    }

    public nint FindPointer(nint baseAddress, long size, nint value)
    {
        const int chunkSize = 1024 * 1024;
        var needle = BitConverter.GetBytes(value.ToInt64());
        var remaining = size;
        var address = baseAddress;
        while (remaining > 0)
        {
            var readSize = (int)Math.Min(chunkSize, remaining);
            byte[] buffer;
            try { buffer = Read(address, readSize); }
            catch { return 0; }

            for (var i = 0; i <= buffer.Length - 8; i += 8)
            {
                if (buffer.AsSpan(i, 8).SequenceEqual(needle)) return address + i;
            }

            address += readSize;
            remaining -= readSize;
        }
        return 0;
    }

    public nint FindPattern(nint baseAddress, long size, IReadOnlyList<byte?> pattern)
        => FindPatterns(baseAddress, size, pattern, maxMatches: 1).FirstOrDefault();

    public IEnumerable<nint> FindPatterns(nint baseAddress, long size, IReadOnlyList<byte?> pattern, int maxMatches)
    {
        const int chunkSize = 1024 * 1024;
        var exactPattern = pattern.All(value => value.HasValue)
            ? pattern.Select(value => value!.Value).ToArray()
            : null;
        var remaining = size;
        var address = baseAddress;
        var overlap = Math.Max(0, pattern.Count - 1);
        byte[] previousTail = [];
        var matches = 0;

        while (remaining > 0)
        {
            var readSize = (int)Math.Min(chunkSize, remaining);
            byte[] current;
            try { current = Read(address, readSize); }
            catch { yield break; }

            var buffer = previousTail.Length == 0 ? current : previousTail.Concat(current).ToArray();
            var bufferBase = address - previousTail.Length;
            if (exactPattern is not null)
            {
                var searchStart = 0;
                while (searchStart <= buffer.Length - exactPattern.Length)
                {
                    var found = buffer.AsSpan(searchStart).IndexOf(exactPattern);
                    if (found < 0) break;
                    var index = searchStart + found;
                    yield return bufferBase + index;
                    if (++matches >= maxMatches) yield break;
                    searchStart = index + 1;
                }
            }
            else
            {
                for (var i = 0; i <= buffer.Length - pattern.Count; i++)
                {
                    if (!Matches(buffer, i, pattern)) continue;
                    yield return bufferBase + i;
                    if (++matches >= maxMatches) yield break;
                }
            }

            previousTail = current.Length > overlap ? current[^overlap..] : current;
            address += readSize;
            remaining -= readSize;
        }
    }

    private static bool Matches(byte[] buffer, int offset, IReadOnlyList<byte?> pattern)
    {
        for (var i = 0; i < pattern.Count; i++)
        {
            if (pattern[i] is { } expected && buffer[offset + i] != expected) return false;
        }
        return true;
    }

    private byte[] Read(nint address, int count)
    {
        var buffer = new byte[count];
        if (!ReadProcessMemory(_handle, address, buffer, count, out var bytesRead) || bytesRead != count)
            throw new Win32Exception(Marshal.GetLastWin32Error());
        return buffer;
    }

    public void Dispose()
    {
        if (_handle != 0) CloseHandle(_handle);
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern nint OpenProcess(int desiredAccess, bool inheritHandle, int processId);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool ReadProcessMemory(nint process, nint baseAddress, byte[] buffer, int size, out int bytesRead);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern int VirtualQueryEx(nint process, nint address, out MemoryBasicInformation buffer, nuint length);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(nint handle);
}

public readonly record struct MemoryRegion(nint BaseAddress, long RegionSize, bool Writable, bool Executable, uint Type);

[StructLayout(LayoutKind.Sequential)]
internal struct MemoryBasicInformation
{
    public nint BaseAddress;
    public nint AllocationBase;
    public int AllocationProtect;
    public nuint RegionSize;
    public int State;
    public int Protect;
    public int Type;
}
