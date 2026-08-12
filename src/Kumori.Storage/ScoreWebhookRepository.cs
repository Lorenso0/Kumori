using Kumori.Core.Models;
using Microsoft.Data.Sqlite;

namespace Kumori.Storage;

public sealed record ScoreWebhookDelivery(
    long AttemptId,
    long PlayerId,
    string PlayerName,
    string State,
    int VerificationAttempts,
    int ApiFailureAttempts,
    int DeliveryAttempts,
    DateTimeOffset NextAttemptAt,
    DateTimeOffset CreatedAt,
    int? ConfirmedRank,
    long? ConfirmedScoreId,
    DateTimeOffset? ReplayDeadlineAt,
    string? ReplayStatus);

public sealed record ScoreAlertProfileChange(
    double? OldTotalPp,
    double? NewTotalPp,
    long? OldGlobalRank,
    long? NewGlobalRank)
{
    public double? PpGained => OldTotalPp is { } oldPp && NewTotalPp is { } newPp
        ? newPp - oldPp
        : null;

    public long? RanksGained => OldGlobalRank is { } oldRank && NewGlobalRank is { } newRank
        ? oldRank - newRank
        : null;
}

public sealed class ScoreWebhookRepository
{
    private readonly SqliteConnectionFactory factory;

    public ScoreWebhookRepository(SqliteConnectionFactory factory) => this.factory = factory;

    public bool TryEnqueue(
        long attemptId,
        long playerId,
        string playerName,
        DateTimeOffset now)
    {
        if (attemptId <= 0 || playerId <= 0 || string.IsNullOrWhiteSpace(playerName))
            return false;
        using var connection = factory.Open();
        using var command = connection.CreateCommand();
        var multiplayerClause = TableExists(connection, "attempt_participants")
            ? "AND NOT EXISTS(SELECT 1 FROM attempt_participants p WHERE p.attempt_id = a.id)"
            : string.Empty;
        command.CommandText = $$"""
            INSERT OR IGNORE INTO score_webhook_deliveries(
                attempt_id, player_id, player_name, state,
                next_attempt_at, created_at)
            SELECT a.id, @player_id, @player_name, 'pending', @next_attempt_at, @created_at
            FROM attempts a
            JOIN beatmaps b ON b.id = a.beatmap_id
            WHERE a.id = @attempt_id
              AND a.outcome = 'completed'
              AND b.beatmap_id IS NOT NULL AND b.beatmap_id > 0
              AND b.set_id IS NOT NULL AND b.set_id > 0
              AND (a.n300 + a.n100 + a.n50 + a.misses) > 0
              AND EXISTS(
                  SELECT 1 FROM attempt_improvements i
                  WHERE i.attempt_id = a.id AND i.metric = 'score')
              AND NOT EXISTS(
                  SELECT 1 FROM attempt_context c
                  WHERE c.attempt_id = a.id
                    AND c.multiplayer_json NOT IN ('', '{}', 'null'))
              {{multiplayerClause}}
            """;
        command.Parameters.AddWithValue("@attempt_id", attemptId);
        command.Parameters.AddWithValue("@player_id", playerId);
        command.Parameters.AddWithValue("@player_name", playerName.Trim());
        command.Parameters.AddWithValue("@created_at", now.ToString("O"));
        command.Parameters.AddWithValue("@next_attempt_at", now.AddMinutes(1).ToString("O"));
        return command.ExecuteNonQuery() > 0;
    }

    public ScoreWebhookDelivery? GetNextDue(DateTimeOffset now)
    {
        if (!factory.DatabaseExists)
            return null;
        using var connection = factory.Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT attempt_id, player_id, player_name, state,
                   verification_attempts, api_failure_attempts, delivery_attempts,
                   next_attempt_at, created_at, confirmed_rank,
                   confirmed_score_id, replay_deadline_at, replay_status
            FROM score_webhook_deliveries
            WHERE state IN ('pending', 'confirmed') AND next_attempt_at <= @now
            ORDER BY next_attempt_at, attempt_id
            LIMIT 1
            """;
        command.Parameters.AddWithValue("@now", now.ToString("O"));
        using var reader = command.ExecuteReader();
        return reader.Read() ? Read(reader) : null;
    }

    public long? GetRandomReplayAttemptId()
    {
        if (!factory.DatabaseExists)
            return null;
        using var connection = factory.Open();
        if (!TableExists(connection, "attempt_movement"))
            return null;
        var multiplayerClause = TableExists(connection, "attempt_participants")
            ? "AND NOT EXISTS(SELECT 1 FROM attempt_participants p WHERE p.attempt_id = a.id)"
            : string.Empty;
        var profileChangePreference = TableExists(connection, "attempt_profile_changes")
            ? "CASE WHEN EXISTS(SELECT 1 FROM attempt_profile_changes pc WHERE pc.attempt_id = a.id) THEN 0 ELSE 1 END,"
            : string.Empty;
        using var command = connection.CreateCommand();
        command.CommandText = $$"""
            SELECT a.id
            FROM attempts a
            JOIN beatmaps b ON b.id = a.beatmap_id
            JOIN attempt_movement m ON m.attempt_id = a.id
            WHERE a.outcome = 'completed'
              AND b.beatmap_id IS NOT NULL AND b.beatmap_id > 0
              AND b.set_id IS NOT NULL AND b.set_id > 0
              AND (a.n300 + a.n100 + a.n50 + a.misses) > 0
              AND m.sample_count > 0
              AND NOT EXISTS(
                  SELECT 1 FROM attempt_context c
                  WHERE c.attempt_id = a.id
                    AND c.multiplayer_json NOT IN ('', '{}', 'null'))
              {{multiplayerClause}}
            ORDER BY {{profileChangePreference}} RANDOM()
            LIMIT 1
            """;
        object? value = command.ExecuteScalar();
        return value is null or DBNull ? null : Convert.ToInt64(value);
    }

    public ScoreAlertProfileChange? GetProfileChange(long attemptId)
    {
        if (!factory.DatabaseExists)
            return null;
        using var connection = factory.Open();
        if (!TableExists(connection, "attempt_profile_changes"))
            return null;
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT old_total_pp, new_total_pp, old_global_rank, new_global_rank
            FROM attempt_profile_changes
            WHERE attempt_id = @attempt_id
            """;
        command.Parameters.AddWithValue("@attempt_id", attemptId);
        using var reader = command.ExecuteReader();
        if (!reader.Read())
            return null;
        return new ScoreAlertProfileChange(
            reader.IsDBNull(0) ? null : reader.GetDouble(0),
            reader.IsDBNull(1) ? null : reader.GetDouble(1),
            reader.IsDBNull(2) ? null : reader.GetInt64(2),
            reader.IsDBNull(3) ? null : reader.GetInt64(3));
    }

    public void ScheduleVerification(long attemptId, DateTimeOffset next, string? category = null)
        => Update(attemptId, """
            UPDATE score_webhook_deliveries
            SET verification_attempts = verification_attempts + 1,
                next_attempt_at = @next, failure_category = @category
            WHERE attempt_id = @attempt_id
            """, next, category);

    public void ScheduleApiFailure(long attemptId, DateTimeOffset next, string? category = null)
        => Update(attemptId, """
            UPDATE score_webhook_deliveries
            SET api_failure_attempts = api_failure_attempts + 1,
                next_attempt_at = @next, failure_category = @category
            WHERE attempt_id = @attempt_id
            """, next, category);

    public void MarkConfirmed(
        long attemptId,
        int rank,
        long scoreId,
        DateTimeOffset now)
        => Update(attemptId, """
            UPDATE score_webhook_deliveries
            SET state = 'confirmed', confirmed_rank = @rank,
                confirmed_score_id = @score_id,
                replay_deadline_at = @replay_deadline,
                next_attempt_at = @next, failure_category = NULL
            WHERE attempt_id = @attempt_id
            """, now, null, rank, scoreId, now.AddMinutes(5));

    public void ScheduleDelivery(long attemptId, DateTimeOffset next, string? category)
        => Update(attemptId, """
            UPDATE score_webhook_deliveries
            SET delivery_attempts = delivery_attempts + 1,
                next_attempt_at = @next, failure_category = @category
            WHERE attempt_id = @attempt_id
            """, next, category);

    public void PostponeDelivery(long attemptId, DateTimeOffset next, string category)
        => Update(attemptId, """
            UPDATE score_webhook_deliveries
            SET next_attempt_at = @next, failure_category = @category
            WHERE attempt_id = @attempt_id
            """, next, category);

    public void MarkDelivered(long attemptId, DateTimeOffset now, string replayStatus)
        => Update(attemptId, """
            UPDATE score_webhook_deliveries
            SET state = 'delivered', delivered_at = @next,
                next_attempt_at = @next, replay_status = @replay_status,
                failure_category = NULL
            WHERE attempt_id = @attempt_id
            """, now, null, replayStatus: replayStatus);

    public void MarkTerminal(long attemptId, string state, DateTimeOffset now, string category)
        => Update(attemptId, """
            UPDATE score_webhook_deliveries
            SET state = @state, next_attempt_at = @next,
                failure_category = @category
            WHERE attempt_id = @attempt_id
            """, now, category, state: state);

    private void Update(
        long attemptId,
        string sql,
        DateTimeOffset next,
        string? category,
        int? rank = null,
        long? scoreId = null,
        DateTimeOffset? replayDeadline = null,
        string? replayStatus = null,
        string? state = null)
    {
        using var connection = factory.Open();
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.Parameters.AddWithValue("@attempt_id", attemptId);
        command.Parameters.AddWithValue("@next", next.ToString("O"));
        command.Parameters.AddWithValue("@category", (object?)category ?? DBNull.Value);
        command.Parameters.AddWithValue("@rank", (object?)rank ?? DBNull.Value);
        command.Parameters.AddWithValue("@score_id", (object?)scoreId ?? DBNull.Value);
        command.Parameters.AddWithValue(
            "@replay_deadline",
            replayDeadline is { } deadline ? deadline.ToString("O") : DBNull.Value);
        command.Parameters.AddWithValue("@replay_status", (object?)replayStatus ?? DBNull.Value);
        command.Parameters.AddWithValue("@state", (object?)state ?? DBNull.Value);
        command.ExecuteNonQuery();
    }

    private static ScoreWebhookDelivery Read(SqliteDataReader reader) => new(
        reader.GetInt64(0),
        reader.GetInt64(1),
        reader.GetString(2),
        reader.GetString(3),
        reader.GetInt32(4),
        reader.GetInt32(5),
        reader.GetInt32(6),
        DateTimeOffset.Parse(reader.GetString(7)),
        DateTimeOffset.Parse(reader.GetString(8)),
        reader.IsDBNull(9) ? null : reader.GetInt32(9),
        reader.IsDBNull(10) ? null : reader.GetInt64(10),
        reader.IsDBNull(11) ? null : DateTimeOffset.Parse(reader.GetString(11)),
        reader.IsDBNull(12) ? null : reader.GetString(12));

    private static bool TableExists(SqliteConnection connection, string table)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT 1 FROM sqlite_master WHERE type='table' AND name=@name";
        command.Parameters.AddWithValue("@name", table);
        return command.ExecuteScalar() is not null;
    }
}
