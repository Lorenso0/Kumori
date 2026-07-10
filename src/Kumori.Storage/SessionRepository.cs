using Kumori.Core.Models;

namespace Kumori.Storage;

public sealed class SessionRepository
{
    private readonly SqliteConnectionFactory _factory;

    public SessionRepository(SqliteConnectionFactory factory)
    {
        _factory = factory;
    }

    public List<SessionSummary> GetRecentSessions(int limit = 50)
    {
        var results = new List<SessionSummary>(limit);
        if (!_factory.DatabaseExists)
        {
            return results;
        }
        using var con = _factory.Open();
        var hasKeys = HasColumn(con, "sessions", "z_count");
        var hasPlayerName = HasColumn(con, "sessions", "player_name");
        var hasLegacy = HasColumn(con, "sessions", "legacy");
        var hasChanges = HasTable(con, "attempt_profile_changes");
        using var cmd = con.CreateCommand();
        cmd.CommandText = $"""
            SELECT s.id, s.started_at, s.ended_at, s.active_seconds,
                   {(hasPlayerName ? "s.player_name" : "NULL")}, s.interrupted,
                   COUNT(a.id),
                   SUM(CASE WHEN a.outcome = 'completed' THEN 1 ELSE 0 END),
                   {(hasKeys ? "s.z_count, s.x_count, s.key1_binding, s.key2_binding" : "0, 0, 'Z', 'X'")},
                   COALESCE(MAX(a.pp), 0),
                   COALESCE(SUM(a.misses), 0),
                   AVG(CASE WHEN a.unstable_rate > 0 THEN a.unstable_rate END),
                   {(hasLegacy ? "s.legacy" : "0")},
                   {(hasChanges
                        ? "COALESCE((SELECT SUM(c.new_total_pp - c.old_total_pp) FROM attempt_profile_changes c JOIN attempts a2 ON a2.id = c.attempt_id WHERE a2.session_id = s.id), 0)"
                        : "0")}
            FROM sessions s
            LEFT JOIN attempts a ON a.session_id = s.id
            GROUP BY s.id
            ORDER BY s.id DESC
            LIMIT @limit
            """;
        cmd.Parameters.AddWithValue("@limit", limit);
        using var r = cmd.ExecuteReader();
        while (r.Read())
        {
            results.Add(new SessionSummary
            {
                Id = r.GetInt64(0),
                StartedAt = r.GetString(1),
                EndedAt = r.IsDBNull(2) ? null : r.GetString(2),
                ActiveSeconds = r.GetDouble(3),
                PlayerName = r.IsDBNull(4) ? null : r.GetString(4),
                Interrupted = r.GetInt64(5) != 0,
                AttemptCount = (int)r.GetInt64(6),
                CompletedCount = r.IsDBNull(7) ? 0 : (int)r.GetInt64(7),
                ZCount = (int)r.GetInt64(8),
                XCount = (int)r.GetInt64(9),
                Key1Binding = r.IsDBNull(10) ? "Z" : r.GetString(10),
                Key2Binding = r.IsDBNull(11) ? "X" : r.GetString(11),
                BestPp = r.IsDBNull(12) ? 0 : r.GetDouble(12),
                TotalMisses = r.IsDBNull(13) ? 0 : (int)r.GetInt64(13),
                AverageUr = r.IsDBNull(14) ? 0 : r.GetDouble(14),
                Legacy = r.GetInt64(15) != 0,
                AccountPpGain = r.IsDBNull(16) ? 0 : r.GetDouble(16),
            });
        }
        return results;
    }

    private static bool HasColumn(Microsoft.Data.Sqlite.SqliteConnection con, string table, string column)
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

    private static bool HasTable(Microsoft.Data.Sqlite.SqliteConnection con, string table)
    {
        using var cmd = con.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type = 'table' AND name = @table";
        cmd.Parameters.AddWithValue("@table", table);
        return (long)(cmd.ExecuteScalar() ?? 0L) > 0;
    }
}
