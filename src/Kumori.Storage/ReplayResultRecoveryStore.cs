using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Data.Sqlite;
using Serilog;

namespace Kumori.Storage;

/// <summary>Result values which are stored in an osu! replay header.</summary>
public sealed record ReplayResultData(
    long Score,
    double Accuracy,
    string? Grade,
    int Combo,
    int N300,
    int N100,
    int N50,
    int Misses,
    int Geki,
    int Katu,
    int LargeTickHits = 0,
    int LargeTickMisses = 0,
    int SmallTickHits = 0,
    int SmallTickMisses = 0,
    int SliderTailHits = 0,
    int SliderTailMisses = 0);

public sealed record ReplayResultRecoveryOutcome(
    bool AttemptReady,
    bool Applied,
    IReadOnlyList<string> RecoveredFields)
{
    public static ReplayResultRecoveryOutcome NotReady { get; } = new(false, false, []);
    public static ReplayResultRecoveryOutcome NoChanges { get; } = new(true, false, []);
}

/// <summary>
/// Repairs result fields which tosu omitted, using the checksum-matched replay.
/// Existing non-zero telemetry wins unless the result snapshot was incomplete
/// enough that its accuracy was only a tosu placeholder, or an older recovery
/// schema replaced replay accuracy with a reconstructed core-hit formula.
/// </summary>
public sealed class ReplayResultRecoveryStore(SqliteConnectionFactory factory)
{
    public const int CurrentSimulationSchema = 2;

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
                   a.misses, a.geki, a.katu, COALESCE(c.source_json, '{}')
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
            reader.Close();
            cancellationToken.ThrowIfCancellationRequested();

            var fields = new List<string>();
            bool replayRecovery = IsReplayRecovery(sourceJson);
            int simulatedCoreTotal = simulation.N300 + simulation.N100 + simulation.N50 + simulation.Misses;
            if (replayRecovery && simulatedCoreTotal > 0)
            {
                Replace(ref n300, simulation.N300, "300");
                Replace(ref n100, simulation.N100, "100");
                Replace(ref n50, simulation.N50, "50");
                Replace(ref misses, simulation.Misses, "misses");
                // The replay decoder (or valid tosu telemetry) owns final accuracy.
                // Re-simulation can produce different modern-lazer slider judgements,
                // so its core 300/100/50/miss counts are not an accuracy substitute.
                accuracy = FillDouble(accuracy, simulation.Accuracy, "accuracy");
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

            using (var update = con.CreateCommand())
            {
                update.Transaction = tx;
                update.CommandText = """
                UPDATE attempts SET
                    accuracy=@accuracy,
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
                    adjusted_stars=@adjusted_stars
                WHERE id=@id AND outcome <> 'active'
                """;
                update.Parameters.AddWithValue("@slider_breaks", sliderBreaks);
                update.Parameters.AddWithValue("@accuracy", accuracy);
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
            if (replayRecovery)
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
            RecordSimulation(con, tx, attemptId, fields);
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
        }
        catch (SqliteException exception) when (cancellationToken.IsCancellationRequested)
        {
            throw new OperationCanceledException(
                "Replay simulation recovery was interrupted by gameplay.",
                exception,
                cancellationToken);
        }
    }

    private static bool IsReplayRecovery(string sourceJson)
    {
        try
        {
            using var document = JsonDocument.Parse(sourceJson);
            return document.RootElement.TryGetProperty("result_recovery", out var recovery)
                   && recovery.TryGetProperty("reason", out var reason)
                   && string.Equals(reason.GetString(), "tosu_gameplay_values_missing", StringComparison.Ordinal);
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static bool ReplayMustOwnAccuracy(CurrentResult current, ReplayResultData replay)
    {
        int currentCoreTotal = current.N300 + current.N100 + current.N50 + current.Misses;
        int replayCoreTotal = replay.N300 + replay.N100 + replay.N50 + replay.Misses;

        // tosu can briefly report 100% while omitting the entire final result.
        // Once a checksum-matched replay supplies those judgements, its header
        // is the authority for accuracy as well as the other missing fields.
        if (currentCoreTotal == 0 && replayCoreTotal > 0)
            return true;

        try
        {
            using var document = JsonDocument.Parse(current.SourceJson);
            return NeedsAccuracyAuthorityRepair(
                document.RootElement,
                current.Accuracy,
                current.N100,
                current.N50,
                current.Misses);
        }
        catch (JsonException)
        {
            return false;
        }
    }

    public static bool NeedsAccuracyAuthorityRepair(JsonElement source)
    {
        if (!source.TryGetProperty("result_recovery", out var recovery)
            || recovery.ValueKind != JsonValueKind.Object)
            return false;

        if (recovery.TryGetProperty("accuracy_source", out var accuracySource)
            && accuracySource.ValueKind == JsonValueKind.String
            && string.Equals(accuracySource.GetString(), "replay_or_tosu", StringComparison.Ordinal))
            return false;

        if (!recovery.TryGetProperty("simulated_fields", out var fields)
            || fields.ValueKind != JsonValueKind.Array)
            return false;

        return fields.EnumerateArray().Any(field =>
            field.ValueKind == JsonValueKind.String
            && string.Equals(field.GetString(), "accuracy", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Also detects results produced by the previous recovery path, which kept
    /// a placeholder 100% after replay judgements had proved the play imperfect.
    /// </summary>
    public static bool NeedsAccuracyAuthorityRepair(
        JsonElement source,
        double accuracy,
        int n100,
        int n50,
        int misses)
    {
        if (NeedsAccuracyAuthorityRepair(source))
            return true;

        if (accuracy < 99.999999 || (n100 == 0 && n50 == 0 && misses == 0))
            return false;

        // A perfect value cannot coexist with an explicitly imperfect core
        // judgement. A checksum-matched replay is the safe authority regardless
        // of which older persistence path wrote the row.
        return true;
    }

    private static void ReplaceRecoveredJudgementEvents(
        SqliteConnection con,
        SqliteTransaction tx,
        long attemptId,
        IReadOnlyList<ReplaySimulationJudgement> judgements,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using (var delete = con.CreateCommand())
        {
            delete.Transaction = tx;
            delete.CommandText = "DELETE FROM attempt_events WHERE attempt_id=@id AND event_type IN ('miss', 'hit_50', 'hit_100', 'slider_break')";
            delete.Parameters.AddWithValue("@id", attemptId);
            delete.ExecuteNonQuery();
        }

        for (var index = 0; index < judgements.Count; index++)
        {
            if ((index & 127) == 0)
                cancellationToken.ThrowIfCancellationRequested();
            var judgement = judgements[index];
            string? eventType = judgement.Kind switch
            {
                0 => "miss",
                1 => "hit_50",
                2 => "hit_100",
                3 => "slider_break",
                _ => null,
            };
            if (eventType is null) continue;

            double mapTime = judgement.Kind == 3 ? judgement.ObjectStartTime : judgement.RootStartTime;
            var data = new JsonObject
            {
                ["source"] = "replay_simulation",
                ["time_offset_ms"] = judgement.TimeOffset,
                ["result_time_ms"] = judgement.EventTime,
            };
            using var insert = con.CreateCommand();
            insert.Transaction = tx;
            insert.CommandText = """
                INSERT INTO attempt_events(attempt_id, captured_at, map_time_ms, event_type, value, data_json)
                VALUES(@id, @captured, @time, @type, 1, @data)
                """;
            insert.Parameters.AddWithValue("@id", attemptId);
            insert.Parameters.AddWithValue("@captured", DateTimeOffset.UtcNow.ToString("O"));
            insert.Parameters.AddWithValue("@time", Math.Max(0, (long)Math.Round(mapTime)));
            insert.Parameters.AddWithValue("@type", eventType);
            insert.Parameters.AddWithValue("@data", data.ToJsonString());
            insert.ExecuteNonQuery();
        }
    }

    private static CurrentResult? Read(
        SqliteConnection con,
        SqliteTransaction tx,
        long attemptId,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var cmd = con.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = """
            SELECT a.outcome, a.score, a.accuracy, a.grade, a.combo, a.n300, a.n100, a.n50,
                   a.misses, a.geki, a.katu, a.large_tick_hits, a.large_tick_misses,
                   a.small_tick_hits, a.small_tick_misses, a.slider_tail_hits,
                   a.slider_tail_misses, COALESCE(c.source_json, '{}')
            FROM attempts a
            LEFT JOIN attempt_context c ON c.attempt_id=a.id
            WHERE a.id=@id
            """;
        cmd.Parameters.AddWithValue("@id", attemptId);
        using var reader = cmd.ExecuteReader();
        cancellationToken.ThrowIfCancellationRequested();
        return reader.Read()
            ? new CurrentResult(
                reader.GetString(0), reader.GetInt64(1), reader.GetDouble(2),
                reader.IsDBNull(3) ? null : reader.GetString(3), reader.GetInt32(4),
                reader.GetInt32(5), reader.GetInt32(6), reader.GetInt32(7),
                reader.GetInt32(8), reader.GetInt32(9), reader.GetInt32(10),
                reader.GetInt32(11), reader.GetInt32(12), reader.GetInt32(13),
                reader.GetInt32(14), reader.GetInt32(15), reader.GetInt32(16),
                reader.GetString(17))
            : null;
    }

    private static void UpsertScoreContext(
        SqliteConnection con, SqliteTransaction tx, long attemptId, long score, string? grade,
        int n300, int n100, int n50, int misses, int geki, int katu,
        int largeTickHits, int largeTickMisses, int smallTickHits, int smallTickMisses,
        int sliderTailHits, int sliderTailMisses)
    {
        string existingJson = "{}";
        using (var read = con.CreateCommand())
        {
            read.Transaction = tx;
            read.CommandText = "SELECT score_json FROM attempt_context WHERE attempt_id=@id";
            read.Parameters.AddWithValue("@id", attemptId);
            existingJson = read.ExecuteScalar() as string ?? "{}";
        }

        JsonObject root;
        try { root = JsonNode.Parse(existingJson) as JsonObject ?? []; }
        catch (JsonException) { root = []; }
        var hits = root["hits"] as JsonObject ?? [];
        root["score"] = score;
        root["grade"] = grade ?? "";
        hits["_300"] = n300;
        hits["_100"] = n100;
        hits["_50"] = n50;
        hits["_0"] = misses;
        hits["geki"] = geki;
        hits["katu"] = katu;
        hits["largeTickHits"] = largeTickHits;
        hits["largeTickMisses"] = largeTickMisses;
        hits["smallTickHits"] = smallTickHits;
        hits["smallTickMisses"] = smallTickMisses;
        hits["sliderTailHits"] = sliderTailHits;
        hits["sliderTailMisses"] = sliderTailMisses;
        root["hits"] = hits;
        root["recovered_from_replay"] = true;

        using var cmd = con.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = """
            INSERT INTO attempt_context(attempt_id, source_json, pp_json, beatmap_json,
                                        score_json, session_json, multiplayer_json)
            VALUES(@id, '{}', '{}', '{}', @score_json, '{}', '{}')
            ON CONFLICT(attempt_id) DO UPDATE SET score_json=excluded.score_json
            """;
        cmd.Parameters.AddWithValue("@id", attemptId);
        cmd.Parameters.AddWithValue("@score_json", root.ToJsonString());
        cmd.ExecuteNonQuery();
    }

    private static void RecordRecoverySource(
        SqliteConnection con, SqliteTransaction tx, long attemptId, string source, IReadOnlyList<string> fields)
    {
        string sourceJson = "{}";
        using (var read = con.CreateCommand())
        {
            read.Transaction = tx;
            read.CommandText = "SELECT source_json FROM attempt_context WHERE attempt_id=@id";
            read.Parameters.AddWithValue("@id", attemptId);
            sourceJson = read.ExecuteScalar() as string ?? "{}";
        }

        JsonObject root;
        try { root = JsonNode.Parse(sourceJson) as JsonObject ?? []; }
        catch (JsonException) { root = []; }
        var recoveredFields = new JsonArray();
        foreach (var field in fields)
            recoveredFields.Add(field);
        var recovery = root["result_recovery"] as JsonObject ?? [];
        recovery["source"] = source;
        recovery["reason"] = "tosu_gameplay_values_missing";
        recovery["recovered_at_utc"] = DateTimeOffset.UtcNow.ToString("O");
        recovery["fields"] = recoveredFields;
        if (fields.Contains("accuracy", StringComparer.OrdinalIgnoreCase))
            recovery["accuracy_source"] = "replay_or_tosu";
        root["result_recovery"] = recovery;

        using var update = con.CreateCommand();
        update.Transaction = tx;
        update.CommandText = "UPDATE attempt_context SET source_json=@json WHERE attempt_id=@id";
        update.Parameters.AddWithValue("@json", root.ToJsonString());
        update.Parameters.AddWithValue("@id", attemptId);
        update.ExecuteNonQuery();
    }

    private static void RebuildRecoveredPersonalBests(
        SqliteConnection con,
        SqliteTransaction tx,
        long attemptId,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        long beatmapId;
        string modsKey;
        using (var group = con.CreateCommand())
        {
            group.Transaction = tx;
            group.CommandText = "SELECT beatmap_id, mods_key FROM attempts WHERE id=@id";
            group.Parameters.AddWithValue("@id", attemptId);
            using var reader = group.ExecuteReader();
            if (!reader.Read()) return;
            beatmapId = reader.GetInt64(0);
            modsKey = reader.GetString(1);
        }

        foreach (var (metric, column, order) in new[]
        {
            ("score", "score", "DESC"),
            ("accuracy", "accuracy", "DESC"),
            ("pp", "pp", "DESC"),
            ("combo", "combo", "DESC"),
            ("fewest_misses", "misses", "ASC"),
        })
        {
            cancellationToken.ThrowIfCancellationRequested();
            using (var delete = con.CreateCommand())
            {
                delete.Transaction = tx;
                delete.CommandText = "DELETE FROM personal_bests WHERE beatmap_id=@beatmap AND mods_key=@mods AND metric=@metric";
                delete.Parameters.AddWithValue("@beatmap", beatmapId);
                delete.Parameters.AddWithValue("@mods", modsKey);
                delete.Parameters.AddWithValue("@metric", metric);
                delete.ExecuteNonQuery();
            }

            using var best = con.CreateCommand();
            best.Transaction = tx;
            // column/order are fixed constants above, never external input.
            best.CommandText = $"""
                INSERT INTO personal_bests(beatmap_id, mods_key, metric, attempt_id, value)
                SELECT @beatmap, @mods, @metric, id, {column}
                FROM attempts
                WHERE beatmap_id=@beatmap AND mods_key=@mods
                  AND outcome IN ('completed', 'failed')
                  AND (score > 0 OR n300 + n100 + n50 + misses > 0)
                ORDER BY {column} {order}, id DESC
                LIMIT 1
                """;
            best.Parameters.AddWithValue("@beatmap", beatmapId);
            best.Parameters.AddWithValue("@mods", modsKey);
            best.Parameters.AddWithValue("@metric", metric);
            best.ExecuteNonQuery();
        }
    }

    private static void UpsertSimulationTiming(
        SqliteConnection con,
        SqliteTransaction tx,
        long attemptId,
        IReadOnlyList<double> offsets,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var values = new double[offsets.Count];
        for (var index = 0; index < offsets.Count; index++)
        {
            if ((index & 1023) == 0)
                cancellationToken.ThrowIfCancellationRequested();
            values[index] = offsets[index];
        }
        var sorted = (double[])values.Clone();
        Array.Sort(sorted);
        cancellationToken.ThrowIfCancellationRequested();
        double mean = values.Average();
        double median = sorted.Length % 2 == 1
            ? sorted[sorted.Length / 2]
            : (sorted[sorted.Length / 2 - 1] + sorted[sorted.Length / 2]) / 2;
        double deviation = Math.Sqrt(values.Average(value => Math.Pow(value - mean, 2)));
        using var cmd = con.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = """
            INSERT INTO attempt_timing(attempt_id, offsets_zlib, hit_count, early_count,
                                       late_count, mean, median, deviation)
            VALUES(@id, @offsets, @count, @early, @late, @mean, @median, @deviation)
            ON CONFLICT(attempt_id) DO UPDATE SET
                offsets_zlib=excluded.offsets_zlib, hit_count=excluded.hit_count,
                early_count=excluded.early_count, late_count=excluded.late_count,
                mean=excluded.mean, median=excluded.median, deviation=excluded.deviation
            """;
        cmd.Parameters.AddWithValue("@id", attemptId);
        cmd.Parameters.Add("@offsets", SqliteType.Blob).Value = BlobCodec.EncodeOffsets(values);
        cmd.Parameters.AddWithValue("@count", values.Length);
        cmd.Parameters.AddWithValue("@early", values.Count(value => value < 0));
        cmd.Parameters.AddWithValue("@late", values.Count(value => value > 0));
        cmd.Parameters.AddWithValue("@mean", mean);
        cmd.Parameters.AddWithValue("@median", median);
        cmd.Parameters.AddWithValue("@deviation", deviation);
        cmd.ExecuteNonQuery();
    }

    private static void UpsertSimulationContext(
        SqliteConnection con,
        SqliteTransaction tx,
        long attemptId,
        ReplaySimulationResult simulation)
    {
        string ppJson = "{}";
        string beatmapJson = "{}";
        using (var read = con.CreateCommand())
        {
            read.Transaction = tx;
            read.CommandText = "SELECT pp_json, beatmap_json FROM attempt_context WHERE attempt_id=@id";
            read.Parameters.AddWithValue("@id", attemptId);
            using var reader = read.ExecuteReader();
            if (reader.Read())
            {
                ppJson = reader.IsDBNull(0) ? "{}" : reader.GetString(0);
                beatmapJson = reader.IsDBNull(1) ? "{}" : reader.GetString(1);
            }
        }

        JsonObject pp = ParseObject(ppJson);
        SetNumberIfMissing(pp, "pp", simulation.Pp);
        SetNumberIfMissing(pp, "fc_pp", simulation.FcPp);
        SetNumberIfMissing(pp, "max_pp", simulation.MaxPp);
        pp["recovered_from_replay_simulation"] = true;

        JsonObject beatmap = ParseObject(beatmapJson);
        JsonObject stats = beatmap["stats"] as JsonObject ?? [];
        JsonObject stars = stats["stars"] as JsonObject ?? [];
        SetNumberIfMissing(stars, "original", simulation.BaseStars);
        SetNumberIfMissing(stars, "total", simulation.AdjustedStars);
        SetNumberIfMissing(stars, "converted", simulation.AdjustedStars);
        stats["stars"] = stars;
        SetPair(stats, "ar", simulation.ApproachRate, simulation.AdjustedApproachRate);
        SetPair(stats, "cs", simulation.CircleSize, simulation.AdjustedCircleSize);
        SetPair(stats, "od", simulation.OverallDifficulty, simulation.AdjustedOverallDifficulty);
        SetPair(stats, "hp", simulation.DrainRate, simulation.AdjustedDrainRate);
        JsonObject bpm = stats["bpm"] as JsonObject ?? [];
        SetNumberIfMissing(bpm, "common", simulation.Bpm);
        SetNumberIfMissing(bpm, "realtime", simulation.AdjustedBpm);
        stats["bpm"] = bpm;
        SetNumberIfMissing(stats, "clockRate", simulation.ClockRate);
        SetNumberIfMissing(stats, "maxCombo", simulation.MaxCombo);
        JsonObject objects = stats["objects"] as JsonObject ?? [];
        SetNumberIfMissing(objects, "circles", simulation.CircleCount);
        SetNumberIfMissing(objects, "sliders", simulation.SliderCount);
        SetNumberIfMissing(objects, "spinners", simulation.SpinnerCount);
        stats["objects"] = objects;
        beatmap["stats"] = stats;
        beatmap["recovered_from_replay_simulation"] = true;

        using var update = con.CreateCommand();
        update.Transaction = tx;
        update.CommandText = """
            INSERT INTO attempt_context(attempt_id, source_json, pp_json, beatmap_json,
                                        score_json, session_json, multiplayer_json)
            VALUES(@id, '{}', @pp, @beatmap, '{}', '{}', '{}')
            ON CONFLICT(attempt_id) DO UPDATE SET
                pp_json=excluded.pp_json,
                beatmap_json=excluded.beatmap_json
            """;
        update.Parameters.AddWithValue("@id", attemptId);
        update.Parameters.AddWithValue("@pp", pp.ToJsonString());
        update.Parameters.AddWithValue("@beatmap", beatmap.ToJsonString());
        update.ExecuteNonQuery();

        static JsonObject ParseObject(string json)
        {
            try { return JsonNode.Parse(json) as JsonObject ?? []; }
            catch (JsonException) { return []; }
        }

        static void SetPair(JsonObject stats, string name, double original, double converted)
        {
            JsonObject pair = stats[name] as JsonObject ?? [];
            SetNumberIfMissing(pair, "original", original, allowZero: true);
            SetNumberIfMissing(pair, "converted", converted, allowZero: true);
            stats[name] = pair;
        }

        static void SetNumberIfMissing(JsonObject parent, string name, double value, bool allowZero = false)
        {
            if ((!allowZero && value <= 0) || value < 0 || HasPositiveNumber(parent[name])) return;
            parent[name] = value;
        }

        static bool HasPositiveNumber(JsonNode? node)
            => node is JsonValue value
               && value.TryGetValue<double>(out double number)
               && number > 0.000001;
    }

    private static void RecordSimulation(
        SqliteConnection con,
        SqliteTransaction tx,
        long attemptId,
        IReadOnlyList<string> fields)
    {
        string sourceJson = "{}";
        using (var read = con.CreateCommand())
        {
            read.Transaction = tx;
            read.CommandText = "SELECT source_json FROM attempt_context WHERE attempt_id=@id";
            read.Parameters.AddWithValue("@id", attemptId);
            sourceJson = read.ExecuteScalar() as string ?? "{}";
        }
        JsonObject root;
        try { root = JsonNode.Parse(sourceJson) as JsonObject ?? []; }
        catch (JsonException) { root = []; }
        var recovery = root["result_recovery"] as JsonObject ?? [];
        var simulatedFields = new JsonArray();
        foreach (string field in fields) simulatedFields.Add(field);
        recovery["simulation"] = "completed";
        recovery["simulation_schema"] = CurrentSimulationSchema;
        recovery["simulation_completed_at_utc"] = DateTimeOffset.UtcNow.ToString("O");
        recovery["simulated_fields"] = simulatedFields;
        root["result_recovery"] = recovery;
        using var update = con.CreateCommand();
        update.Transaction = tx;
        update.CommandText = "UPDATE attempt_context SET source_json=@json WHERE attempt_id=@id";
        update.Parameters.AddWithValue("@json", root.ToJsonString());
        update.Parameters.AddWithValue("@id", attemptId);
        update.ExecuteNonQuery();
    }

    private sealed record CurrentResult(
        string Outcome, long Score, double Accuracy, string? Grade, int Combo,
        int N300, int N100, int N50, int Misses, int Geki, int Katu,
        int LargeTickHits, int LargeTickMisses, int SmallTickHits, int SmallTickMisses,
        int SliderTailHits, int SliderTailMisses, string SourceJson);
}
