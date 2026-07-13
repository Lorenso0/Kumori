using osu.Framework.Bindables;
using osu.Framework.Extensions.Color4Extensions;
using osu.Framework.Graphics;
using osu.Framework.Graphics.Containers;
using osu.Framework.Graphics.Shapes;
using osu.Framework.Graphics.Sprites;
using osuTK;

namespace Kumori.ReplayViewer;

/// <summary>
/// Labels bad judgements from the comparison replay at its cursor position.
/// The explicit C prefix prevents them being confused with lazer's primary
/// replay feedback.
/// </summary>
internal partial class KumoriComparisonJudgementOverlay : CompositeDrawable
{
    private const double discontinuityMs = 250;
    private readonly JudgementEventContract[] events;
    private readonly MovementSample[] samples;
    private readonly Func<double> currentTime;
    private readonly IBindable<Colour4> colour;
    private readonly Container markers;
    private int nextEvent;
    private double? previousTime;

    public KumoriComparisonJudgementOverlay(
        IReadOnlyList<JudgementEventContract> judgementEvents,
        IReadOnlyList<MovementSample> movementSamples,
        Func<double> currentTime,
        IBindable<Colour4> colour)
    {
        this.currentTime = currentTime;
        this.colour = colour;
        events = judgementEvents
            .Where(judgement => judgement.Kind is "100" or "50" or "miss" or "slider_break")
            .OrderBy(judgement => judgement.MapTimeMs)
            .ToArray();
        samples = KumoriComparisonMovement.Prepare(movementSamples);
        RelativeSizeAxes = Axes.Both;
        InternalChild = markers = new Container { RelativeSizeAxes = Axes.Both };
    }

    protected override void Update()
    {
        base.Update();
        double time = currentTime();
        if (previousTime is not { } previous || time < previous || time - previous > discontinuityMs)
        {
            resetAt(time);
            return;
        }

        while (nextEvent < events.Length && events[nextEvent].MapTimeMs <= time)
        {
            JudgementEventContract judgement = events[nextEvent++];
            if (judgement.MapTimeMs > previous)
                show(judgement);
        }
        previousTime = time;
    }

    private void resetAt(double time)
    {
        markers.Clear();
        nextEvent = Array.FindIndex(events, judgement => judgement.MapTimeMs > time);
        if (nextEvent < 0)
            nextEvent = events.Length;
        previousTime = time;
    }

    private void show(JudgementEventContract judgement)
    {
        if (!KumoriComparisonMovement.TryPositionAt(samples, judgement.MapTimeMs, out Vector2 position))
            return;

        string label = judgement.Kind switch
        {
            "100" => "C 100",
            "50" => "C 50",
            "slider_break" => "C BREAK",
            _ => "C MISS",
        };
        if (judgement.Delta > 1)
            label += $" x{judgement.Delta}";

        var badge = new Container
        {
            Anchor = Anchor.TopLeft,
            Origin = Anchor.BottomCentre,
            Position = position + new Vector2(0, -18),
            Size = new Vector2(label.Length > 7 ? 92 : 70, 25),
            Masking = true,
            CornerRadius = 12.5f,
            BorderThickness = 2,
            BorderColour = colour.Value,
            Children =
            [
                new Box
                {
                    RelativeSizeAxes = Axes.Both,
                    Colour = Colour4.Black.Opacity(0.86f),
                },
                new SpriteText
                {
                    Text = label,
                    Anchor = Anchor.Centre,
                    Origin = Anchor.Centre,
                    Font = FontUsage.Default.With(size: 10, weight: "bold"),
                    Colour = colour.Value,
                },
            ],
        };
        markers.Add(badge);
        badge.ScaleTo(1.12f, 650, Easing.OutQuint);
        badge.FadeOut(650, Easing.OutQuint);
        Scheduler.AddDelayed(() => markers.Remove(badge, true), 700);
    }
}
