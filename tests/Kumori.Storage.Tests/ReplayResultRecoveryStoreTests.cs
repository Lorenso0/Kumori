using Kumori.Storage;
using Kumori.Tracking;
using Microsoft.Data.Sqlite;
using Xunit;

namespace Kumori.Storage.Tests;

public sealed class ReplayResultRecoveryStoreTests : IDisposable
{
    private readonly string path = Path.Combine(Path.GetTempPath(), $"kumori-replay-recovery-{Guid.NewGuid():N}.sqlite3");

    [Fact]
    public void Apply_RepairsMissingFinalValuesAndRecordsReason()
    {
        var factory = new SqliteConnectionFactory(path, readOnly: false);
        var sink = new AttemptSqliteSink(factory);
        sink.StartAttempt(Start());
        long id = sink.CurrentAttemptId!.Value;
        sink.Finalize(Final(score: 0, accuracy: 0, combo: 0, n300: 0, n100: 0));

        var outcome = new ReplayResultRecoveryStore(factory).Apply(
            id,
            new ReplayResultData(
                1_234_567, 98.75, "A", 456, 700, 20, 3, 2, 4, 5,
                LargeTickHits: 80, LargeTickMisses: 2, SmallTickHits: 5,
                SmallTickMisses: 1, SliderTailHits: 20),
            "lazer_replay");

        Assert.True(outcome.Applied);
        Assert.Contains("300", outcome.RecoveredFields);
        using var con = factory.Open();
        using var cmd = con.CreateCommand();
        cmd.CommandText = """
            SELECT score, accuracy, grade, combo, n300, n100, n50, misses, geki, katu,
                   large_tick_hits, large_tick_misses, small_tick_hits, small_tick_misses, slider_tail_hits
            FROM attempts WHERE id=@id
            """;
        cmd.Parameters.AddWithValue("@id", id);
        using var reader = cmd.ExecuteReader();
        Assert.True(reader.Read());
        Assert.Equal(1_234_567, reader.GetInt64(0));
        Assert.Equal(98.75, reader.GetDouble(1), 4);
        Assert.Equal("A", reader.GetString(2));
        Assert.Equal(456, reader.GetInt32(3));
        Assert.Equal(700, reader.GetInt32(4));
        Assert.Equal(20, reader.GetInt32(5));
        Assert.Equal(3, reader.GetInt32(6));
        Assert.Equal(2, reader.GetInt32(7));
        Assert.Equal(4, reader.GetInt32(8));
        Assert.Equal(5, reader.GetInt32(9));
        Assert.Equal(80, reader.GetInt32(10));
        Assert.Equal(2, reader.GetInt32(11));
        Assert.Equal(5, reader.GetInt32(12));
        Assert.Equal(1, reader.GetInt32(13));
        Assert.Equal(20, reader.GetInt32(14));
        reader.Close();

        using var context = con.CreateCommand();
        context.CommandText = "SELECT source_json FROM attempt_context WHERE attempt_id=@id";
        context.Parameters.AddWithValue("@id", id);
        var json = Assert.IsType<string>(context.ExecuteScalar());
        Assert.Contains("tosu_gameplay_values_missing", json);
        Assert.Contains("lazer_replay", json);

        using var personalBest = con.CreateCommand();
        personalBest.CommandText = "SELECT value FROM personal_bests WHERE metric='score'";
        Assert.Equal(1_234_567d, Convert.ToDouble(personalBest.ExecuteScalar()));
    }

    [Fact]
    public void Apply_PreservesValidTosuValuesAndOnlyFillsMissingFields()
    {
        var factory = new SqliteConnectionFactory(path, readOnly: false);
        var sink = new AttemptSqliteSink(factory);
        sink.StartAttempt(Start());
        long id = sink.CurrentAttemptId!.Value;
        sink.Finalize(Final(score: 900_000, accuracy: 97.1, combo: 321, n300: 600, n100: 0));

        var outcome = new ReplayResultRecoveryStore(factory).Apply(
            id,
            new ReplayResultData(1_234_567, 98.75, "S", 456, 700, 20, 0, 0, 0, 0),
            "stable_replay");

        Assert.True(outcome.Applied);
        Assert.Equal(["100"], outcome.RecoveredFields);
        using var con = factory.Open();
        using var cmd = con.CreateCommand();
        cmd.CommandText = "SELECT score, accuracy, grade, combo, n300, n100 FROM attempts WHERE id=@id";
        cmd.Parameters.AddWithValue("@id", id);
        using var reader = cmd.ExecuteReader();
        Assert.True(reader.Read());
        Assert.Equal(900_000, reader.GetInt64(0));
        Assert.Equal(97.1, reader.GetDouble(1), 4);
        Assert.Equal("A", reader.GetString(2));
        Assert.Equal(321, reader.GetInt32(3));
        Assert.Equal(600, reader.GetInt32(4));
        Assert.Equal(20, reader.GetInt32(5));
    }

    [Fact]
    public void Apply_WaitsUntilAttemptIsFinalized()
    {
        var factory = new SqliteConnectionFactory(path, readOnly: false);
        var sink = new AttemptSqliteSink(factory);
        sink.StartAttempt(Start());

        var outcome = new ReplayResultRecoveryStore(factory).Apply(
            sink.CurrentAttemptId!.Value,
            new ReplayResultData(100, 100, "X", 1, 1, 0, 0, 0, 0, 0),
            "stable_replay");

        Assert.False(outcome.AttemptReady);
        Assert.False(outcome.Applied);
    }

    [Fact]
    public void ApplySimulation_PreservesValidTelemetry()
    {
        var factory = new SqliteConnectionFactory(path, readOnly: false);
        var sink = new AttemptSqliteSink(factory);
        var capturedMap = new BeatmapStats
        {
            BaseStars = 4.5,
            Stars = 5.5,
            ApproachRate = 8,
            CircleSize = 3.5,
            OverallDifficulty = 7,
            DrainRate = 5,
            Bpm = 160,
            MaxCombo = 600,
            RawJson = """{"stars":{"original":4.5,"total":5.5}}""",
        };
        sink.StartAttempt(Start() with { BeatmapStats = capturedMap });
        long id = sink.CurrentAttemptId!.Value;
        var final = Final(score: 500_000, accuracy: 98, combo: 500, n300: 400, n100: 10);
        sink.Finalize(final with
        {
            Snapshot = final.Snapshot with
            {
                Pp = 150,
                FcPp = 170,
                MaxPp = 200,
                BeatmapStats = capturedMap,
            },
        });

        new ReplayResultRecoveryStore(factory).ApplySimulation(id, new ReplaySimulationResult
        {
            Pp = 999,
            FcPp = 998,
            MaxPp = 997,
            BaseStars = 9,
            AdjustedStars = 10,
            ApproachRate = 10,
            CircleSize = 6,
            OverallDifficulty = 10,
            DrainRate = 10,
            Bpm = 300,
            MaxCombo = 1000,
        });

        using var con = factory.Open();
        using var cmd = con.CreateCommand();
        cmd.CommandText = """
            SELECT a.pp, a.fc_pp, a.max_pp, a.base_stars, a.adjusted_stars,
                   b.stars, b.ar, b.cs, b.od, b.hp, b.bpm, b.max_combo
            FROM attempts a JOIN beatmaps b ON b.id=a.beatmap_id WHERE a.id=@id
            """;
        cmd.Parameters.AddWithValue("@id", id);
        using var reader = cmd.ExecuteReader();
        Assert.True(reader.Read());
        Assert.Equal(150, reader.GetDouble(0));
        Assert.Equal(170, reader.GetDouble(1));
        Assert.Equal(200, reader.GetDouble(2));
        Assert.Equal(4.5, reader.GetDouble(3));
        Assert.Equal(5.5, reader.GetDouble(4));
        Assert.Equal(4.5, reader.GetDouble(5));
        Assert.Equal(8, reader.GetDouble(6));
        Assert.Equal(3.5, reader.GetDouble(7));
        Assert.Equal(7, reader.GetDouble(8));
        Assert.Equal(5, reader.GetDouble(9));
        Assert.Equal(160, reader.GetDouble(10));
        Assert.Equal(600, reader.GetInt32(11));
    }

    [Fact]
    public void ApplySimulation_FillsRichJudgementsAndTimingAndMarksProvenance()
    {
        var factory = new SqliteConnectionFactory(path, readOnly: false);
        var sink = new AttemptSqliteSink(factory);
        sink.StartAttempt(Start());
        long id = sink.CurrentAttemptId!.Value;
        sink.Finalize(Final(score: 0, accuracy: 0, combo: 0, n300: 0, n100: 0));
        var store = new ReplayResultRecoveryStore(factory);
        store.Apply(id, new ReplayResultData(100_000, 95, "A", 100, 90, 8, 1, 1, 0, 0), "lazer_replay");

        var outcome = store.ApplySimulation(id, new ReplaySimulationResult
        {
            SliderBreaks = 2,
            LargeTickHits = 40,
            LargeTickMisses = 3,
            SmallTickHits = 5,
            SmallTickMisses = 1,
            SliderTailHits = 20,
            SliderTailMisses = 2,
            UnstableRate = 123.4,
            TimingOffsets = [-10, 0, 20],
            Pp = 187.42,
            FcPp = 211.35,
            MaxPp = 245.67,
            BaseStars = 5.12,
            AdjustedStars = 6.34,
            ApproachRate = 9,
            AdjustedApproachRate = 10.33,
            CircleSize = 4,
            AdjustedCircleSize = 4,
            OverallDifficulty = 8,
            AdjustedOverallDifficulty = 10.08,
            DrainRate = 6,
            AdjustedDrainRate = 6,
            Bpm = 180,
            AdjustedBpm = 270,
            ClockRate = 1.5,
            MaxCombo = 777,
            CircleCount = 300,
            SliderCount = 200,
            SpinnerCount = 3,
        });

        Assert.True(outcome.Applied);
        using var con = factory.Open();
        using (var values = con.CreateCommand())
        {
            values.CommandText = """
                SELECT slider_breaks, large_tick_hits, large_tick_misses,
                       small_tick_hits, small_tick_misses, slider_tail_hits,
                       slider_tail_misses, unstable_rate, pp, fc_pp, max_pp,
                       base_stars, adjusted_stars,
                       b.stars, b.ar, b.cs, b.od, b.hp, b.bpm, b.max_combo
                FROM attempts a JOIN beatmaps b ON b.id=a.beatmap_id WHERE a.id=@id
                """;
            values.Parameters.AddWithValue("@id", id);
            using var reader = values.ExecuteReader();
            Assert.True(reader.Read());
            Assert.Equal(2, reader.GetInt32(0));
            Assert.Equal(40, reader.GetInt32(1));
            Assert.Equal(3, reader.GetInt32(2));
            Assert.Equal(5, reader.GetInt32(3));
            Assert.Equal(1, reader.GetInt32(4));
            Assert.Equal(20, reader.GetInt32(5));
            Assert.Equal(2, reader.GetInt32(6));
            Assert.Equal(123.4, reader.GetDouble(7), 4);
            Assert.Equal(187.42, reader.GetDouble(8), 4);
            Assert.Equal(211.35, reader.GetDouble(9), 4);
            Assert.Equal(245.67, reader.GetDouble(10), 4);
            Assert.Equal(5.12, reader.GetDouble(11), 4);
            Assert.Equal(6.34, reader.GetDouble(12), 4);
            Assert.Equal(5.12, reader.GetDouble(13), 4);
            Assert.Equal(9, reader.GetDouble(14), 4);
            Assert.Equal(4, reader.GetDouble(15), 4);
            Assert.Equal(8, reader.GetDouble(16), 4);
            Assert.Equal(6, reader.GetDouble(17), 4);
            Assert.Equal(180, reader.GetDouble(18), 4);
            Assert.Equal(777, reader.GetInt32(19));
        }
        using (var timing = con.CreateCommand())
        {
            timing.CommandText = "SELECT hit_count, early_count, late_count FROM attempt_timing WHERE attempt_id=@id";
            timing.Parameters.AddWithValue("@id", id);
            using var reader = timing.ExecuteReader();
            Assert.True(reader.Read());
            Assert.Equal(3, reader.GetInt32(0));
            Assert.Equal(1, reader.GetInt32(1));
            Assert.Equal(1, reader.GetInt32(2));
        }
        var details = new AttemptDetailsRepository(factory).GetDetails(id);
        Assert.NotNull(details);
        Assert.True(details.ResultRecoveredFromReplay);
        Assert.True(details.ResultRecoverySimulationCompleted);
        Assert.Equal("lazer_replay", details.ResultRecoverySource);
        Assert.Equal(187.42, details.Summary.Pp, 4);
        Assert.Equal(211.35, details.FcPp, 4);
        Assert.Equal(245.67, details.MaxPp, 4);
        Assert.Equal(5.12, details.BaseStars!.Value, 4);
        Assert.Equal(6.34, details.AdjustedStars!.Value, 4);
        Assert.Equal(10.33, details.CapturedDifficulty["ar"].Converted!.Value, 4);
        Assert.Equal(270, details.CapturedDifficulty["bpm"].Converted!.Value, 4);

        using var ppBest = con.CreateCommand();
        ppBest.CommandText = "SELECT value FROM personal_bests WHERE metric='pp'";
        Assert.Equal(187.42, Convert.ToDouble(ppBest.ExecuteScalar()), 4);
    }

    private static AttemptStart Start() => new()
    {
        Identity = "checksum:test",
        WallTime = 1_700_000_000,
        Checksum = "test",
        Title = "Test",
        Difficulty = "Normal",
        ModsKey = "NM",
    };

    private static AttemptFinalization Final(int score, double accuracy, int combo, int n300, int n100) => new(
        "completed",
        "results",
        new AttemptSnapshot
        {
            Identity = "checksum:test",
            WallTime = 1_700_000_100,
            DurationSeconds = 100,
            Score = score,
            Accuracy = accuracy,
            Grade = score > 0 ? "A" : null,
            Combo = combo,
            N300 = n300,
            N100 = n100,
            Progress = 1,
        },
        1);

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        foreach (var candidate in new[] { path, path + "-wal", path + "-shm" })
            if (File.Exists(candidate)) File.Delete(candidate);
    }
}
