using osu.Framework.Extensions.Color4Extensions;
using osu.Framework.Graphics;
using osu.Framework.Graphics.Containers;
using osu.Framework.Graphics.Lines;
using osu.Framework.Graphics.Shapes;
using osu.Framework.Graphics.Sprites;
using osuTK;
using osuTK.Graphics;

namespace Kumori.ReplayViewer;

internal partial class AdvancedAnalyzerAccuracyHeatmap : CompositeDrawable
{
    private const float heatmap_height = 210;
    private readonly AdvancedAnalyzerViewModel viewModel;
    private MissAnalysisEntry? entry;
    private float renderedWidth;

    public AdvancedAnalyzerAccuracyHeatmap(AdvancedAnalyzerViewModel viewModel)
    {
        this.viewModel = viewModel;
        RelativeSizeAxes = Axes.X;
        Height = 0;
        Alpha = 0;
        Masking = true;
    }

    protected override void LoadComplete()
    {
        base.LoadComplete();
        viewModel.ShowInputMarkers.ValueChanged += _ => rebuild();
        viewModel.ShowMovementSamples.ValueChanged += _ => rebuild();
        viewModel.ShowHeldSamples.ValueChanged += _ => rebuild();
        viewModel.ComparisonCursorColour.ValueChanged += _ => rebuild();
        rebuild();
    }

    protected override void Update()
    {
        base.Update();
        if (Math.Abs(renderedWidth - DrawWidth) > 1)
            rebuild();
    }

    public void SetEntry(MissAnalysisEntry? value)
    {
        entry = value;
        rebuild();
    }

    private void rebuild()
    {
        renderedWidth = DrawWidth;
        ClearInternal();

        if (entry is not { } selected
            || !supportsHeatmap(selected)
            || DrawWidth <= 1)
        {
            Height = 0;
            Alpha = 0;
            return;
        }

        Height = heatmap_height;
        Alpha = 1;
        Vector2 centre = new(DrawWidth / 2, 108);
        // Prepared analysis owns frame slicing. Rendering must never widen
        // this object-local segment back into neighbouring cursor movement.
        MissReplayFrameSample[] samples = selected.ReplayFrames.ToArray();
        float nominalCircleRadius = Math.Min(64, DrawWidth * 0.23f);
        float scale = nominalCircleRadius / Math.Max(1, (float)selected.TargetRadius);

        // Fit the complete object-local approach into the panel. A fixed scale
        // made valid samples near the edge of the local review radius draw as
        // giant clipped diagonals and could shrink the useful target area to a
        // small corner of the graph.
        if (samples.Length > 0)
        {
            float maxX = samples.Max(sample => Math.Abs(sample.Position.X - selected.TargetPosition.X));
            float maxY = samples.Max(sample => Math.Abs(sample.Position.Y - selected.TargetPosition.Y));
            float availableX = Math.Max(1, DrawWidth / 2 - 14);
            float availableY = Math.Max(1, Math.Min(centre.Y - 44, heatmap_height - centre.Y - 28));
            if (maxX > 0.01f)
                scale = Math.Min(scale, availableX / maxX);
            if (maxY > 0.01f)
                scale = Math.Min(scale, availableY / maxY);
        }
        float circleRadius = Math.Max(1, (float)selected.TargetRadius * scale);

        AddInternal(new Box
        {
            RelativeSizeAxes = Axes.Both,
            Colour = Color4.Black.Opacity(0.24f),
        });
        AddInternal(new SpriteText
        {
            Text = selected.ObjectType.StartsWith("Slider", StringComparison.OrdinalIgnoreCase)
                ? "SLIDER CURSOR PATH"
                : "MISS CURSOR PATH",
            Position = new Vector2(10, 8),
            Font = FontUsage.Default.With(size: 12, weight: "bold"),
            Colour = Color4.White.Opacity(0.82f),
        });
        AddInternal(new SpriteText
        {
            Text = viewModel.Comparison is null
                ? "PRIMARY move/held - RED X tap - arrows start/end"
                : "PRIMARY path - COMPARISON path - RED X tap",
            Position = new Vector2(10, 27),
            Font = FontUsage.Default.With(size: 8, weight: "bold"),
            Colour = Color4.White.Opacity(0.55f),
        });

        AddInternal(new CircularContainer
        {
            Position = centre,
            Size = new Vector2(circleRadius * 2),
            Anchor = Anchor.TopLeft,
            Origin = Anchor.Centre,
            Masking = true,
            BorderThickness = 2,
            BorderColour = Color4.White.Opacity(0.85f),
            Child = new Box
            {
                RelativeSizeAxes = Axes.Both,
                Colour = Color4.Black.Opacity(0.16f),
            },
        });
        addCross(centre, 8, Color4.White.Opacity(0.55f), 1.5f);

        for (int i = 1; i < samples.Length; i++)
        {
            MissReplayFrameSample previous = samples[i - 1];
            MissReplayFrameSample current = samples[i];
            if (!shouldConnect(previous, current))
                continue;
            bool held = previous.HasAction || current.HasAction;
            if (held ? !viewModel.ShowHeldSamples.Value : !viewModel.ShowMovementSamples.Value)
                continue;

            Vector2 from = centre + (previous.Position - selected.TargetPosition) * scale;
            Vector2 to = centre + (current.Position - selected.TargetPosition) * scale;
            AddInternal(new SmoothPath
            {
                AutoSizeAxes = Axes.None,
                Size = new Vector2(DrawWidth, heatmap_height),
                PathRadius = held ? 2.75f : 1.25f,
                Colour = Color4.Cyan.Opacity(held ? 0.58f : 0.32f),
                Vertices = [from, to],
            });
        }

        if (viewModel.Comparison is { } comparison)
        {
            MovementSample[] comparisonSamples = comparison.Samples
                .Where(s => Math.Abs(s.MapTimeMs - selected.EventTime) <= 700)
                .ToArray();
            for (int i = 1; i < comparisonSamples.Length; i++)
            {
                Vector2 from = centre + (new Vector2((float)comparisonSamples[i - 1].X, (float)comparisonSamples[i - 1].Y) - selected.TargetPosition) * scale;
                Vector2 to = centre + (new Vector2((float)comparisonSamples[i].X, (float)comparisonSamples[i].Y) - selected.TargetPosition) * scale;
                AddInternal(new SmoothPath { AutoSizeAxes = Axes.None, Size = new Vector2(DrawWidth, heatmap_height), PathRadius = 1.5f, Colour = viewModel.ComparisonCursorColour.Value.Opacity(0.6f), Vertices = [from, to] });
            }
        }

        for (int i = 0; i < samples.Length; i++)
        {
            MissReplayFrameSample sample = samples[i];
            if (sample.HasAction ? !viewModel.ShowHeldSamples.Value : !viewModel.ShowMovementSamples.Value)
                continue;
            Vector2 position = centre + (sample.Position - selected.TargetPosition) * scale;
            float progress = samples.Length <= 1 ? 1 : i / (float)(samples.Length - 1);
            AddInternal(new Circle
            {
                Position = position,
                Size = new Vector2(sample.HasAction ? 5 : 3),
                Anchor = Anchor.TopLeft,
                Origin = Anchor.Centre,
                Colour = Color4.Cyan.Opacity(0.18f + progress * 0.58f),
            });
        }

        if (samples.Length > 0)
        {
            Vector2 start = centre + (samples[0].Position - selected.TargetPosition) * scale;
            Vector2 end = centre + (samples[^1].Position - selected.TargetPosition) * scale;
            Vector2 startDirection = endpointDirection(samples, fromStart: true);
            Vector2 endDirection = endpointDirection(samples, fromStart: false);

            if ((end - start).Length < 5)
            {
                start -= startDirection * 5;
                end += endDirection * 5;
            }
            // Keep direction arrows away from clipped panel edges and from
            // the input X, which commonly occupies the final sample.
            Vector2 startArrow = clampArrow(start + startDirection * 11, 11);
            Vector2 endArrow = clampArrow(end - endDirection * 14, 12);
            AddInternal(endpointArrow(startArrow, startDirection, Color4.Cyan, 11));
            AddInternal(endpointArrow(endArrow, endDirection, AdvancedAnalyzerColours.Miss, 12));
        }

        if (viewModel.ShowInputMarkers.Value && selected.InputFrame is { } input)
        {
            Vector2 click = centre + (input.Position - selected.TargetPosition) * scale;
            addCross(click, 13, AdvancedAnalyzerColours.Miss, 2.75f);
        }

        AddInternal(new SpriteText
        {
            Text = directionalLabel(selected),
            Anchor = Anchor.BottomCentre,
            Origin = Anchor.BottomCentre,
            Position = new Vector2(0, -8),
            Font = FontUsage.Default.With(size: 12, weight: "bold"),
            Colour = Color4.White.Opacity(0.72f),
        });
    }

    private void addCross(Vector2 position, float size, Color4 colour, float thickness)
    {
        AddInternal(line(position, size, thickness, 45, colour));
        AddInternal(line(position, size, thickness, -45, colour));
    }

    private Vector2 clampArrow(Vector2 position, float size)
    {
        float margin = size / 2 + 3;
        return new Vector2(
            Math.Clamp(position.X, margin, Math.Max(margin, DrawWidth - margin)),
            Math.Clamp(position.Y, 42 + margin, heatmap_height - 25 - margin));
    }

    private static Box line(Vector2 position, float length, float thickness, float rotation, Color4 colour) => new()
    {
        Position = position,
        Size = new Vector2(length, thickness),
        Anchor = Anchor.TopLeft,
        Origin = Anchor.Centre,
        Rotation = rotation,
        Colour = colour,
    };

    private static SpriteIcon endpointArrow(Vector2 position, Vector2 direction, Color4 colour, float size)
    {
        if (direction.LengthSquared < 0.01f)
            direction = Vector2.UnitX;

        return new SpriteIcon
        {
            Position = position,
            Size = new Vector2(size),
            Anchor = Anchor.TopLeft,
            Origin = Anchor.Centre,
            Rotation = (float)(Math.Atan2(direction.Y, direction.X) * 180 / Math.PI),
            Icon = FontAwesome.Solid.ArrowRight,
            Colour = colour,
            Shadow = true,
        };
    }

    private static Vector2 endpointDirection(IReadOnlyList<MissReplayFrameSample> samples, bool fromStart)
    {
        if (samples.Count < 2)
            return Vector2.UnitX;

        Vector2 endpoint = fromStart ? samples[0].Position : samples[^1].Position;
        IEnumerable<MissReplayFrameSample> candidates = fromStart
            ? samples.Skip(1)
            : samples.Take(samples.Count - 1).Reverse();

        foreach (MissReplayFrameSample candidate in candidates)
        {
            Vector2 direction = fromStart
                ? candidate.Position - endpoint
                : endpoint - candidate.Position;
            if (direction.LengthSquared > 0.01f)
                return direction / direction.Length;
        }

        return Vector2.UnitX;
    }

    private static bool shouldConnect(MissReplayFrameSample previous, MissReplayFrameSample current)
    {
        double elapsed = current.Time - previous.Time;
        if (elapsed <= 0 || elapsed > 50)
            return false;

        // Preserve discontinuous samples as points without drawing a false
        // cursor stroke across capture or seek jumps.
        float maximumPlausibleDistance = 16 + (float)elapsed * 5;
        return (current.Position - previous.Position).Length <= maximumPlausibleDistance;
    }

    private static string directionalLabel(MissAnalysisEntry entry)
    {
        MissReplayFrameSample? input = entry.InputFrame
            ?? entry.ReplayFrames.MinBy(frame => (frame.Position - entry.TargetPosition).Length);
        if (entry.PreviousPosition is not { } previous || input == null)
            return "No cursor approach samples";

        Vector2 incoming = entry.TargetPosition - previous;
        if (incoming.LengthSquared < 0.01f)
            return "No incoming movement vector";

        incoming.Normalize();
        float directionalError = Vector2.Dot(input.Position - entry.TargetPosition, incoming);
        if (Math.Abs(directionalError) < 1)
            return entry.InputFrame != null ? "Click aligned with target" : "Closest approach aligned";

        float percentOfHitRadius = Math.Abs(directionalError) / Math.Max(1, (float)entry.TargetRadius) * 100;
        if (entry.InputFrame != null)
        {
            return directionalError > 0
                ? $"Click: {percentOfHitRadius:0}% of hit radius past center"
                : $"Click: {percentOfHitRadius:0}% of hit radius short";
        }

        return directionalError > 0
            ? $"Closest: {percentOfHitRadius:0}% of hit radius past center"
            : $"Closest: {percentOfHitRadius:0}% of hit radius short";
    }

    private static bool supportsHeatmap(MissAnalysisEntry entry)
        => (entry.Kind == KumoriTimelineMarkerKind.Miss
            && (entry.ObjectType.Equals("Circle", StringComparison.OrdinalIgnoreCase)
                || entry.ObjectType.StartsWith("Slider", StringComparison.OrdinalIgnoreCase)))
           || entry.Kind == KumoriTimelineMarkerKind.SliderBreak;
}
