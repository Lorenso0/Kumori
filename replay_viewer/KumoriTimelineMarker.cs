using osu.Game.Rulesets.Judgements;
using osu.Game.Rulesets.Osu.Objects;
using osu.Game.Rulesets.Scoring;

namespace Kumori.ReplayViewer;

internal readonly record struct KumoriTimelineMarker(double Time, KumoriTimelineMarkerKind Kind);

internal enum KumoriTimelineMarkerKind
{
    Miss,
    Meh,
    Ok,
    SliderBreak,
}

internal static class KumoriTimelineMarkers
{
    public static KumoriTimelineMarkerKind? KindFromContract(string? kind)
    {
        if (string.IsNullOrWhiteSpace(kind))
            return null;

        return kind.Trim().ToLowerInvariant() switch
        {
            "miss" => KumoriTimelineMarkerKind.Miss,
            "slider_break" or "sliderbreak" or "slider break" => KumoriTimelineMarkerKind.SliderBreak,
            "hit_50" or "50" or "meh" => KumoriTimelineMarkerKind.Meh,
            "hit_100" or "100" or "ok" => KumoriTimelineMarkerKind.Ok,
            _ => null,
        };
    }

    public static KumoriTimelineMarkerKind? KindFromHitResult(HitResult result)
    {
        if (result == HitResult.Meh)
            return KumoriTimelineMarkerKind.Meh;
        if (result == HitResult.Ok)
            return KumoriTimelineMarkerKind.Ok;

        if (result is HitResult.LargeTickMiss or HitResult.SmallTickMiss or HitResult.ComboBreak)
            return KumoriTimelineMarkerKind.SliderBreak;

        if (result == HitResult.Miss)
            return KumoriTimelineMarkerKind.Miss;

        return null;
    }

    public static KumoriTimelineMarkerKind? KindFromJudgement(JudgementResult result)
        => result.Type == HitResult.Miss && result.HitObject is SliderTick or SliderRepeat or SliderTailCircle
            ? KumoriTimelineMarkerKind.SliderBreak
            : KindFromHitResult(result.Type);

    public static IEnumerable<KumoriTimelineMarker> FromContract(IEnumerable<JudgementEventContract> events)
    {
        foreach (JudgementEventContract judgement in events)
        {
            if (KindFromContract(judgement.Kind) is not { } kind)
                continue;

            int count = Math.Max(1, judgement.Delta);

            for (int i = 0; i < count; i++)
                yield return new KumoriTimelineMarker(judgement.MapTimeMs, kind);
        }
    }
}
