using osu.Framework.Bindables;
using osu.Framework.Extensions.Color4Extensions;
using osu.Framework.Graphics;
using osu.Framework.Graphics.Containers;
using osu.Framework.Graphics.Shapes;
using osu.Framework.Graphics.Sprites;
using osuTK;

namespace Kumori.ReplayViewer;

/// <summary>Side-by-side recorded results for the active comparison.</summary>
internal partial class KumoriComparisonStatsOverlay : CompositeDrawable
{
    private readonly IBindable<Colour4> comparisonColour;
    private readonly Box primaryAccent;
    private readonly Box comparisonAccent;
    private readonly SpriteText primaryTitle;
    private readonly SpriteText primaryResult;
    private readonly SpriteText primaryHits;
    private readonly SpriteText comparisonTitle;
    private readonly SpriteText comparisonResult;
    private readonly SpriteText comparisonHits;

    public KumoriComparisonStatsOverlay(
        AttemptContract primary,
        FinalHitsContract? primaryHits,
        ComparisonContract comparison,
        IBindable<Colour4> comparisonColour)
    {
        this.comparisonColour = comparisonColour;
        Anchor = Anchor.TopLeft;
        Origin = Anchor.TopLeft;
        Position = new Vector2(20, 18);
        Size = new Vector2(560, 78);
        Depth = -2000;

        InternalChildren =
        [
            new Box { RelativeSizeAxes = Axes.Both, Colour = Colour4.Black.Opacity(0.76f) },
            primaryAccent = accent(0),
            comparisonAccent = accent(278),
            primaryTitle = title("PRIMARY", new Vector2(14, 9)),
            primaryResult = stats($"{primary.Accuracy:0.00}%  |  {primary.Score:N0}", new Vector2(14, 29), 13),
            this.primaryHits = stats(formatHits(primaryHits?.N100 ?? 0, primaryHits?.N50 ?? 0, primaryHits?.Misses ?? 0), new Vector2(14, 51), 11),
            comparisonTitle = title("COMPARISON", new Vector2(294, 9)),
            comparisonResult = stats($"{comparison.Accuracy:0.00}%  |  {comparison.Score:N0}", new Vector2(294, 29), 13),
            comparisonHits = stats(formatHits(comparison.N100, comparison.N50, comparison.Misses), new Vector2(294, 51), 11),
        ];
    }

    protected override void Update()
    {
        base.Update();
        primaryAccent.Colour = primaryTitle.Colour = primaryResult.Colour = primaryHits.Colour = Colour4.White;
        comparisonAccent.Colour = comparisonTitle.Colour = comparisonResult.Colour = comparisonHits.Colour = comparisonColour.Value;
    }

    private static Box accent(float x) => new()
    {
        Position = new Vector2(x, 0),
        Size = new Vector2(4, 78),
    };

    private static SpriteText title(string text, Vector2 position) => new()
    {
        Text = text,
        Position = position,
        Font = FontUsage.Default.With(size: 11, weight: "bold"),
    };

    private static SpriteText stats(string text, Vector2 position, float size) => new()
    {
        Text = text,
        Position = position,
        Font = FontUsage.Default.With(size: size, weight: "bold"),
    };

    private static string formatHits(int n100, int n50, int misses)
        => $"100s {n100}   50s {n50}   Misses {misses}";
}
