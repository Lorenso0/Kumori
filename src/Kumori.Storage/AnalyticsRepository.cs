using Kumori.Core.Models;

namespace Kumori.Storage;

public sealed class AnalyticsRepository
{
    private readonly SqliteConnectionFactory _factory;

    public AnalyticsRepository(SqliteConnectionFactory factory)
    {
        _factory = factory;
    }

    public AnalyticsSummary GetSummary(int days = int.MaxValue)
    {
        if (!_factory.DatabaseExists)
        {
            return new AnalyticsSummary();
        }

        using var con = _factory.Open();
        var hasUtc = HasColumn(con, "attempts", "started_at_utc_ms");
        var hasDuration = HasColumn(con, "attempts", "duration_seconds");
        var hasKeys = HasColumn(con, "attempts", "z_count");
        using var totals = con.CreateCommand();
        totals.CommandText = $"""
            SELECT COUNT(*),
                   SUM(CASE WHEN outcome='completed' THEN 1 ELSE 0 END),
                   SUM(CASE WHEN outcome='failed' THEN 1 ELSE 0 END),
                   COALESCE(AVG(CASE WHEN outcome='completed' THEN accuracy END), 0),
                   COALESCE(MAX(pp), 0),
                   COALESCE(SUM(score), 0),
                   {(hasDuration ? "COALESCE(SUM(a.duration_seconds), 0)" : "0")},
                   {(hasKeys ? "COALESCE(SUM(a.z_count), 0)" : "0")},
                   {(hasKeys ? "COALESCE(SUM(a.x_count), 0)" : "0")}
            FROM attempts a
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
            TotalDurationSeconds = Convert.ToDouble(r.GetValue(6), System.Globalization.CultureInfo.InvariantCulture),
            ZTotal = Convert.ToInt64(r.GetValue(7), System.Globalization.CultureInfo.InvariantCulture),
            XTotal = Convert.ToInt64(r.GetValue(8), System.Globalization.CultureInfo.InvariantCulture),
        };
        r.Close();

        var dailyAccountChanges = ReadDailyProfileChanges(con);
        using var daily = con.CreateCommand();
        var dayExpression = hasUtc ? "date(a.started_at_utc_ms / 1000, 'unixepoch', 'localtime')" : "substr(a.started_at, 1, 10)";
        daily.CommandText = $"""
            SELECT {dayExpression} day,
                   COUNT(*) attempts,
                   SUM(CASE WHEN outcome='completed' THEN 1 ELSE 0 END) completed,
                   COALESCE(AVG(CASE WHEN outcome='completed' THEN accuracy END), 0) average_accuracy,
                   COALESCE(MAX(pp), 0) best_pp
            FROM attempts a
            GROUP BY {dayExpression}
            ORDER BY day DESC
            LIMIT @days
            """;
        daily.Parameters.AddWithValue("@days", Math.Clamp(days, 1, int.MaxValue));
        using var dailyReader = daily.ExecuteReader();
        var rows = new List<DailyAttemptTrend>();
        while (dailyReader.Read())
        {
            var day = dailyReader.GetString(0);
            dailyAccountChanges.TryGetValue(day, out var accountChange);
            rows.Add(new DailyAttemptTrend
            {
                Day = day,
                Attempts = dailyReader.GetInt64(1),
                Completed = dailyReader.IsDBNull(2) ? 0 : dailyReader.GetInt64(2),
                AverageAccuracy = dailyReader.GetDouble(3),
                BestPp = dailyReader.GetDouble(4),
                PpChange = accountChange?.PpChange,
                RankChange = accountChange?.RankChange,
            });
        }
        dailyReader.Close();
        var keyBindings = ReadLatestKeyBindings(con);
        return summary with
        {
            Daily = rows,
            LatestAccountChange = ReadLatestProfileChange(con),
            Key1Binding = keyBindings.Key1,
            Key2Binding = keyBindings.Key2,
            LastSyncedAt = ReadLastSynced(con),
        };
    }

    private static IReadOnlyDictionary<string, DailyAccountChange> ReadDailyProfileChanges(
        Microsoft.Data.Sqlite.SqliteConnection con)
    {
        var changes = new Dictionary<string, DailyAccountChange>(StringComparer.Ordinal);
        if (!HasTable(con, "profile_snapshots"))
        {
            return changes;
        }

        using var cmd = con.CreateCommand();
        cmd.CommandText = """
            SELECT captured_at, total_pp, global_rank
            FROM profile_snapshots
            WHERE player_id = (
                SELECT player_id
                FROM profile_snapshots
                WHERE player_id IS NOT NULL
                ORDER BY id DESC
                LIMIT 1)
            ORDER BY captured_at ASC, id ASC
            """;
        using var reader = cmd.ExecuteReader();
        var readings = new List<DailyProfileReading>();
        while (reader.Read())
        {
            if (!DateTimeOffset.TryParse(
                    reader.GetString(0),
                    System.Globalization.CultureInfo.InvariantCulture,
                    System.Globalization.DateTimeStyles.AllowWhiteSpaces,
                    out var capturedAt))
            {
                continue;
            }

            readings.Add(new DailyProfileReading(
                capturedAt.ToLocalTime().ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture),
                reader.IsDBNull(1) ? null : reader.GetDouble(1),
                reader.IsDBNull(2) ? null : reader.GetInt64(2)));
        }

        DailyProfileReading? previousDayLast = null;
        foreach (var group in readings.GroupBy(reading => reading.Day, StringComparer.Ordinal))
        {
            var first = group.First();
            var latest = group.Last();
            var baseline = previousDayLast ?? first;
            changes[group.Key] = new DailyAccountChange(
                baseline.TotalPp is { } oldPp && latest.TotalPp is { } newPp ? newPp - oldPp : null,
                baseline.GlobalRank is { } oldRank && latest.GlobalRank is { } newRank ? oldRank - newRank : null);
            previousDayLast = latest;
        }

        return changes;
    }

    private sealed record DailyProfileReading(string Day, double? TotalPp, long? GlobalRank);
    private sealed record DailyAccountChange(double? PpChange, long? RankChange);

    private static (string Key1, string Key2) ReadLatestKeyBindings(Microsoft.Data.Sqlite.SqliteConnection con)
    {
        if (!HasColumn(con, "attempts", "key1_binding")) return ("Z", "X");
        using var cmd = con.CreateCommand();
        cmd.CommandText = "SELECT key1_binding, key2_binding FROM attempts ORDER BY id DESC LIMIT 1";
        using var reader = cmd.ExecuteReader();
        return reader.Read()
            ? (reader.IsDBNull(0) ? "Z" : reader.GetString(0), reader.IsDBNull(1) ? "X" : reader.GetString(1))
            : ("Z", "X");
    }

    private static string? ReadLastSynced(Microsoft.Data.Sqlite.SqliteConnection con)
    {
        if (!HasTable(con, "profile_snapshots"))
        {
            return null;
        }
        using var cmd = con.CreateCommand();
        cmd.CommandText = "SELECT captured_at FROM profile_snapshots ORDER BY id DESC LIMIT 1";
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

    private static AccountChangeSummary? ReadLatestProfileChange(Microsoft.Data.Sqlite.SqliteConnection con)
    {
        if (HasTable(con, "profile_snapshots"))
        {
            using var snapshots = con.CreateCommand();
            snapshots.CommandText = """
                SELECT total_pp, global_rank, accuracy, play_count
                FROM profile_snapshots
                WHERE player_id = (
                    SELECT player_id FROM profile_snapshots
                    WHERE player_id IS NOT NULL
                    ORDER BY id DESC
                    LIMIT 1)
                ORDER BY id ASC
                LIMIT 1;
                SELECT total_pp, global_rank, accuracy, play_count
                FROM profile_snapshots
                WHERE player_id = (
                    SELECT player_id FROM profile_snapshots
                    WHERE player_id IS NOT NULL
                    ORDER BY id DESC
                    LIMIT 1)
                ORDER BY id DESC
                LIMIT 1
                """;
            using var reader = snapshots.ExecuteReader();
            if (!reader.Read()) return null;
            var first = ReadAccountChangeRow(reader);
            if (!reader.NextResult() || !reader.Read()) return null;
            var latest = ReadAccountChangeRow(reader);
            return new AccountChangeSummary
            {
                OldTotalPp = first.TotalPp,
                NewTotalPp = latest.TotalPp,
                OldGlobalRank = first.GlobalRank,
                NewGlobalRank = latest.GlobalRank,
                OldAccuracy = first.Accuracy,
                NewAccuracy = latest.Accuracy,
                OldPlayCount = first.PlayCount,
                NewPlayCount = latest.PlayCount,
            };
        }

        return ReadLatestAttemptProfileChange(con);
    }

    // Kept solely for databases created by older versions that do not contain
    // profile snapshots yet. New dashboard values compare the first and latest
    // snapshots for the active player profile.
    private static AccountChangeSummary? ReadLatestAttemptProfileChange(Microsoft.Data.Sqlite.SqliteConnection con)
    {
        if (!HasTable(con, "attempt_profile_changes") || !HasTable(con, "sessions")) return null;

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

    private static (double? TotalPp, long? GlobalRank, double? Accuracy, long? PlayCount) ReadAccountChangeRow(Microsoft.Data.Sqlite.SqliteDataReader reader) =>
        (reader.IsDBNull(0) ? null : reader.GetDouble(0),
         reader.IsDBNull(1) ? null : reader.GetInt64(1),
         reader.IsDBNull(2) ? null : reader.GetDouble(2),
         reader.IsDBNull(3) ? null : reader.GetInt64(3));

    private static bool HasTable(Microsoft.Data.Sqlite.SqliteConnection con, string table)
    {
        using var cmd = con.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type = 'table' AND name = @table";
        cmd.Parameters.AddWithValue("@table", table);
        return (long)(cmd.ExecuteScalar() ?? 0L) > 0;
    }
}
