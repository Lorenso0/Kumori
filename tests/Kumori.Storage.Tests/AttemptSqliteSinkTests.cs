using Kumori.Storage;
using Kumori.Tracking;
using Kumori.Core.Models;
using Microsoft.Data.Sqlite;
using Xunit;
using static Kumori.Tracking.AttemptStateMachine;

namespace Kumori.Storage.Tests;

public class AttemptSqliteSinkTests : IDisposable
{
    private readonly string _dbPath;

    public AttemptSqliteSinkTests()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"kumori-sink-{Guid.NewGuid():N}.sqlite3");
    }

    [Fact]
    public void CompletedAttempt_WritesRowsReadableByRepositories()
    {
        var sink = CreateSink();
        var tracker = new AttemptTracker(sink);

        tracker.Ingest(Play(0, score: 0, n300: 0));
        tracker.Ingest(Play(1.2, live: 1200, score: 10_000, n300: 60, progress: 0.5));
        tracker.Ingest(Play(3.5, live: 3500, score: 55_000, n300: 300, miss: 1, progress: 1));
        tracker.Ingest(Results(3.7, score: 55_000, n300: 300, miss: 1));

        var repo = new AttemptRepository(new SqliteConnectionFactory(_dbPath));
        var row = Assert.Single(repo.GetRecentAttempts(limit: 10));
        Assert.Equal("completed", row.Outcome);
        Assert.Equal("Song", row.Title);
        Assert.Equal("HDDT", row.ModsKey);
        Assert.Equal(55_000, row.Score);
        var reloaded = Assert.IsType<AttemptSummary>(repo.GetAttempt(row.Id));
        Assert.Equal(row with { Mods = Array.Empty<ModEntry>() }, reloaded with { Mods = Array.Empty<ModEntry>() });
        Assert.Equal(row.Mods.ToArray(), reloaded.Mods.ToArray());
        Assert.Null(repo.GetAttempt(row.Id + 1));

        var visibleSession = Assert.Single(
            new SessionRepository(new SqliteConnectionFactory(_dbPath))
                .GetSessions([row.SessionId]));
        Assert.Equal(row.SessionId, visibleSession.Id);
        Assert.Equal(1, visibleSession.AttemptCount);

        using var con = Open();
        Assert.Equal(1, Scalar<long>(con, "SELECT COUNT(*) FROM sessions"));
        Assert.Equal(1, Scalar<long>(con, "SELECT COUNT(*) FROM attempt_timing"));
        Assert.Equal(1, Scalar<long>(con, "SELECT COUNT(*) FROM attempt_context"));
        Assert.Equal(5, Scalar<long>(con, "SELECT COUNT(*) FROM personal_bests"));
        Assert.True(Scalar<long>(con, "SELECT COUNT(*) FROM attempt_events WHERE event_type='checkpoint'") > 0);
        Assert.Equal("DT", Scalar<string>(con, "SELECT acronym FROM attempt_mods WHERE position=1"));
    }

    [Fact]
    public void CompletedAttempt_WritesRichHitCounts()
    {
        var sink = CreateSink();
        var tracker = new AttemptTracker(sink);

        tracker.Ingest(Play(0, score: 0, n300: 0));
        tracker.Ingest(Play(3.5, live: 3500, score: 55_000, n300: 300, miss: 1, progress: 1));
        tracker.Ingest(Results(3.7, score: 55_000, n300: 300, miss: 1) with
        {
            Play = Results(3.7, score: 55_000, n300: 300, miss: 1).Play with
            {
                Geki = 2,
                Katu = 3,
                LargeTickHit = 74,
                LargeTickMiss = 5,
                SmallTickHit = 6,
                SmallTickMiss = 7,
                SliderTailHit = 84,
                SliderTailMiss = 8,
            },
        });

        using var con = Open();
        Assert.Equal(2, Scalar<long>(con, "SELECT geki FROM attempts"));
        Assert.Equal(3, Scalar<long>(con, "SELECT katu FROM attempts"));
        Assert.Equal(74, Scalar<long>(con, "SELECT large_tick_hits FROM attempts"));
        Assert.Equal(5, Scalar<long>(con, "SELECT large_tick_misses FROM attempts"));
        Assert.Equal(6, Scalar<long>(con, "SELECT small_tick_hits FROM attempts"));
        Assert.Equal(7, Scalar<long>(con, "SELECT small_tick_misses FROM attempts"));
        Assert.Equal(84, Scalar<long>(con, "SELECT slider_tail_hits FROM attempts"));
        Assert.Equal(8, Scalar<long>(con, "SELECT slider_tail_misses FROM attempts"));
    }

    [Fact]
    public void EmptyRetryPulse_IsDeletedAndOrdinalReused()
    {
        var sink = CreateSink();
        var tracker = new AttemptTracker(sink);

        tracker.Ingest(Play(0));
        var discardedId = Assert.IsType<long>(sink.CurrentAttemptId);
        tracker.Ingest(Menu(0.1));
        tracker.Ingest(Play(0.2));
        Assert.Equal(discardedId, Assert.IsType<long>(sink.CurrentAttemptId));
        tracker.Ingest(Play(3.4, live: 3400, score: 1000, n300: 20, progress: 0.4));
        tracker.Ingest(Results(3.5, score: 1000, n300: 20, progress: 0.4));

        using var con = Open();
        Assert.Equal(1, Scalar<long>(con, "SELECT COUNT(*) FROM attempts"));
        Assert.Equal(1, Scalar<long>(con, "SELECT MIN(id) FROM attempts"));
    }

    [Fact]
    public void InvalidTooShortFinalAttempt_IsDeleted()
    {
        var sink = CreateSink();
        var tracker = new AttemptTracker(sink);

        tracker.Ingest(Play(0));
        tracker.Ingest(Play(1.0, live: 1000, score: 1000, n300: 5));
        tracker.Ingest(Results(1.2, score: 1000, n300: 5));

        using var con = Open();
        Assert.Equal(0, Scalar<long>(con, "SELECT COUNT(*) FROM attempts"));
    }

    [Fact]
    public void EndSession_PersistsStagedRowsAndMarksInterrupted()
    {
        var sink = CreateSink();
        var tracker = new AttemptTracker(sink);

        tracker.Ingest(Play(0));
        var attemptId = Assert.IsType<long>(sink.CurrentAttemptId);
        sink.EndSession(interrupted: true);

        using var con = Open();
        Assert.Equal(1, Scalar<long>(con, "SELECT interrupted FROM sessions"));
        Assert.False(string.IsNullOrWhiteSpace(Scalar<string>(con, "SELECT ended_at FROM sessions")));
        Assert.Equal(1, Scalar<long>(con, "SELECT COUNT(*) FROM attempts"));
        Assert.Equal(attemptId, Scalar<long>(con, "SELECT id FROM attempts"));
        Assert.Equal("active", Scalar<string>(con, "SELECT outcome FROM attempts"));
    }

    [Fact]
    public void MovementCaptureStore_WritesMovementAndInputSummary()
    {
        var factory = new SqliteConnectionFactory(_dbPath, readOnly: false);
        var sink = new AttemptSqliteSink(factory, (_, work) => work(CancellationToken.None));
        sink.StartAttempt(new AttemptStart
        {
            Identity = "mapA",
            WallTime = 1_788_000_000,
            ModsKey = "NM",
            Artist = "Artist",
            Title = "Song",
            Difficulty = "Extra",
        });
        var attemptId = Assert.IsType<long>(sink.CurrentAttemptId);
        PersistStartedAttempt(sink, 1_788_000_003);
        var capture = new MovementCaptureStore(factory);

        capture.Start(attemptId);
        capture.AddSamples([
            new MovementSample { MapTimeMs = 0, MonotonicMs = 0, X = 256, Y = 192, Buttons = 0 },
            new MovementSample { MapTimeMs = 10, MonotonicMs = 10, X = 257, Y = 193, Buttons = 0x10 },
            new MovementSample { MapTimeMs = 20, MonotonicMs = 20, X = 258, Y = 194, Buttons = 0 },
            new MovementSample { MapTimeMs = 30, MonotonicMs = 30, X = 259, Y = 195, Buttons = 0x20 },
            new MovementSample { MapTimeMs = 40, MonotonicMs = 40, X = 260, Y = 196, Buttons = 0 },
        ]);
        capture.Complete(2, "live", """{"method":"test"}""");

        var movement = new MovementRepository(factory);
        Assert.Equal(5, movement.GetSamples(attemptId).Count);
        Assert.Equal(5, movement.GetMetadata(attemptId)!.SampleCount);

        var details = new AttemptDetailsRepository(factory).GetDetails(attemptId)!;
        Assert.True(details.Movement!.Available);
        Assert.Equal(1, details.Input!.Key1Presses);
        Assert.Equal(1, details.Input.Key2Presses);
        Assert.Equal(1, details.Key1Count);
        Assert.Equal(1, details.Key2Count);
    }

    [Fact]
    public void MovementCaptureStore_PreservesExistingCaptureUntilReplacementCompletes()
    {
        var factory = new SqliteConnectionFactory(_dbPath, readOnly: false);
        var sink = new AttemptSqliteSink(factory, (_, work) => work(CancellationToken.None));
        sink.StartAttempt(new AttemptStart { Identity = "atomic-map", WallTime = 1_788_000_000 });
        var attemptId = Assert.IsType<long>(sink.CurrentAttemptId);
        PersistStartedAttempt(sink, 1_788_000_003, identity: "atomic-map");
        var initial = new MovementCaptureStore(factory);
        initial.Start(attemptId);
        initial.AddSamples([new MovementSample { MapTimeMs = 1, MonotonicMs = 1 }]);
        initial.Complete(0, "stable_memory", "{}");

        var replacement = new MovementCaptureStore(factory);
        replacement.Start(attemptId);
        replacement.AddSamples([new MovementSample { MapTimeMs = 2, MonotonicMs = 2 }]);

        var repository = new MovementRepository(factory);
        Assert.Equal("stable_memory", repository.GetMetadata(attemptId)!.Source);
        Assert.Equal(1, repository.GetSamples(attemptId).Single().MapTimeMs);
    }

    [Fact]
    public void SharedBeatmapMetadata_StoresBaseRatherThanModAdjustedStars()
    {
        var sink = CreateSink();
        sink.StartAttempt(new AttemptStart
        {
            Identity = "mapA",
            WallTime = 1_788_000_000,
            BeatmapStats = new BeatmapStats { BaseStars = 6.16, Stars = 7.30 },
        });
        PersistStartedAttempt(
            sink,
            1_788_000_003,
            beatmapStats: new BeatmapStats { BaseStars = 6.16, Stars = 7.30 });

        using var con = Open();
        Assert.Equal(6.16, Scalar<double>(con, "SELECT stars FROM beatmaps WHERE identity = 'mapA'"), precision: 2);
    }

    [Fact]
    public void StableAttempt_PersistsOriginalSongsPathForReplayAnalysis()
    {
        string directory = Path.Combine(Path.GetTempPath(), $"kumori-stable-map-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        string beatmap = Path.Combine(directory, "map.osu");
        File.WriteAllText(beatmap, "osu file format v14");
        try
        {
            var sink = CreateSink();
            sink.StartAttempt(new AttemptStart
            {
                Identity = "stable-map",
                WallTime = 1_788_000_000,
                ClientKind = OsuClientKind.Stable,
                BeatmapFile = beatmap,
                GameFolder = Path.GetDirectoryName(directory),
                SongsFolder = directory,
            });

            long id = Assert.IsType<long>(sink.CurrentAttemptId);
            PersistStartedAttempt(sink, 1_788_000_003, identity: "stable-map");
            AttemptDetails details = new AttemptDetailsRepository(new SqliteConnectionFactory(_dbPath)).GetDetails(id)!;

            Assert.Equal(beatmap, details.LocalBeatmapPath);
            Assert.Equal(directory, details.LocalMediaDirectory);
            Assert.Equal("stable", details.ClientKind);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void SessionTracker_PersistsActiveSeconds()
    {
        var sink = CreateSink();
        var session = new SessionTracker(sink);

        session.Ingest(new SessionTracker.Frame
        {
            WallTime = 1_788_000_000,
            MonoTime = 0,
            IsPlaying = true,
        });
        session.Ingest(new SessionTracker.Frame
        {
            WallTime = 1_788_000_000.4,
            MonoTime = 0.4,
            IsPlaying = true,
        });
        session.Ingest(new SessionTracker.Frame
        {
            WallTime = 1_788_002_000,
            MonoTime = 2,
            IsPlaying = true,
        });
        session.EndClean(1_788_000_010, 10);

        using var con = Open();
        Assert.Equal(1.4, Scalar<double>(con, "SELECT active_seconds FROM sessions"), precision: 6);
        Assert.Equal(0, Scalar<long>(con, "SELECT interrupted FROM sessions"));
    }

    [Fact]
    public void ActiveSeconds_AccumulateUntilNextDurabilityBoundary()
    {
        var sink = CreateSink();
        sink.StartSession(new SessionStart(1_788_000_000, 0));

        sink.AddActiveSeconds(4.9);
        using (var con = Open())
        {
            Assert.Equal(1, Scalar<long>(con, "SELECT COUNT(*) FROM sessions"));
            Assert.Equal(0, Scalar<double>(con, "SELECT active_seconds FROM sessions"));
        }

        sink.AddActiveSeconds(0.1);
        using (var con = Open())
        {
            Assert.Equal(1, Scalar<long>(con, "SELECT COUNT(*) FROM sessions"));
            Assert.Equal(0, Scalar<double>(con, "SELECT active_seconds FROM sessions"));
        }

        sink.AddActiveSeconds(0.4);
        sink.EndSession(new SessionEnd(1_788_000_010, 10, Interrupted: false));
        using (var con = Open())
        {
            Assert.Equal(5.4, Scalar<double>(con, "SELECT active_seconds FROM sessions"), precision: 6);
        }
    }

    [Fact]
    public async Task Checkpoints_PersistActiveAttemptBeforeAttemptBoundary()
    {
        var sink = new AttemptSqliteSink(new SqliteConnectionFactory(_dbPath, readOnly: false));
        var tracker = new AttemptTracker(sink);

        tracker.Ingest(Play(0, score: 0, n300: 0));
        tracker.Ingest(Play(3.2, live: 3200, score: 10_000, n300: 60, progress: 0.5));
        await sink.FlushPendingPersistenceAsync();

        using (var con = Open())
        {
            Assert.Equal(1, Scalar<long>(con, "SELECT COUNT(*) FROM sessions"));
            Assert.Equal(1, Scalar<long>(con, "SELECT COUNT(*) FROM attempts"));
            Assert.Equal(1, Scalar<long>(con, "SELECT COUNT(*) FROM beatmaps"));
            Assert.True(Scalar<long>(con, "SELECT COUNT(*) FROM attempt_events") > 0);
            Assert.Equal("active", Scalar<string>(con, "SELECT outcome FROM attempts"));
            Assert.Equal(10_000, Scalar<long>(con, "SELECT score FROM attempts"));
        }

        tracker.Ingest(Results(3.4, score: 10_000, n300: 60, progress: 0.5));
        await sink.FlushPendingPersistenceAsync();
        using (var con = Open())
        {
            Assert.True(Scalar<long>(con, "SELECT COUNT(*) FROM attempt_events") > 0);
            Assert.Equal(10_000, Scalar<long>(con, "SELECT score FROM attempts"));
        }
    }

    [Fact]
    public async Task StartSessionAndAttempt_PersistActiveRowsWithoutWaitingForFinalize()
    {
        var sink = new AttemptSqliteSink(new SqliteConnectionFactory(_dbPath, readOnly: false));
        sink.StartSession(new SessionStart(1_788_000_000, 0));
        Assert.Equal(1, sink.CurrentSessionId);
        sink.StartAttempt(new AttemptStart
        {
            Identity = "staged-map",
            WallTime = 1_788_000_000,
            Artist = "Artist",
            Title = "Song",
            ModsKey = "HD",
            Mods = [new AttemptMod("HD")],
        });
        Assert.Equal(1, sink.CurrentAttemptId);
        sink.AddActiveSeconds(2.5);
        await sink.FlushPendingPersistenceAsync();

        using (var active = Open())
        {
            Assert.Equal(1, Scalar<long>(active, "SELECT COUNT(*) FROM sessions"));
            Assert.Equal(1, Scalar<long>(active, "SELECT COUNT(*) FROM beatmaps"));
            Assert.Equal(1, Scalar<long>(active, "SELECT COUNT(*) FROM attempts"));
            Assert.Equal(1, Scalar<long>(active, "SELECT COUNT(*) FROM attempt_mods"));
            Assert.Equal(1, Scalar<long>(active, "SELECT COUNT(*) FROM attempt_context"));
            Assert.Equal("active", Scalar<string>(active, "SELECT outcome FROM attempts"));
        }

        sink.Finalize(new AttemptFinalization(
            "completed",
            "results_screen",
            new AttemptSnapshot
            {
                Identity = "staged-map",
                WallTime = 1_788_000_004,
                DurationSeconds = 4,
                Score = 12_345,
                Progress = 1,
                ModsKey = "HD",
                Mods = [new AttemptMod("HD")],
            },
            Ordinal: 1));
        await sink.FlushPendingPersistenceAsync();

        using var persisted = Open();
        Assert.Equal(1, Scalar<long>(persisted, "SELECT COUNT(*) FROM sessions"));
        Assert.Equal(1, Scalar<long>(persisted, "SELECT COUNT(*) FROM beatmaps"));
        Assert.Equal(1, Scalar<long>(persisted, "SELECT COUNT(*) FROM attempts"));
        Assert.Equal(1, Scalar<long>(persisted, "SELECT COUNT(*) FROM attempt_mods"));
        Assert.Equal(1, Scalar<long>(persisted, "SELECT COUNT(*) FROM attempt_context"));
        Assert.Equal(12_345, Scalar<long>(persisted, "SELECT score FROM attempts"));
        Assert.Equal(2.5, Scalar<double>(persisted, "SELECT active_seconds FROM sessions"), precision: 6);
    }

    [Fact]
    public async Task Finalize_DetachesWithoutWaitingForSqliteOrWritingRows()
    {
        var scheduler = new ControlledPersistenceScheduler();
        var sink = new AttemptSqliteSink(
            new SqliteConnectionFactory(_dbPath, readOnly: false),
            scheduler.Schedule);
        sink.StartAttempt(new AttemptStart
        {
            Identity = "nonblocking-map",
            WallTime = 1_788_000_000,
        });

        using var blocker = Open();
        using var blockerTx = blocker.BeginTransaction();
        using (var writeLock = blocker.CreateCommand())
        {
            writeLock.Transaction = blockerTx;
            writeLock.CommandText = "INSERT OR REPLACE INTO metadata(key, value) VALUES('test-lock', 'held')";
            writeLock.ExecuteNonQuery();
        }

        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        sink.Finalize(new AttemptFinalization(
            "completed",
            "results_screen",
            new AttemptSnapshot
            {
                Identity = "nonblocking-map",
                WallTime = 1_788_000_004,
                DurationSeconds = 4,
                Progress = 1,
                Score = 25_000,
            },
            Ordinal: 1));
        stopwatch.Stop();

        Assert.True(stopwatch.Elapsed < TimeSpan.FromMilliseconds(500));
        Assert.Equal(2, sink.PendingPersistenceCount);
        using (var reader = Open())
        {
            Assert.Equal(0, Scalar<long>(reader, "SELECT COUNT(*) FROM sessions"));
            Assert.Equal(0, Scalar<long>(reader, "SELECT COUNT(*) FROM attempts"));
        }

        blockerTx.Rollback();
        await scheduler.RunToCompletionAsync();
        using var persisted = Open();
        Assert.Equal(25_000, Scalar<long>(persisted, "SELECT score FROM attempts"));
    }

    [Fact]
    public async Task AttemptPersisted_FiresExactlyOnceAfterFinalizedRowIsIndependentlyQueryable()
    {
        var scheduler = new ControlledPersistenceScheduler();
        var sink = new AttemptSqliteSink(
            new SqliteConnectionFactory(_dbPath, readOnly: false),
            scheduler.Schedule);
        var notifications = new List<long>();
        long? independentlyReadScore = null;
        sink.AttemptPersisted += attemptId =>
        {
            using var reader = Open();
            using var command = reader.CreateCommand();
            command.CommandText = "SELECT score FROM attempts WHERE id = $attempt_id";
            command.Parameters.AddWithValue("$attempt_id", attemptId);
            independentlyReadScore = Convert.ToInt64(command.ExecuteScalar());
            notifications.Add(attemptId);
        };

        sink.StartAttempt(new AttemptStart
        {
            Identity = "durable-notification-map",
            WallTime = 1_788_000_000,
        });
        var attemptId = Assert.IsType<long>(sink.CurrentAttemptId);
        sink.Finalize(new AttemptFinalization(
            "completed",
            "results_screen",
            new AttemptSnapshot
            {
                Identity = "durable-notification-map",
                WallTime = 1_788_000_004,
                DurationSeconds = 4,
                Progress = 1,
                Score = 88_000,
            },
            Ordinal: 1));

        Assert.Empty(notifications);
        Assert.Null(independentlyReadScore);
        using (var beforeScheduler = Open())
            Assert.Equal(0, Scalar<long>(beforeScheduler, "SELECT COUNT(*) FROM attempts"));

        await scheduler.RunToCompletionAsync();

        Assert.Equal([attemptId], notifications);
        Assert.Equal(88_000, independentlyReadScore);

        await scheduler.RunOnceAsync(CancellationToken.None);
        Assert.Equal([attemptId], notifications);
    }

    [Fact]
    public async Task Flush_MakesStartedAttemptAndCheckpointDurableWithoutFinalization()
    {
        var scheduler = new ControlledPersistenceScheduler();
        var sink = new AttemptSqliteSink(
            new SqliteConnectionFactory(_dbPath, readOnly: false),
            scheduler.Schedule);
        var notifications = new List<long>();
        sink.AttemptPersisted += notifications.Add;

        sink.StartAttempt(new AttemptStart
        {
            Identity = "checkpoint-durability-map",
            WallTime = 1_788_000_000,
            Artist = "Artist",
            Title = "Song",
            ModsKey = "HD",
            Mods = [new AttemptMod("HD")],
        });
        var attemptId = Assert.IsType<long>(sink.CurrentAttemptId);

        using (var beforeFlush = Open())
        {
            Assert.Equal(0, Scalar<long>(beforeFlush, "SELECT COUNT(*) FROM sessions"));
            Assert.Equal(0, Scalar<long>(beforeFlush, "SELECT COUNT(*) FROM attempts"));
        }

        await sink.FlushPendingPersistenceAsync();

        using (var started = Open())
        {
            Assert.Equal(1, Scalar<long>(started, "SELECT COUNT(*) FROM sessions"));
            Assert.Equal(attemptId, Scalar<long>(started, "SELECT id FROM attempts"));
            Assert.Equal("active", Scalar<string>(started, "SELECT outcome FROM attempts"));
            Assert.Equal("HD", Scalar<string>(started, "SELECT acronym FROM attempt_mods"));
        }
        Assert.Empty(notifications);

        sink.AddActiveSeconds(3.5);
        sink.Checkpoint(new AttemptCheckpoint(
            new AttemptSnapshot
            {
                Identity = "checkpoint-durability-map",
                WallTime = 1_788_000_003.5,
                LiveTimeMs = 3_500,
                DurationSeconds = 3.5,
                Score = 42_000,
                Progress = 0.6,
                ModsKey = "HD",
                Mods = [new AttemptMod("HD")],
            },
            [new JudgementCapture.CapturedEvent("miss", 1, "{}")],
            Forced: false));

        await sink.FlushPendingPersistenceAsync();
        await sink.FlushPendingPersistenceAsync();

        using (var checkpointed = Open())
        {
            Assert.Equal(42_000, Scalar<long>(checkpointed, "SELECT score FROM attempts"));
            Assert.Equal(1, Scalar<long>(checkpointed, "SELECT COUNT(*) FROM attempt_events"));
            Assert.Equal(0, Scalar<long>(checkpointed, "SELECT COUNT(*) FROM attempt_timing"));
            Assert.Equal(1, Scalar<long>(checkpointed, "SELECT COUNT(*) FROM attempt_context"));
            Assert.Equal(1, Scalar<long>(checkpointed, "SELECT COUNT(*) FROM attempt_persistence_commits"));
            Assert.Equal(3.5, Scalar<double>(checkpointed, "SELECT active_seconds FROM sessions"), precision: 6);
        }
        Assert.Empty(notifications);

        await scheduler.RunToCompletionAsync();
    }

    [Fact]
    public async Task Flush_MakesDiscardDurableAndReplacementCanReuseAttemptId()
    {
        var scheduler = new ControlledPersistenceScheduler();
        var sink = new AttemptSqliteSink(
            new SqliteConnectionFactory(_dbPath, readOnly: false),
            scheduler.Schedule);

        sink.StartAttempt(new AttemptStart
        {
            Identity = "empty-pulse",
            WallTime = 1_788_000_000,
        });
        var discardedId = Assert.IsType<long>(sink.CurrentAttemptId);
        await sink.FlushPendingPersistenceAsync();
        using (var started = Open())
            Assert.Equal(1, Scalar<long>(started, "SELECT COUNT(*) FROM attempts"));

        sink.DiscardIfEmpty(new AttemptDiscard(
            "empty_preplay",
            new AttemptSnapshot
            {
                Identity = "empty-pulse",
                WallTime = 1_788_000_000.1,
                DurationSeconds = 0.1,
            },
            Ordinal: 1));
        await sink.FlushPendingPersistenceAsync();

        using (var discarded = Open())
            Assert.Equal(0, Scalar<long>(discarded, "SELECT COUNT(*) FROM attempts"));

        sink.StartAttempt(new AttemptStart
        {
            Identity = "real-attempt",
            WallTime = 1_788_000_000.2,
        });
        Assert.Equal(discardedId, Assert.IsType<long>(sink.CurrentAttemptId));
        await sink.FlushPendingPersistenceAsync();

        using (var replacement = Open())
        {
            Assert.Equal(discardedId, Scalar<long>(replacement, "SELECT id FROM attempts"));
            Assert.Equal("real-attempt", Scalar<string>(replacement, """
                SELECT b.identity
                FROM attempts a
                JOIN beatmaps b ON b.id = a.beatmap_id
                """));
        }

        await scheduler.RunToCompletionAsync();
    }

    [Fact]
    public async Task RapidRetry_ReservesNextIdAndCommitsDetachedAttemptsInOrder()
    {
        var scheduler = new ControlledPersistenceScheduler();
        var sink = new AttemptSqliteSink(
            new SqliteConnectionFactory(_dbPath, readOnly: false),
            scheduler.Schedule);
        sink.StartSession(new SessionStart(1_788_000_000, 0));

        sink.StartAttempt(new AttemptStart { Identity = "retry-one", WallTime = 1_788_000_000 });
        var firstId = Assert.IsType<long>(sink.CurrentAttemptId);
        sink.Finalize(new AttemptFinalization(
            "retried",
            "retry",
            new AttemptSnapshot
            {
                Identity = "retry-one",
                WallTime = 1_788_000_004,
                DurationSeconds = 4,
                Score = 1_000,
            },
            Ordinal: 1));

        sink.StartAttempt(new AttemptStart { Identity = "retry-two", WallTime = 1_788_000_004.01 });
        var secondId = Assert.IsType<long>(sink.CurrentAttemptId);
        Assert.Equal(firstId + 1, secondId);
        sink.Finalize(new AttemptFinalization(
            "completed",
            "results_screen",
            new AttemptSnapshot
            {
                Identity = "retry-two",
                WallTime = 1_788_000_010,
                DurationSeconds = 5.99,
                Progress = 1,
                Score = 50_000,
            },
            Ordinal: 2));
        sink.EndSession(new SessionEnd(1_788_000_011, 11, Interrupted: false));

        Assert.Equal(6, sink.PendingPersistenceCount);
        using (var beforeDrain = Open())
            Assert.Equal(0, Scalar<long>(beforeDrain, "SELECT COUNT(*) FROM attempts"));

        await scheduler.RunToCompletionAsync();

        using var persisted = Open();
        using var attempts = persisted.CreateCommand();
        attempts.CommandText = "SELECT id, outcome, score FROM attempts ORDER BY id";
        using var rows = attempts.ExecuteReader();
        Assert.True(rows.Read());
        Assert.Equal(firstId, rows.GetInt64(0));
        Assert.Equal("retried", rows.GetString(1));
        Assert.Equal(1_000, rows.GetInt64(2));
        Assert.True(rows.Read());
        Assert.Equal(secondId, rows.GetInt64(0));
        Assert.Equal("completed", rows.GetString(1));
        Assert.Equal(50_000, rows.GetInt64(2));
        Assert.False(rows.Read());
    }

    [Fact]
    public async Task CanceledBusyCommit_RemainsQueuedAndRetriesWithoutLosingAttempt()
    {
        var scheduler = new ControlledPersistenceScheduler();
        var sink = new AttemptSqliteSink(
            new SqliteConnectionFactory(_dbPath, readOnly: false),
            scheduler.Schedule);
        sink.StartAttempt(new AttemptStart { Identity = "busy-map", WallTime = 1_788_000_000 });
        var attemptId = Assert.IsType<long>(sink.CurrentAttemptId);
        sink.Finalize(new AttemptFinalization(
            "completed",
            "results_screen",
            new AttemptSnapshot
            {
                Identity = "busy-map",
                WallTime = 1_788_000_004,
                DurationSeconds = 4,
                Progress = 1,
                Score = 75_000,
            },
            Ordinal: 1));

        using var blocker = Open();
        using var blockerTx = blocker.BeginTransaction();
        using (var writeLock = blocker.CreateCommand())
        {
            writeLock.Transaction = blockerTx;
            writeLock.CommandText = "INSERT OR REPLACE INTO metadata(key, value) VALUES('busy-lock', 'held')";
            writeLock.ExecuteNonQuery();
        }

        using var interruption = new CancellationTokenSource();
        var interruptedCommit = Task.Run(() => scheduler.RunOnceAsync(interruption.Token));
        await Task.Delay(100);
        await interruption.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => interruptedCommit);
        Assert.Equal(2, sink.PendingPersistenceCount);

        blockerTx.Rollback();
        await scheduler.RunToCompletionAsync();

        using var persisted = Open();
        Assert.Equal(1, Scalar<long>(persisted, "SELECT COUNT(*) FROM attempts"));
        Assert.Equal(attemptId, Scalar<long>(persisted, "SELECT id FROM attempts"));
        Assert.Equal(75_000, Scalar<long>(persisted, "SELECT score FROM attempts"));
    }

    private AttemptSqliteSink CreateSink() =>
        new(
            new SqliteConnectionFactory(_dbPath, readOnly: false),
            (_, work) => work(CancellationToken.None));

    private SqliteConnection Open()
    {
        var con = new SqliteConnection($"Data Source={_dbPath}");
        con.Open();
        return con;
    }

    private static T Scalar<T>(SqliteConnection con, string sql)
    {
        using var cmd = con.CreateCommand();
        cmd.CommandText = sql;
        return (T)Convert.ChangeType(cmd.ExecuteScalar()!, typeof(T));
    }

    private static void PersistStartedAttempt(
        AttemptSqliteSink sink,
        double wallTime,
        string identity = "mapA",
        BeatmapStats? beatmapStats = null)
    {
        sink.Finalize(new AttemptFinalization(
            "completed",
            "test_boundary",
            new AttemptSnapshot
            {
                Identity = identity,
                WallTime = wallTime,
                DurationSeconds = 3,
                Progress = 1,
                BeatmapStats = beatmapStats ?? new BeatmapStats(),
            },
            Ordinal: 1));
    }

    private sealed class ControlledPersistenceScheduler
    {
        private readonly TaskCompletionSource _lifetime = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private Func<CancellationToken, Task>? _work;

        public Task Schedule(string key, Func<CancellationToken, Task> work)
        {
            Assert.Equal("attempt-sqlite-persistence", key);
            Assert.Null(_work);
            _work = work;
            return _lifetime.Task;
        }

        public Task RunOnceAsync(CancellationToken cancellationToken) =>
            Assert.IsType<Func<CancellationToken, Task>>(_work)(cancellationToken);

        public async Task RunToCompletionAsync()
        {
            await RunOnceAsync(CancellationToken.None);
            _lifetime.TrySetResult();
        }
    }

    private static AttemptTracker.Frame Play(
        double t,
        long live = 0,
        int score = 0,
        double n300 = 0,
        double miss = 0,
        double progress = 0) => new()
        {
            WallTime = 1_788_000_000 + t,
            Artist = "Artist",
            Title = "Song",
            Difficulty = "Insane",
            Mapper = "Mapper",
            ModsKey = "HDDT",
            Mods =
        [
            new AttemptMod("HD"),
            new AttemptMod("DT"),
        ],
            Packet = new PacketView
            {
                MonoTime = t,
                State = "play",
                IsPlaying = true,
                Identity = "mapA",
                LiveTimeMs = live,
                Health = 1,
            },
            Score = score,
            Play = new JudgementCapture.PlayValues
            {
                Hit300 = n300,
                Miss = miss,
                Combo = n300,
                Progress = progress,
                Accuracy = n300 + miss == 0 ? 0 : n300 / (n300 + miss),
            },
        };

    private static AttemptTracker.Frame Menu(double t) => new()
    {
        WallTime = 1_788_000_000 + t,
        Packet = new PacketView
        {
            MonoTime = t,
            State = "songselect",
            Identity = "mapA",
        },
    };

    private static AttemptTracker.Frame Results(
        double t,
        int score,
        double n300,
        double miss = 0,
        double progress = 1) => new()
        {
            WallTime = 1_788_000_000 + t,
            Packet = new PacketView
            {
                MonoTime = t,
                State = "resultscreen",
                IsResults = true,
                Identity = "mapA",
                Grade = "A",
            },
            Score = score,
            Grade = "A",
            Play = new JudgementCapture.PlayValues
            {
                Hit300 = n300,
                Miss = miss,
                Combo = n300,
                Progress = progress,
                Accuracy = n300 + miss == 0 ? 0 : n300 / (n300 + miss),
            },
        };

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try { File.Delete(_dbPath); } catch { /* best effort */ }
        try { File.Delete(_dbPath + "-wal"); } catch { /* best effort */ }
        try { File.Delete(_dbPath + "-shm"); } catch { /* best effort */ }
    }
}
