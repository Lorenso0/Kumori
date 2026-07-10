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
        float circleRadius = nominalCircleRadius;

        AddInternal(new Box
        {
            RelativeSizeAxes = Axes.Both,
            Colour = Color4.Black.Opacity(0.24f),
        });
        AddInternal(new SpriteText
        {
            Text = selected.ObjectType.StartsWith("Slider", StringComparison.OrdinalIgnoreCase)
                ? "SLIDER HEATMAP"
                : "MISS HEATMAP",
            Position = new Vector2(10, 8),
            Font = FontUsage.Default.With(size: 12, weight: "bold"),
            Colour = Color4.White.Opacity(0.82f),
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

            if ((end - start).Length < 5)
            {
                start.X -= 4;
                end.X += 4;
            }
            AddInternal(endpointMarker(start, Color4.Cyan, filled: false, size: 8));
            AddInternal(endpointMarker(end, AdvancedAnalyzerColours.Miss, filled: true, size: 7));
        }

        if (viewModel.ShowInputMarkers.Value && selected.InputFrame is { } input)
        {
            Vector2 click = centre + (input.Position - selected.TargetPosition) * scale;
            addCross(click, 18, AdvancedAnalyzerColours.Miss, 3.5f);
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

    private static Box line(Vector2 position, float length, float thickness, float rotation, Color4 colour) => new()
    {
        Position = position,
        Size = new Vector2(length, thickness),
        Anchor = Anchor.TopLeft,
        Origin = Anchor.Centre,
        Rotation = rotation,
        Colour = colour,
    };

    private static CircularContainer endpointMarker(Vector2 position, Color4 colour, bool filled, float size) => new()
    {
        Position = position,
        Size = new Vector2(size),
        Anchor = Anchor.TopLeft,
        Origin = Anchor.Centre,
        Masking = true,
        BorderThickness = 1.2f,
        BorderColour = colour,
        Child = new Box
        {
            RelativeSizeAxes = Axes.Both,
            Colour = filled ? colour.Opacity(0.88f) : Color4.Black.Opacity(0.75f),
        },
    };

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
        return directionalError > 0
            ? $"{(entry.InputFrame != null ? "Overshoot" : "Closest approach overshoot")} {directionalError:0.0}px"
            : $"{(entry.InputFrame != null ? "Undershoot" : "Closest approach undershoot")} {Math.Abs(directionalError):0.0}px";
    }

    private static bool supportsHeatmap(MissAnalysisEntry entry)
        => (entry.Kind == KumoriTimelineMarkerKind.Miss
            && (entry.ObjectType.Equals("Circle", StringComparison.OrdinalIgnoreCase)
                || entry.ObjectType.StartsWith("Slider", StringComparison.OrdinalIgnoreCase)))
           || entry.Kind == KumoriTimelineMarkerKind.SliderBreak;
}
