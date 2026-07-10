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
    {
        if (!_factory.DatabaseExists)
        {
            return null;
        }
        using var con = _factory.Open();
        using var cmd = con.CreateCommand();
        cmd.CommandText = """
            SELECT source, sample_rate, sample_count, dropped_samples,
                   replay_status, calibration_json
            FROM attempt_movement WHERE attempt_id = @id
            """;
        cmd.Parameters.AddWithValue("@id", attemptId);
        using var r = cmd.ExecuteReader();
        if (!r.Read())
        {
            return null;
        }
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

    public IReadOnlyList<MovementSample> GetSamples(long attemptId)
    {
        if (!_factory.DatabaseExists)
        {
            return Array.Empty<MovementSample>();
        }
        using var con = _factory.Open();
        using var cmd = con.CreateCommand();
        cmd.CommandText = """
            SELECT payload_zlib FROM attempt_movement_chunks
            WHERE attempt_id = @id ORDER BY position
            """;
        cmd.Parameters.AddWithValue("@id", attemptId);
        var samples = new List<MovementSample>();
        using var r = cmd.ExecuteReader();
        while (r.Read())
        {
            samples.AddRange(DecodeSamples((byte[])r.GetValue(0)));
        }
        return samples
            .OrderBy(s => s.MapTimeMs)
            .ThenBy(s => s.MonotonicMs)
            .ToArray();
    }

    public static byte[] EncodeSamples(IReadOnlyList<MovementSample> samples)
    {
        var raw = new byte[samples.Count * SampleSize];
        for (var i = 0; i < samples.Count; i++)
        {
            var span = raw.AsSpan(i * SampleSize, SampleSize);
            BinaryPrimitives.WriteInt32LittleEndian(span[0..4], checked((int)Math.Round(samples[i].MapTimeMs)));
            BinaryPrimitives.WriteSingleLittleEndian(span[4..8], (float)samples[i].MonotonicMs);
            BinaryPrimitives.WriteSingleLittleEndian(span[8..12], (float)samples[i].X);
            BinaryPrimitives.WriteSingleLittleEndian(span[12..16], (float)samples[i].Y);
            BinaryPrimitives.WriteInt16LittleEndian(span[16..18], samples[i].RawX);
            BinaryPrimitives.WriteInt16LittleEndian(span[18..20], samples[i].RawY);
            span[20] = (byte)samples[i].Buttons;
            span[21] = (byte)samples[i].Flags;
            BinaryPrimitives.WriteUInt32LittleEndian(span[22..26], samples[i].Pressure);
        }
        using var output = new MemoryStream();
        using (var zlib = new ZLibStream(output, CompressionLevel.SmallestSize, leaveOpen: true))
        {
            zlib.Write(raw);
        }
        return output.ToArray();
    }

    private static IReadOnlyList<MovementSample> DecodeSamples(byte[] zlibBlob)
    {
        using var input = new MemoryStream(zlibBlob);
        using var zlib = new ZLibStream(input, CompressionMode.Decompress);
        using var output = new MemoryStream();
        zlib.CopyTo(output);
        var raw = output.ToArray();
        var result = new List<MovementSample>(raw.Length / SampleSize);
        for (var offset = 0; offset + SampleSize <= raw.Length; offset += SampleSize)
        {
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
        return result;
    }
}
