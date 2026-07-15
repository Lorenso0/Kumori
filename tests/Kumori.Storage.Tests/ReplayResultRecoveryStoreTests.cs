using System.Text.Json;
using Kumori.Storage;
using Kumori.Tracking;
using Microsoft.Data.Sqlite;
using Xunit;

namespace Kumori.Storage.Tests;

public sealed class ReplayResultRecoveryStoreTests : IDisposable
{
    private readonly string path = Path.Combine(Path.GetTempPath(), $"kumori-replay-recovery-{Guid.NewGuid():N}.sqlite3");

    [Fact]
    public void Apply_PreCancelledTokenStopsBeforeOpeningDatabase()
    {
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();

        Assert.Throws<OperationCanceledException>(() =>
            new ReplayResultRecoveryStore(new SqliteConnectionFactory(path, readOnly: false)).Apply(
                1,
                new ReplayResultData(100, 100, "X", 1, 1, 0, 0, 0, 0, 0),
                "test",
                cancelled.Token));
        Assert.False(File.Exists(path));
    }

    [Fact]
    public void Apply_RepairsMissingFinalValuesAndRecordsReason()
    {
        var factory = new SqliteConnectionFactory(path, readOnly: false);
        var sink = new AttemptSqliteSink(factory, (_, work) => work(CancellationToken.None));
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
        var sink = new AttemptSqliteSink(factory, (_, work) => work(CancellationToken.None));
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
    public void Apply_ReplacesPlaceholderAccuracyWhenEntireTosuResultWasMissing()
    {
        var factory = new SqliteConnectionFactory(path, readOnly: false);
        var sink = new AttemptSqliteSink(factory, (_, work) => work(CancellationToken.None));
        sink.StartAttempt(Start());
        long id = sink.CurrentAttemptId!.Value;
        sink.Finalize(Final(score: 0, accuracy: 100, combo: 0, n300: 0, n100: 0));

        var outcome = new ReplayResultRecoveryStore(factory).Apply(
            id,
            new ReplayResultData(444_621, 87.3526, "B", 63, 113, 13, 0, 9, 0, 0,
                SliderTailHits: 39),
            "lazer_replay");

        Assert.True(outcome.Applied);
        Assert.Contains("accuracy", outcome.RecoveredFields);
        using var con = factory.Open();
        using var command = con.CreateCommand();
        command.CommandText = "SELECT accuracy, n300, n100, misses FROM attempts WHERE id=@id";
        command.Parameters.AddWithValue("@id", id);
        using var reader = command.ExecuteReader();
        Assert.True(reader.Read());
        Assert.Equal(87.3526, reader.GetDouble(0), 4);
        Assert.Equal(113, reader.GetInt32(1));
        Assert.Equal(13, reader.GetInt32(2));
        Assert.Equal(9, reader.GetInt32(3));
    }

    [Fact]
    public void Apply_RepairsPersistedPerfectPlaceholderFromEarlierRecovery()
    {
        var factory = new SqliteConnectionFactory(path, readOnly: false);
        var sink = new AttemptSqliteSink(factory, (_, work) => work(CancellationToken.None));
        sink.StartAttempt(Start());
        long id = sink.CurrentAttemptId!.Value;
        var final = Final(score: 444_621, accuracy: 100, combo: 63, n300: 113, n100: 13);
        sink.Finalize(final with { Snapshot = final.Snapshot with { Misses = 9 } });
        using (var con = factory.Open())
        using (var markRecovered = con.CreateCommand())
        {
            markRecovered.CommandText = """
                UPDATE attempt_context
                SET source_json='{"result_recovery":{"reason":"tosu_gameplay_values_missing","fields":["score","combo","300","100","misses"]}}'
                WHERE attempt_id=@id
                """;
            markRecovered.Parameters.AddWithValue("@id", id);
            markRecovered.ExecuteNonQuery();
        }

        var replay = new ReplayResultData(444_621, 87.3526, "B", 63, 113, 13, 0, 9, 0, 0);
        var outcome = new ReplayResultRecoveryStore(factory).Apply(id, replay, "lazer_replay_reconciliation");

        Assert.True(outcome.Applied);
        Assert.Equal(["accuracy"], outcome.RecoveredFields);
        using var verify = factory.Open();
        using var accuracy = verify.CreateCommand();
        accuracy.CommandText = "SELECT accuracy FROM attempts WHERE id=@id";
        accuracy.Parameters.AddWithValue("@id", id);
        Assert.Equal(87.3526, Convert.ToDouble(accuracy.ExecuteScalar()), 4);
    }

    [Fact]
    public void Apply_RepairsAccuracyOverwrittenByLegacySimulation()
    {
        var factory = new SqliteConnectionFactory(path, readOnly: false);
        var sink = new AttemptSqliteSink(factory, (_, work) => work(CancellationToken.None));
        sink.StartAttempt(Start());
        long id = sink.CurrentAttemptId!.Value;
        sink.Finalize(Final(score: 0, accuracy: 0, combo: 0, n300: 0, n100: 0));
        var store = new ReplayResultRecoveryStore(factory);
        var replay = new ReplayResultData(123_456, 97.4321, "A", 300, 280, 12, 3, 5, 0, 0);
        store.Apply(id, replay, "lazer_replay");

        using (var con = factory.Open())
        using (var damage = con.CreateCommand())
        {
            damage.CommandText = """
                UPDATE attempts SET accuracy=83.25 WHERE id=@id;
                UPDATE attempt_context
                SET source_json='{"result_recovery":{"reason":"tosu_gameplay_values_missing","simulation_schema":2,"simulated_fields":["accuracy"]}}'
                WHERE attempt_id=@id;
                """;
            damage.Parameters.AddWithValue("@id", id);
            damage.ExecuteNonQuery();
        }

        var outcome = store.Apply(id, replay, "lazer_replay_reconciliation");

        Assert.True(outcome.Applied);
        Assert.Contains("accuracy", outcome.RecoveredFields);
        using var verify = factory.Open();
        using var accuracy = verify.CreateCommand();
        accuracy.CommandText = "SELECT accuracy FROM attempts WHERE id=@id";
        accuracy.Parameters.AddWithValue("@id", id);
        Assert.Equal(97.4321, Convert.ToDouble(accuracy.ExecuteScalar()), 4);

        using var provenance = verify.CreateCommand();
        provenance.CommandText = "SELECT source_json FROM attempt_context WHERE attempt_id=@id";
        provenance.Parameters.AddWithValue("@id", id);
        using var document = JsonDocument.Parse((string)provenance.ExecuteScalar()!);
        var recovery = document.RootElement.GetProperty("result_recovery");
        Assert.Equal("replay_or_tosu", recovery.GetProperty("accuracy_source").GetString());
        Assert.Equal(2, recovery.GetProperty("simulation_schema").GetInt32());
    }

    [Fact]
    public void Apply_WaitsUntilAttemptIsFinalized()
    {
        var factory = new SqliteConnectionFactory(path, readOnly: false);
        var sink = new AttemptSqliteSink(factory, (_, work) => work(CancellationToken.None));
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
        var sink = new AttemptSqliteSink(factory, (_, work) => work(CancellationToken.None));
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
    public void ApplySimulation_PartialCaptureOwnsCoreResultWithoutChangingOutcome()
    {
        var factory = new SqliteConnectionFactory(path, readOnly: false);
        var sink = new AttemptSqliteSink(factory, (_, work) => work(CancellationToken.None));
        sink.StartAttempt(Start());
        long id = sink.CurrentAttemptId!.Value;
        AttemptFinalization final = Final(score: 0, accuracy: 0, combo: 0, n300: 0, n100: 0);
        sink.Finalize(final with
        {
            Outcome = "quit",
            Snapshot = final.Snapshot with { Progress = 0.42 },
        });

        var outcome = new ReplayResultRecoveryStore(factory).ApplySimulation(
            id,
            new ReplaySimulationResult
            {
                N300 = 73,
                N100 = 6,
                N50 = 2,
                Misses = 3,
                Accuracy = 90.674603,
                Score = 123_456,
                AchievedCombo = 41,
                SliderBreaks = 1,
                TimingOffsets = [-8, 3, 11],
            },
            simulationOwnsCoreResult: true,
            tosuResultWasMissing: true);

        Assert.True(outcome.Applied);
        using var con = factory.Open();
        using var values = con.CreateCommand();
        values.CommandText = "SELECT outcome, progress, n300, n100, n50, misses, accuracy, combo, score FROM attempts WHERE id=@id";
        values.Parameters.AddWithValue("@id", id);
        using var reader = values.ExecuteReader();
        Assert.True(reader.Read());
        Assert.Equal("quit", reader.GetString(0));
        Assert.Equal(0.42, reader.GetDouble(1), 4);
        Assert.Equal(73, reader.GetInt32(2));
        Assert.Equal(6, reader.GetInt32(3));
        Assert.Equal(2, reader.GetInt32(4));
        Assert.Equal(3, reader.GetInt32(5));
        Assert.Equal(90.674603, reader.GetDouble(6), 4);
        Assert.Equal(41, reader.GetInt32(7));
        Assert.Equal(123_456, reader.GetInt64(8));

        reader.Close();
        var details = new AttemptDetailsRepository(factory).GetDetails(id);
        Assert.NotNull(details);
        Assert.True(details.ResultRecoveredFromReplay);
        Assert.True(details.ResultRecoverySimulationCompleted);
        using var personalBests = con.CreateCommand();
        personalBests.CommandText = "SELECT COUNT(*) FROM personal_bests";
        Assert.Equal(0L, (long)personalBests.ExecuteScalar()!);
    }

    [Fact]
    public void ApplySimulation_MissingTosuResultReplacesPerfectAccuracyPlaceholder()
    {
        var factory = new SqliteConnectionFactory(path, readOnly: false);
        var sink = new AttemptSqliteSink(factory, (_, work) => work(CancellationToken.None));
        sink.StartAttempt(Start());
        long id = sink.CurrentAttemptId!.Value;
        AttemptFinalization final = Final(score: 0, accuracy: 100, combo: 0, n300: 0, n100: 0);
        sink.Finalize(final with
        {
            Outcome = "quit",
            Snapshot = final.Snapshot with { Progress = 0.42 },
        });

        new ReplayResultRecoveryStore(factory).ApplySimulation(
            id,
            new ReplaySimulationResult
            {
                N300 = 73,
                N100 = 6,
                N50 = 2,
                Misses = 3,
                Accuracy = 90.674603,
            },
            simulationOwnsCoreResult: true,
            tosuResultWasMissing: true);

        using var con = factory.Open();
        using var values = con.CreateCommand();
        values.CommandText = "SELECT accuracy, n300, n100, n50, misses FROM attempts WHERE id=@id";
        values.Parameters.AddWithValue("@id", id);
        using var reader = values.ExecuteReader();
        Assert.True(reader.Read());
        Assert.Equal(90.674603, reader.GetDouble(0), 4);
        Assert.Equal(73, reader.GetInt32(1));
        Assert.Equal(6, reader.GetInt32(2));
        Assert.Equal(2, reader.GetInt32(3));
        Assert.Equal(3, reader.GetInt32(4));
    }

    [Fact]
    public void ApplySimulation_NormalPartialPlayPreservesTosuCoreResult()
    {
        var factory = new SqliteConnectionFactory(path, readOnly: false);
        var sink = new AttemptSqliteSink(factory, (_, work) => work(CancellationToken.None));
        sink.StartAttempt(Start());
        long id = sink.CurrentAttemptId!.Value;
        AttemptFinalization final = Final(score: 140_420, accuracy: 94.3389, combo: 98, n300: 82, n100: 4);
        sink.Finalize(final with
        {
            Outcome = "quit",
            Snapshot = final.Snapshot with { N50 = 0, Misses = 3, Progress = 0.25 },
        });
        using (var seed = factory.Open())
        using (var miss = seed.CreateCommand())
        {
            miss.CommandText = """
                INSERT INTO attempt_events(attempt_id, captured_at, map_time_ms, event_type, value, data_json)
                VALUES(@id, '2026-07-15T18:40:00Z', 17000, 'miss', 3, '{}')
                """;
            miss.Parameters.AddWithValue("@id", id);
            miss.ExecuteNonQuery();
        }

        new ReplayResultRecoveryStore(factory).ApplySimulation(id, new ReplaySimulationResult
        {
            N300 = 10,
            N100 = 1,
            N50 = 0,
            Misses = 0,
            Accuracy = 94.3389,
            AchievedCombo = 98,
            MaxPp = 250,
            Judgements =
            [
                new ReplaySimulationJudgement { Kind = 2, RootStartTime = 2000, EventTime = 2010 },
            ],
        });

        using var con = factory.Open();
        using var values = con.CreateCommand();
        values.CommandText = "SELECT n300, n100, n50, misses, score, accuracy, combo FROM attempts WHERE id=@id";
        values.Parameters.AddWithValue("@id", id);
        using var reader = values.ExecuteReader();
        Assert.True(reader.Read());
        Assert.Equal(82, reader.GetInt32(0));
        Assert.Equal(4, reader.GetInt32(1));
        Assert.Equal(0, reader.GetInt32(2));
        Assert.Equal(3, reader.GetInt32(3));
        Assert.Equal(140_420, reader.GetInt64(4));
        Assert.Equal(94.3389, reader.GetDouble(5), 4);
        Assert.Equal(98, reader.GetInt32(6));
        reader.Close();
        using var events = con.CreateCommand();
        events.CommandText = "SELECT event_type, value, data_json FROM attempt_events WHERE attempt_id=@id";
        events.Parameters.AddWithValue("@id", id);
        using var eventReader = events.ExecuteReader();
        Assert.True(eventReader.Read());
        Assert.Equal("miss", eventReader.GetString(0));
        Assert.Equal(3, eventReader.GetDouble(1));
        Assert.Equal("{}", eventReader.GetString(2));
        Assert.False(eventReader.Read());
        eventReader.Close();

        var details = new AttemptDetailsRepository(factory).GetDetails(id);
        Assert.NotNull(details);
        Assert.False(details.ResultRecoveredFromReplay);
        Assert.True(details.ResultRecoverySimulationCompleted);
    }

    [Fact]
    public void ApplySimulation_MissingFinalResultFillsHeaderWithoutOverwritingCheckpointCore()
    {
        var factory = new SqliteConnectionFactory(path, readOnly: false);
        var sink = new AttemptSqliteSink(factory, (_, work) => work(CancellationToken.None));
        sink.StartAttempt(Start());
        long id = sink.CurrentAttemptId!.Value;
        AttemptFinalization final = Final(score: 0, accuracy: 86.04, combo: 150, n300: 187, n100: 15);
        sink.Finalize(final with
        {
            Outcome = "completed",
            Snapshot = final.Snapshot with { N50 = 2, Misses = 21, Progress = 0.523 },
        });
        using (var seed = factory.Open())
        using (var source = seed.CreateCommand())
        {
            source.CommandText = """
                UPDATE attempt_context
                SET source_json='{"result_recovery":{"reason":"tosu_gameplay_values_missing","core_result_source":"tosu_checkpoint"}}'
                WHERE attempt_id=@id
                """;
            source.Parameters.AddWithValue("@id", id);
            source.ExecuteNonQuery();
        }

        new ReplayResultRecoveryStore(factory).ApplySimulation(
            id,
            new ReplaySimulationResult
            {
                N300 = 187,
                N100 = 15,
                N50 = 2,
                Misses = 25,
                Accuracy = 84,
                Score = 456_789,
                AchievedCombo = 120,
            },
            simulationOwnsCoreResult: false,
            tosuResultWasMissing: true);

        using var con = factory.Open();
        using var values = con.CreateCommand();
        values.CommandText = "SELECT score, accuracy, combo, n300, n100, n50, misses FROM attempts WHERE id=@id";
        values.Parameters.AddWithValue("@id", id);
        using var reader = values.ExecuteReader();
        Assert.True(reader.Read());
        Assert.Equal(456_789, reader.GetInt64(0));
        Assert.Equal(86.04, reader.GetDouble(1), precision: 4);
        Assert.Equal(150, reader.GetInt32(2));
        Assert.Equal(187, reader.GetInt32(3));
        Assert.Equal(15, reader.GetInt32(4));
        Assert.Equal(2, reader.GetInt32(5));
        Assert.Equal(21, reader.GetInt32(6));
    }

    [Fact]
    public void ApplySimulation_FillsRichJudgementsAndTimingAndMarksProvenance()
    {
        var factory = new SqliteConnectionFactory(path, readOnly: false);
        var sink = new AttemptSqliteSink(factory, (_, work) => work(CancellationToken.None));
        sink.StartAttempt(Start());
        long id = sink.CurrentAttemptId!.Value;
        sink.Finalize(Final(score: 0, accuracy: 0, combo: 0, n300: 0, n100: 0));
        var store = new ReplayResultRecoveryStore(factory);
        store.Apply(id, new ReplayResultData(100_000, 95, "A", 100, 90, 8, 1, 1, 0, 0), "lazer_replay");

        var outcome = store.ApplySimulation(id, new ReplaySimulationResult
        {
            N300 = 80,
            N100 = 10,
            N50 = 2,
            Misses = 8,
            Accuracy = 83.6666667,
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
            Judgements =
            [
                new ReplaySimulationJudgement { Kind = 0, RootStartTime = 50_000, ObjectStartTime = 50_000, EventTime = 50_120, TimeOffset = 120 },
                new ReplaySimulationJudgement { Kind = 2, RootStartTime = 60_000, ObjectStartTime = 60_000, EventTime = 60_040, TimeOffset = 40 },
            ],
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
        Assert.Equal(80, details.N300);
        Assert.Equal(10, details.N100);
        Assert.Equal(2, details.N50);
        Assert.Equal(8, details.Summary.Misses);
        Assert.Equal(95, details.Summary.Accuracy, 4);
        Assert.DoesNotContain("accuracy", outcome.RecoveredFields);
        Assert.Equal(2, details.Events.Count);
        Assert.Contains(details.Events, entry => entry.EventType == "miss" && entry.MapTimeMs == 50_000);
        Assert.Contains(details.Events, entry => entry.EventType == "hit_100" && entry.MapTimeMs == 60_000);

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
