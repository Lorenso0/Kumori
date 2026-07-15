using Microsoft.Data.Sqlite;
using Serilog;
using System.Text.Json;

namespace Kumori.Storage;

public sealed partial class AttemptSqliteSink
{
    private void EnsureSchema()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_factory.DatabasePath)!);
        using var con = _factory.Open();
        using (var journal = con.CreateCommand())
        {
            journal.CommandText = "PRAGMA journal_mode=WAL;";
            journal.ExecuteNonQuery();
        }
        using var tx = con.BeginTransaction();
        using var cmd = con.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = """
            CREATE TABLE IF NOT EXISTS metadata(
                key TEXT PRIMARY KEY, value TEXT NOT NULL
            );
            CREATE TABLE IF NOT EXISTS attempt_persistence_commits(
                operation_key TEXT PRIMARY KEY,
                committed_at TEXT NOT NULL
            );
            CREATE TABLE IF NOT EXISTS tracking_id_sequences(
                entity TEXT PRIMARY KEY,
                next_id INTEGER NOT NULL CHECK(next_id > 0)
            );
            CREATE TABLE IF NOT EXISTS sessions(
                id INTEGER PRIMARY KEY,
                started_at TEXT NOT NULL,
                ended_at TEXT,
                active_seconds REAL NOT NULL DEFAULT 0,
                z_count INTEGER NOT NULL DEFAULT 0,
                x_count INTEGER NOT NULL DEFAULT 0,
                key1_binding TEXT NOT NULL DEFAULT 'Z',
                key2_binding TEXT NOT NULL DEFAULT 'X',
                player_name TEXT,
                interrupted INTEGER NOT NULL DEFAULT 0,
                legacy INTEGER NOT NULL DEFAULT 0
            );
            CREATE TABLE IF NOT EXISTS beatmaps(
                id INTEGER PRIMARY KEY,
                identity TEXT NOT NULL UNIQUE,
                beatmap_id INTEGER,
                set_id INTEGER,
                checksum TEXT,
                artist TEXT,
                title TEXT,
                mapper TEXT,
                difficulty TEXT,
                stars REAL,
                ar REAL,
                cs REAL,
                od REAL,
                hp REAL,
                bpm REAL,
                max_combo INTEGER NOT NULL DEFAULT 0,
                raw_json TEXT
            );
            CREATE TABLE IF NOT EXISTS attempts(
                id INTEGER PRIMARY KEY,
                session_id INTEGER NOT NULL REFERENCES sessions(id) ON DELETE CASCADE,
                beatmap_id INTEGER NOT NULL REFERENCES beatmaps(id),
                started_at TEXT NOT NULL,
                ended_at TEXT,
                outcome TEXT NOT NULL DEFAULT 'active',
                termination_evidence TEXT,
                progress REAL NOT NULL DEFAULT 0,
                duration_seconds REAL NOT NULL DEFAULT 0,
                score INTEGER NOT NULL DEFAULT 0,
                accuracy REAL NOT NULL DEFAULT 0,
                grade TEXT,
                pp REAL NOT NULL DEFAULT 0,
                fc_pp REAL NOT NULL DEFAULT 0,
                max_pp REAL NOT NULL DEFAULT 0,
                combo INTEGER NOT NULL DEFAULT 0,
                n300 INTEGER NOT NULL DEFAULT 0,
                n100 INTEGER NOT NULL DEFAULT 0,
                n50 INTEGER NOT NULL DEFAULT 0,
                misses INTEGER NOT NULL DEFAULT 0,
                geki INTEGER NOT NULL DEFAULT 0,
                katu INTEGER NOT NULL DEFAULT 0,
                slider_breaks INTEGER NOT NULL DEFAULT 0,
                large_tick_hits INTEGER NOT NULL DEFAULT 0,
                large_tick_misses INTEGER NOT NULL DEFAULT 0,
                small_tick_hits INTEGER NOT NULL DEFAULT 0,
                small_tick_misses INTEGER NOT NULL DEFAULT 0,
                slider_tail_hits INTEGER NOT NULL DEFAULT 0,
                slider_tail_misses INTEGER NOT NULL DEFAULT 0,
                unstable_rate REAL NOT NULL DEFAULT 0,
                z_count INTEGER NOT NULL DEFAULT 0,
                x_count INTEGER NOT NULL DEFAULT 0,
                key1_binding TEXT NOT NULL DEFAULT 'Z',
                key2_binding TEXT NOT NULL DEFAULT 'X',
                mods_key TEXT NOT NULL DEFAULT 'NM',
                raw_json TEXT
            );
            CREATE TABLE IF NOT EXISTS attempt_mods(
                attempt_id INTEGER NOT NULL REFERENCES attempts(id) ON DELETE CASCADE,
                position INTEGER NOT NULL,
                acronym TEXT NOT NULL,
                settings_json TEXT NOT NULL DEFAULT '{}',
                PRIMARY KEY(attempt_id, position)
            );
            CREATE TABLE IF NOT EXISTS attempt_timing(
                attempt_id INTEGER PRIMARY KEY REFERENCES attempts(id) ON DELETE CASCADE,
                offsets_zlib BLOB NOT NULL,
                hit_count INTEGER NOT NULL,
                early_count INTEGER NOT NULL,
                late_count INTEGER NOT NULL,
                mean REAL NOT NULL,
                median REAL NOT NULL,
                deviation REAL NOT NULL
            );
            CREATE TABLE IF NOT EXISTS attempt_events(
                id INTEGER PRIMARY KEY,
                attempt_id INTEGER NOT NULL REFERENCES attempts(id) ON DELETE CASCADE,
                captured_at TEXT NOT NULL,
                map_time_ms INTEGER,
                event_type TEXT NOT NULL,
                value REAL,
                data_json TEXT NOT NULL DEFAULT '{}'
            );
            CREATE TABLE IF NOT EXISTS attempt_context(
                attempt_id INTEGER PRIMARY KEY REFERENCES attempts(id) ON DELETE CASCADE,
                source_json TEXT NOT NULL DEFAULT '{}',
                pp_json TEXT NOT NULL DEFAULT '{}',
                beatmap_json TEXT NOT NULL DEFAULT '{}',
                score_json TEXT NOT NULL DEFAULT '{}',
                session_json TEXT NOT NULL DEFAULT '{}',
                multiplayer_json TEXT NOT NULL DEFAULT '{}'
            );
            CREATE TABLE IF NOT EXISTS attempt_input_summary(
                attempt_id INTEGER PRIMARY KEY REFERENCES attempts(id) ON DELETE CASCADE,
                key1_presses INTEGER NOT NULL DEFAULT 0,
                key2_presses INTEGER NOT NULL DEFAULT 0,
                alternations INTEGER NOT NULL DEFAULT 0,
                same_key_repeats INTEGER NOT NULL DEFAULT 0,
                simultaneous_presses INTEGER NOT NULL DEFAULT 0,
                key1_hold_ms REAL NOT NULL DEFAULT 0,
                key2_hold_ms REAL NOT NULL DEFAULT 0,
                peak_kps INTEGER NOT NULL DEFAULT 0,
                average_kps REAL NOT NULL DEFAULT 0
            );
            CREATE TABLE IF NOT EXISTS personal_bests(
                beatmap_id INTEGER NOT NULL REFERENCES beatmaps(id) ON DELETE CASCADE,
                mods_key TEXT NOT NULL,
                metric TEXT NOT NULL,
                attempt_id INTEGER NOT NULL REFERENCES attempts(id) ON DELETE CASCADE,
                value REAL NOT NULL,
                PRIMARY KEY(beatmap_id, mods_key, metric)
            );
            CREATE TABLE IF NOT EXISTS attempt_improvements(
                attempt_id INTEGER NOT NULL REFERENCES attempts(id) ON DELETE CASCADE,
                metric TEXT NOT NULL,
                previous_value REAL,
                new_value REAL NOT NULL,
                delta REAL,
                PRIMARY KEY(attempt_id, metric)
            );
            """ + MovementSchema.Sql + """
            CREATE INDEX IF NOT EXISTS idx_attempt_session ON attempts(session_id, started_at);
            CREATE INDEX IF NOT EXISTS idx_attempt_map_mods ON attempts(beatmap_id, mods_key);
            CREATE INDEX IF NOT EXISTS idx_attempt_events ON attempt_events(attempt_id, map_time_ms);
            """;
        cmd.ExecuteNonQuery();

        using (var sequences = con.CreateCommand())
        {
            sequences.Transaction = tx;
            sequences.CommandText = """
                INSERT INTO tracking_id_sequences(entity, next_id)
                VALUES('session', (SELECT COALESCE(MAX(id) + 1, 1) FROM sessions))
                ON CONFLICT(entity) DO UPDATE SET
                    next_id = MAX(tracking_id_sequences.next_id, excluded.next_id);
                INSERT INTO tracking_id_sequences(entity, next_id)
                VALUES('attempt', (SELECT COALESCE(MAX(id) + 1, 1) FROM attempts))
                ON CONFLICT(entity) DO UPDATE SET
                    next_id = MAX(tracking_id_sequences.next_id, excluded.next_id);
                """;
            sequences.ExecuteNonQuery();
        }

        EnsureColumn(con, tx, "beatmaps", "beatmap_id", "INTEGER");
        EnsureColumn(con, tx, "beatmaps", "set_id", "INTEGER");
        EnsureColumn(con, tx, "beatmaps", "checksum", "TEXT");
        EnsureColumn(con, tx, "beatmaps", "ar", "REAL");
        EnsureColumn(con, tx, "beatmaps", "cs", "REAL");
        EnsureColumn(con, tx, "beatmaps", "od", "REAL");
        EnsureColumn(con, tx, "beatmaps", "hp", "REAL");
        EnsureColumn(con, tx, "beatmaps", "bpm", "REAL");
        EnsureColumn(con, tx, "beatmaps", "max_combo", "INTEGER NOT NULL DEFAULT 0");
        EnsureColumn(con, tx, "sessions", "player_name", "TEXT");
        EnsureColumn(con, tx, "attempts", "geki", "INTEGER NOT NULL DEFAULT 0");
        EnsureColumn(con, tx, "attempts", "katu", "INTEGER NOT NULL DEFAULT 0");
        EnsureColumn(con, tx, "attempts", "large_tick_hits", "INTEGER NOT NULL DEFAULT 0");
        EnsureColumn(con, tx, "attempts", "large_tick_misses", "INTEGER NOT NULL DEFAULT 0");
        EnsureColumn(con, tx, "attempts", "small_tick_hits", "INTEGER NOT NULL DEFAULT 0");
        EnsureColumn(con, tx, "attempts", "small_tick_misses", "INTEGER NOT NULL DEFAULT 0");
        EnsureColumn(con, tx, "attempts", "slider_tail_hits", "INTEGER NOT NULL DEFAULT 0");
        EnsureColumn(con, tx, "attempts", "slider_tail_misses", "INTEGER NOT NULL DEFAULT 0");
        EnsureColumn(con, tx, "attempts", "base_stars", "REAL");
        EnsureColumn(con, tx, "attempts", "adjusted_stars", "REAL");
        BackfillAttemptStars(con, tx);
        using (var version = con.CreateCommand())
        {
            version.Transaction = tx;
            version.CommandText = "INSERT OR IGNORE INTO metadata(key, value) VALUES('schema_version', '2')";
            version.ExecuteNonQuery();
        }
        tx.Commit();
        DatabaseMigrator.Apply(con);
    }

    /// <summary>
    /// Migrates historical attempts from their immutable captured beatmap
    /// context. The beatmaps table cannot be used here because one row is
    /// shared between every mod combination of a map.
    /// </summary>
    private static void BackfillAttemptStars(SqliteConnection con, SqliteTransaction tx)
    {
        if (!TableExists(con, tx, "attempt_context"))
        {
            return;
        }

        var values = new List<(long AttemptId, double BaseStars, double AdjustedStars)>();
        using (var select = con.CreateCommand())
        {
            select.Transaction = tx;
            select.CommandText = """
                SELECT a.id, c.beatmap_json
                FROM attempts a
                JOIN attempt_context c ON c.attempt_id = a.id
                WHERE a.base_stars IS NULL OR a.adjusted_stars IS NULL
                """;
            using var reader = select.ExecuteReader();
            while (reader.Read())
            {
                try
                {
                    using var document = JsonDocument.Parse(reader.GetString(1));
                    if (!document.RootElement.TryGetProperty("stats", out var stats)
                        || !stats.TryGetProperty("stars", out var stars)
                        || stars.ValueKind != JsonValueKind.Object)
                    {
                        continue;
                    }

                    double? original = TryGetDouble(stars, "original");
                    double? adjusted = TryGetDouble(stars, "total") ?? TryGetDouble(stars, "converted") ?? original;
                    if (adjusted is { } adjustedValue)
                    {
                        values.Add((reader.GetInt64(0), original ?? adjustedValue, adjustedValue));
                    }
                }
                catch (JsonException ex)
                {
                    Log.Warning(ex, "Invalid captured beatmap context while backfilling stars for attempt {AttemptId}", reader.GetInt64(0));
                }
            }
        }

        foreach (var value in values)
        {
            using var update = con.CreateCommand();
            update.Transaction = tx;
            update.CommandText = """
                UPDATE attempts
                SET base_stars = COALESCE(base_stars, @base_stars),
                    adjusted_stars = COALESCE(adjusted_stars, @adjusted_stars)
                WHERE id = @id
                """;
            update.Parameters.AddWithValue("@base_stars", value.BaseStars);
            update.Parameters.AddWithValue("@adjusted_stars", value.AdjustedStars);
            update.Parameters.AddWithValue("@id", value.AttemptId);
            update.ExecuteNonQuery();
        }
    }

    private static double? TryGetDouble(JsonElement element, string property) =>
        element.TryGetProperty(property, out var value)
        && value.ValueKind == JsonValueKind.Number
        && value.TryGetDouble(out var number)
            ? number
            : null;

    private static bool TableExists(SqliteConnection con, SqliteTransaction tx, string table)
    {
        using var cmd = con.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = "SELECT 1 FROM sqlite_master WHERE type = 'table' AND name = @name";
        cmd.Parameters.AddWithValue("@name", table);
        return cmd.ExecuteScalar() is not null;
    }

}
