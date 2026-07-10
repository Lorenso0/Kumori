namespace Kumori.ReplayViewer;

internal static class AdvancedAnalyzerMetrics
{
    public static string Diagnosis(MissAnalysisEntry entry)
    {
        if (entry.Kind == KumoriTimelineMarkerKind.SliderBreak)
        {
            if (entry.NearestFrame == null)
                return "Insufficient data";
            if (!entry.NearestFrame.HasAction)
                return "Likely slider follow: input released";
            if (entry.DistanceFromTarget is { } sliderDistance && sliderDistance > entry.TargetRadius * 1.5)
                return "Likely slider follow: cursor outside path";
            return "Slider follow break";
        }

        bool? aimOutside = entry.DistanceFromTarget is { } distance ? distance > entry.TargetRadius : null;
        bool? timingOutside = entry.InputOffsetMs is { } offset ? Math.Abs(offset) > 80 : null;
        return (aimOutside, timingOutside) switch
        {
            (true, true) => "Likely aim and timing",
            (true, _) => "Likely aim",
            (_, true) => "Likely timing",
            (false, false) when entry.Kind == KumoriTimelineMarkerKind.Miss => "Cursor and tap aligned; inspect playback",
            (false, false) => "Inside target; timing reduced the result",
            (false, null) when entry.Kind == KumoriTimelineMarkerKind.Miss => "No press in hit window",
            _ => "Limited replay evidence",
        };
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

    public static string FormatDistance(double? distance)
        => distance is { } value ? $"{value:0.0}px" : "n/a";
}
