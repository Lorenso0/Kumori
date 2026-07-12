using Kumori.Tracking;
using Microsoft.Data.Sqlite;
using Serilog;
using System.Text.Json;

namespace Kumori.Storage;

public sealed class AttemptSqliteSink : IAttemptSink, ISessionSink
{
    private readonly SqliteConnectionFactory _factory;
    private long? _sessionId;
    private long? _attemptId;

    public AttemptSqliteSink(SqliteConnectionFactory factory)
    {
        _factory = factory;
        EnsureSchema();
    }

    public long? CurrentSessionId => _sessionId;
    public long? CurrentAttemptId => _attemptId;

    public void StartSession(SessionStart start)
    {
        using var con = _factory.Open();
        _sessionId ??= EnsureSession(con, start.WallTime);
    }

    public void AddActiveSeconds(double seconds)
    {
        if (_sessionId is not { } sessionId)
        {
            return;
        }

        using var con = _factory.Open();
        using var cmd = con.CreateCommand();
        cmd.CommandText = "UPDATE sessions SET active_seconds = active_seconds + @seconds WHERE id = @id";
        cmd.Parameters.AddWithValue("@seconds", seconds);
        cmd.Parameters.AddWithValue("@id", sessionId);
        cmd.ExecuteNonQuery();
    }

    public void PromptOsuClosed(SessionClosePrompt prompt)
    {
        // The WPF/tray notification layer observes this through app state in production.
        // The sink intentionally has no UI side effect.
    }

    public void EndSession(SessionEnd end) => EndSession(end.Interrupted, end.WallTime);

    public void StartAttempt(AttemptStart start)
    {
        using var con = _factory.Open();
        using var tx = con.BeginTransaction();
        _sessionId ??= EnsureSession(con, start.WallTime);
        var beatmapId = EnsureBeatmap(con, start);

        using var insert = con.CreateCommand();
        insert.Transaction = tx;
        insert.CommandText = """
            INSERT INTO attempts(session_id, beatmap_id, started_at, outcome,
                                 progress, duration_seconds, mods_key,
                                 key1_binding, key2_binding, base_stars, adjusted_stars)
            VALUES(@session_id, @beatmap_id, @started_at, 'active',
                   0, 0, @mods_key, 'Z', 'X', @base_stars, @adjusted_stars)
            RETURNING id
            """;
        insert.Parameters.AddWithValue("@session_id", _sessionId.Value);
        insert.Parameters.AddWithValue("@beatmap_id", beatmapId);
        insert.Parameters.AddWithValue("@started_at", IsoFromUnixSeconds(start.WallTime));
        insert.Parameters.AddWithValue("@mods_key", start.ModsKey);
        insert.Parameters.AddWithValue("@base_stars", (object?)start.BeatmapStats.BaseStars ?? DBNull.Value);
        insert.Parameters.AddWithValue("@adjusted_stars", (object?)start.BeatmapStats.Stars ?? DBNull.Value);
        _attemptId = (long)insert.ExecuteScalar()!;
        UpsertSourceContext(con, tx, _attemptId.Value, start);

        for (var i = 0; i < start.Mods.Count; i++)
        {
            using var mod = con.CreateCommand();
            mod.Transaction = tx;
            mod.CommandText = """
                INSERT INTO attempt_mods(attempt_id, position, acronym, settings_json)
                VALUES(@attempt_id, @position, @acronym, @settings_json)
                """;
            mod.Parameters.AddWithValue("@attempt_id", _attemptId.Value);
            mod.Parameters.AddWithValue("@position", i);
            mod.Parameters.AddWithValue("@acronym", start.Mods[i].Acronym);
            mod.Parameters.AddWithValue("@settings_json", start.Mods[i].SettingsJson);
            mod.ExecuteNonQuery();
        }

        tx.Commit();
    }

    public void Checkpoint(AttemptCheckpoint checkpoint)
    {
        if (_attemptId is not { } attemptId)
        {
            return;
        }

        using var con = _factory.Open();
        using var tx = con.BeginTransaction();
        UpdateAttempt(con, tx, attemptId, checkpoint.Snapshot);
        if (checkpoint.Snapshot.Mods.Count > 0 || !checkpoint.Snapshot.ModsKey.Equals("NM", StringComparison.OrdinalIgnoreCase))
            ReplaceMods(con, tx, attemptId, checkpoint.Snapshot.ModsKey, checkpoint.Snapshot.Mods);
        foreach (var evt in checkpoint.Events)
        {
            InsertEvent(con, tx, attemptId, checkpoint.Snapshot, evt);
        }
        if (checkpoint.Forced)
        {
            UpsertTiming(con, tx, attemptId, checkpoint.Snapshot);
            UpsertContext(con, tx, attemptId, checkpoint.Snapshot);
        }
        tx.Commit();
    }

    public void DiscardIfEmpty(AttemptDiscard discard)
    {
        if (_attemptId is not { } attemptId)
        {
            return;
        }

        using var con = _factory.Open();
        using var cmd = con.CreateCommand();
        cmd.CommandText = "DELETE FROM attempts WHERE id = @id";
        cmd.Parameters.AddWithValue("@id", attemptId);
        cmd.ExecuteNonQuery();
        _attemptId = null;
    }

    public void Finalize(AttemptFinalization finalization)
    {
        if (_attemptId is not { } attemptId)
        {
            return;
        }

        using var con = _factory.Open();
        using var tx = con.BeginTransaction();
        UpdateAttempt(con, tx, attemptId, finalization.Snapshot);
        if (finalization.Snapshot.Mods.Count > 0 || !finalization.Snapshot.ModsKey.Equals("NM", StringComparison.OrdinalIgnoreCase))
            ReplaceMods(con, tx, attemptId, finalization.Snapshot.ModsKey, finalization.Snapshot.Mods);

        using var cmd = con.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = """
            UPDATE attempts
            SET outcome = @outcome,
                termination_evidence = @evidence,
                ended_at = @ended_at,
                progress = CASE WHEN @outcome = 'completed' THEN 1 ELSE progress END
            WHERE id = @id
            """;
        cmd.Parameters.AddWithValue("@outcome", finalization.Outcome);
        cmd.Parameters.AddWithValue("@evidence", finalization.Evidence);
        cmd.Parameters.AddWithValue("@ended_at", IsoFromUnixSeconds(finalization.Snapshot.WallTime));
        cmd.Parameters.AddWithValue("@id", attemptId);
        cmd.ExecuteNonQuery();

        UpsertTiming(con, tx, attemptId, finalization.Snapshot);
        UpsertContext(con, tx, attemptId, finalization.Snapshot);
        UpsertInputSummary(con, tx, attemptId);
        if (finalization.Outcome is "completed" or "failed")
        {
            UpdatePersonalBests(con, tx, attemptId);
        }
        tx.Commit();
        _attemptId = null;
    }

    public void EndSession(bool interrupted = false, double? wallTime = null)
    {
        if (_sessionId is not { } sessionId)
        {
            return;
        }

        using var con = _factory.Open();
        using var cmd = con.CreateCommand();
        cmd.CommandText = """
            UPDATE sessions
            SET ended_at = @ended_at, interrupted = @interrupted
            WHERE id = @id
            """;
        cmd.Parameters.AddWithValue("@ended_at",
            wallTime is { } value ? IsoFromUnixSeconds(value) : DateTimeOffset.Now.ToString("O"));
        cmd.Parameters.AddWithValue("@interrupted", interrupted ? 1 : 0);
        cmd.Parameters.AddWithValue("@id", sessionId);
        cmd.ExecuteNonQuery();
        _sessionId = null;
    }

    private void EnsureSchema()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_factory.DatabasePath)!);
        using var con = _factory.Open();
        using var cmd = con.CreateCommand();
        cmd.CommandText = """
            PRAGMA journal_mode=WAL;
            CREATE TABLE IF NOT EXISTS metadata(
                key TEXT PRIMARY KEY, value TEXT NOT NULL
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
            INSERT OR REPLACE INTO metadata(key, value) VALUES('schema_version', '2');
            """;
        cmd.ExecuteNonQuery();

        EnsureColumn(con, "beatmaps", "beatmap_id", "INTEGER");
        EnsureColumn(con, "beatmaps", "set_id", "INTEGER");
        EnsureColumn(con, "beatmaps", "checksum", "TEXT");
        EnsureColumn(con, "beatmaps", "ar", "REAL");
        EnsureColumn(con, "beatmaps", "cs", "REAL");
        EnsureColumn(con, "beatmaps", "od", "REAL");
        EnsureColumn(con, "beatmaps", "hp", "REAL");
        EnsureColumn(con, "beatmaps", "bpm", "REAL");
        EnsureColumn(con, "beatmaps", "max_combo", "INTEGER NOT NULL DEFAULT 0");
        EnsureColumn(con, "sessions", "player_name", "TEXT");
        EnsureColumn(con, "attempts", "geki", "INTEGER NOT NULL DEFAULT 0");
        EnsureColumn(con, "attempts", "katu", "INTEGER NOT NULL DEFAULT 0");
        EnsureColumn(con, "attempts", "large_tick_hits", "INTEGER NOT NULL DEFAULT 0");
        EnsureColumn(con, "attempts", "large_tick_misses", "INTEGER NOT NULL DEFAULT 0");
        EnsureColumn(con, "attempts", "small_tick_hits", "INTEGER NOT NULL DEFAULT 0");
        EnsureColumn(con, "attempts", "small_tick_misses", "INTEGER NOT NULL DEFAULT 0");
        EnsureColumn(con, "attempts", "slider_tail_hits", "INTEGER NOT NULL DEFAULT 0");
        EnsureColumn(con, "attempts", "slider_tail_misses", "INTEGER NOT NULL DEFAULT 0");
        EnsureColumn(con, "attempts", "base_stars", "REAL");
        EnsureColumn(con, "attempts", "adjusted_stars", "REAL");
        BackfillAttemptStars(con);
    }

    /// <summary>
    /// Migrates historical attempts from their immutable captured beatmap
    /// context. The beatmaps table cannot be used here because one row is
    /// shared between every mod combination of a map.
    /// </summary>
    private static void BackfillAttemptStars(SqliteConnection con)
    {
        if (!TableExists(con, "attempt_context"))
        {
            return;
        }

        var values = new List<(long AttemptId, double BaseStars, double AdjustedStars)>();
        using (var select = con.CreateCommand())
        {
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

    private static bool TableExists(SqliteConnection con, string table)
    {
        using var cmd = con.CreateCommand();
        cmd.CommandText = "SELECT 1 FROM sqlite_master WHERE type = 'table' AND name = @name";
        cmd.Parameters.AddWithValue("@name", table);
        return cmd.ExecuteScalar() is not null;
    }

    private static long EnsureSession(SqliteConnection con, double wallTime)
    {
        using var cmd = con.CreateCommand();
        cmd.CommandText = """
            INSERT INTO sessions(started_at, key1_binding, key2_binding)
            VALUES(@started_at, 'Z', 'X')
            RETURNING id
            """;
        cmd.Parameters.AddWithValue("@started_at", IsoFromUnixSeconds(wallTime));
        return (long)cmd.ExecuteScalar()!;
    }

    private static long EnsureBeatmap(SqliteConnection con, AttemptStart start)
    {
        using var cmd = con.CreateCommand();
        cmd.CommandText = """
            INSERT INTO beatmaps(identity, beatmap_id, set_id, checksum, artist, title, mapper,
                                 difficulty, stars, ar, cs, od, hp, bpm, max_combo, raw_json)
            VALUES(@identity, @beatmap_id, @set_id, @checksum, @artist, @title, @mapper,
                   @difficulty, @stars, @ar, @cs, @od, @hp, @bpm, @max_combo, @raw_json)
            ON CONFLICT(identity) DO UPDATE SET
                beatmap_id = COALESCE(excluded.beatmap_id, beatmaps.beatmap_id),
                set_id = COALESCE(excluded.set_id, beatmaps.set_id),
                checksum = COALESCE(excluded.checksum, beatmaps.checksum),
                artist = COALESCE(excluded.artist, beatmaps.artist),
                title = COALESCE(excluded.title, beatmaps.title),
                mapper = COALESCE(excluded.mapper, beatmaps.mapper),
                difficulty = COALESCE(excluded.difficulty, beatmaps.difficulty),
                stars = COALESCE(excluded.stars, beatmaps.stars),
                ar = COALESCE(excluded.ar, beatmaps.ar),
                cs = COALESCE(excluded.cs, beatmaps.cs),
                od = COALESCE(excluded.od, beatmaps.od),
                hp = COALESCE(excluded.hp, beatmaps.hp),
                bpm = COALESCE(excluded.bpm, beatmaps.bpm),
                max_combo = CASE WHEN excluded.max_combo > 0 THEN excluded.max_combo ELSE beatmaps.max_combo END,
                raw_json = excluded.raw_json
            RETURNING id
            """;
        cmd.Parameters.AddWithValue("@identity", start.Identity);
        cmd.Parameters.AddWithValue("@beatmap_id", (object?)start.BeatmapId ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@set_id", (object?)start.BeatmapSetId ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@checksum", (object?)start.Checksum ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@artist", (object?)start.Artist ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@title", (object?)start.Title ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@mapper", (object?)start.Mapper ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@difficulty", (object?)start.Difficulty ?? DBNull.Value);
        // This is shared map metadata. The mod-adjusted value belongs on the
        // attempt, otherwise a DT/HR play can change the apparent base stars
        // for every other play of the same map.
        cmd.Parameters.AddWithValue("@stars", (object?)(start.BeatmapStats.BaseStars ?? start.BeatmapStats.Stars) ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@ar", (object?)start.BeatmapStats.ApproachRate ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@cs", (object?)start.BeatmapStats.CircleSize ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@od", (object?)start.BeatmapStats.OverallDifficulty ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@hp", (object?)start.BeatmapStats.DrainRate ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@bpm", (object?)start.BeatmapStats.Bpm ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@max_combo", start.BeatmapStats.MaxCombo ?? 0);
        cmd.Parameters.AddWithValue("@raw_json", start.BeatmapStats.RawJson);
        return (long)cmd.ExecuteScalar()!;
    }

    private static void UpdateAttempt(
        SqliteConnection con,
        SqliteTransaction tx,
        long attemptId,
        AttemptSnapshot snapshot)
    {
        using var cmd = con.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = """
            UPDATE attempts
            SET progress = @progress,
                duration_seconds = @duration,
                score = @score,
                accuracy = @accuracy,
                grade = @grade,
                pp = @pp,
                fc_pp = @fc_pp,
                max_pp = @max_pp,
                combo = @combo,
                n300 = @n300,
                n100 = @n100,
                n50 = @n50,
                misses = @misses,
                geki = @geki,
                katu = @katu,
                slider_breaks = @slider_breaks,
                large_tick_hits = @large_tick_hits,
                large_tick_misses = @large_tick_misses,
                small_tick_hits = @small_tick_hits,
                small_tick_misses = @small_tick_misses,
                slider_tail_hits = @slider_tail_hits,
                slider_tail_misses = @slider_tail_misses,
                unstable_rate = @unstable_rate,
                base_stars = COALESCE(@base_stars, base_stars),
                adjusted_stars = COALESCE(@adjusted_stars, adjusted_stars),
                raw_json = @raw_json
            WHERE id = @id
            """;
        cmd.Parameters.AddWithValue("@progress", snapshot.Progress);
        cmd.Parameters.AddWithValue("@duration", snapshot.DurationSeconds);
        cmd.Parameters.AddWithValue("@score", snapshot.Score);
        cmd.Parameters.AddWithValue("@accuracy", snapshot.Accuracy);
        cmd.Parameters.AddWithValue("@grade", (object?)snapshot.Grade ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@pp", snapshot.Pp);
        cmd.Parameters.AddWithValue("@fc_pp", snapshot.FcPp);
        cmd.Parameters.AddWithValue("@max_pp", snapshot.MaxPp);
        cmd.Parameters.AddWithValue("@combo", snapshot.Combo);
        cmd.Parameters.AddWithValue("@n300", snapshot.N300);
        cmd.Parameters.AddWithValue("@n100", snapshot.N100);
        cmd.Parameters.AddWithValue("@n50", snapshot.N50);
        cmd.Parameters.AddWithValue("@misses", snapshot.Misses);
        cmd.Parameters.AddWithValue("@geki", snapshot.Geki);
        cmd.Parameters.AddWithValue("@katu", snapshot.Katu);
        cmd.Parameters.AddWithValue("@slider_breaks", snapshot.SliderBreaks);
        cmd.Parameters.AddWithValue("@large_tick_hits", snapshot.LargeTickHits);
        cmd.Parameters.AddWithValue("@large_tick_misses", snapshot.LargeTickMisses);
        cmd.Parameters.AddWithValue("@small_tick_hits", snapshot.SmallTickHits);
        cmd.Parameters.AddWithValue("@small_tick_misses", snapshot.SmallTickMisses);
        cmd.Parameters.AddWithValue("@slider_tail_hits", snapshot.SliderTailHits);
        cmd.Parameters.AddWithValue("@slider_tail_misses", snapshot.SliderTailMisses);
        cmd.Parameters.AddWithValue("@unstable_rate", snapshot.UnstableRate);
        cmd.Parameters.AddWithValue("@base_stars", (object?)snapshot.BeatmapStats.BaseStars ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@adjusted_stars", (object?)snapshot.BeatmapStats.Stars ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@raw_json", "{}");
        cmd.Parameters.AddWithValue("@id", attemptId);
        cmd.ExecuteNonQuery();
    }

    private static void ReplaceMods(
        SqliteConnection con,
        SqliteTransaction tx,
        long attemptId,
        string modsKey,
        IReadOnlyList<AttemptMod> mods)
    {
        using (var update = con.CreateCommand())
        {
            update.Transaction = tx;
            update.CommandText = "UPDATE attempts SET mods_key = @mods_key WHERE id = @id";
            update.Parameters.AddWithValue("@mods_key", string.IsNullOrWhiteSpace(modsKey) ? "NM" : modsKey);
            update.Parameters.AddWithValue("@id", attemptId);
            update.ExecuteNonQuery();
        }
        using (var delete = con.CreateCommand())
        {
            delete.Transaction = tx;
            delete.CommandText = "DELETE FROM attempt_mods WHERE attempt_id = @id";
            delete.Parameters.AddWithValue("@id", attemptId);
            delete.ExecuteNonQuery();
        }
        for (var position = 0; position < mods.Count; position++)
        {
            using var insert = con.CreateCommand();
            insert.Transaction = tx;
            insert.CommandText = "INSERT INTO attempt_mods(attempt_id, position, acronym, settings_json) VALUES(@id, @position, @acronym, @settings)";
            insert.Parameters.AddWithValue("@id", attemptId);
            insert.Parameters.AddWithValue("@position", position);
            insert.Parameters.AddWithValue("@acronym", mods[position].Acronym);
            insert.Parameters.AddWithValue("@settings", mods[position].SettingsJson);
            insert.ExecuteNonQuery();
        }
    }

    private static void InsertEvent(
        SqliteConnection con,
        SqliteTransaction tx,
        long attemptId,
        AttemptSnapshot snapshot,
        JudgementCapture.CapturedEvent evt)
    {
        using var cmd = con.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = """
            INSERT INTO attempt_events(attempt_id, captured_at, map_time_ms, event_type, value, data_json)
            VALUES(@attempt_id, @captured_at, @map_time_ms, @event_type, @value, @data_json)
            """;
        cmd.Parameters.AddWithValue("@attempt_id", attemptId);
        cmd.Parameters.AddWithValue("@captured_at", IsoFromUnixSeconds(snapshot.WallTime));
        cmd.Parameters.AddWithValue("@map_time_ms", snapshot.LiveTimeMs);
        cmd.Parameters.AddWithValue("@event_type", evt.EventType);
        cmd.Parameters.AddWithValue("@value", evt.Value);
        cmd.Parameters.AddWithValue("@data_json", evt.DataJson);
        cmd.ExecuteNonQuery();
    }

    private static void UpsertTiming(
        SqliteConnection con,
        SqliteTransaction tx,
        long attemptId,
        AttemptSnapshot snapshot)
    {
        using var cmd = con.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = """
            INSERT INTO attempt_timing(attempt_id, offsets_zlib, hit_count, early_count,
                                       late_count, mean, median, deviation)
            VALUES(@attempt_id, @offsets, @hit_count, @early_count, @late_count,
                   @mean, @median, @deviation)
            ON CONFLICT(attempt_id) DO UPDATE SET
                offsets_zlib = excluded.offsets_zlib,
                hit_count = excluded.hit_count,
                early_count = excluded.early_count,
                late_count = excluded.late_count,
                mean = excluded.mean,
                median = excluded.median,
                deviation = excluded.deviation
            """;
        var offsets = snapshot.TimingOffsets.ToArray();
        var sorted = offsets.OrderBy(v => v).ToArray();
        var mean = offsets.Length > 0 ? offsets.Average() : 0;
        var median = sorted.Length switch
        {
            0 => 0,
            var n when n % 2 == 1 => sorted[n / 2],
            var n => (sorted[n / 2 - 1] + sorted[n / 2]) / 2,
        };
        var deviation = offsets.Length > 0
            ? Math.Sqrt(offsets.Average(v => Math.Pow(v - mean, 2)))
            : 0;
        cmd.Parameters.AddWithValue("@attempt_id", attemptId);
        cmd.Parameters.Add("@offsets", SqliteType.Blob).Value = BlobCodec.EncodeOffsets(offsets);
        cmd.Parameters.AddWithValue("@hit_count", offsets.Length);
        cmd.Parameters.AddWithValue("@early_count", offsets.Count(v => v < 0));
        cmd.Parameters.AddWithValue("@late_count", offsets.Count(v => v > 0));
        cmd.Parameters.AddWithValue("@mean", mean);
        cmd.Parameters.AddWithValue("@median", median);
        cmd.Parameters.AddWithValue("@deviation", deviation);
        cmd.ExecuteNonQuery();
    }

    private static void UpsertContext(
        SqliteConnection con,
        SqliteTransaction tx,
        long attemptId,
        AttemptSnapshot snapshot)
    {
        using var cmd = con.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = """
            INSERT INTO attempt_context(attempt_id, source_json, pp_json, beatmap_json,
                                        score_json, session_json, multiplayer_json)
            VALUES(@attempt_id, '{}', @pp_json, @beatmap_json, @score_json, '{}', '{}')
            ON CONFLICT(attempt_id) DO UPDATE SET
                pp_json = excluded.pp_json,
                beatmap_json = excluded.beatmap_json,
                score_json = excluded.score_json
            """;
        cmd.Parameters.AddWithValue("@attempt_id", attemptId);
        cmd.Parameters.AddWithValue("@pp_json", JsonSerializer.Serialize(new { pp = snapshot.Pp, fc_pp = snapshot.FcPp, max_pp = snapshot.MaxPp }));
        cmd.Parameters.AddWithValue("@beatmap_json", BeatmapContextJson(snapshot.BeatmapStats));
        cmd.Parameters.AddWithValue("@score_json", JsonSerializer.Serialize(new
        {
            score = snapshot.Score,
            grade = snapshot.Grade ?? "",
            hits = new
            {
                _300 = snapshot.N300,
                _100 = snapshot.N100,
                _50 = snapshot.N50,
                _0 = snapshot.Misses,
                geki = snapshot.Geki,
                katu = snapshot.Katu,
                largeTickHits = snapshot.LargeTickHits,
                largeTickMisses = snapshot.LargeTickMisses,
                smallTickHits = snapshot.SmallTickHits,
                smallTickMisses = snapshot.SmallTickMisses,
                sliderTailHits = snapshot.SliderTailHits,
                sliderTailMisses = snapshot.SliderTailMisses,
                sliderBreaks = snapshot.SliderBreaks,
            },
        }));
        cmd.ExecuteNonQuery();
    }

    private static void UpsertSourceContext(
        SqliteConnection con,
        SqliteTransaction tx,
        long attemptId,
        AttemptStart start)
    {
        string? beatmapPath = start.ClientKind == OsuClientKind.Stable ? ResolveStableBeatmapPath(start) : null;
        string? mediaDirectory = beatmapPath is null ? null : Path.GetDirectoryName(beatmapPath);
        using var cmd = con.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = """
            INSERT INTO attempt_context(attempt_id, source_json, pp_json, beatmap_json,
                                        score_json, session_json, multiplayer_json)
            VALUES(@attempt_id, @source_json, '{}', '{}', '{}', '{}', '{}')
            ON CONFLICT(attempt_id) DO UPDATE SET source_json = excluded.source_json
            """;
        cmd.Parameters.AddWithValue("@attempt_id", attemptId);
        cmd.Parameters.AddWithValue("@source_json", JsonSerializer.Serialize(new
        {
            client_kind = start.ClientKind.ToString().ToLowerInvariant(),
            beatmap_path = beatmapPath,
            media_directory = mediaDirectory,
            game_folder = start.GameFolder,
            songs_folder = start.SongsFolder,
        }));
        cmd.ExecuteNonQuery();
    }

    private static string? ResolveStableBeatmapPath(AttemptStart start)
    {
        if (string.IsNullOrWhiteSpace(start.BeatmapFile))
            return null;
        string file = start.BeatmapFile;
        var candidates = new List<string>();
        if (Path.IsPathRooted(file))
            candidates.Add(file);
        string? songs = start.SongsFolder;
        if (!string.IsNullOrWhiteSpace(songs) && !Path.IsPathRooted(songs) && !string.IsNullOrWhiteSpace(start.GameFolder))
            songs = Path.Combine(start.GameFolder, songs);
        if (!string.IsNullOrWhiteSpace(songs))
            candidates.Add(Path.Combine(songs, file));
        if (!string.IsNullOrWhiteSpace(start.BeatmapFolder))
        {
            string folder = start.BeatmapFolder;
            if (!Path.IsPathRooted(folder) && !string.IsNullOrWhiteSpace(songs))
                folder = Path.Combine(songs, folder);
            candidates.Add(Path.Combine(folder, Path.GetFileName(file)));
        }
        if (!string.IsNullOrWhiteSpace(start.GameFolder))
            candidates.Add(Path.Combine(start.GameFolder, "Songs", file));
        return candidates.Select(Path.GetFullPath).Distinct(StringComparer.OrdinalIgnoreCase).FirstOrDefault(File.Exists);
    }

    private static string BeatmapContextJson(BeatmapStats stats)
    {
        try
        {
            using var doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(stats.RawJson) ? "{}" : stats.RawJson);
            return JsonSerializer.Serialize(new { stats = doc.RootElement });
        }
        catch (JsonException ex)
        {
            Log.Warning(ex, "Invalid beatmap stats JSON while writing attempt context");
            return "{}";
        }
    }

    private static void UpsertInputSummary(SqliteConnection con, SqliteTransaction tx, long attemptId)
    {
        using var cmd = con.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = """
            INSERT INTO attempt_input_summary(attempt_id)
            VALUES(@attempt_id)
            ON CONFLICT(attempt_id) DO NOTHING
            """;
        cmd.Parameters.AddWithValue("@attempt_id", attemptId);
        cmd.ExecuteNonQuery();
    }

    private static void UpdatePersonalBests(SqliteConnection con, SqliteTransaction tx, long attemptId)
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
            var improved = previous is null || previous == DBNull.Value
                || (lowerIsBetter ? value < Convert.ToDouble(previous) : value > Convert.ToDouble(previous));
            if (!improved)
            {
                continue;
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

    private static string IsoFromUnixSeconds(double unixSeconds) =>
        DateTimeOffset.FromUnixTimeMilliseconds((long)(unixSeconds * 1000)).ToString("O");

    private static void EnsureColumn(SqliteConnection con, string table, string column, string definition)
    {
        using (var info = con.CreateCommand())
        {
            info.CommandText = $"PRAGMA table_info({table})";
            using var reader = info.ExecuteReader();
            while (reader.Read())
            {
                if (string.Equals(reader.GetString(1), column, StringComparison.OrdinalIgnoreCase))
                {
                    return;
                }
            }
        }

        using var alter = con.CreateCommand();
        alter.CommandText = $"ALTER TABLE {table} ADD COLUMN {column} {definition}";
        alter.ExecuteNonQuery();
    }
}
