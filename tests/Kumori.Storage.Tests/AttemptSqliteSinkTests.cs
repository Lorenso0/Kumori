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
        tracker.Ingest(Menu(0.1));
        tracker.Ingest(Play(0.2));
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
    public void EndSession_MarksInterrupted()
    {
        var sink = CreateSink();
        var tracker = new AttemptTracker(sink);

        tracker.Ingest(Play(0));
        sink.EndSession(interrupted: true);

        using var con = Open();
        Assert.Equal(1, Scalar<long>(con, "SELECT interrupted FROM sessions"));
        Assert.False(string.IsNullOrWhiteSpace(Scalar<string>(con, "SELECT ended_at FROM sessions")));
    }

    [Fact]
    public void MovementCaptureStore_WritesMovementAndInputSummary()
    {
        var factory = new SqliteConnectionFactory(_dbPath, readOnly: false);
        var sink = new AttemptSqliteSink(factory);
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
    public void SharedBeatmapMetadata_StoresBaseRatherThanModAdjustedStars()
    {
        var sink = CreateSink();
        sink.StartAttempt(new AttemptStart
        {
            Identity = "mapA",
            WallTime = 1_788_000_000,
            BeatmapStats = new BeatmapStats { BaseStars = 6.16, Stars = 7.30 },
        });

        using var con = Open();
        Assert.Equal(6.16, Scalar<double>(con, "SELECT stars FROM beatmaps WHERE identity = 'mapA'"), precision: 2);
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

    private AttemptSqliteSink CreateSink() =>
        new(new SqliteConnectionFactory(_dbPath, readOnly: false));

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
