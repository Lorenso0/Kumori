using osu.Framework.Extensions.Color4Extensions;
using osu.Framework.Graphics;
using osu.Framework.Graphics.Containers;
using osu.Framework.Graphics.Cursor;
using osu.Framework.Graphics.Effects;
using osu.Framework.Graphics.Shapes;
using osu.Framework.Graphics.Sprites;
using osuTK;
using osuTK.Graphics;

namespace Kumori.ReplayViewer;

internal partial class AdvancedAnalyzerEventTooltip : VisibilityContainer, ITooltip<MissAnalysisEntry>
{
    private readonly Box accent;
    private readonly SpriteText title;
    private readonly SpriteText detail;

    public AdvancedAnalyzerEventTooltip()
    {
        Width = 220;
        Height = 54;
        Masking = true;
        CornerRadius = 5;
        EdgeEffect = new EdgeEffectParameters
        {
            Type = EdgeEffectType.Shadow,
            Colour = Color4.Black.Opacity(0.55f),
            Radius = 7,
        };

        Children =
        [
            new Box
            {
                RelativeSizeAxes = Axes.Both,
                Colour = AdvancedAnalyzerColours.Panel.Opacity(0.97f),
            },
            accent = new Box
            {
                RelativeSizeAxes = Axes.Y,
                Width = 4,
            },
            new FillFlowContainer
            {
                RelativeSizeAxes = Axes.X,
                AutoSizeAxes = Axes.Y,
                Padding = new MarginPadding { Horizontal = 11, Vertical = 8 },
                Direction = FillDirection.Vertical,
                Spacing = new Vector2(0, 2),
                Children =
                [
                    title = new SpriteText
                    {
                        RelativeSizeAxes = Axes.X,
                        Font = FontUsage.Default.With(size: 12, weight: "bold"),
                        Truncate = true,
                    },
                    detail = new SpriteText
                    {
                        RelativeSizeAxes = Axes.X,
                        Font = FontUsage.Default.With(size: 10),
                        Colour = Color4.White.Opacity(0.7f),
                        Truncate = true,
                    },
                ],
            },
        ];
    }

    public void SetContent(MissAnalysisEntry entry)
    {
        Color4 colour = AdvancedAnalyzerColours.For(entry.Kind);
        accent.Colour = colour;
        title.Colour = colour;
        title.Text = $"#{entry.Index}  {entry.Label}    {AdvancedAnalyzerMetrics.FormatTime(entry.EventTime)}";
        detail.Text = $"{entry.ObjectType}  -  {AdvancedAnalyzerMetrics.Diagnosis(entry)}";
    }

    public void Move(Vector2 pos) => Position = pos;

    // Analyzer playback is normally paused while inspecting an event, so the
    // popup cannot rely on gameplay-clock-driven fade transforms.
    protected override void PopIn() => Alpha = 1;
    protected override void PopOut() => Alpha = 0;
}
