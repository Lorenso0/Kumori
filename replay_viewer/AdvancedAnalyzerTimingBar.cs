using osu.Framework.Extensions.Color4Extensions;
using osu.Framework.Graphics;
using osu.Framework.Graphics.Containers;
using osu.Framework.Graphics.Cursor;
using osu.Framework.Graphics.Shapes;
using osu.Framework.Graphics.Sprites;
using osu.Framework.Localisation;
using osuTK;
using osuTK.Graphics;

namespace Kumori.ReplayViewer;

internal partial class AdvancedAnalyzerTimingBar : CompositeDrawable
{
    private const float height = 46;
    private const float miss_cap_width = 0.08f;
    private readonly Container content;

    public AdvancedAnalyzerTimingBar()
    {
        RelativeSizeAxes = Axes.X;
        Height = 0;
        Alpha = 0;
        InternalChild = content = new Container { RelativeSizeAxes = Axes.Both };
    }

    public void SetEntry(MissAnalysisEntry? entry)
    {
        content.Clear();
        if (entry?.InputOffsetMs is not { } offset)
        {
            Height = 0;
            Alpha = 0;
            return;
        }

        Height = height;
        Alpha = 1;
        float markerPosition = positionFor(offset, entry.HitWindows);
        content.Children =
        [
            new Box
            {
                RelativeSizeAxes = Axes.X,
                Height = 10,
                Y = 18,
                Colour = Color4.White.Opacity(0.18f),
            },
            segment(-entry.HitWindows.Miss, -entry.HitWindows.Meh, entry.HitWindows, Color4.White.Opacity(0.28f),
                $"Miss window: {entry.HitWindows.Meh:0.#}–{entry.HitWindows.Miss:0.#} ms early"),
            segment(entry.HitWindows.Meh, entry.HitWindows.Miss, entry.HitWindows, Color4.White.Opacity(0.28f),
                $"Miss window: {entry.HitWindows.Meh:0.#}–{entry.HitWindows.Miss:0.#} ms late"),
            segment(-entry.HitWindows.Meh, -entry.HitWindows.Ok, entry.HitWindows, Color4.OrangeRed.Opacity(0.78f),
                $"50 window: {entry.HitWindows.Ok:0.#}–{entry.HitWindows.Meh:0.#} ms early"),
            segment(entry.HitWindows.Ok, entry.HitWindows.Meh, entry.HitWindows, Color4.OrangeRed.Opacity(0.78f),
                $"50 window: {entry.HitWindows.Ok:0.#}–{entry.HitWindows.Meh:0.#} ms late"),
            segment(-entry.HitWindows.Ok, -entry.HitWindows.Great, entry.HitWindows, Color4.Gold.Opacity(0.82f),
                $"100 window: {entry.HitWindows.Great:0.#}–{entry.HitWindows.Ok:0.#} ms early"),
            segment(entry.HitWindows.Great, entry.HitWindows.Ok, entry.HitWindows, Color4.Gold.Opacity(0.82f),
                $"100 window: {entry.HitWindows.Great:0.#}–{entry.HitWindows.Ok:0.#} ms late"),
            segment(-entry.HitWindows.Great, entry.HitWindows.Great, entry.HitWindows, Color4.LimeGreen.Opacity(0.85f),
                $"300 window: 0–{entry.HitWindows.Great:0.#} ms early or late"),
            new TimingMarker(markerTooltip(offset, entry.Kind))
            {
                RelativePositionAxes = Axes.X,
                X = markerPosition,
                Y = 13,
                Size = new Vector2(20),
                Origin = Anchor.TopCentre,
            },
            label("EARLY", Anchor.TopLeft, Anchor.TopLeft),
            label("LATE", Anchor.TopRight, Anchor.TopRight),
        ];
    }

    private static TimingWindow segment(double start, double end, HitWindowAnalysis windows, Color4 colour, string tooltip) => new(tooltip)
    {
        RelativeSizeAxes = Axes.X,
        RelativePositionAxes = Axes.X,
        X = positionFor(start, windows),
        Width = positionFor(end, windows) - positionFor(start, windows),
        Height = 10,
        Y = 18,
        Colour = colour,
    };

    private static float positionFor(double offset, HitWindowAnalysis windows)
    {
        double meh = Math.Max(1, windows.Meh);
        double miss = Math.Max(meh + 1, windows.Miss);

        if (offset <= -meh)
            return (float)(Math.Clamp((offset + miss) / (miss - meh), 0, 1) * miss_cap_width);
        if (offset >= meh)
            return 1 - miss_cap_width
                   + (float)(Math.Clamp((offset - meh) / (miss - meh), 0, 1) * miss_cap_width);

        return miss_cap_width
               + (float)((offset + meh) / (meh * 2) * (1 - miss_cap_width * 2));
    }

    private static string markerTooltip(double offset, KumoriTimelineMarkerKind kind)
    {
        string timing = Math.Abs(offset) < 0.05
            ? "on time (0 ms)"
            : $"{Math.Abs(offset):0.0} ms {(offset < 0 ? "early" : "late")}";
        string result = kind switch
        {
            KumoriTimelineMarkerKind.Ok => "100",
            KumoriTimelineMarkerKind.Meh => "50",
            KumoriTimelineMarkerKind.Miss => "miss",
            _ => "slider break",
        };
        return $"Input: {timing} — {result} result";
    }

    private static SpriteText label(string value, Anchor anchor, Anchor origin) => new()
    {
        Text = value,
        Anchor = anchor,
        Origin = origin,
        Font = FontUsage.Default.With(size: 9, weight: "bold"),
        Colour = Color4.White.Opacity(0.55f),
    };

    private partial class TimingWindow : Box, IHasTooltip
    {
        public LocalisableString TooltipText { get; }

        public TimingWindow(string tooltip) => TooltipText = tooltip;
    }

    private partial class TimingMarker : CompositeDrawable, IHasTooltip
    {
        public override bool HandlePositionalInput => true;
        public LocalisableString TooltipText { get; }

        public TimingMarker(string tooltip)
        {
            TooltipText = tooltip;
            InternalChild = new CircularContainer
            {
                Anchor = Anchor.Centre,
                Origin = Anchor.Centre,
                Size = new Vector2(12),
                Masking = true,
                BorderThickness = 2,
                BorderColour = Color4.Black,
                Child = new Box
                {
                    RelativeSizeAxes = Axes.Both,
                    Colour = Color4.Cyan,
                },
            };
        }
    }
}
