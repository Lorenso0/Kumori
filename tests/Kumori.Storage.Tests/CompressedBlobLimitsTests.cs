using System.IO.Compression;
using System.Text;
using Kumori.Core.Models;
using Kumori.Storage;
using Microsoft.Data.Sqlite;
using Xunit;

namespace Kumori.Storage.Tests;

public sealed class CompressedBlobLimitsTests : IDisposable
{
    private readonly string root = Path.Combine(
        Path.GetTempPath(),
        $"kumori-compressed-limits-{Guid.NewGuid():N}");
    private readonly string databasePath;

    public CompressedBlobLimitsTests()
    {
        Directory.CreateDirectory(root);
        databasePath = Path.Combine(root, "tracking.sqlite3");
    }

    [Fact]
    public void DecodeOffsetsRejectsOversizedCompressedInputBeforeDecompression()
    {
        var blob = new byte[BlobCodec.MaxCompressedBytes + 1];

        Assert.Throws<InvalidDataException>(() => BlobCodec.DecodeOffsets(blob));
    }

    [Fact]
    public void DecodeOffsetsRejectsSmallBlobWithOversizedDecompressedOutput()
    {
        var blob = Compress(new byte[BlobCodec.MaxDecompressedBytes + 1]);
        Assert.True(blob.Length < BlobCodec.MaxCompressedBytes);

        Assert.Throws<InvalidDataException>(() => BlobCodec.DecodeOffsets(blob));
    }

    [Fact]
    public void DecodeOffsetsRejectsTooManyRecordsWithinByteLimit()
    {
        byte[] blob;
        using (var output = new MemoryStream())
        {
            using (var zlib = new ZLibStream(output, CompressionLevel.Fastest, leaveOpen: true))
            using (var writer = new StreamWriter(
                       zlib,
                       new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
                       bufferSize: 64 * 1024,
                       leaveOpen: true))
            {
                writer.Write('[');
                for (var index = 0; index <= BlobCodec.MaxOffsetCount; index++)
                {
                    if (index > 0)
                    {
                        writer.Write(',');
                    }
                    writer.Write('0');
                }
                writer.Write(']');
            }
            blob = output.ToArray();
        }

        Assert.Throws<InvalidDataException>(() => BlobCodec.DecodeOffsets(blob));
    }

    [Fact]
    public void MovementRejectsMetadataAboveAttemptRecordLimit()
    {
        var factory = CreateAttempt();
        using (var connection = factory.Open())
        using (var command = connection.CreateCommand())
        {
            command.CommandText = """
                INSERT INTO attempt_movement(
                    attempt_id, source, sample_rate, sample_count, dropped_samples,
                    replay_status, calibration_json, captured_at)
                VALUES(1, 'test', 60, @count, 0, 'not_checked', '{}', '2026-07-15T10:00:00Z')
                """;
            command.Parameters.AddWithValue("@count", MovementRepository.MaxSamplesPerAttempt + 1L);
            command.ExecuteNonQuery();
        }

        Assert.Throws<InvalidDataException>(() => new MovementRepository(factory).GetSamples(1));
    }

    [Fact]
    public void MovementRejectsChunkThatExpandsPastDeclaredRecordCount()
    {
        var factory = CreateAttempt();
        var payload = MovementRepository.EncodeSamples([
            new MovementSample { MapTimeMs = 1 },
            new MovementSample { MapTimeMs = 2 },
        ]);
        using (var connection = factory.Open())
        using (var command = connection.CreateCommand())
        {
            command.CommandText = """
                INSERT INTO attempt_movement(
                    attempt_id, source, sample_rate, sample_count, dropped_samples,
                    replay_status, calibration_json, captured_at)
                VALUES(1, 'test', 60, 1, 0, 'not_checked', '{}', '2026-07-15T10:00:00Z');
                INSERT INTO attempt_movement_chunks(
                    attempt_id, position, first_map_time_ms, last_map_time_ms, sample_count, payload_zlib)
                VALUES(1, 0, 1, 2, 1, @payload);
                """;
            command.Parameters.Add("@payload", SqliteType.Blob).Value = payload;
            command.ExecuteNonQuery();
        }

        Assert.Throws<InvalidDataException>(() => new MovementRepository(factory).GetSamples(1));
    }

    [Fact]
    public void MovementRejectsOversizedSqliteBlobUsingItsDeclaredLength()
    {
        var factory = CreateAttempt();
        using (var connection = factory.Open())
        using (var command = connection.CreateCommand())
        {
            command.CommandText = """
                INSERT INTO attempt_movement(
                    attempt_id, source, sample_rate, sample_count, dropped_samples,
                    replay_status, calibration_json, captured_at)
                VALUES(1, 'test', 60, 1, 0, 'not_checked', '{}', '2026-07-15T10:00:00Z');
                INSERT INTO attempt_movement_chunks(
                    attempt_id, position, first_map_time_ms, last_map_time_ms, sample_count, payload_zlib)
                VALUES(1, 0, 1, 1, 1, zeroblob(@size));
                """;
            command.Parameters.AddWithValue("@size", MovementRepository.MaxCompressedChunkBytes + 1);
            command.ExecuteNonQuery();
        }

        Assert.Throws<InvalidDataException>(() => new MovementRepository(factory).GetSamples(1));
    }

    [Fact]
    public void MovementReadsMetadataAndChunksFromOneSnapshot()
    {
        var factory = CreateAttempt();
        var initialPayload = MovementRepository.EncodeSamples([
            new MovementSample { MapTimeMs = 1 },
        ]);
        var replacementPayload = MovementRepository.EncodeSamples([
            new MovementSample { MapTimeMs = 2 },
            new MovementSample { MapTimeMs = 3 },
        ]);
        using (var connection = factory.Open())
        using (var command = connection.CreateCommand())
        {
            command.CommandText = """
                INSERT INTO attempt_movement(
                    attempt_id, source, sample_rate, sample_count, dropped_samples,
                    replay_status, calibration_json, captured_at)
                VALUES(1, 'test', 60, 1, 0, 'not_checked', '{}', '2026-07-15T10:00:00Z');
                INSERT INTO attempt_movement_chunks(
                    attempt_id, position, first_map_time_ms, last_map_time_ms, sample_count, payload_zlib)
                VALUES(1, 0, 1, 1, 1, @payload);
                """;
            command.Parameters.Add("@payload", SqliteType.Blob).Value = initialPayload;
            command.ExecuteNonQuery();
        }

        var repository = new MovementRepository(factory, () =>
        {
            using var writer = factory.Open();
            using var transaction = writer.BeginTransaction();
            using var replace = writer.CreateCommand();
            replace.Transaction = transaction;
            replace.CommandText = """
                UPDATE attempt_movement SET sample_count = 2 WHERE attempt_id = 1;
                DELETE FROM attempt_movement_chunks WHERE attempt_id = 1;
                INSERT INTO attempt_movement_chunks(
                    attempt_id, position, first_map_time_ms, last_map_time_ms, sample_count, payload_zlib)
                VALUES(1, 0, 2, 3, 2, @payload);
                """;
            replace.Parameters.Add("@payload", SqliteType.Blob).Value = replacementPayload;
            replace.ExecuteNonQuery();
            transaction.Commit();
        });

        var snapshot = repository.GetSamples(1);

        Assert.Single(snapshot);
        Assert.Equal(1, snapshot[0].MapTimeMs);
        Assert.Equal(2, new MovementRepository(factory).GetSamples(1).Count);
    }

    private SqliteConnectionFactory CreateAttempt()
    {
        var factory = new SqliteConnectionFactory(databasePath, readOnly: false);
        _ = new AttemptSqliteSink(factory, (_, work) => work(CancellationToken.None));
        using var connection = factory.Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO sessions(id, started_at, ended_at)
            VALUES(1, '2026-07-15T10:00:00Z', '2026-07-15T10:10:00Z');
            INSERT INTO beatmaps(id, identity) VALUES(1, 'map-a');
            INSERT INTO attempts(id, session_id, beatmap_id, started_at, ended_at, outcome)
            VALUES(1, 1, 1, '2026-07-15T10:00:00Z', '2026-07-15T10:10:00Z', 'completed');
            """;
        command.ExecuteNonQuery();
        return factory;
    }

    private static byte[] Compress(byte[] bytes)
    {
        using var output = new MemoryStream();
        using (var zlib = new ZLibStream(output, CompressionLevel.SmallestSize, leaveOpen: true))
        {
            zlib.Write(bytes);
        }
        return output.ToArray();
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(root, recursive: true); } catch { }
    }
}
