using System.Buffers;
using System.Buffers.Binary;
using System.IO.Compression;
using Kumori.Core.Models;
using Microsoft.Data.Sqlite;

namespace Kumori.Storage;

public sealed class MovementRepository
{
    private const int SampleSize = 4 + 4 + 4 + 4 + 2 + 2 + 1 + 1 + 4;
    public const int MaxCompressedChunkBytes = 8 * 1024 * 1024;
    public const int MaxSamplesPerChunk = 250_000;
    public const int MaxSamplesPerAttempt = 1_000_000;
    public const int MaxChunksPerAttempt = 4_096;
    private readonly SqliteConnectionFactory _factory;
    private readonly Action? _afterMetadataRead;

    public MovementRepository(SqliteConnectionFactory factory)
        : this(factory, afterMetadataRead: null)
    {
    }

    internal MovementRepository(SqliteConnectionFactory factory, Action? afterMetadataRead)
    {
        _factory = factory;
        _afterMetadataRead = afterMetadataRead;
    }

    public MovementMetadata? GetMetadata(long attemptId)
        => GetMetadata(attemptId, CancellationToken.None);

    public void ReplaceWithOfficialReplay(
        long attemptId,
        IReadOnlyList<MovementSample> samples,
        CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(attemptId);
        ArgumentNullException.ThrowIfNull(samples);
        if (samples.Count is <= 1 or > MaxSamplesPerAttempt)
            throw new InvalidDataException("Replay movement sample count is outside the supported range.");

        var capture = new MovementCaptureStore(_factory);
        capture.Start(attemptId);
        const int persistenceChunkSize = 25_000;
        for (int offset = 0; offset < samples.Count; offset += persistenceChunkSize)
        {
            cancellationToken.ThrowIfCancellationRequested();
            int count = Math.Min(persistenceChunkSize, samples.Count - offset);
            if (samples is MovementSample[] array)
            {
                capture.AddSamples(new ArraySegment<MovementSample>(array, offset, count));
            }
            else
            {
                var chunk = new MovementSample[count];
                for (int index = 0; index < count; index++)
                    chunk[index] = samples[offset + index];
                capture.AddSamples(chunk);
            }
        }
        capture.Complete(
            droppedSamples: 0,
            "official_replay",
            "{\"source\":\"official_replay\",\"recovered\":true}",
            cancellationToken);
    }

    public MovementMetadata? GetMetadata(long attemptId, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!_factory.DatabaseExists)
        {
            return null;
        }
        cancellationToken.ThrowIfCancellationRequested();
        using var con = _factory.Open();
        if (cancellationToken.CanBeCanceled)
        {
            con.DefaultTimeout = 1;
        }
        cancellationToken.ThrowIfCancellationRequested();
        using var cmd = con.CreateCommand();
        cmd.CommandText = """
            SELECT source, sample_rate, sample_count, dropped_samples,
                   replay_status, calibration_json
            FROM attempt_movement WHERE attempt_id = @id
            """;
        cmd.Parameters.AddWithValue("@id", attemptId);
        try
        {
            using var interruptRegistration = cancellationToken.Register(
                static state => SQLitePCL.raw.sqlite3_interrupt(((SqliteConnection)state!).Handle),
                con);
            cancellationToken.ThrowIfCancellationRequested();
            using var r = cmd.ExecuteReader();
            cancellationToken.ThrowIfCancellationRequested();
            if (!r.Read())
            {
                return null;
            }
            cancellationToken.ThrowIfCancellationRequested();
            return new MovementMetadata
            {
                Source = r.GetString(0),
                SampleRate = r.GetDouble(1),
                SampleCount = (int)r.GetInt64(2),
                DroppedSamples = (int)r.GetInt64(3),
                ReplayStatus = r.GetString(4),
                CalibrationJson = r.GetString(5),
            };
        }
        catch (SqliteException exception) when (cancellationToken.IsCancellationRequested)
        {
            throw new OperationCanceledException(
                "Movement metadata loading was interrupted by gameplay.",
                exception,
                cancellationToken);
        }
    }

    public IReadOnlyList<MovementSample> GetSamples(long attemptId)
        => GetSamples(attemptId, CancellationToken.None);

    public IReadOnlyList<MovementSample> GetSamples(long attemptId, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!_factory.DatabaseExists)
        {
            return Array.Empty<MovementSample>();
        }
        cancellationToken.ThrowIfCancellationRequested();
        using var con = _factory.Open();
        if (cancellationToken.CanBeCanceled)
        {
            con.DefaultTimeout = 1;
        }
        cancellationToken.ThrowIfCancellationRequested();
        // A deferred read transaction pins one SQLite snapshot without taking a
        // reserved writer lock. Movement capture can continue in WAL mode while
        // this historical read stays internally consistent.
        using var snapshot = con.BeginTransaction(
            System.Data.IsolationLevel.Serializable,
            deferred: true);
        int? expectedSampleCount;
        using (var metadata = con.CreateCommand())
        {
            metadata.Transaction = snapshot;
            metadata.CommandText = "SELECT sample_count FROM attempt_movement WHERE attempt_id = @id";
            metadata.Parameters.AddWithValue("@id", attemptId);
            var value = metadata.ExecuteScalar();
            var declaredSampleCount = value is null || value == DBNull.Value
                ? (long?)null
                : Convert.ToInt64(value);
            if (declaredSampleCount is < 0 or > MaxSamplesPerAttempt)
            {
                throw new InvalidDataException(
                    $"Movement metadata exceeds the {MaxSamplesPerAttempt:N0}-sample limit.");
            }
            expectedSampleCount = declaredSampleCount is { } declared
                ? (int)declared
                : null;
        }
        _afterMetadataRead?.Invoke();

        using var cmd = con.CreateCommand();
        cmd.Transaction = snapshot;
        cmd.CommandText = """
            SELECT sample_count, length(payload_zlib), payload_zlib FROM attempt_movement_chunks
            WHERE attempt_id = @id ORDER BY position
            """;
        cmd.Parameters.AddWithValue("@id", attemptId);
        var samples = new List<MovementSample>();
        var totalSampleCount = 0;
        var chunkCount = 0;
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            using var interruptRegistration = cancellationToken.Register(
                static state => SQLitePCL.raw.sqlite3_interrupt(((SqliteConnection)state!).Handle),
                con);
            using var r = cmd.ExecuteReader();
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!r.Read())
                {
                    break;
                }
                cancellationToken.ThrowIfCancellationRequested();
                if (++chunkCount > MaxChunksPerAttempt)
                {
                    throw new InvalidDataException(
                        $"Movement data exceeds the {MaxChunksPerAttempt:N0}-chunk limit.");
                }
                var declaredChunkSampleCount = r.GetInt64(0);
                if (declaredChunkSampleCount is < 0 or > MaxSamplesPerChunk)
                {
                    throw new InvalidDataException(
                        $"A movement chunk exceeds the {MaxSamplesPerChunk:N0}-sample limit.");
                }
                var chunkSampleCount = (int)declaredChunkSampleCount;
                var compressedLength = r.GetInt64(1);
                if (compressedLength is < 0 or > MaxCompressedChunkBytes)
                {
                    throw new InvalidDataException(
                        $"Compressed movement data exceeds the {MaxCompressedChunkBytes:N0}-byte limit.");
                }
                totalSampleCount = checked(totalSampleCount + chunkSampleCount);
                if (totalSampleCount > MaxSamplesPerAttempt
                    || expectedSampleCount is { } expected && totalSampleCount > expected)
                {
                    throw new InvalidDataException(
                        $"Movement data exceeds its declared or {MaxSamplesPerAttempt:N0}-sample limit.");
                }
                var payload = (byte[])r.GetValue(2);
                if (payload.LongLength != compressedLength)
                {
                    throw new InvalidDataException("Movement chunk length changed while it was being read.");
                }
                samples.AddRange(DecodeSamples(
                    payload,
                    chunkSampleCount,
                    cancellationToken));
            }
        }
        catch (SqliteException exception) when (cancellationToken.IsCancellationRequested)
        {
            throw new OperationCanceledException(
                "Movement loading was interrupted by gameplay.",
                exception,
                cancellationToken);
        }
        if (expectedSampleCount is { } declaredCount && totalSampleCount != declaredCount)
        {
            throw new InvalidDataException(
                $"Movement metadata declares {declaredCount:N0} samples, but chunks contain {totalSampleCount:N0}.");
        }
        snapshot.Commit();
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            samples.Sort(new CancellationAwareSampleComparer(cancellationToken));
        }
        catch (InvalidOperationException exception)
            when (exception.InnerException is OperationCanceledException cancellationException)
        {
            // Array.Sort wraps comparer exceptions. Preserve cancellation as
            // cancellation so the gameplay coordinator can stop the idle job.
            System.Runtime.ExceptionServices.ExceptionDispatchInfo
                .Capture(cancellationException)
                .Throw();
            throw;
        }
        cancellationToken.ThrowIfCancellationRequested();
        var result = samples.ToArray();
        cancellationToken.ThrowIfCancellationRequested();
        return result;
    }

    public static byte[] EncodeSamples(IReadOnlyList<MovementSample> samples)
    {
        ArgumentNullException.ThrowIfNull(samples);
        if (samples.Count > MaxSamplesPerChunk)
        {
            throw new ArgumentOutOfRangeException(
                nameof(samples),
                $"A movement chunk cannot contain more than {MaxSamplesPerChunk:N0} samples.");
        }

        using var output = new MemoryStream();
        // Deferred capture/recovery work favors short compression slices over
        // marginal size savings. A pooled buffer also keeps the raw chunk off
        // the large object heap.
        var rawLength = checked(samples.Count * SampleSize);
        var rented = ArrayPool<byte>.Shared.Rent(Math.Max(rawLength, 1));
        try
        {
            var raw = rented.AsSpan(0, rawLength);
            for (var i = 0; i < samples.Count; i++)
            {
                var sample = raw.Slice(i * SampleSize, SampleSize);
                BinaryPrimitives.WriteInt32LittleEndian(sample[0..4], checked((int)Math.Round(samples[i].MapTimeMs)));
                BinaryPrimitives.WriteSingleLittleEndian(sample[4..8], (float)samples[i].MonotonicMs);
                BinaryPrimitives.WriteSingleLittleEndian(sample[8..12], (float)samples[i].X);
                BinaryPrimitives.WriteSingleLittleEndian(sample[12..16], (float)samples[i].Y);
                BinaryPrimitives.WriteInt16LittleEndian(sample[16..18], samples[i].RawX);
                BinaryPrimitives.WriteInt16LittleEndian(sample[18..20], samples[i].RawY);
                sample[20] = (byte)samples[i].Buttons;
                sample[21] = (byte)samples[i].Flags;
                BinaryPrimitives.WriteUInt32LittleEndian(sample[22..26], samples[i].Pressure);
            }
            using (var zlib = new ZLibStream(output, CompressionLevel.Fastest, leaveOpen: true))
            {
                zlib.Write(raw);
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(rented);
        }
        if (output.Length > MaxCompressedChunkBytes)
        {
            throw new InvalidDataException(
                $"Compressed movement data exceeds the {MaxCompressedChunkBytes:N0}-byte limit.");
        }
        return output.ToArray();
    }

    public static IReadOnlyList<MovementSample> DecodeSamples(byte[] zlibBlob, int expectedSampleCount) =>
        DecodeSamples(zlibBlob, expectedSampleCount, CancellationToken.None);

    private static IReadOnlyList<MovementSample> DecodeSamples(
        byte[] zlibBlob,
        int expectedSampleCount,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (zlibBlob.Length > MaxCompressedChunkBytes)
        {
            throw new InvalidDataException(
                $"Compressed movement data exceeds the {MaxCompressedChunkBytes:N0}-byte limit.");
        }
        if (expectedSampleCount is < 0 or > MaxSamplesPerChunk)
        {
            throw new InvalidDataException(
                $"A movement chunk exceeds the {MaxSamplesPerChunk:N0}-sample limit.");
        }

        using var input = new MemoryStream(zlibBlob);
        using var zlib = new ZLibStream(input, CompressionMode.Decompress);
        var raw = new byte[checked(expectedSampleCount * SampleSize)];
        var offset = 0;
        while (offset < raw.Length)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var read = zlib.Read(raw, offset, raw.Length - offset);
            if (read == 0)
            {
                throw new InvalidDataException(
                    $"Movement chunk is truncated: expected {raw.Length:N0} decompressed bytes.");
            }
            offset += read;
        }
        if (zlib.ReadByte() != -1)
        {
            throw new InvalidDataException(
                $"Movement chunk exceeds its declared {expectedSampleCount:N0}-sample size.");
        }

        cancellationToken.ThrowIfCancellationRequested();
        var result = new List<MovementSample>(expectedSampleCount);
        for (offset = 0; offset < raw.Length; offset += SampleSize)
        {
            if ((offset & 0x1fff) == 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
            }
            var span = raw.AsSpan(offset, SampleSize);
            result.Add(new MovementSample
            {
                MapTimeMs = BinaryPrimitives.ReadInt32LittleEndian(span[0..4]),
                MonotonicMs = BinaryPrimitives.ReadSingleLittleEndian(span[4..8]),
                X = BinaryPrimitives.ReadSingleLittleEndian(span[8..12]),
                Y = BinaryPrimitives.ReadSingleLittleEndian(span[12..16]),
                RawX = BinaryPrimitives.ReadInt16LittleEndian(span[16..18]),
                RawY = BinaryPrimitives.ReadInt16LittleEndian(span[18..20]),
                Buttons = span[20],
                Flags = span[21],
                Pressure = BinaryPrimitives.ReadUInt32LittleEndian(span[22..26]),
            });
        }
        cancellationToken.ThrowIfCancellationRequested();
        return result;
    }

    private sealed class CancellationAwareSampleComparer(CancellationToken cancellationToken)
        : IComparer<MovementSample>
    {
        private int _comparisons;

        public int Compare(MovementSample? left, MovementSample? right)
        {
            if ((_comparisons++ & 0xff) == 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
            }

            if (ReferenceEquals(left, right)) return 0;
            if (left is null) return -1;
            if (right is null) return 1;
            var byMapTime = left.MapTimeMs.CompareTo(right.MapTimeMs);
            return byMapTime != 0
                ? byMapTime
                : left.MonotonicMs.CompareTo(right.MonotonicMs);
        }
    }
}
