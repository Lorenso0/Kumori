using Kumori.Core.Models;
using Kumori.Storage;
using Kumori.Tracking;
using Microsoft.Data.Sqlite;
using System.Diagnostics;
using Xunit;

namespace Kumori.Storage.Tests;

public sealed class MovementCancellationTests : IDisposable
{
    private readonly string _databasePath = Path.Combine(
        Path.GetTempPath(),
        $"kumori-movement-cancellation-{Guid.NewGuid():N}.sqlite3");

    [Fact]
    public void Complete_CancellationAfterReplacementClears_RollsBackTheWholeReplacement()
    {
        var (factory, attemptId) = CreateAttempt("movement-rollback");
        var initial = new MovementCaptureStore(factory);
        initial.Start(attemptId);
        initial.AddSamples([
            new MovementSample { MapTimeMs = 10, MonotonicMs = 10 },
            new MovementSample { MapTimeMs = 20, MonotonicMs = 20, Buttons = 0x10 },
        ]);
        initial.Complete(0, "stable_memory", "{}");

        var replacement = new MovementCaptureStore(factory);
        replacement.Start(attemptId);
        replacement.AddSamples([
            new MovementSample { MapTimeMs = 30, MonotonicMs = 30 },
            new MovementSample { MapTimeMs = 40, MonotonicMs = 40, Buttons = 0x20 },
        ]);
        using var cancelled = new CancellationTokenSource();

        Assert.Throws<OperationCanceledException>(() => replacement.Complete(
            0,
            "stable_replay",
            "{}",
            cancelled.Token,
            afterReplacementCleared: cancelled.Cancel));

        var repository = new MovementRepository(factory);
        Assert.Equal("stable_memory", repository.GetMetadata(attemptId)!.Source);
        Assert.Equal([10d, 20d], repository.GetSamples(attemptId).Select(sample => sample.MapTimeMs));
        using (var connection = factory.Open())
        {
            Assert.Equal(1L, Scalar<long>(connection, "SELECT z_count FROM attempts WHERE id = @id", attemptId));
            Assert.Equal(0L, Scalar<long>(connection, "SELECT x_count FROM attempts WHERE id = @id", attemptId));
            Assert.Equal(1L, Scalar<long>(connection, "SELECT z_count FROM sessions"));
            Assert.Equal(0L, Scalar<long>(connection, "SELECT x_count FROM sessions"));
        }

        // Cancellation leaves the staged replacement intact, so the idle worker
        // can retry it without reconstructing the capture.
        replacement.Complete(0, "stable_replay", "{}", CancellationToken.None);
        Assert.Equal("stable_replay", repository.GetMetadata(attemptId)!.Source);
        Assert.Equal([30d, 40d], repository.GetSamples(attemptId).Select(sample => sample.MapTimeMs));
    }

    [Fact]
    public void GetSamples_PreservesChronologicalOrderAcrossUnorderedChunks()
    {
        var (factory, attemptId) = CreateAttempt("movement-order");
        var capture = new MovementCaptureStore(factory);
        capture.Start(attemptId);
        capture.AddSamples([
            new MovementSample { MapTimeMs = 30, MonotonicMs = 31 },
            new MovementSample { MapTimeMs = 10, MonotonicMs = 11 },
        ]);
        capture.AddSamples([
            new MovementSample { MapTimeMs = 20, MonotonicMs = 21 },
            new MovementSample { MapTimeMs = 10, MonotonicMs = 9 },
        ]);
        capture.Complete(0, "stable_replay", "{}", CancellationToken.None);

        var samples = new MovementRepository(factory).GetSamples(attemptId, CancellationToken.None);

        Assert.Equal([10d, 10d, 20d, 30d], samples.Select(sample => sample.MapTimeMs));
        Assert.Equal([9d, 11d, 21d, 31d], samples.Select(sample => sample.MonotonicMs));
    }

    [Fact]
    public void GetSamples_PreCancelledTokenStopsBeforeOpeningOrDecoding()
    {
        var (factory, attemptId) = CreateAttempt("movement-read-cancel");
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();

        Assert.Throws<OperationCanceledException>(() =>
            new MovementRepository(factory).GetSamples(attemptId, cancelled.Token));
    }

    [Fact]
    public void GetMetadata_PreCancelledTokenStopsBeforeOpeningDatabase()
    {
        var (factory, attemptId) = CreateAttempt("movement-metadata-cancel");
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();

        Assert.Throws<OperationCanceledException>(() =>
            new MovementRepository(factory).GetMetadata(attemptId, cancelled.Token));
    }

    [Fact]
    public void Complete_CancellationInterruptsSqliteBusyWait()
    {
        var (factory, attemptId) = CreateAttempt("movement-busy-cancel");
        var replacement = new MovementCaptureStore(factory);
        replacement.Start(attemptId);
        replacement.AddSamples([new MovementSample { MapTimeMs = 10, MonotonicMs = 10 }]);

        using var blocker = factory.Open();
        using var blockerTransaction = blocker.BeginTransaction();
        using (var command = blocker.CreateCommand())
        {
            command.Transaction = blockerTransaction;
            command.CommandText = "UPDATE attempts SET score = score WHERE id = @id";
            command.Parameters.AddWithValue("@id", attemptId);
            command.ExecuteNonQuery();
        }

        using var cancelled = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));
        var elapsed = Stopwatch.StartNew();
        Assert.Throws<OperationCanceledException>(() =>
            replacement.Complete(0, "stable_replay", "{}", cancelled.Token));
        elapsed.Stop();

        Assert.True(
            elapsed.Elapsed < TimeSpan.FromSeconds(3),
            $"Cancellation remained blocked in SQLite for {elapsed.Elapsed}.");
    }

    private (SqliteConnectionFactory Factory, long AttemptId) CreateAttempt(string identity)
    {
        var factory = new SqliteConnectionFactory(_databasePath, readOnly: false);
        var attemptSink = new AttemptSqliteSink(factory, (_, work) => work(CancellationToken.None));
        var start = new AttemptStart
        {
            Identity = identity,
            WallTime = 1_788_000_000,
        };
        attemptSink.StartAttempt(start);
        var attemptId = Assert.IsType<long>(attemptSink.CurrentAttemptId);
        attemptSink.Finalize(new AttemptFinalization(
            "completed",
            "test_boundary",
            new AttemptSnapshot
            {
                Identity = identity,
                WallTime = 1_788_000_004,
                DurationSeconds = 4,
                Progress = 1,
            },
            Ordinal: 1));
        return (factory, attemptId);
    }

    private static T Scalar<T>(SqliteConnection connection, string sql, long attemptId = 0)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.Parameters.AddWithValue("@id", attemptId);
        return (T)Convert.ChangeType(command.ExecuteScalar()!, typeof(T));
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        if (File.Exists(_databasePath))
        {
            File.Delete(_databasePath);
        }
    }
}
