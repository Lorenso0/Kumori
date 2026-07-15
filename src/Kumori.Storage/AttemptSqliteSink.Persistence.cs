using Kumori.Tracking;
using Microsoft.Data.Sqlite;
using Serilog;
using System.Text.Json;

namespace Kumori.Storage;

public sealed partial class AttemptSqliteSink
{
    private static void InsertAttempt(
        SqliteConnection con,
        SqliteTransaction tx,
        long attemptId,
        long sessionId,
        AttemptStart start)
    {
        var beatmapId = EnsureBeatmap(con, tx, start);
        using (var insert = con.CreateCommand())
        {
            insert.Transaction = tx;
            insert.CommandText = """
            INSERT INTO attempts(id, session_id, beatmap_id, started_at, started_at_utc_ms, outcome,
                                           progress, duration_seconds, mods_key,
                                           key1_binding, key2_binding, base_stars, adjusted_stars)
            VALUES(@id, @session_id, @beatmap_id, @started_at, @started_at_utc_ms, 'active',
                   0, 0, @mods_key, 'Z', 'X', @base_stars, @adjusted_stars)
            """;
            insert.Parameters.AddWithValue("@id", attemptId);
            insert.Parameters.AddWithValue("@session_id", sessionId);
            insert.Parameters.AddWithValue("@beatmap_id", beatmapId);
            insert.Parameters.AddWithValue("@started_at", IsoFromUnixSeconds(start.WallTime));
            insert.Parameters.AddWithValue("@started_at_utc_ms", UnixMilliseconds(start.WallTime));
            insert.Parameters.AddWithValue("@mods_key", start.ModsKey);
            insert.Parameters.AddWithValue("@base_stars", (object?)start.BeatmapStats.BaseStars ?? DBNull.Value);
            insert.Parameters.AddWithValue("@adjusted_stars", (object?)start.BeatmapStats.Stars ?? DBNull.Value);
            insert.ExecuteNonQuery();
        }

        UpsertSourceContext(con, tx, attemptId, start);
        ReplaceMods(con, tx, attemptId, start.ModsKey, start.Mods);
    }

    private static long EnsureBeatmap(
        SqliteConnection con,
        SqliteTransaction tx,
        AttemptStart start)
    {
        using var cmd = con.CreateCommand();
        cmd.Transaction = tx;
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
        double wallTime,
        long liveTimeMs,
        JudgementCapture.CapturedEvent evt)
    {
        using var cmd = con.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = """
            INSERT INTO attempt_events(attempt_id, captured_at, map_time_ms, event_type, value, data_json)
            VALUES(@attempt_id, @captured_at, @map_time_ms, @event_type, @value, @data_json)
            """;
        cmd.Parameters.AddWithValue("@attempt_id", attemptId);
        cmd.Parameters.AddWithValue("@captured_at", IsoFromUnixSeconds(wallTime));
        cmd.Parameters.AddWithValue("@map_time_ms", liveTimeMs);
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

    private static string IsoFromUnixSeconds(double unixSeconds) =>
        DateTimeOffset.FromUnixTimeMilliseconds((long)(unixSeconds * 1000)).ToString("O");

    private static long UnixMilliseconds(double unixSeconds) => (long)(unixSeconds * 1000);

    private static void EnsureColumn(SqliteConnection con, SqliteTransaction tx, string table, string column, string definition)
    {
        using (var info = con.CreateCommand())
        {
            info.Transaction = tx;
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
        alter.Transaction = tx;
        alter.CommandText = $"ALTER TABLE {table} ADD COLUMN {column} {definition}";
        alter.ExecuteNonQuery();
    }
}
