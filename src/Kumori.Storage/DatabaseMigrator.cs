using Microsoft.Data.Sqlite;

namespace Kumori.Storage;

/// <summary>Ordered, transactional migrations for additive application-owned schema changes.</summary>
internal static class DatabaseMigrator
{
    private const int CurrentVersion = 4;

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
                case 4:
                    ApplyVersion4(connection, transaction);
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

    private static void ApplyVersion4(SqliteConnection connection, SqliteTransaction transaction)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            CREATE TABLE IF NOT EXISTS score_webhook_deliveries(
                attempt_id INTEGER PRIMARY KEY REFERENCES attempts(id) ON DELETE CASCADE,
                player_id INTEGER NOT NULL,
                player_name TEXT NOT NULL,
                state TEXT NOT NULL DEFAULT 'pending',
                verification_attempts INTEGER NOT NULL DEFAULT 0,
                api_failure_attempts INTEGER NOT NULL DEFAULT 0,
                delivery_attempts INTEGER NOT NULL DEFAULT 0,
                next_attempt_at TEXT NOT NULL,
                created_at TEXT NOT NULL,
                confirmed_rank INTEGER,
                confirmed_score_id INTEGER,
                replay_deadline_at TEXT,
                replay_status TEXT,
                failure_category TEXT,
                delivered_at TEXT
            );
            CREATE INDEX IF NOT EXISTS idx_score_webhook_due
                ON score_webhook_deliveries(state, next_attempt_at);
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
