using System.IO.Compression;
using System.Text.Json;

namespace Kumori.Storage;

/// <summary>
/// Decodes the Python tracker's compressed blobs:
/// zlib.compress(json.dumps(values).encode()) → double[].
/// ZLibStream understands the zlib wrapper (RFC 1950) that Python emits.
/// </summary>
public static class BlobCodec
{
    public const int MaxCompressedBytes = 4 * 1024 * 1024;
    public const int MaxDecompressedBytes = 16 * 1024 * 1024;
    public const int MaxOffsetCount = 1_000_000;

    public static byte[] EncodeOffsets(IReadOnlyList<double> offsets)
    {
        ArgumentNullException.ThrowIfNull(offsets);
        if (offsets.Count > MaxOffsetCount)
        {
            throw new ArgumentOutOfRangeException(
                nameof(offsets),
                $"Timing data cannot contain more than {MaxOffsetCount:N0} offsets.");
        }

        using var output = new MemoryStream();
        using (var zlib = new ZLibStream(output, CompressionLevel.SmallestSize, leaveOpen: true))
        using (var limited = new SizeLimitedWriteStream(zlib, MaxDecompressedBytes))
        {
            JsonSerializer.Serialize(limited, offsets);
        }
        if (output.Length > MaxCompressedBytes)
        {
            throw new InvalidDataException(
                $"Compressed timing data exceeds the {MaxCompressedBytes:N0}-byte limit.");
        }

        return output.ToArray();
    }

    public static double[] DecodeOffsets(byte[] zlibBlob)
    {
        ArgumentNullException.ThrowIfNull(zlibBlob);
        if (zlibBlob.Length > MaxCompressedBytes)
        {
            throw new InvalidDataException(
                $"Compressed timing data exceeds the {MaxCompressedBytes:N0}-byte limit.");
        }

        using var input = new MemoryStream(zlibBlob);
        using var zlib = new ZLibStream(input, CompressionMode.Decompress);
        using var output = new MemoryStream();
        var buffer = new byte[64 * 1024];
        while (true)
        {
            var read = zlib.Read(buffer, 0, buffer.Length);
            if (read == 0)
            {
                break;
            }
            if (output.Length + read > MaxDecompressedBytes)
            {
                throw new InvalidDataException(
                    $"Decompressed timing data exceeds the {MaxDecompressedBytes:N0}-byte limit.");
            }
            output.Write(buffer, 0, read);
        }

        var json = output.GetBuffer().AsSpan(0, checked((int)output.Length));
        ValidateOffsetCount(json);
        var values = JsonSerializer.Deserialize<double[]>(json)
            ?? Array.Empty<double>();
        if (values.Length > MaxOffsetCount)
        {
            throw new InvalidDataException(
                $"Timing data exceeds the {MaxOffsetCount:N0}-offset limit.");
        }
        return values;
    }

    private static void ValidateOffsetCount(ReadOnlySpan<byte> json)
    {
        var reader = new Utf8JsonReader(json);
        if (!reader.Read() || reader.TokenType != JsonTokenType.StartArray)
        {
            throw new JsonException("Timing data must be a JSON array.");
        }

        var count = 0;
        var closed = false;
        while (reader.Read())
        {
            if (reader.TokenType == JsonTokenType.EndArray)
            {
                closed = true;
                break;
            }
            if (reader.TokenType != JsonTokenType.Number)
            {
                throw new JsonException("Timing data may contain only numbers.");
            }
            if (++count > MaxOffsetCount)
            {
                throw new InvalidDataException(
                    $"Timing data exceeds the {MaxOffsetCount:N0}-offset limit.");
            }
        }
        if (!closed || reader.Read())
        {
            throw new JsonException("Timing data contains an incomplete or trailing JSON value.");
        }
    }

    private sealed class SizeLimitedWriteStream(Stream inner, long maximumBytes) : Stream
    {
        private long written;

        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => written;

        public override long Position
        {
            get => written;
            set => throw new NotSupportedException();
        }

        public override void Flush() => inner.Flush();

        public override void Write(byte[] buffer, int offset, int count)
        {
            EnsureCapacity(count);
            inner.Write(buffer, offset, count);
            written += count;
        }

        public override void Write(ReadOnlySpan<byte> buffer)
        {
            EnsureCapacity(buffer.Length);
            inner.Write(buffer);
            written += buffer.Length;
        }

        public override void WriteByte(byte value)
        {
            EnsureCapacity(1);
            inner.WriteByte(value);
            written++;
        }

        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                inner.Flush();
            }
            base.Dispose(disposing);
        }

        private void EnsureCapacity(int count)
        {
            if (count < 0 || written > maximumBytes - count)
            {
                throw new InvalidDataException(
                    $"Uncompressed timing data exceeds the {maximumBytes:N0}-byte limit.");
            }
        }
    }
}
