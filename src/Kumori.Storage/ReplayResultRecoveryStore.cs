using Microsoft.Data.Sqlite;
using Serilog;

namespace Kumori.Storage;

/// <summary>
/// Repairs result fields which tosu omitted, using the checksum-matched replay.
/// Existing non-zero telemetry wins unless the result snapshot was incomplete
/// enough that its accuracy was only a tosu placeholder, or an older recovery
/// schema replaced replay accuracy with a reconstructed core-hit formula.
/// </summary>
public sealed partial class ReplayResultRecoveryStore(SqliteConnectionFactory factory)
{
    // Schema 4 restores frame-accurate 100x playback and lets simulation own a
    // missing core result when timing existed but tosu's counters were all zero.
    public const int CurrentSimulationSchema = 4;

    public ReplayResultRecoveryOutcome Apply(
        long attemptId,
        ReplayResultData replay,
        string source,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var con = factory.Open();
        if (cancellationToken.CanBeCanceled)
            con.DefaultTimeout = 1;
        using var interruptRegistration = cancellationToken.Register(
            static state => SQLitePCL.raw.sqlite3_interrupt(((SqliteConnection)state!).Handle),
            con);
        try
        {
            using var tx = con.BeginTransaction();
            ReplayResultRecoveryOutcome ApplyCore()
            {
                cancellationToken.ThrowIfCancellationRequested();
                var current = Read(con, tx, attemptId, cancellationToken);
                if (current is null || string.Equals(current.Outcome, "active", StringComparison.OrdinalIgnoreCase))
                    return ReplayResultRecoveryOutcome.NotReady;

                var fields = new List<string>();
                long score = FillLong(current.Score, replay.Score, "score");
                double accuracy = ReplayMustOwnAccuracy(current, replay)
                    ? ReplaceDouble(current.Accuracy, replay.Accuracy, "accuracy")
                    : FillDouble(current.Accuracy, replay.Accuracy, "accuracy");
                int combo = FillInt(current.Combo, replay.Combo, "combo");
                int n300 = FillInt(current.N300, replay.N300, "300");
                int n100 = FillInt(current.N100, replay.N100, "100");
                int n50 = FillInt(current.N50, replay.N50, "50");
                int misses = FillInt(current.Misses, replay.Misses, "misses");
                int geki = FillInt(current.Geki, replay.Geki, "geki");
                int katu = FillInt(current.Katu, replay.Katu, "katu");
                int largeTickHits = FillInt(current.LargeTickHits, replay.LargeTickHits, "large tick hits");
                int largeTickMisses = FillInt(current.LargeTickMisses, replay.LargeTickMisses, "large tick misses");
                int smallTickHits = FillInt(current.SmallTickHits, replay.SmallTickHits, "small tick hits");
                int smallTickMisses = FillInt(current.SmallTickMisses, replay.SmallTickMisses, "small tick misses");
                int sliderTailHits = FillInt(current.SliderTailHits, replay.SliderTailHits, "slider tail hits");
                int sliderTailMisses = FillInt(current.SliderTailMisses, replay.SliderTailMisses, "slider tail misses");
                string? grade = current.Grade;
                if (string.IsNullOrWhiteSpace(grade) && !string.IsNullOrWhiteSpace(replay.Grade))
                {
                    grade = replay.Grade;
                    fields.Add("grade");
                }

                if (fields.Count == 0)
                    return ReplayResultRecoveryOutcome.NoChanges;

                using (var update = con.CreateCommand())
                {
                    update.Transaction = tx;
                    update.CommandText = """
                UPDATE attempts
                SET score=@score, accuracy=@accuracy, grade=@grade, combo=@combo,
                    n300=@n300, n100=@n100, n50=@n50, misses=@misses,
                    geki=@geki, katu=@katu,
                    large_tick_hits=@large_tick_hits, large_tick_misses=@large_tick_misses,
                    small_tick_hits=@small_tick_hits, small_tick_misses=@small_tick_misses,
                    slider_tail_hits=@slider_tail_hits, slider_tail_misses=@slider_tail_misses
                WHERE id=@id AND outcome <> 'active'
                """;
                    update.Parameters.AddWithValue("@score", score);
                    update.Parameters.AddWithValue("@accuracy", accuracy);
                    update.Parameters.AddWithValue("@grade", (object?)grade ?? DBNull.Value);
                    update.Parameters.AddWithValue("@combo", combo);
                    update.Parameters.AddWithValue("@n300", n300);
                    update.Parameters.AddWithValue("@n100", n100);
                    update.Parameters.AddWithValue("@n50", n50);
                    update.Parameters.AddWithValue("@misses", misses);
                    update.Parameters.AddWithValue("@geki", geki);
                    update.Parameters.AddWithValue("@katu", katu);
                    update.Parameters.AddWithValue("@large_tick_hits", largeTickHits);
                    update.Parameters.AddWithValue("@large_tick_misses", largeTickMisses);
                    update.Parameters.AddWithValue("@small_tick_hits", smallTickHits);
                    update.Parameters.AddWithValue("@small_tick_misses", smallTickMisses);
                    update.Parameters.AddWithValue("@slider_tail_hits", sliderTailHits);
                    update.Parameters.AddWithValue("@slider_tail_misses", sliderTailMisses);
                    update.Parameters.AddWithValue("@id", attemptId);
                    if (update.ExecuteNonQuery() == 0)
                        return ReplayResultRecoveryOutcome.NotReady;
                }

                cancellationToken.ThrowIfCancellationRequested();
                UpsertScoreContext(
                    con, tx, attemptId, score, grade, n300, n100, n50, misses, geki, katu,
                    largeTickHits, largeTickMisses, smallTickHits, smallTickMisses, sliderTailHits, sliderTailMisses);
                cancellationToken.ThrowIfCancellationRequested();
                RecordRecoverySource(con, tx, attemptId, source, fields);
                RebuildRecoveredPersonalBests(con, tx, attemptId, cancellationToken);
                cancellationToken.ThrowIfCancellationRequested();
                tx.Commit();
                Log.Warning(
                    "Recovered missing tosu result fields for attempt {AttemptId} from {Source}: {Fields}",
                    attemptId, source, string.Join(", ", fields));
                return new ReplayResultRecoveryOutcome(true, true, fields);

                long FillLong(long currentValue, long replayValue, string field)
                {
                    if (currentValue != 0 || replayValue == 0) return currentValue;
                    fields.Add(field);
                    return replayValue;
                }

                int FillInt(int currentValue, int replayValue, string field)
                {
                    if (currentValue != 0 || replayValue == 0) return currentValue;
                    fields.Add(field);
                    return replayValue;
                }

                double FillDouble(double currentValue, double replayValue, string field)
                {
                    if (Math.Abs(currentValue) > 0.000001 || Math.Abs(replayValue) <= 0.000001) return currentValue;
                    fields.Add(field);
                    return replayValue;
                }

                double ReplaceDouble(double currentValue, double replayValue, string field)
                {
                    if (Math.Abs(replayValue) <= 0.000001)
                        return currentValue;
                    fields.Add(field);
                    return Math.Abs(currentValue - replayValue) <= 0.000001 ? currentValue : replayValue;
                }
            }

            return ApplyCore();
        }
        catch (SqliteException exception) when (cancellationToken.IsCancellationRequested)
        {
            throw new OperationCanceledException(
                "Replay result recovery was interrupted by gameplay.",
                exception,
                cancellationToken);
        }
    }

    public ReplayResultRecoveryOutcome ApplySimulation(
        long attemptId,
        ReplaySimulationResult simulation,
        bool simulationOwnsCoreResult = false,
        bool tosuResultWasMissing = false,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var con = factory.Open();
        if (cancellationToken.CanBeCanceled)
            con.DefaultTimeout = 1;
        using var interruptRegistration = cancellationToken.Register(
            static state => SQLitePCL.raw.sqlite3_interrupt(((SqliteConnection)state!).Handle),
            con);
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            using var tx = con.BeginTransaction();
            using var read = con.CreateCommand();
            read.Transaction = tx;
            read.CommandText = """
            SELECT a.outcome, a.slider_breaks, a.large_tick_hits, a.large_tick_misses,
                   a.small_tick_hits, a.small_tick_misses, a.slider_tail_hits, a.slider_tail_misses,
                   a.unstable_rate,
                   COALESCE((SELECT hit_count FROM attempt_timing WHERE attempt_id=@id), 0),
                   a.pp, a.fc_pp, a.max_pp, a.base_stars, a.adjusted_stars, a.beatmap_id,
                   b.stars, b.ar, b.cs, b.od, b.hp, b.bpm, b.max_combo,
                   a.score, a.accuracy, a.grade, a.combo, a.n300, a.n100, a.n50,
                   a.misses, a.geki, a.katu, COALESCE(c.source_json, '{}'),
                   a.duration_seconds, a.progress
            FROM attempts a
            JOIN beatmaps b ON b.id = a.beatmap_id
            LEFT JOIN attempt_context c ON c.attempt_id = a.id
            WHERE a.id=@id
            """;
            read.Parameters.AddWithValue("@id", attemptId);
            cancellationToken.ThrowIfCancellationRequested();
            using var reader = read.ExecuteReader();
            if (!reader.Read() || reader.GetString(0).Equals("active", StringComparison.OrdinalIgnoreCase))
                return ReplayResultRecoveryOutcome.NotReady;

            int sliderBreaks = reader.GetInt32(1);
            int largeTickHits = reader.GetInt32(2);
            int largeTickMisses = reader.GetInt32(3);
            int smallTickHits = reader.GetInt32(4);
            int smallTickMisses = reader.GetInt32(5);
            int sliderTailHits = reader.GetInt32(6);
            int sliderTailMisses = reader.GetInt32(7);
            double unstableRate = reader.GetDouble(8);
            int timingCount = reader.GetInt32(9);
            double pp = reader.GetDouble(10);
            double fcPp = reader.GetDouble(11);
            double maxPp = reader.GetDouble(12);
            double? baseStars = reader.IsDBNull(13) ? null : reader.GetDouble(13);
            double? adjustedStars = reader.IsDBNull(14) ? null : reader.GetDouble(14);
            long beatmapId = reader.GetInt64(15);
            double? beatmapStars = reader.IsDBNull(16) ? null : reader.GetDouble(16);
            double? ar = reader.IsDBNull(17) ? null : reader.GetDouble(17);
            double? cs = reader.IsDBNull(18) ? null : reader.GetDouble(18);
            double? od = reader.IsDBNull(19) ? null : reader.GetDouble(19);
            double? hp = reader.IsDBNull(20) ? null : reader.GetDouble(20);
            double? bpm = reader.IsDBNull(21) ? null : reader.GetDouble(21);
            int maxCombo = reader.GetInt32(22);
            long score = reader.GetInt64(23);
            double accuracy = reader.GetDouble(24);
            string? grade = reader.IsDBNull(25) ? null : reader.GetString(25);
            int combo = reader.GetInt32(26);
            int n300 = reader.GetInt32(27);
            int n100 = reader.GetInt32(28);
            int n50 = reader.GetInt32(29);
            int misses = reader.GetInt32(30);
            int geki = reader.GetInt32(31);
            int katu = reader.GetInt32(32);
            string sourceJson = reader.GetString(33);
            double durationSeconds = reader.GetDouble(34);
            double progress = reader.GetDouble(35);
            reader.Close();
            cancellationToken.ThrowIfCancellationRequested();

            var fields = new List<string>();
            bool replayRecovery = IsReplayRecovery(sourceJson);
            bool checkpointOwnsCoreResult = CheckpointOwnsCoreResult(sourceJson);
            int currentCoreTotal = n300 + n100 + n50 + misses;
            bool simulationOwnsMissingCore = tosuResultWasMissing && currentCoreTotal == 0;
            int simulatedCoreTotal = simulation.N300 + simulation.N100 + simulation.N50 + simulation.Misses;
            if ((replayRecovery || simulationOwnsCoreResult || simulationOwnsMissingCore)
                && (!checkpointOwnsCoreResult || currentCoreTotal == 0)
                && simulatedCoreTotal > 0)
            {
                Replace(ref n300, simulation.N300, "300");
                Replace(ref n100, simulation.N100, "100");
                Replace(ref n50, simulation.N50, "50");
                Replace(ref misses, simulation.Misses, "misses");
                // The replay decoder (or valid tosu telemetry) owns final accuracy.
                // Re-simulation can produce different modern-lazer slider judgements,
                // so its core 300/100/50/miss counts are not an accuracy substitute.
                if ((simulationOwnsCoreResult || simulationOwnsMissingCore) && tosuResultWasMissing)
                    ReplaceDouble(ref accuracy, simulation.Accuracy, "accuracy");
                else
                    accuracy = FillDouble(accuracy, simulation.Accuracy, "accuracy");
                score = FillLong(score, simulation.Score, "score");
                combo = FillInt(combo, simulation.AchievedCombo, "combo");
            }
            else if (tosuResultWasMissing && simulatedCoreTotal > 0)
            {
                // A retained tosu checkpoint is stronger core-result evidence
                // than a ruleset re-simulation, but simulation can still fill
                // fields the broken final packet omitted entirely.
                score = FillLong(score, simulation.Score, "score");
                accuracy = FillDouble(accuracy, simulation.Accuracy, "accuracy");
                combo = FillInt(combo, simulation.AchievedCombo, "combo");
            }
            sliderBreaks = FillInt(sliderBreaks, simulation.SliderBreaks, "slider breaks");
            largeTickHits = FillInt(largeTickHits, simulation.LargeTickHits, "large tick hits");
            largeTickMisses = FillInt(largeTickMisses, simulation.LargeTickMisses, "large tick misses");
            smallTickHits = FillInt(smallTickHits, simulation.SmallTickHits, "small tick hits");
            smallTickMisses = FillInt(smallTickMisses, simulation.SmallTickMisses, "small tick misses");
            sliderTailHits = FillInt(sliderTailHits, simulation.SliderTailHits, "slider tail hits");
            sliderTailMisses = FillInt(sliderTailMisses, simulation.SliderTailMisses, "slider tail misses");
            if (Math.Abs(unstableRate) <= 0.000001 && simulation.UnstableRate > 0)
            {
                unstableRate = simulation.UnstableRate;
                fields.Add("unstable rate");
            }
            pp = FillDouble(pp, simulation.Pp, "pp");
            fcPp = FillDouble(fcPp, simulation.FcPp, "FC pp");
            maxPp = FillDouble(maxPp, simulation.MaxPp, "max pp");
            baseStars = FillNullable(baseStars, simulation.BaseStars, "base star rating");
            adjustedStars = FillNullable(adjustedStars, simulation.AdjustedStars, "mod-adjusted star rating");
            beatmapStars = FillNullable(beatmapStars, simulation.BaseStars, "beatmap star rating");
            ar = FillNullable(ar, simulation.ApproachRate, "AR", allowZero: true);
            cs = FillNullable(cs, simulation.CircleSize, "CS", allowZero: true);
            od = FillNullable(od, simulation.OverallDifficulty, "OD", allowZero: true);
            hp = FillNullable(hp, simulation.DrainRate, "HP", allowZero: true);
            bpm = FillNullable(bpm, simulation.Bpm, "BPM");
            maxCombo = FillInt(maxCombo, simulation.MaxCombo, "map max combo");
            durationSeconds = FillDouble(durationSeconds, simulation.DurationSeconds, "duration");
            progress = FillDouble(progress, simulation.Progress, "progress");

            using (var update = con.CreateCommand())
            {
                update.Transaction = tx;
                update.CommandText = """
                UPDATE attempts SET
                    score=@score,
                    accuracy=@accuracy,
                    combo=@combo,
                    n300=@n300,
                    n100=@n100,
                    n50=@n50,
                    misses=@misses,
                    slider_breaks=@slider_breaks,
                    large_tick_hits=@large_tick_hits,
                    large_tick_misses=@large_tick_misses,
                    small_tick_hits=@small_tick_hits,
                    small_tick_misses=@small_tick_misses,
                    slider_tail_hits=@slider_tail_hits,
                    slider_tail_misses=@slider_tail_misses,
                    unstable_rate=@unstable_rate,
                    pp=@pp,
                    fc_pp=@fc_pp,
                    max_pp=@max_pp,
                    base_stars=@base_stars,
                    adjusted_stars=@adjusted_stars,
                    duration_seconds=@duration_seconds,
                    progress=@progress
                WHERE id=@id AND outcome <> 'active'
                """;
                update.Parameters.AddWithValue("@slider_breaks", sliderBreaks);
                update.Parameters.AddWithValue("@score", score);
                update.Parameters.AddWithValue("@accuracy", accuracy);
                update.Parameters.AddWithValue("@combo", combo);
                update.Parameters.AddWithValue("@n300", n300);
                update.Parameters.AddWithValue("@n100", n100);
                update.Parameters.AddWithValue("@n50", n50);
                update.Parameters.AddWithValue("@misses", misses);
                update.Parameters.AddWithValue("@large_tick_hits", largeTickHits);
                update.Parameters.AddWithValue("@large_tick_misses", largeTickMisses);
                update.Parameters.AddWithValue("@small_tick_hits", smallTickHits);
                update.Parameters.AddWithValue("@small_tick_misses", smallTickMisses);
                update.Parameters.AddWithValue("@slider_tail_hits", sliderTailHits);
                update.Parameters.AddWithValue("@slider_tail_misses", sliderTailMisses);
                update.Parameters.AddWithValue("@unstable_rate", unstableRate);
                update.Parameters.AddWithValue("@pp", pp);
                update.Parameters.AddWithValue("@fc_pp", fcPp);
                update.Parameters.AddWithValue("@max_pp", maxPp);
                update.Parameters.AddWithValue("@base_stars", (object?)baseStars ?? DBNull.Value);
                update.Parameters.AddWithValue("@adjusted_stars", (object?)adjustedStars ?? DBNull.Value);
                update.Parameters.AddWithValue("@duration_seconds", durationSeconds);
                update.Parameters.AddWithValue("@progress", progress);
                update.Parameters.AddWithValue("@id", attemptId);
                cancellationToken.ThrowIfCancellationRequested();
                update.ExecuteNonQuery();
            }

            using (var updateBeatmap = con.CreateCommand())
            {
                updateBeatmap.Transaction = tx;
                updateBeatmap.CommandText = """
                UPDATE beatmaps SET stars=@stars, ar=@ar, cs=@cs, od=@od, hp=@hp,
                                    bpm=@bpm, max_combo=@max_combo
                WHERE id=@id
                """;
                updateBeatmap.Parameters.AddWithValue("@stars", (object?)beatmapStars ?? DBNull.Value);
                updateBeatmap.Parameters.AddWithValue("@ar", (object?)ar ?? DBNull.Value);
                updateBeatmap.Parameters.AddWithValue("@cs", (object?)cs ?? DBNull.Value);
                updateBeatmap.Parameters.AddWithValue("@od", (object?)od ?? DBNull.Value);
                updateBeatmap.Parameters.AddWithValue("@hp", (object?)hp ?? DBNull.Value);
                updateBeatmap.Parameters.AddWithValue("@bpm", (object?)bpm ?? DBNull.Value);
                updateBeatmap.Parameters.AddWithValue("@max_combo", maxCombo);
                updateBeatmap.Parameters.AddWithValue("@id", beatmapId);
                cancellationToken.ThrowIfCancellationRequested();
                updateBeatmap.ExecuteNonQuery();
            }

            if (timingCount == 0 && simulation.TimingOffsets.Count > 0)
            {
                UpsertSimulationTiming(con, tx, attemptId, simulation.TimingOffsets, cancellationToken);
                fields.Add("timing offsets");
            }
            if (replayRecovery || simulationOwnsCoreResult || simulationOwnsMissingCore)
            {
                UpsertScoreContext(
                    con, tx, attemptId, score, grade, n300, n100, n50, misses, geki, katu,
                    largeTickHits, largeTickMisses, smallTickHits, smallTickMisses, sliderTailHits, sliderTailMisses);
                cancellationToken.ThrowIfCancellationRequested();
                ReplaceRecoveredJudgementEvents(con, tx, attemptId, simulation.Judgements, cancellationToken);
                if (simulation.Judgements.Count > 0)
                    fields.Add("judgement events");
            }
            cancellationToken.ThrowIfCancellationRequested();
            UpsertSimulationContext(con, tx, attemptId, simulation);
            cancellationToken.ThrowIfCancellationRequested();
            RecordSimulation(con, tx, attemptId, fields, tosuResultWasMissing);
            RebuildRecoveredPersonalBests(con, tx, attemptId, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            tx.Commit();
            Log.Information(
                "Completed replay simulation recovery for attempt {AttemptId}: {Fields}",
                attemptId, fields.Count == 0 ? "no additional non-zero fields" : string.Join(", ", fields));
            return new ReplayResultRecoveryOutcome(true, fields.Count > 0, fields);

            int FillInt(int current, int simulated, string field)
            {
                if (current != 0 || simulated == 0) return current;
                fields.Add(field);
                return simulated;
            }

            long FillLong(long current, long simulated, string field)
            {
                if (current != 0 || simulated == 0) return current;
                fields.Add(field);
                return simulated;
            }

            double FillDouble(double current, double simulated, string field)
            {
                if (Math.Abs(current) > 0.000001 || simulated <= 0) return current;
                fields.Add(field);
                return simulated;
            }

            double? FillNullable(double? current, double simulated, string field, bool allowZero = false)
            {
                bool hasCurrentValue = allowZero
                    ? current is not null
                    : current is { } currentValue && currentValue > 0.000001;
                if (hasCurrentValue || (!allowZero && simulated <= 0) || simulated < 0) return current;
                fields.Add(field);
                return simulated;
            }

            void Replace(ref int current, int simulated, string field)
            {
                if (current == simulated) return;
                current = simulated;
                fields.Add(field);
            }

            void ReplaceDouble(ref double current, double simulated, string field)
            {
                if (simulated < 0 || Math.Abs(current - simulated) <= 0.000001) return;
                current = simulated;
                fields.Add(field);
            }
        }
        catch (SqliteException exception) when (cancellationToken.IsCancellationRequested)
        {
            throw new OperationCanceledException(
                "Replay simulation recovery was interrupted by gameplay.",
                exception,
                cancellationToken);
        }
    }
}
