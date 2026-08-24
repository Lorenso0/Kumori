using Kumori.Core.Models;
using Kumori.Storage;
using Kumori.Tracking;
using Microsoft.Data.Sqlite;
using Xunit;

namespace Kumori.Storage.Tests;

public sealed class ScoreWebhookRepositoryTests : IDisposable
{
    private readonly DirectoryInfo directory = Directory.CreateTempSubdirectory();
    private readonly string database;

    public ScoreWebhookRepositoryTests() =>
        database = Path.Combine(directory.FullName, "tracking.sqlite3");

    [Fact]
    public void EligibleScorePb_IsQueuedOnce_AndRetryKindsRemainIndependent()
    {
        var factory = new SqliteConnectionFactory(database, readOnly: false);
        var sink = new AttemptSqliteSink(factory, (_, work) => work(CancellationToken.None));
        long attemptId = PersistCompleted(sink, 1_000_000, 1);
        var capture = new MovementCaptureStore(factory);
        capture.Start(attemptId);
        capture.AddSamples([new MovementSample { MapTimeMs = 1, MonotonicMs = 1, X = 256, Y = 192 }]);
        capture.Complete(0, "live", "{}");
        var repository = new ScoreWebhookRepository(factory);
        var now = DateTimeOffset.Parse("2026-08-11T10:00:00Z");

        Assert.True(repository.TryEnqueue(attemptId, 99, "Lorenzo", now));
        Assert.False(repository.TryEnqueue(attemptId, 99, "Lorenzo", now));
        Assert.Equal(attemptId, repository.GetRandomReplayAttemptId());
        Assert.Null(repository.GetNextDue(now.AddSeconds(59)));
        ScoreWebhookDelivery queued = Assert.IsType<ScoreWebhookDelivery>(
            repository.GetNextDue(now.AddMinutes(1)));
        Assert.Equal(attemptId, queued.AttemptId);

        repository.ScheduleApiFailure(attemptId, now.AddMinutes(2), "osu_api");
        ScoreWebhookDelivery apiRetry = Assert.IsType<ScoreWebhookDelivery>(
            repository.GetNextDue(now.AddMinutes(2)));
        Assert.Equal(1, apiRetry.ApiFailureAttempts);
        Assert.Equal(0, apiRetry.VerificationAttempts);

        repository.ScheduleVerification(attemptId, now.AddMinutes(3), "not_propagated");
        ScoreWebhookDelivery propagationRetry = Assert.IsType<ScoreWebhookDelivery>(
            repository.GetNextDue(now.AddMinutes(3)));
        Assert.Equal(1, propagationRetry.ApiFailureAttempts);
        Assert.Equal(1, propagationRetry.VerificationAttempts);

        repository.MarkConfirmed(attemptId, 20, 777, now.AddMinutes(3));
        ScoreWebhookDelivery confirmed = Assert.IsType<ScoreWebhookDelivery>(
            repository.GetNextDue(now.AddMinutes(3)));
        Assert.Equal("confirmed", confirmed.State);
        Assert.Equal(20, confirmed.ConfirmedRank);
        Assert.Equal(now.AddMinutes(4), confirmed.ReplayDeadlineAt);
        Assert.Null(repository.GetProfileChange(attemptId));

        using (SqliteConnection connection = factory.Open())
        using (SqliteCommand command = connection.CreateCommand())
        {
            command.CommandText = """
                CREATE TABLE attempt_profile_changes(
                    attempt_id INTEGER PRIMARY KEY,
                    captured_at TEXT NOT NULL,
                    old_total_pp REAL,
                    new_total_pp REAL,
                    old_global_rank INTEGER,
                    new_global_rank INTEGER);
                INSERT INTO attempt_profile_changes(
                    attempt_id, captured_at, old_total_pp, new_total_pp,
                    old_global_rank, new_global_rank)
                VALUES(@id, @captured, 12450.25, 12458.54, 25500, 25420)
                """;
            command.Parameters.AddWithValue("@id", attemptId);
            command.Parameters.AddWithValue("@captured", now.ToString("O"));
            command.ExecuteNonQuery();
        }
        ScoreAlertProfileChange change = Assert.IsType<ScoreAlertProfileChange>(
            repository.GetProfileChange(attemptId));
        Assert.InRange(Assert.IsType<double>(change.PpGained), 8.289, 8.291);
        Assert.Equal(80, change.RanksGained);
        ScoreAlertProfileChange baseline = Assert.IsType<ScoreAlertProfileChange>(
            repository.GetProfileBaseline(attemptId, 99));
        Assert.Equal(12_450.25, baseline.OldTotalPp);
        Assert.Equal(25_500, baseline.OldGlobalRank);

        repository.MarkDelivered(attemptId, now.AddMinutes(4), "attached");
        Assert.Null(repository.GetNextDue(now.AddDays(1)));
    }

    [Fact]
    public void MultiplayerContext_IsNotQueued()
    {
        var factory = new SqliteConnectionFactory(database, readOnly: false);
        var sink = new AttemptSqliteSink(factory, (_, work) => work(CancellationToken.None));
        long attemptId = PersistCompleted(sink, 1_000_000, 1);
        using (SqliteConnection connection = factory.Open())
        using (SqliteCommand command = connection.CreateCommand())
        {
            command.CommandText = "UPDATE attempt_context SET multiplayer_json='{\"room_id\":1}' WHERE attempt_id=@id";
            command.Parameters.AddWithValue("@id", attemptId);
            command.ExecuteNonQuery();
        }

        Assert.False(new ScoreWebhookRepository(factory).TryEnqueue(
            attemptId,
            99,
            "Lorenzo",
            DateTimeOffset.UtcNow));
    }

    [Fact]
    public void ScoreOnlyPb_WithoutPpImprovement_IsNotQueued()
    {
        var factory = new SqliteConnectionFactory(database, readOnly: false);
        var sink = new AttemptSqliteSink(factory, (_, work) => work(CancellationToken.None));
        _ = PersistCompleted(sink, 1_000_000, 1);
        long scoreOnlyPb = PersistCompleted(sink, 1_100_000, 2);

        Assert.False(new ScoreWebhookRepository(factory).TryEnqueue(
            scoreOnlyPb,
            99,
            "Lorenzo",
            DateTimeOffset.UtcNow));
    }

    private static long PersistCompleted(AttemptSqliteSink sink, int score, int ordinal)
    {
        sink.StartAttempt(new AttemptStart
        {
            Identity = "webhook-map",
            WallTime = 1_786_000_000 + ordinal * 200,
            PlayerName = "Lorenzo",
            Artist = "Kano",
            Title = "Yellow",
            Mapper = "Mapper",
            Difficulty = "Insane",
            Checksum = "webhook-checksum",
            BeatmapId = 123,
            BeatmapSetId = 456,
            ClientKind = OsuClientKind.Stable,
            ModsKey = "HD",
            Mods = [new AttemptMod("HD", "{}")],
            BeatmapStats = new BeatmapStats { BaseStars = 6.03, Stars = 6.03, MaxCombo = 767 },
        });
        long attemptId = Assert.IsType<long>(sink.CurrentAttemptId);
        sink.Finalize(new AttemptFinalization(
            "completed",
            "results_screen",
            new AttemptSnapshot
            {
                Identity = "webhook-map",
                WallTime = 1_786_000_100 + ordinal * 200,
                PlayerName = "Lorenzo",
                DurationSeconds = 100,
                Score = score,
                Accuracy = 99.21,
                Grade = "S",
                Pp = 287,
                Combo = 640,
                N300 = 543,
                N100 = 5,
                Misses = 1,
                Progress = 1,
                ModsKey = "HD",
                Mods = [new AttemptMod("HD", "{}")],
                BeatmapStats = new BeatmapStats { BaseStars = 6.03, Stars = 6.03, MaxCombo = 767 },
            },
            ordinal));
        return attemptId;
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        directory.Delete(recursive: true);
    }
}
