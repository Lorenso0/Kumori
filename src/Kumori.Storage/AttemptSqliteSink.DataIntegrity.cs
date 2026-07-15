using Microsoft.Data.Sqlite;

namespace Kumori.Storage;

public sealed partial class AttemptSqliteSink
{
    private long ReserveSessionId() => ReserveDatabaseIdBlock(
        "session",
        "sessions",
        ref _nextReservedSessionId,
        ref _reservedSessionIdEndExclusive);

    private long ReserveAttemptId() => ReserveDatabaseIdBlock(
        "attempt",
        "attempts",
        ref _nextReservedAttemptId,
        ref _reservedAttemptIdEndExclusive);

    private long ReserveDatabaseIdBlock(
        string entity,
        string table,
        ref long nextReservedId,
        ref long reservedIdEndExclusive)
    {
        // Callers hold _gate, so consuming this sink's local reservation is serialized.
        if (nextReservedId < reservedIdEndExclusive)
        {
            return nextReservedId++;
        }

        // IDs are public before the asynchronous persistence queue runs, so reserve
        // a block durably in SQLite. The UPDATE is the first write in the transaction;
        // SQLite serializes it with reservations made by other sink instances. Gaps
        // after a restart are intentional; blocks never overlap.
        using var con = _factory.Open();
        using var tx = con.BeginTransaction();
        using var cmd = con.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = $"""
            UPDATE tracking_id_sequences
            SET next_id = MAX(
                next_id,
                COALESCE((SELECT MAX(id) + 1 FROM {table}), 1)
                ) + @block_size
            WHERE entity = @entity
            RETURNING next_id - @block_size
            """;
        cmd.Parameters.AddWithValue("@entity", entity);
        cmd.Parameters.AddWithValue("@block_size", IdReservationBlockSize);
        var result = cmd.ExecuteScalar()
            ?? throw new InvalidOperationException($"Tracking ID sequence '{entity}' is missing.");
        var firstId = Convert.ToInt64(result);
        var endExclusive = checked(firstId + IdReservationBlockSize);
        tx.Commit();

        nextReservedId = checked(firstId + 1);
        reservedIdEndExclusive = endExclusive;
        return firstId;
    }

    private static void UpdatePersonalBests(
        SqliteConnection con,
        SqliteTransaction tx,
        long attemptId)
    {
        using var rowCmd = con.CreateCommand();
        rowCmd.Transaction = tx;
        rowCmd.CommandText = """
            SELECT beatmap_id, mods_key, score, accuracy, pp, combo, misses
            FROM attempts WHERE id = @id
            """;
        rowCmd.Parameters.AddWithValue("@id", attemptId);
        using var reader = rowCmd.ExecuteReader();
        if (!reader.Read())
        {
            return;
        }

        var beatmapId = reader.GetInt64(0);
        var modsKey = reader.GetString(1);
        var metrics = new Dictionary<string, double>
        {
            ["score"] = reader.GetDouble(2),
            ["accuracy"] = reader.GetDouble(3),
            ["pp"] = reader.GetDouble(4),
            ["combo"] = reader.GetDouble(5),
            ["fewest_misses"] = reader.GetDouble(6),
        };
        reader.Close();

        foreach (var (metric, value) in metrics)
        {
            var lowerIsBetter = metric == "fewest_misses";
            using var existing = con.CreateCommand();
            existing.Transaction = tx;
            existing.CommandText = """
                SELECT value FROM personal_bests
                WHERE beatmap_id = @beatmap_id AND mods_key = @mods_key AND metric = @metric
                """;
            existing.Parameters.AddWithValue("@beatmap_id", beatmapId);
            existing.Parameters.AddWithValue("@mods_key", modsKey);
            existing.Parameters.AddWithValue("@metric", metric);
            var previous = existing.ExecuteScalar();
            var previousValue = previous is null || previous == DBNull.Value
                ? (double?)null
                : Convert.ToDouble(previous);
            var improved = previousValue is null
                || (lowerIsBetter ? value < previousValue.Value : value > previousValue.Value);
            if (!improved)
            {
                continue;
            }

            using (var improvement = con.CreateCommand())
            {
                improvement.Transaction = tx;
                improvement.CommandText = """
                    INSERT INTO attempt_improvements(attempt_id, metric, previous_value, new_value, delta)
                    VALUES(@attempt_id, @metric, @previous_value, @new_value, @delta)
                    ON CONFLICT(attempt_id, metric) DO UPDATE SET
                        previous_value = excluded.previous_value,
                        new_value = excluded.new_value,
                        delta = excluded.delta
                    """;
                improvement.Parameters.AddWithValue("@attempt_id", attemptId);
                improvement.Parameters.AddWithValue("@metric", metric);
                improvement.Parameters.AddWithValue("@previous_value", (object?)previousValue ?? DBNull.Value);
                improvement.Parameters.AddWithValue("@new_value", value);
                improvement.Parameters.AddWithValue(
                    "@delta",
                    previousValue is { } prior ? value - prior : DBNull.Value);
                improvement.ExecuteNonQuery();
            }

            using var best = con.CreateCommand();
            best.Transaction = tx;
            best.CommandText = """
                INSERT INTO personal_bests(beatmap_id, mods_key, metric, attempt_id, value)
                VALUES(@beatmap_id, @mods_key, @metric, @attempt_id, @value)
                ON CONFLICT(beatmap_id, mods_key, metric) DO UPDATE SET
                    attempt_id = excluded.attempt_id,
                    value = excluded.value
                """;
            best.Parameters.AddWithValue("@beatmap_id", beatmapId);
            best.Parameters.AddWithValue("@mods_key", modsKey);
            best.Parameters.AddWithValue("@metric", metric);
            best.Parameters.AddWithValue("@attempt_id", attemptId);
            best.Parameters.AddWithValue("@value", value);
            best.ExecuteNonQuery();
        }
    }
}
