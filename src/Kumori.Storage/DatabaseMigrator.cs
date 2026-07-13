using Microsoft.Data.Sqlite;

namespace Kumori.Storage;

/// <summary>Ordered, transactional migrations for additive application-owned schema changes.</summary>
internal static class DatabaseMigrator
{
    private const int CurrentVersion = 3;

    public static void Apply(SqliteConnection connection)
    {
        var version = ReadVersion(connection);
        while (version < CurrentVersion)
        {
            var next = version + 1;
            using var transaction = connection.BeginTransaction();
            switch (next)
            {
                // The base-schema compatibility pass already performs the
                // historical version-2 additive upgrades transactionally.
                case 2:
                    break;
                case 3:
                    ApplyVersion3(connection, transaction);
                    break;
                default:
                    throw new InvalidOperationException($"No database migration exists for version {next}.");
            }

            using var update = connection.CreateCommand();
            update.Transaction = transaction;
            update.CommandText = "INSERT OR REPLACE INTO metadata(key, value) VALUES('schema_version', @version)";
            update.Parameters.AddWithValue("@version", next.ToString(System.Globalization.CultureInfo.InvariantCulture));
            update.ExecuteNonQuery();
            transaction.Commit();
            version = next;
        }
    }

    private static int ReadVersion(SqliteConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT value FROM metadata WHERE key='schema_version'";
        return int.TryParse(command.ExecuteScalar() as string, out var version) ? version : 2;
    }

    private static void ApplyVersion3(SqliteConnection connection, SqliteTransaction transaction)
    {
        AddColumnIfMissing(connection, transaction, "attempts", "started_at_utc_ms", "INTEGER");
        AddColumnIfMissing(connection, transaction, "attempts", "ended_at_utc_ms", "INTEGER");
        AddColumnIfMissing(connection, transaction, "sessions", "started_at_utc_ms", "INTEGER");
        AddColumnIfMissing(connection, transaction, "sessions", "ended_at_utc_ms", "INTEGER");

        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            UPDATE attempts
            SET started_at_utc_ms = CAST(strftime('%s', started_at) AS INTEGER) * 1000
            WHERE started_at_utc_ms IS NULL;
            UPDATE attempts
            SET ended_at_utc_ms = CAST(strftime('%s', ended_at) AS INTEGER) * 1000
            WHERE ended_at IS NOT NULL AND ended_at_utc_ms IS NULL;
            UPDATE sessions
            SET started_at_utc_ms = CAST(strftime('%s', started_at) AS INTEGER) * 1000
            WHERE started_at_utc_ms IS NULL;
            UPDATE sessions
            SET ended_at_utc_ms = CAST(strftime('%s', ended_at) AS INTEGER) * 1000
            WHERE ended_at IS NOT NULL AND ended_at_utc_ms IS NULL;

            CREATE INDEX IF NOT EXISTS idx_attempt_started_utc ON attempts(started_at_utc_ms DESC);
            CREATE INDEX IF NOT EXISTS idx_session_started_utc ON sessions(started_at_utc_ms DESC);
            """;
        command.ExecuteNonQuery();
    }

    private static void AddColumnIfMissing(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string table,
        string column,
        string definition)
    {
        using (var info = connection.CreateCommand())
        {
            info.Transaction = transaction;
            info.CommandText = $"PRAGMA table_info({table})";
            using var reader = info.ExecuteReader();
            while (reader.Read())
            {
                if (string.Equals(reader.GetString(1), column, StringComparison.OrdinalIgnoreCase)) return;
            }
        }

        using var alter = connection.CreateCommand();
        alter.Transaction = transaction;
        alter.CommandText = $"ALTER TABLE {table} ADD COLUMN {column} {definition}";
        alter.ExecuteNonQuery();
    }
}
