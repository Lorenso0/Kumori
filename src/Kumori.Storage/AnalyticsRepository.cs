using Kumori.Core.Models;

namespace Kumori.Storage;

public sealed class AnalyticsRepository
{
    private readonly SqliteConnectionFactory _factory;

    public AnalyticsRepository(SqliteConnectionFactory factory)
    {
        _factory = factory;
    }

    public AnalyticsSummary GetSummary(int days = 30)
    {
        if (!_factory.DatabaseExists)
        {
            return new AnalyticsSummary();
        }

        using var con = _factory.Open();
        using var totals = con.CreateCommand();
        totals.CommandText = """
            SELECT COUNT(*),
                   SUM(CASE WHEN outcome='completed' THEN 1 ELSE 0 END),
                   SUM(CASE WHEN outcome='failed' THEN 1 ELSE 0 END),
                   COALESCE(AVG(CASE WHEN outcome='completed' THEN accuracy END), 0),
                   COALESCE(MAX(pp), 0),
                   COALESCE(SUM(score), 0)
            FROM attempts
            """;
        using var r = totals.ExecuteReader();
        r.Read();
        var summary = new AnalyticsSummary
        {
            Attempts = r.GetInt64(0),
            Completed = r.IsDBNull(1) ? 0 : r.GetInt64(1),
            Failed = r.IsDBNull(2) ? 0 : r.GetInt64(2),
            AverageAccuracy = r.GetDouble(3),
            BestPp = r.GetDouble(4),
            TotalScore = r.GetInt64(5),
        };
        r.Close();

        using var daily = con.CreateCommand();
        daily.CommandText = """
            SELECT substr(started_at, 1, 10) day,
                   COUNT(*) attempts,
                   SUM(CASE WHEN outcome='completed' THEN 1 ELSE 0 END) completed,
                   COALESCE(AVG(CASE WHEN outcome='completed' THEN accuracy END), 0) average_accuracy,
                   COALESCE(MAX(pp), 0) best_pp
            FROM attempts
            GROUP BY substr(started_at, 1, 10)
            ORDER BY day DESC
            LIMIT @days
            """;
        daily.Parameters.AddWithValue("@days", Math.Clamp(days, 1, 366));
        using var dailyReader = daily.ExecuteReader();
        var rows = new List<DailyAttemptTrend>();
        while (dailyReader.Read())
        {
            rows.Add(new DailyAttemptTrend
            {
                Day = dailyReader.GetString(0),
                Attempts = dailyReader.GetInt64(1),
                Completed = dailyReader.IsDBNull(2) ? 0 : dailyReader.GetInt64(2),
                AverageAccuracy = dailyReader.GetDouble(3),
                BestPp = dailyReader.GetDouble(4),
            });
        }
        dailyReader.Close();
        var keys = ReadKeyTotals(con);
        return summary with
        {
            Daily = rows,
            LatestAccountChange = ReadLatestSessionAccountChange(con),
            TotalDurationSeconds = ReadTotalDurationSeconds(con),
            ZTotal = keys.Z,
            XTotal = keys.X,
            Key1Binding = keys.Key1,
            Key2Binding = keys.Key2,
            LastSyncedAt = ReadLastSynced(con),
        };
    }

    private static double ReadTotalDurationSeconds(Microsoft.Data.Sqlite.SqliteConnection con)
    {
        if (!HasColumn(con, "attempts", "duration_seconds"))
        {
            return 0;
        }

        using var cmd = con.CreateCommand();
        cmd.CommandText = "SELECT COALESCE(SUM(duration_seconds), 0) FROM attempts";
        return Convert.ToDouble(cmd.ExecuteScalar() ?? 0d, System.Globalization.CultureInfo.InvariantCulture);
    }

    private static (long Z, long X, string Key1, string Key2) ReadKeyTotals(
        Microsoft.Data.Sqlite.SqliteConnection con)
    {
        if (!HasColumn(con, "attempts", "z_count"))
        {
            return (0, 0, "Z", "X");
        }
        long z = 0, x = 0;
        using (var cmd = con.CreateCommand())
        {
            cmd.CommandText = "SELECT COALESCE(SUM(z_count), 0), COALESCE(SUM(x_count), 0) FROM attempts";
            using var r = cmd.ExecuteReader();
            if (r.Read())
            {
                z = r.GetInt64(0);
                x = r.GetInt64(1);
            }
        }
        string key1 = "Z", key2 = "X";
        if (HasColumn(con, "attempts", "key1_binding"))
        {
            using var cmd = con.CreateCommand();
            cmd.CommandText = "SELECT key1_binding, key2_binding FROM attempts ORDER BY id DESC LIMIT 1";
            using var r = cmd.ExecuteReader();
            if (r.Read())
            {
                key1 = r.IsDBNull(0) ? "Z" : r.GetString(0);
                key2 = r.IsDBNull(1) ? "X" : r.GetString(1);
            }
        }
        return (z, x, key1, key2);
    }

    private static string? ReadLastSynced(Microsoft.Data.Sqlite.SqliteConnection con)
    {
        if (!HasTable(con, "profile_snapshots"))
        {
            return null;
        }
        using var cmd = con.CreateCommand();
        cmd.CommandText = "SELECT MAX(captured_at) FROM profile_snapshots";
        return cmd.ExecuteScalar() is string value && value.Length > 0 ? value : null;
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

    private static AccountChangeSummary? ReadLatestSessionAccountChange(Microsoft.Data.Sqlite.SqliteConnection con)
    {
        if (!HasTable(con, "attempt_profile_changes") || !HasTable(con, "sessions"))
        {
            return null;
        }

        using var cmd = con.CreateCommand();
        cmd.CommandText = """
            SELECT old_total_pp, new_total_pp,
                   old_global_rank, new_global_rank,
                   old_accuracy, new_accuracy,
                   old_play_count, new_play_count
            FROM attempt_profile_changes
            WHERE attempt_id IN (
                SELECT id
                FROM attempts
                WHERE session_id = (SELECT id FROM sessions ORDER BY id DESC LIMIT 1)
            )
            ORDER BY captured_at DESC, attempt_id DESC
            LIMIT 1
            """;
        using var r = cmd.ExecuteReader();
        if (!r.Read())
        {
            return null;
        }

        return new AccountChangeSummary
        {
            OldTotalPp = r.IsDBNull(0) ? null : r.GetDouble(0),
            NewTotalPp = r.IsDBNull(1) ? null : r.GetDouble(1),
            OldGlobalRank = r.IsDBNull(2) ? null : r.GetInt64(2),
            NewGlobalRank = r.IsDBNull(3) ? null : r.GetInt64(3),
            OldAccuracy = r.IsDBNull(4) ? null : r.GetDouble(4),
            NewAccuracy = r.IsDBNull(5) ? null : r.GetDouble(5),
            OldPlayCount = r.IsDBNull(6) ? null : r.GetInt64(6),
            NewPlayCount = r.IsDBNull(7) ? null : r.GetInt64(7),
        };
    }

    private static bool HasTable(Microsoft.Data.Sqlite.SqliteConnection con, string table)
    {
        using var cmd = con.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type = 'table' AND name = @table";
        cmd.Parameters.AddWithValue("@table", table);
        return (long)(cmd.ExecuteScalar() ?? 0L) > 0;
    }
}
