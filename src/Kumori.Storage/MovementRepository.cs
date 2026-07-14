using System.Buffers;
using System.Buffers.Binary;
using System.IO.Compression;
using Kumori.Core.Models;
using Microsoft.Data.Sqlite;

namespace Kumori.Storage;

public sealed class MovementRepository
{
    private const int SampleSize = 4 + 4 + 4 + 4 + 2 + 2 + 1 + 1 + 4;
    private readonly SqliteConnectionFactory _factory;

    public MovementRepository(SqliteConnectionFactory factory)
    {
        _factory = factory;
    }

    public MovementMetadata? GetMetadata(long attemptId)
        => GetMetadata(attemptId, CancellationToken.None);

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
        using var cmd = con.CreateCommand();
        cmd.CommandText = """
            SELECT payload_zlib FROM attempt_movement_chunks
            WHERE attempt_id = @id ORDER BY position
            """;
        cmd.Parameters.AddWithValue("@id", attemptId);
        var samples = new List<MovementSample>();
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
                samples.AddRange(DecodeSamples((byte[])r.GetValue(0), cancellationToken));
            }
        }
        catch (SqliteException exception) when (cancellationToken.IsCancellationRequested)
        {
            throw new OperationCanceledException(
                "Movement loading was interrupted by gameplay.",
                exception,
                cancellationToken);
        }
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
        return output.ToArray();
    }

    private static IReadOnlyList<MovementSample> DecodeSamples(
        byte[] zlibBlob,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var input = new MemoryStream(zlibBlob);
        using var zlib = new ZLibStream(input, CompressionMode.Decompress);
        using var output = new MemoryStream();
        var buffer = ArrayPool<byte>.Shared.Rent(64 * 1024);
        try
        {
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var read = zlib.Read(buffer, 0, buffer.Length);
                if (read == 0)
                {
                    break;
                }
                output.Write(buffer, 0, read);
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
        cancellationToken.ThrowIfCancellationRequested();
        var raw = output.ToArray();
        var result = new List<MovementSample>(raw.Length / SampleSize);
        for (var offset = 0; offset + SampleSize <= raw.Length; offset += SampleSize)
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
