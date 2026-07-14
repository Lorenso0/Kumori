using System.Globalization;
using System.IO;
using Kumori.Core;
using Kumori.Core.Models;
using osu.Game.Beatmaps;
using osu.Game.Rulesets;
using osu.Game.Rulesets.Osu;
using osu.Game.Rulesets.Osu.Replays;
using osu.Game.Replays;
using osu.Game.Rulesets.Scoring;
using osu.Game.Scoring.Legacy;

namespace Kumori.App;

/// <summary>Validates a replay through lazer's official .osr decoder.</summary>
internal static class OsrValidationService
{
    public static OsrValidationResult Validate(string path, AttemptDetails attempt, string beatmapPath, IReadOnlyList<MovementSample> capturedSamples)
    {
        using var stream = File.OpenRead(path);
        var decoder = new OsrScoreDecoder(beatmapPath);
        var decoded = decoder.Parse(stream);
        var score = decoded.ScoreInfo;

        if (score.Ruleset.OnlineID != 0)
            throw new InvalidDataException("This replay is not an osu!standard replay.");

        var checksumMatches = !string.IsNullOrWhiteSpace(attempt.Summary.Checksum)
            && string.Equals(decoder.ReplayBeatmapHash, attempt.Summary.Checksum, StringComparison.OrdinalIgnoreCase);
        var n300 = Count(score, HitResult.Great);
        var n100 = Count(score, HitResult.Ok);
        var n50 = Count(score, HitResult.Meh);
        var misses = Count(score, HitResult.Miss);
        var replayAccuracy = score.Accuracy * 100d;
        var recordedAccuracy = attempt.Summary.Accuracy;
        var scoreDelta = score.TotalScore - attempt.Summary.Score;
        var comboDelta = score.MaxCombo - attempt.Summary.Combo;
        var judgementDelta = Math.Abs(n300 - attempt.N300) + Math.Abs(n100 - attempt.N100)
            + Math.Abs(n50 - attempt.N50) + Math.Abs(misses - attempt.Summary.Misses);
        var exactScore = scoreDelta == 0;
        var exactJudgements = judgementDelta == 0;
        var accuracyDelta = replayAccuracy - recordedAccuracy;
        var confidence = (checksumMatches ? 45 : 0)
            + (exactScore ? 20 : Math.Max(0, 20 - Math.Min(20, Math.Abs(scoreDelta) / Math.Max(1d, Math.Abs(score.TotalScore)) * 100)))
            + (exactJudgements ? 20 : Math.Max(0, 20 - Math.Min(20, judgementDelta * 2)))
            + Math.Max(0, 15 - Math.Min(15, Math.Abs(accuracyDelta) * 10));

        return new OsrValidationResult(
            Path.GetFileName(path), checksumMatches, Math.Clamp(confidence, 0, 100),
            replayAccuracy, recordedAccuracy, accuracyDelta, score.TotalScore, attempt.Summary.Score,
            scoreDelta, score.MaxCombo, attempt.Summary.Combo, comboDelta, n300, n100, n50, misses,
            attempt.N300, attempt.N100, attempt.N50, attempt.Summary.Misses,
            score.User.Username, score.Date, score.OnlineID, attempt.Movement,
            CompareMovement(decoded.Replay, capturedSamples));
    }

    private static MovementComparison? CompareMovement(Replay replay, IReadOnlyList<MovementSample> captured)
    {
        var recorded = captured.Where(s => (s.Flags & 0x02) == 0).OrderBy(s => s.MapTimeMs).ToArray();
        var replayFrames = replay.Frames.OfType<OsuReplayFrame>().OrderBy(f => f.Time).ToArray();
        if (recorded.Length == 0 || replayFrames.Length == 0)
            return null;

        var replayPresses = Presses(replayFrames.Select(f => new InputState(f.Time, Buttons(f))));
        var recordedPresses = Presses(recorded.Select(s => new InputState(s.MapTimeMs, s.Buttons)));
        var alignment = AlignmentOffset(replayPresses, recordedPresses);
        var distances = new List<double>();
        var covered = 0;
        foreach (var sample in recorded)
        {
            var frame = Nearest(replayFrames, sample.MapTimeMs + alignment);
            if (frame is null || Math.Abs(frame.Time - (sample.MapTimeMs + alignment)) > 45)
                continue;
            covered++;
            var dx = sample.X - frame.Position.X;
            var dy = sample.Y - frame.Position.Y;
            distances.Add(Math.Sqrt(dx * dx + dy * dy));
        }

        var matched = 0;
        var used = new HashSet<int>();
        foreach (var press in recordedPresses)
        {
            var target = press.Time + alignment;
            var match = replayPresses.Select((candidate, index) => (candidate, index))
                .Where(x => !used.Contains(x.index) && x.candidate.Button == press.Button)
                .Where(x => Math.Abs(x.candidate.Time - target) <= 35)
                .OrderBy(x => Math.Abs(x.candidate.Time - target)).FirstOrDefault();
            if (match.candidate is not null)
            {
                used.Add(match.index);
                matched++;
            }
        }

        distances.Sort();
        return new MovementComparison(
            alignment, recorded.Length, covered, distances.Count == 0 ? null : distances.Average(),
            distances.Count == 0 ? null : Percentile(distances, .5),
            distances.Count == 0 ? null : Percentile(distances, .95),
            recordedPresses.Count, replayPresses.Count, matched);
    }

    private static int Buttons(OsuReplayFrame frame) =>
        (frame.Actions.Contains(OsuAction.LeftButton) ? 0x10 : 0) |
        (frame.Actions.Contains(OsuAction.RightButton) ? 0x20 : 0);

    private static List<Press> Presses(IEnumerable<InputState> frames)
    {
        var presses = new List<Press>();
        var last = 0;
        foreach (var frame in frames)
        {
            foreach (var button in new[] { 0x10, 0x20 })
                if ((frame.Buttons & button) != 0 && (last & button) == 0)
                    presses.Add(new Press(frame.Time, button));
            last = frame.Buttons;
        }
        return presses;
    }

    private static double AlignmentOffset(IReadOnlyList<Press> replay, IReadOnlyList<Press> recorded)
    {
        var count = Math.Min(12, Math.Min(replay.Count, recorded.Count));
        if (count == 0) return 0;
        var offsets = Enumerable.Range(0, count).Where(i => replay[i].Button == recorded[i].Button)
            .Select(i => replay[i].Time - recorded[i].Time).OrderBy(v => v).ToArray();
        return offsets.Length == 0 ? 0 : Percentile(offsets, .5);
    }

    private static OsuReplayFrame? Nearest(IReadOnlyList<OsuReplayFrame> frames, double time)
    {
        if (frames.Count == 0)
            return null;

        var low = 0;
        var high = frames.Count - 1;
        while (low < high)
        {
            var middle = low + (high - low) / 2;
            if (frames[middle].Time < time)
                low = middle + 1;
            else
                high = middle;
        }

        var after = frames[low];
        if (low == 0)
            return after;
        var before = frames[low - 1];
        return Math.Abs(before.Time - time) <= Math.Abs(after.Time - time) ? before : after;
    }

    private static double Percentile(IReadOnlyList<double> sorted, double percentile)
    {
        var position = (sorted.Count - 1) * percentile;
        var low = (int)Math.Floor(position);
        var high = (int)Math.Ceiling(position);
        return sorted[low] + (sorted[high] - sorted[low]) * (position - low);
    }

    private sealed record InputState(double Time, int Buttons);
    private sealed record Press(double Time, int Button);

    private static int Count(osu.Game.Scoring.ScoreInfo score, HitResult result) => score.Statistics.GetValueOrDefault(result);

    private sealed class OsrScoreDecoder(string beatmapPath) : LegacyScoreDecoder
    {
        private readonly WorkingBeatmap _beatmap = new FlatWorkingBeatmap(beatmapPath);

        public string ReplayBeatmapHash { get; private set; } = "";

        protected override Ruleset GetRuleset(int rulesetId) => rulesetId == 0
            ? new OsuRuleset()
            : throw new InvalidDataException("This replay is not an osu!standard replay.");

        protected override WorkingBeatmap GetBeatmap(string md5Hash)
        {
            ReplayBeatmapHash = md5Hash;
            return _beatmap;
        }
    }
}

public sealed record OsrValidationResult(
    string FileName, bool ChecksumMatches, double Confidence, double ReplayAccuracy,
    double RecordedAccuracy, double AccuracyDelta, long ReplayScore, long RecordedScore,
    long ScoreDelta, int ReplayCombo, int RecordedCombo, int ComboDelta, int Replay300,
    int Replay100, int Replay50, int ReplayMisses, int Recorded300, int Recorded100,
    int Recorded50, int RecordedMisses, string ReplayPlayer, DateTimeOffset ReplayDate,
    long ReplayOnlineId, MovementSummary? Movement, MovementComparison? Comparison)
{
    public bool HasComparison => Comparison is not null;
    public string Verdict => ChecksumMatches
        ? Confidence >= 99.9 ? "Exact match" : "Same map — capture differs"
        : "Different map replay";
    public string ConfidenceText => $"{Confidence:0.0}%";
    public string ReplayAccuracyText => $"{ReplayAccuracy:0.00}%";
    public string RecordedAccuracyText => $"{RecordedAccuracy:0.00}%";
    public string AccuracyDeltaText => $"{AccuracyDelta:+0.00;-0.00;0.00}%";
    public string AccuracyComparisonText => $"Kumori {RecordedAccuracyText} · difference {AccuracyDeltaText}";
    public string ReplayScoreText => ReplayScore.ToString("N0", CultureInfo.InvariantCulture);
    public string RecordedScoreText => RecordedScore.ToString("N0", CultureInfo.InvariantCulture);
    public string ScoreDeltaText => ScoreDelta.ToString("+#,0;-#,0;0", CultureInfo.InvariantCulture);
    public string ScoreComparisonText => $"Kumori {RecordedScoreText} · difference {ScoreDeltaText}";
    public string ReplayComboText => ReplayCombo.ToString("N0", CultureInfo.InvariantCulture);
    public string RecordedComboText => RecordedCombo.ToString("N0", CultureInfo.InvariantCulture);
    public string ComboDeltaText => ComboDelta.ToString("+#,0;-#,0;0", CultureInfo.InvariantCulture);
    public string ComboComparisonText => $"Kumori {RecordedComboText} · difference {ComboDeltaText}";
    public string ReplayIdentityText => $"{ReplayPlayer} · {DisplayDateTime.FormatLocalDateTimeWithSeconds(ReplayDate)}" + (ReplayOnlineId > 0 ? $" · score #{ReplayOnlineId}" : "");
    public string CaptureText => Movement is { Available: true } m
        ? $"{m.SampleCount:N0} samples at {m.SampleRate:0} Hz · {m.DroppedSamples:N0} dropped"
        : "No cursor samples were saved for this attempt.";
}

public sealed record MovementComparison(double AlignmentOffsetMs, int RecordedSamples, int MatchedSamples,
    double? MeanCursorError, double? MedianCursorError, double? P95CursorError,
    int RecordedPresses, int ReplayPresses, int MatchedPresses)
{
    public string CoverageText => $"{MatchedSamples:N0}/{RecordedSamples:N0} ({(RecordedSamples == 0 ? 0 : MatchedSamples * 100d / RecordedSamples):0.0}%)";
    public string AlignmentText => Math.Abs(AlignmentOffsetMs) < 0.05 ? "already aligned" : $"{AlignmentOffsetMs:+0.0;-0.0;0.0} ms offset";
    public string MeanErrorText => Pixels(MeanCursorError);
    public string MedianErrorText => Pixels(MedianCursorError);
    public string P95ErrorText => Pixels(P95CursorError);
    public string ClickMatchText => $"{MatchedPresses:N0}/{RecordedPresses:N0} ({(RecordedPresses == 0 ? 0 : MatchedPresses * 100d / RecordedPresses):0.0}%)";
    private static string Pixels(double? value) => value switch
    {
        null => "—",
        < 0.05 => "exact (0.0 px)",
        _ => $"{value:0.0} px",
    };
}
