using System.Globalization;
using System.Text;
using System.Text.Json;
using Kumori.Core;
using Microsoft.Data.Sqlite;

namespace Kumori.Storage;

public sealed class TrackingMaintenanceRepository
{
    private readonly SqliteConnectionFactory _factory;

    public TrackingMaintenanceRepository(SqliteConnectionFactory factory)
    {
        _factory = factory;
    }

    public int EndOpenSessions()
    {
        if (!_factory.DatabaseExists)
        {
            return 0;
        }

        using var con = OpenWriteConnection();
        using var cmd = con.CreateCommand();
        cmd.CommandText = """
            UPDATE sessions
            SET ended_at = @ended_at, ended_at_utc_ms = @ended_at_utc_ms, interrupted = 0
            WHERE ended_at IS NULL
            """;
        var endedAt = DateTimeOffset.UtcNow;
        cmd.Parameters.AddWithValue("@ended_at", endedAt.ToString("O"));
        cmd.Parameters.AddWithValue("@ended_at_utc_ms", endedAt.ToUnixTimeMilliseconds());
        return cmd.ExecuteNonQuery();
    }

    public int DeleteAttempt(long attemptId)
    {
        using var con = OpenWriteConnection();
        using var cmd = con.CreateCommand();
        cmd.CommandText = "DELETE FROM attempts WHERE id = @id";
        cmd.Parameters.AddWithValue("@id", attemptId);
        var deleted = cmd.ExecuteNonQuery();
        RebuildPersonalBests(con);
        PruneUnusedBeatmaps(con);
        return deleted;
    }

    public int DeleteSession(long sessionId)
    {
        using var con = OpenWriteConnection();
        using var cmd = con.CreateCommand();
        cmd.CommandText = "DELETE FROM sessions WHERE id = @id";
        cmd.Parameters.AddWithValue("@id", sessionId);
        var deleted = cmd.ExecuteNonQuery();
        RebuildPersonalBests(con);
        PruneUnusedBeatmaps(con);
        return deleted;
    }

    public int DeleteBefore(string isoTimestamp)
    {
        using var con = OpenWriteConnection();
        using var tx = con.BeginTransaction();
        using var cmd = con.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = "DELETE FROM sessions WHERE started_at < @before";
        cmd.Parameters.AddWithValue("@before", isoTimestamp);
        var deleted = cmd.ExecuteNonQuery();
        DeleteIfExists(con, tx, "profile_snapshots", "captured_at < @before", ("@before", isoTimestamp));
        tx.Commit();
        RebuildPersonalBests(con);
        PruneUnusedBeatmaps(con);
        return deleted;
    }

    public int DeleteAll()
    {
        using var con = OpenWriteConnection();
        using var tx = con.BeginTransaction();
        var deleted = DeleteAllFrom(con, tx, "sessions");
        DeleteAllFrom(con, tx, "profile_snapshots");
        DeleteAllFrom(con, tx, "attempts");
        DeleteAllFrom(con, tx, "personal_bests");
        DeleteAllFrom(con, tx, "attempt_improvements");
        DeleteAllFrom(con, tx, "beatmaps");
        tx.Commit();
        return deleted;
    }

    public void ClearBeatmapCache()
    {
        var path = AppPaths.BeatmapMediaDir;
        if (Directory.Exists(path))
        {
            Directory.Delete(path, recursive: true);
        }
        Directory.CreateDirectory(path);
    }

    public (int InvalidAttempts, int EmptySessions, int ReclassifiedCompleted) CleanupInvalidAttempts(double minimumSeconds = 3.0)
    {
        using var con = OpenWriteConnection();
        using var tx = con.BeginTransaction();
        var reclassified = ScalarCount(con, tx, """
            SELECT COUNT(*) FROM attempts
            WHERE outcome IN ('retried','quit','abandoned')
              AND progress >= 0.99
              AND (score > 0 OR n300 > 0 OR n100 > 0 OR n50 > 0 OR misses > 0)
            """);
        Execute(con, tx, """
            UPDATE attempts
            SET outcome='completed',
                termination_evidence=COALESCE(termination_evidence,'') || ':complete_progress',
                progress=1
            WHERE outcome IN ('retried','quit','abandoned')
              AND progress >= 0.99
              AND (score > 0 OR n300 > 0 OR n100 > 0 OR n50 > 0 OR misses > 0)
            """);
        var invalid = ScalarCount(con, tx, """
            SELECT COUNT(*) FROM attempts
            WHERE outcome != 'active'
              AND (
                duration_seconds < @minimum
                OR (score=0 AND n300=0 AND n100=0 AND n50=0 AND misses=0)
              )
            """, ("@minimum", minimumSeconds));
        Execute(con, tx, """
            DELETE FROM attempts
            WHERE outcome != 'active'
              AND (
                duration_seconds < @minimum
                OR (score=0 AND n300=0 AND n100=0 AND n50=0 AND misses=0)
              )
            """, ("@minimum", minimumSeconds));
        var emptySessions = ScalarCount(con, tx, """
            SELECT COUNT(*) FROM sessions s
            WHERE s.ended_at IS NOT NULL
              AND NOT EXISTS (SELECT 1 FROM attempts a WHERE a.session_id=s.id)
            """);
        Execute(con, tx, """
            DELETE FROM sessions
            WHERE ended_at IS NOT NULL
              AND NOT EXISTS (SELECT 1 FROM attempts a WHERE a.session_id=sessions.id)
            """);
        Execute(con, tx, "DELETE FROM beatmaps WHERE id NOT IN (SELECT beatmap_id FROM attempts)");
        tx.Commit();
        RebuildPersonalBests(con);
        PruneUnusedBeatmaps(con);
        return (invalid, emptySessions, reclassified);
    }

    public (int InvalidAttempts, int EmptySessions, int ReclassifiableCompleted, int ModBackfillCandidates) PreviewCleanup(double minimumSeconds = 3.0)
    {
        if (!_factory.DatabaseExists)
        {
            return (0, 0, 0, 0);
        }

        using var con = _factory.Open();
        var reclassifiable = ScalarCount(con, null, """
            SELECT COUNT(*) FROM attempts
            WHERE outcome IN ('retried','quit','abandoned')
              AND progress >= 0.99
              AND (score > 0 OR n300 > 0 OR n100 > 0 OR n50 > 0 OR misses > 0)
            """);
        var invalid = ScalarCount(con, null, """
            SELECT COUNT(*) FROM attempts
            WHERE outcome != 'active'
              AND (
                duration_seconds < @minimum
                OR (score=0 AND n300=0 AND n100=0 AND n50=0 AND misses=0)
              )
            """, ("@minimum", minimumSeconds));
        var emptySessions = ScalarCount(con, null, """
            SELECT COUNT(*) FROM sessions s
            WHERE s.ended_at IS NOT NULL
              AND NOT EXISTS (SELECT 1 FROM attempts a WHERE a.session_id=s.id)
            """);
        var modBackfill = TableExists(con, "attempt_raw_snapshots")
            ? ScalarCount(con, null, """
                SELECT COUNT(DISTINCT a.id)
                FROM attempts a
                JOIN attempt_raw_snapshots r ON r.attempt_id=a.id
                WHERE r.kind='start'
                """)
            : 0;
        return (invalid, emptySessions, reclassifiable, modBackfill);
    }

    public IReadOnlyList<string> PreviewCleanupDetails(double minimumSeconds = 3.0, int limit = 30)
    {
        var rows = new List<string>();
        if (!_factory.DatabaseExists)
        {
            return rows;
        }

        using var con = _factory.Open();
        using var cmd = con.CreateCommand();
        cmd.CommandText = """
            SELECT a.id, a.outcome, a.duration_seconds, a.progress,
                   COALESCE(b.artist, ''), COALESCE(b.title, ''), COALESCE(b.difficulty, '')
            FROM attempts a
            JOIN beatmaps b ON b.id = a.beatmap_id
            WHERE outcome != 'active'
              AND (
                duration_seconds < @minimum
                OR (score=0 AND n300=0 AND n100=0 AND n50=0 AND misses=0)
                OR (outcome IN ('retried','quit','abandoned') AND progress >= 0.99)
              )
            ORDER BY a.id DESC
            LIMIT @limit
            """;
        cmd.Parameters.AddWithValue("@minimum", minimumSeconds);
        cmd.Parameters.AddWithValue("@limit", limit);
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            var id = reader.GetInt64(0);
            var outcome = reader.GetString(1);
            var duration = reader.GetDouble(2);
            var progress = reader.GetDouble(3);
            var map = $"{reader.GetString(4)} - {reader.GetString(5)} [{reader.GetString(6)}]";
            var action = outcome is "retried" or "quit" or "abandoned" && progress >= 0.99
                ? "reclassify"
                : "delete";
            rows.Add($"#{id} {action}: {outcome}, {duration:0.0}s, {progress:P0}, {map}");
        }
        return rows;
    }

    public int BackfillModSettingsFromSnapshots()
    {
        using var con = OpenWriteConnection();
        if (!TableExists(con, "attempt_raw_snapshots"))
        {
            return 0;
        }
        var updated = 0;
        using var select = con.CreateCommand();
        select.CommandText = """
            SELECT a.id, r.payload_zlib
            FROM attempts a
            JOIN attempt_raw_snapshots r ON r.attempt_id=a.id
            WHERE r.kind='start'
            ORDER BY a.id
            """;
        using var reader = select.ExecuteReader();
        var rows = new List<(long Id, byte[] Payload)>();
        while (reader.Read())
        {
            rows.Add((reader.GetInt64(0), (byte[])reader["payload_zlib"]));
        }
        foreach (var (id, payload) in rows)
        {
            var mods = TryExtractMods(payload);
            if (mods.Count == 0)
            {
                continue;
            }
            var modsKey = string.Join(",", mods.Select(m => m.Acronym));
            using var tx = con.BeginTransaction();
            Execute(con, tx, "UPDATE attempts SET mods_key=@mods WHERE id=@id", ("@mods", (object)modsKey), ("@id", id));
            Execute(con, tx, "DELETE FROM attempt_mods WHERE attempt_id=@id", ("@id", id));
            for (var i = 0; i < mods.Count; i++)
            {
                Execute(con, tx,
                    "INSERT INTO attempt_mods(attempt_id,position,acronym,settings_json) VALUES(@id,@pos,@acronym,@settings)",
                    ("@id", id), ("@pos", i), ("@acronym", (object)mods[i].Acronym), ("@settings", mods[i].SettingsJson));
            }
            tx.Commit();
            updated++;
        }
        if (updated > 0)
        {
            RebuildPersonalBests(con);
        }
        return updated;
    }

    public void ExportJson(string destination)
    {
        var payload = new
        {
            schema_version = 2,
            exported_at = DateTimeOffset.Now.ToString("O"),
            sessions = QueryExportRows()
                .GroupBy(r => new { r.SessionId, r.SessionStarted, r.SessionEnded })
                .Select(g => new
                {
                    id = g.Key.SessionId,
                    started_at = g.Key.SessionStarted,
                    ended_at = g.Key.SessionEnded,
                    attempts = g.Select(r => new
                    {
                        id = r.AttemptId,
                        started_at = r.StartedAt,
                        ended_at = r.EndedAt,
                        outcome = r.Outcome,
                        artist = r.Artist,
                        title = r.Title,
                        difficulty = r.Difficulty,
                        mods_key = r.ModsKey,
                        score = r.Score,
                        accuracy = r.Accuracy,
                        grade = r.Grade,
                        pp = r.Pp,
                        fc_pp = r.FcPp,
                        combo = r.Combo,
                        n300 = r.N300,
                        n100 = r.N100,
                        n50 = r.N50,
                        misses = r.Misses,
                        unstable_rate = r.UnstableRate,
                        z_count = r.ZCount,
                        x_count = r.XCount,
                        progress = r.Progress,
                    }).ToArray(),
                }).ToArray(),
        };
        File.WriteAllText(destination, JsonSerializer.Serialize(payload, JsonOptions), Encoding.UTF8);
    }

    public void ExportCsv(string destination)
    {
        var fields = new[]
        {
            "session_id", "session_started", "attempt_id", "started_at", "outcome",
            "artist", "title", "difficulty", "mods_key", "score", "accuracy", "grade",
            "pp", "fc_pp", "combo", "n300", "n100", "n50", "misses", "unstable_rate",
            "z_count", "x_count", "progress",
        };
        using var writer = new StreamWriter(destination, false, new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
        writer.WriteLine(string.Join(",", fields));
        foreach (var row in QueryExportRows())
        {
            writer.WriteLine(string.Join(",", new[]
            {
                row.SessionId.ToString(CultureInfo.InvariantCulture),
                Csv(row.SessionStarted),
                row.AttemptId.ToString(CultureInfo.InvariantCulture),
                Csv(row.StartedAt),
                Csv(row.Outcome),
                Csv(row.Artist),
                Csv(row.Title),
                Csv(row.Difficulty),
                Csv(row.ModsKey),
                row.Score.ToString(CultureInfo.InvariantCulture),
                row.Accuracy.ToString(CultureInfo.InvariantCulture),
                Csv(row.Grade),
                row.Pp.ToString(CultureInfo.InvariantCulture),
                row.FcPp.ToString(CultureInfo.InvariantCulture),
                row.Combo.ToString(CultureInfo.InvariantCulture),
                row.N300.ToString(CultureInfo.InvariantCulture),
                row.N100.ToString(CultureInfo.InvariantCulture),
                row.N50.ToString(CultureInfo.InvariantCulture),
                row.Misses.ToString(CultureInfo.InvariantCulture),
                row.UnstableRate.ToString(CultureInfo.InvariantCulture),
                row.ZCount.ToString(CultureInfo.InvariantCulture),
                row.XCount.ToString(CultureInfo.InvariantCulture),
                row.Progress.ToString(CultureInfo.InvariantCulture),
            }));
        }
    }

    private IReadOnlyList<ExportRow> QueryExportRows()
    {
        var rows = new List<ExportRow>();
        if (!_factory.DatabaseExists)
        {
            return rows;
        }

        using var con = _factory.Open();
        using var cmd = con.CreateCommand();
        cmd.CommandText = """
            SELECT s.id, s.started_at, s.ended_at,
                   a.id, a.started_at, a.ended_at, a.outcome,
                   COALESCE(b.artist, ''), COALESCE(b.title, ''), COALESCE(b.difficulty, ''),
                   a.mods_key, a.score, a.accuracy, a.grade, a.pp, a.fc_pp, a.combo,
                   a.n300, a.n100, a.n50, a.misses, a.unstable_rate, a.z_count, a.x_count,
                   a.progress
            FROM sessions s
            JOIN attempts a ON a.session_id = s.id
            JOIN beatmaps b ON b.id = a.beatmap_id
            ORDER BY s.started_at DESC, a.started_at DESC, a.id DESC
            """;
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            rows.Add(new ExportRow(
                reader.GetInt64(0),
                reader.GetString(1),
                reader.IsDBNull(2) ? null : reader.GetString(2),
                reader.GetInt64(3),
                reader.GetString(4),
                reader.IsDBNull(5) ? null : reader.GetString(5),
                reader.GetString(6),
                reader.GetString(7),
                reader.GetString(8),
                reader.GetString(9),
                reader.GetString(10),
                reader.GetInt64(11),
                reader.GetDouble(12),
                reader.IsDBNull(13) ? "" : reader.GetString(13),
                reader.GetDouble(14),
                reader.GetDouble(15),
                (int)reader.GetInt64(16),
                (int)reader.GetInt64(17),
                (int)reader.GetInt64(18),
                (int)reader.GetInt64(19),
                (int)reader.GetInt64(20),
                reader.GetDouble(21),
                (int)reader.GetInt64(22),
                (int)reader.GetInt64(23),
                reader.GetDouble(24)));
        }
        return rows;
    }

    private SqliteConnection OpenWriteConnection()
    {
        var con = _factory.Open();
        using var cmd = con.CreateCommand();
        cmd.CommandText = "PRAGMA foreign_keys=ON; PRAGMA busy_timeout=5000;";
        cmd.ExecuteNonQuery();
        return con;
    }

    private static void RebuildPersonalBests(SqliteConnection con)
    {
        DeleteAllFrom(con, null, "personal_bests");
        DeleteAllFrom(con, null, "attempt_improvements");
        using var cmd = con.CreateCommand();
        cmd.CommandText = """
            INSERT OR REPLACE INTO personal_bests(beatmap_id, mods_key, metric, attempt_id, value)
            SELECT beatmap_id, mods_key, 'pp', id, pp
            FROM (
                SELECT a.*,
                       ROW_NUMBER() OVER (
                           PARTITION BY beatmap_id, mods_key
                           ORDER BY pp DESC, accuracy DESC, score DESC, id DESC
                       ) row_number
                FROM attempts a
                WHERE outcome IN ('completed', 'failed')
            )
            WHERE row_number = 1
            """;
        cmd.ExecuteNonQuery();
    }

    private static void PruneUnusedBeatmaps(SqliteConnection con)
    {
        using var cmd = con.CreateCommand();
        cmd.CommandText = "DELETE FROM beatmaps WHERE id NOT IN (SELECT beatmap_id FROM attempts)";
        cmd.ExecuteNonQuery();
    }

    private static int DeleteAllFrom(SqliteConnection con, SqliteTransaction? tx, string table)
    {
        if (!TableExists(con, table))
        {
            return 0;
        }

        using var cmd = con.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = $"DELETE FROM {table}";
        return cmd.ExecuteNonQuery();
    }

    private static void Execute(SqliteConnection con, SqliteTransaction? tx, string sql, params (string Name, object Value)[] parameters)
    {
        using var cmd = con.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = sql;
        foreach (var (name, value) in parameters)
        {
            cmd.Parameters.AddWithValue(name, value);
        }
        cmd.ExecuteNonQuery();
    }

    private static int ScalarCount(SqliteConnection con, SqliteTransaction? tx, string sql, params (string Name, object Value)[] parameters)
    {
        using var cmd = con.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = sql;
        foreach (var (name, value) in parameters)
        {
            cmd.Parameters.AddWithValue(name, value);
        }
        return Convert.ToInt32(cmd.ExecuteScalar());
    }

    private static IReadOnlyList<(string Acronym, string SettingsJson)> TryExtractMods(byte[] zlibPayload)
    {
        try
        {
            using var input = new MemoryStream(zlibPayload);
            using var zlib = new System.IO.Compression.ZLibStream(input, System.IO.Compression.CompressionMode.Decompress);
            using var doc = JsonDocument.Parse(zlib);
            var root = doc.RootElement.TryGetProperty("payload", out var payload) ? payload : doc.RootElement;
            if (!TryFindProperty(root, "mods", out var modsElement) || modsElement.ValueKind != JsonValueKind.Array)
            {
                return Array.Empty<(string, string)>();
            }
            var mods = new List<(string, string)>();
            foreach (var mod in modsElement.EnumerateArray())
            {
                var acronym = mod.TryGetProperty("acronym", out var acronymElement)
                    ? acronymElement.GetString()
                    : null;
                if (string.IsNullOrWhiteSpace(acronym))
                {
                    continue;
                }
                var settings = mod.TryGetProperty("settings", out var settingsElement)
                    ? settingsElement.GetRawText()
                    : "{}";
                mods.Add((acronym, settings));
            }
            return mods;
        }
        catch
        {
            return Array.Empty<(string, string)>();
        }
    }

    private static bool TryFindProperty(JsonElement element, string name, out JsonElement value)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            if (element.TryGetProperty(name, out value))
            {
                return true;
            }
            foreach (var property in element.EnumerateObject())
            {
                if (TryFindProperty(property.Value, name, out value))
                {
                    return true;
                }
            }
        }
        value = default;
        return false;
    }

    private static void DeleteIfExists(
        SqliteConnection con,
        SqliteTransaction tx,
        string table,
        string where,
        (string Name, object Value) parameter)
    {
        if (!TableExists(con, table))
        {
            return;
        }

        using var cmd = con.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = $"DELETE FROM {table} WHERE {where}";
        cmd.Parameters.AddWithValue(parameter.Name, parameter.Value);
        cmd.ExecuteNonQuery();
    }

    private static bool TableExists(SqliteConnection con, string table)
    {
        using var cmd = con.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type = 'table' AND name = @name";
        cmd.Parameters.AddWithValue("@name", table);
        return (long)(cmd.ExecuteScalar() ?? 0L) > 0;
    }

    private static string Csv(string? value)
    {
        value ??= "";
        return "\"" + value.Replace("\"", "\"\"") + "\"";
    }

    private sealed record ExportRow(
        long SessionId,
        string SessionStarted,
        string? SessionEnded,
        long AttemptId,
        string StartedAt,
        string? EndedAt,
        string Outcome,
        string Artist,
        string Title,
        string Difficulty,
        string ModsKey,
        long Score,
        double Accuracy,
        string Grade,
        double Pp,
        double FcPp,
        int Combo,
        int N300,
        int N100,
        int N50,
        int Misses,
        double UnstableRate,
        int ZCount,
        int XCount,
        double Progress);

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        WriteIndented = true,
    };
}
