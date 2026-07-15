namespace Kumori.Native;

/// <summary>
/// Retains the small tail needed to find an aligned pointer split across two
/// adjacent ReadProcessMemory chunks. This source is shared with the x86 bridge.
/// </summary>
internal sealed class ProcessMemoryPointerSearch
{
    private readonly int pointerSize;
    private readonly byte[] needle;
    private readonly byte[] tail;
    private int tailCount;
    private long? nextAddress;

    internal ProcessMemoryPointerSearch(nint value, int pointerSize)
    {
        if (pointerSize is not (sizeof(int) or sizeof(long)))
            throw new ArgumentOutOfRangeException(nameof(pointerSize));

        this.pointerSize = pointerSize;
        needle = pointerSize == sizeof(long)
            ? BitConverter.GetBytes(value.ToInt64())
            : BitConverter.GetBytes(unchecked((int)value.ToInt64()));
        tail = new byte[pointerSize - 1];
    }

    internal bool TrySearch(nint chunkAddress, ReadOnlySpan<byte> chunk, out nint match)
    {
        var address = chunkAddress.ToInt64();
        if (nextAddress is not null && nextAddress != address)
            tailCount = 0;
        if (tailCount > 0 && nextAddress == address)
        {
            var prefixCount = Math.Min(pointerSize - 1, chunk.Length);
            Span<byte> boundary = stackalloc byte[(sizeof(long) - 1) * 2];
            tail.AsSpan(0, tailCount).CopyTo(boundary);
            chunk[..prefixCount].CopyTo(boundary[tailCount..]);
            var boundaryLength = tailCount + prefixCount;
            var boundaryBase = address - tailCount;
            var offset = FirstAlignedOffset(boundary[..boundaryLength], boundaryBase);
            while (offset >= 0)
            {
                // Only accept a candidate which actually crosses the seam.
                // Candidates wholly inside either chunk are checked there.
                if (offset < tailCount
                    && offset + pointerSize > tailCount
                    && boundary.Slice(offset, pointerSize).SequenceEqual(needle))
                {
                    match = (nint)(boundaryBase + offset);
                    RememberTail(chunk, address);
                    return true;
                }
                offset = NextAlignedOffset(boundaryLength, offset);
            }
        }

        var currentOffset = FirstAlignedOffset(chunk, address);
        while (currentOffset >= 0)
        {
            if (chunk.Slice(currentOffset, pointerSize).SequenceEqual(needle))
            {
                match = chunkAddress + currentOffset;
                RememberTail(chunk, address);
                return true;
            }
            currentOffset = NextAlignedOffset(chunk.Length, currentOffset);
        }

        RememberTail(chunk, address);
        match = 0;
        return false;
    }

    private int FirstAlignedOffset(ReadOnlySpan<byte> buffer, long baseAddress)
    {
        var remainder = (int)(baseAddress % pointerSize);
        if (remainder < 0)
            remainder += pointerSize;
        var offset = (pointerSize - remainder) % pointerSize;
        return offset <= buffer.Length - pointerSize ? offset : -1;
    }

    private int NextAlignedOffset(int bufferLength, int currentOffset)
    {
        var next = currentOffset + pointerSize;
        return next <= bufferLength - pointerSize ? next : -1;
    }

    private void RememberTail(ReadOnlySpan<byte> chunk, long address)
    {
        var retained = Math.Min(tail.Length, tailCount + chunk.Length);
        if (retained == 0)
        {
            tailCount = 0;
            nextAddress = address;
            return;
        }

        if (chunk.Length >= retained)
        {
            chunk[^retained..].CopyTo(tail);
        }
        else
        {
            Span<byte> combined = stackalloc byte[(sizeof(long) - 1) * 2];
            tail.AsSpan(0, tailCount).CopyTo(combined);
            chunk.CopyTo(combined[tailCount..]);
            combined.Slice(tailCount + chunk.Length - retained, retained).CopyTo(tail);
        }
        tailCount = retained;
        nextAddress = checked(address + chunk.Length);
    }
}
