using osuTK;

namespace Kumori.ReplayViewer;

internal enum AnalyzerEvidenceConfidence
{
    Low,
    Medium,
    High,
}

internal static class AdvancedAnalyzerMetrics
{
    public static string Diagnosis(MissAnalysisEntry entry)
    {
        if (entry.Kind == KumoriTimelineMarkerKind.SliderBreak)
        {
            if (entry.NearestFrame == null)
                return "Not enough data to classify";
            if (!entry.NearestFrame.HasAction || entry.InputFrame?.Released == true)
                return "Slider released early";
            if (entry.DistanceFromTarget is { } sliderDistance && sliderDistance > entry.TargetRadius * 1.5)
                return "Cursor left the slider follow area";
            return "Slider follow was interrupted";
        }

        bool? aimOutside = entry.DistanceFromTarget is { } distance ? distance > entry.TargetRadius : null;
        bool? timingOutside = entry.InputOffsetMs is { } offset ? Math.Abs(offset) > entry.HitWindowMs : null;

        if (entry.Kind == KumoriTimelineMarkerKind.Meh && entry.InputOffsetMs != null)
            return $"Tap landed in the 50 timing window ({TimingWord(entry)})";
        if (entry.Kind == KumoriTimelineMarkerKind.Ok && entry.InputOffsetMs != null)
            return $"Tap landed in the 100 timing window ({TimingWord(entry)})";

        if (entry.InputFrame == null && entry.InputOffsetMs == null && entry.Kind == KumoriTimelineMarkerKind.Miss)
            return entry.DistanceFromTarget is { } nearest && nearest <= entry.TargetRadius
                ? "Cursor reached the target, but no tap was detected"
                : "No tap was detected in the hit window";

        if (aimOutside == true)
        {
            string aimDirection = DirectionalError(entry) switch
            {
                < -1 => "Cursor stopped short",
                > 1 => "Cursor passed beyond the target",
                _ => "Cursor was outside the hit area",
            };
            return timingOutside == true ? $"{aimDirection}; tap was {TimingWord(entry)}" : aimDirection;
        }

        if (timingOutside == true)
            return $"Aim was on target, but the tap was {TimingWord(entry)}";
        if (aimOutside == false && timingOutside == false && entry.Kind == KumoriTimelineMarkerKind.Miss)
            return "Cursor and tap aligned; inspect playback";
        if (aimOutside == false && timingOutside == false)
            return "Inside target; timing reduced the result";
        return "Not enough data to classify";
    }

    public static string EventSummary(MissAnalysisEntry entry)
    {
        string aim = FormatCursorPosition(entry).Replace("Cursor: ", string.Empty, StringComparison.Ordinal);

        if (entry.Kind == KumoriTimelineMarkerKind.SliderBreak)
            return Diagnosis(entry);
        if (entry.InputFrame == null && entry.InputOffsetMs == null)
            return $"No tap detected; {aim}";

        double offset = entry.InputOffsetMs ?? 0;
        return $"{Math.Abs(offset):0} ms {(offset < 0 ? "early" : "late")}; {aim}";
    }

    public static AnalyzerEvidenceConfidence Confidence(MissAnalysisEntry entry)
    {
        if (entry.Source == AnalysisDataSource.Lazer && entry.ExactTiming && entry.DistanceFromTarget != null)
            return AnalyzerEvidenceConfidence.High;
        if (entry.InputFrame != null || entry.NearestFrame != null)
            return AnalyzerEvidenceConfidence.Medium;
        return AnalyzerEvidenceConfidence.Low;
    }

    public static string EvidenceLabel(MissAnalysisEntry entry) => Confidence(entry) switch
    {
        AnalyzerEvidenceConfidence.High => "HIGH CONFIDENCE · EXACT LAZER JUDGEMENT",
        AnalyzerEvidenceConfidence.Medium when entry.Source == AnalysisDataSource.Lazer => "MEDIUM CONFIDENCE · PARTIAL INPUT EVIDENCE",
        AnalyzerEvidenceConfidence.Medium => "MEDIUM CONFIDENCE · RECONSTRUCTED CAPTURE",
        _ => "LOW CONFIDENCE · INCOMPLETE EVIDENCE",
    };

    public static string PatternSummary(IReadOnlyList<MissAnalysisEntry> entries)
    {
        MissAnalysisEntry[] misses = entries.Where(e => e.Kind == KumoriTimelineMarkerKind.Miss).ToArray();
        if (misses.Length == 0)
            return entries.Count == 0 ? "No review events found." : "No misses in the current analysis.";

        int shortCount = misses.Count(e => DirectionalError(e) is < -1);
        int overshootCount = misses.Count(e => DirectionalError(e) is > 1);
        int noTapCount = misses.Count(e => e.InputFrame == null);
        double[] offsets = misses.Where(e => e.InputOffsetMs != null).Select(e => e.InputOffsetMs!.Value).ToArray();

        var patterns = new List<(int Count, string Text)>
        {
            (shortCount, $"{shortCount}/{misses.Length} misses stopped short"),
            (overshootCount, $"{overshootCount}/{misses.Length} misses passed the target"),
            (noTapCount, $"{noTapCount}/{misses.Length} misses had no detected tap"),
        };
        string strongest = patterns.Where(p => p.Count > 0).OrderByDescending(p => p.Count).FirstOrDefault().Text
                           ?? $"{misses.Length} misses had mixed causes";
        string direction = DirectionSummary(misses);
        if (offsets.Length == 0)
            return $"{strongest}. {direction}";

        double average = offsets.Average();
        return $"{strongest}. {direction} Average tap: {Math.Abs(average):0} ms {(average < 0 ? "early" : "late")}.";
    }

    public static string TimingBoundaryExplanation(MissAnalysisEntry entry)
    {
        if (entry.InputOffsetMs is not { } offset)
            return string.Empty;
        double absolute = Math.Abs(offset);
        return entry.Kind switch
        {
            KumoriTimelineMarkerKind.Ok => $"100: {Math.Max(0, absolute - entry.HitWindows.Great):0.0} ms outside 300 window",
            KumoriTimelineMarkerKind.Meh => $"50: {Math.Max(0, absolute - entry.HitWindows.Ok):0.0} ms outside 100 window",
            KumoriTimelineMarkerKind.Miss => $"Miss: {Math.Max(0, absolute - entry.HitWindows.Meh):0.0} ms outside 50 window",
            _ => "Inside the 300 timing window",
        };
    }

    public static string RecentTrendSummary(ViewerContract? contract)
    {
        if (contract?.FinalHits is not { } current || contract.RecentAttempts.Count == 0)
            return "No earlier attempts on this map to compare.";
        double accuracy = contract.RecentAttempts.Average(a => a.Accuracy);
        double misses = contract.RecentAttempts.Average(a => a.Misses);
        string accuracyTrend = contract.Attempt.Accuracy >= accuracy ? "up" : "down";
        string missTrend = current.Misses <= misses ? "fewer" : "more";
        string timing = contract.Attempt.MeanOffset is { } now
                        && contract.RecentAttempts.Where(a => a.MeanOffset != null).Select(a => a.MeanOffset!.Value).ToArray() is { Length: > 0 } prior
            ? $" Timing shifted {Math.Abs(now - prior.Average()):0} ms {(now >= prior.Average() ? "later" : "earlier")}."
            : string.Empty;
        return $"Vs last {contract.RecentAttempts.Count}: accuracy {accuracyTrend} {Math.Abs(contract.Attempt.Accuracy - accuracy):0.00}%; {missTrend} misses ({current.Misses} vs {misses:0.0}).{timing}";
    }

    private static string DirectionSummary(IEnumerable<MissAnalysisEntry> entries)
    {
        string[] directions = entries.Select(AimDirection).Where(d => d != null).Cast<string>().ToArray();
        if (directions.Length == 0)
            return "No consistent aim direction.";
        var strongest = directions.GroupBy(d => d).OrderByDescending(g => g.Count()).First();
        return strongest.Count() * 2 > directions.Length
            ? $"Aim bias: {strongest.Count()}/{directions.Length} {strongest.Key}."
            : "No consistent aim direction.";
    }

    private static string? AimDirection(MissAnalysisEntry entry)
    {
        MissReplayFrameSample? frame = entry.InputFrame ?? entry.NearestFrame;
        if (frame == null)
            return null;
        Vector2 error = frame.Position - entry.TargetPosition;
        if (error.Length < entry.TargetRadius * 0.15)
            return null;
        return Math.Abs(error.X) >= Math.Abs(error.Y)
            ? error.X < 0 ? "left" : "right"
            : error.Y < 0 ? "above" : "below";
    }

    public static string FormatInputTiming(MissAnalysisEntry entry)
    {
        if (entry.InputOffsetMs is not { } value)
            return entry.Kind == KumoriTimelineMarkerKind.SliderBreak ? "No input transition in review window" : "No press in hit window";
        string timing = FormatOffset(value);
        if (entry.Kind == KumoriTimelineMarkerKind.SliderBreak && entry.InputFrame is { } frame)
            return $"{(frame.Released ? "Release" : "Press")} {timing}";
        if (entry.Kind == KumoriTimelineMarkerKind.Miss)
            return $"Press {timing}";
        return entry.ExactTiming ? $"Hit {timing}" : $"Estimated tap {timing}";
    }

    public static string FormatTapOffset(double? offset)
        => offset is { } value ? FormatOffset(value) : "no nearby tap";

    public static string FormatOffset(double value)
    {
        if (Math.Abs(value) < 0.5)
            return "on time (0ms)";
        return value < 0 ? $"{Math.Abs(value):0.0}ms early" : $"{value:0.0}ms late";
    }

    public static string FormatTime(double time)
        => $"{(int)(time / 60000):00}:{(int)(time / 1000) % 60:00}.{(int)(time % 1000):000}";

    public static string FormatCursorPosition(MissAnalysisEntry entry)
    {
        if (entry.DistanceFromTarget is not { } distance)
            return "Cursor: no position sample available";

        double radius = Math.Max(1, entry.TargetRadius);
        double edgeDifference = distance - radius;
        double percentOfRadius = Math.Abs(edgeDifference) / radius * 100;

        if (Math.Abs(edgeDifference) < 0.5)
            return "Cursor: on the edge of the hit area";
        return edgeDifference > 0
            ? $"Cursor: {percentOfRadius:0}% beyond hit radius"
            : $"Cursor: {percentOfRadius:0}% within hit radius";
    }

    public static float? DirectionalError(MissAnalysisEntry entry)
    {
        MissReplayFrameSample? input = entry.InputFrame
            ?? entry.ReplayFrames.MinBy(frame => (frame.Position - entry.TargetPosition).Length);
        if (entry.PreviousPosition is not { } previous || input == null)
            return null;
        Vector2 incoming = entry.TargetPosition - previous;
        if (incoming.LengthSquared < 0.01f)
            return null;
        incoming.Normalize();
        return Vector2.Dot(input.Position - entry.TargetPosition, incoming);
    }

    private static string TimingWord(MissAnalysisEntry entry)
        => entry.InputOffsetMs < 0 ? "early" : "late";
}
