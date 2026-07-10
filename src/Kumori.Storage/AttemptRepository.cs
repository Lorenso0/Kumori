using Kumori.Core.Models;
using Microsoft.Data.Sqlite;

namespace Kumori.Storage;

/// <summary>
/// Read-only queries over the tracking schema.
/// All methods are synchronous ADO calls — callers run them off the UI
/// thread (Task.Run / background service).
/// </summary>
public sealed class AttemptRepository
{
    private readonly SqliteConnectionFactory _factory;

    public AttemptRepository(SqliteConnectionFactory factory)
    {
        _factory = factory;
    }

    /// <summary>
    /// Keyset-paged recent attempts, newest first. Pass null to get the
    /// first page; pass the smallest id of the previous page to get older rows.
    /// Optional search matches artist/title/difficulty/mods (case-insensitive).
    /// </summary>
    public List<AttemptSummary> GetRecentAttempts(
        long? beforeId = null, int limit = 100, string? search = null)
    {
        var results = new List<AttemptSummary>(limit);
        if (!_factory.DatabaseExists)
        {
            return results;
        }

        using var con = _factory.Open();
        var hasExternalBeatmapId = HasColumn(con, "beatmaps", "beatmap_id");
        var hasSetId = HasColumn(con, "beatmaps", "set_id");
        var hasChecksum = HasColumn(con, "beatmaps", "checksum");
        var hasAttemptImprovements = HasTable(con, "attempt_improvements");
        var hasParticipants = HasTable(con, "attempt_participants");
        var hasMovement = HasTable(con, "attempt_movement");
        var hasMapper = HasColumn(con, "beatmaps", "mapper");
        var hasAdjustedStars = HasColumn(con, "attempts", "adjusted_stars");
        var hasMaxCombo = HasColumn(con, "beatmaps", "max_combo");
        var hasKeyCounts = HasColumn(con, "attempts", "z_count") && HasColumn(con, "attempts", "x_count");
        using var cmd = con.CreateCommand();
        cmd.CommandText = $"""
            SELECT a.id, a.session_id, a.started_at, a.ended_at, a.outcome,
                   a.grade, a.accuracy, a.score, a.pp, a.combo, a.misses,
                   a.mods_key, a.progress,
                   COALESCE(b.artist, ''), COALESCE(b.title, ''),
                   COALESCE(b.difficulty, ''), {(hasAdjustedStars ? "COALESCE(a.base_stars, b.stars)" : "b.stars")},
                   {(hasExternalBeatmapId ? "b.beatmap_id" : "NULL")},
                   {(hasSetId ? "b.set_id" : "NULL")},
                   {(hasChecksum ? "b.checksum" : "b.identity")},
                   {(hasAttemptImprovements ? "EXISTS(SELECT 1 FROM attempt_improvements i WHERE i.attempt_id = a.id)" : "0")},
                   {(hasParticipants ? "EXISTS(SELECT 1 FROM attempt_participants p WHERE p.attempt_id = a.id)" : "0")},
                    {(hasMapper ? "COALESCE(b.mapper, '')" : "''")},
                    {(hasAdjustedStars ? "a.adjusted_stars" : "NULL")},
                    {(hasMovement ? "EXISTS(SELECT 1 FROM attempt_movement m WHERE m.attempt_id = a.id AND m.sample_count > 0)" : "0")},
                    {(hasMaxCombo ? "COALESCE(b.max_combo, 0)" : "0")},
                    {(hasKeyCounts ? "a.z_count" : "0")},
                    {(hasKeyCounts ? "a.x_count" : "0")}
            FROM attempts a
            JOIN beatmaps b ON b.id = a.beatmap_id
            WHERE (@beforeId IS NULL OR a.id < @beforeId)
              AND (@search IS NULL OR
                   b.artist LIKE @search ESCAPE '\' OR
                   b.title LIKE @search ESCAPE '\' OR
                   b.difficulty LIKE @search ESCAPE '\' OR
                   a.mods_key LIKE @search ESCAPE '\')
            ORDER BY a.id DESC
            LIMIT @limit
            """;
        cmd.Parameters.AddWithValue("@beforeId", (object?)beforeId ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@limit", limit);
        cmd.Parameters.AddWithValue("@search",
            string.IsNullOrWhiteSpace(search)
                ? DBNull.Value
                : "%" + EscapeLike(search.Trim()) + "%");

        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            results.Add(new AttemptSummary
            {
                Id = reader.GetInt64(0),
                SessionId = reader.GetInt64(1),
                StartedAt = reader.GetString(2),
                EndedAt = reader.IsDBNull(3) ? null : reader.GetString(3),
                Outcome = reader.GetString(4),
                Grade = reader.IsDBNull(5) ? null : reader.GetString(5),
                Accuracy = reader.GetDouble(6),
                Score = reader.GetInt64(7),
                Pp = reader.GetDouble(8),
                Combo = (int)reader.GetInt64(9),
                BeatmapMaxCombo = (int)reader.GetInt64(25),
                Misses = (int)reader.GetInt64(10),
                ModsKey = reader.GetString(11),
                Progress = reader.GetDouble(12),
                Artist = reader.GetString(13),
                Title = reader.GetString(14),
                Difficulty = reader.GetString(15),
                Stars = reader.IsDBNull(16) ? null : reader.GetDouble(16),
                OsuBeatmapId = reader.IsDBNull(17) ? null : reader.GetInt64(17),
                BeatmapSetId = reader.IsDBNull(18) ? null : reader.GetInt64(18),
                Checksum = reader.IsDBNull(19) ? null : reader.GetString(19),
                IsPersonalBest = reader.GetInt64(20) != 0,
                IsMultiplayer = reader.GetInt64(21) != 0,
                Mapper = reader.GetString(22),
                AdjustedStars = reader.IsDBNull(23) ? null : reader.GetDouble(23),
                HasMovement = reader.GetInt64(24) != 0,
                Key1Count = (int)reader.GetInt64(26),
                Key2Count = (int)reader.GetInt64(27),
            });
        }
        return results;
    }

    private static bool HasColumn(SqliteConnection con, string table, string column)
    {
        using var cmd = con.CreateCommand();
        cmd.CommandText = $"PRAGMA table_info({table})";
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            if (string.Equals(reader.GetString(1), column, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }
        return false;
    }

    private static bool HasTable(SqliteConnection con, string table)
    {
        using var cmd = con.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type = 'table' AND name = @table";
        cmd.Parameters.AddWithValue("@table", table);
        return (long)(cmd.ExecuteScalar() ?? 0L) > 0;
    }

    private static string EscapeLike(string value) =>
        value.Replace(@"\", @"\\").Replace("%", @"\%").Replace("_", @"\_");

    public long CountAttempts()
    {
        if (!_factory.DatabaseExists)
        {
            return 0;
        }
        using var con = _factory.Open();
        using var cmd = con.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM attempts";
        return (long)(cmd.ExecuteScalar() ?? 0L);
    }
}
