using osu.Framework.Graphics;
using osu.Framework.Graphics.Colour;
using osu.Framework.Graphics.Containers;
using osu.Framework.Graphics.Shapes;
using osu.Framework.Extensions.Color4Extensions;
using osu.Game.Graphics.Containers;
using osu.Game.Screens.Play.PlayerSettings;
using osuTK;
using osuTK.Graphics;

namespace Kumori.ReplayViewer;

/// <summary>
/// Kumori-owned equivalent of lazer's replay settings sidebar. It keeps the
/// analyzer UI independent from lazer's internal replay-overlay implementation.
/// </summary>
internal partial class KumoriSettingsSidebar : CompositeDrawable
{
    private const float padding = 10;
    private const float default_width = 290;

    private readonly FillFlowContainer<PlayerSettingsGroup> content;
    private readonly OsuScrollContainer scroll;

    public KumoriSettingsSidebar(bool anchorLeft, float width = default_width)
    {
        RelativeSizeAxes = Axes.Y;
        Width = width;
        Anchor = anchorLeft ? Anchor.TopLeft : Anchor.TopRight;
        Origin = anchorLeft ? Anchor.TopLeft : Anchor.TopRight;

        InternalChildren =
        [
            new Box
            {
                RelativeSizeAxes = Axes.Both,
                Colour = anchorLeft
                    ? ColourInfo.GradientHorizontal(Color4.Black.Opacity(0.8f), Color4.Black.Opacity(0))
                    : ColourInfo.GradientHorizontal(Color4.Black.Opacity(0), Color4.Black.Opacity(0.8f)),
                Depth = float.MaxValue,
            },
            scroll = new OsuScrollContainer(Direction.Vertical)
            {
                RelativeSizeAxes = Axes.Both,
                Child = content = new FillFlowContainer<PlayerSettingsGroup>
                {
                    RelativeSizeAxes = Axes.X,
                    AutoSizeAxes = Axes.Y,
                    Direction = FillDirection.Vertical,
                    Spacing = new Vector2(0, 20),
                    Padding = new MarginPadding
                    {
                        Top = padding,
                        Bottom = padding,
                        Left = padding,
                        Right = padding + OsuScrollContainer.SCROLL_BAR_WIDTH,
                    },
                },
            },
        ];
    }

    public void SetGroups(IEnumerable<PlayerSettingsGroup> groups)
    {
        content.Clear(false);
        content.AddRange(groups);
    }

    public void ScrollIntoView(Drawable drawable)
        => Scheduler.Add(() => scroll.ScrollIntoView(drawable, animated: true, extraScroll: 14));
}

internal partial class KumoriAnalyzerSidebar : KumoriSettingsSidebar
{
    public const float COMPACT_WIDTH = 218;

    public KumoriAnalyzerSidebar()
        : base(anchorLeft: true, COMPACT_WIDTH)
    {
    }
}
