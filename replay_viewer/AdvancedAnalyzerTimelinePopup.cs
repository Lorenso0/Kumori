using osu.Framework.Graphics;
using osu.Framework.Graphics.Containers;
using osuTK;

namespace Kumori.ReplayViewer;

internal partial class AdvancedAnalyzerTimelinePopup : CompositeDrawable
{
    private readonly AdvancedAnalyzerEventTooltip card;
    private Vector2 screenAnchor;

    public AdvancedAnalyzerTimelinePopup()
    {
        RelativeSizeAxes = Axes.Both;
        Depth = float.NegativeInfinity;
        InternalChild = card = new AdvancedAnalyzerEventTooltip
        {
            Origin = Anchor.BottomCentre,
        };
        card.Hide();
    }

    public void Show(MissAnalysisEntry entry, Vector2 anchor)
    {
        screenAnchor = anchor;
        card.SetContent(entry);
        updatePosition();
        card.Show();
    }

    public void HideCard() => card.Hide();

    protected override void Update()
    {
        base.Update();
        if (card.IsPresent)
            updatePosition();
    }

    private void updatePosition()
    {
        Vector2 local = ToLocalSpace(screenAnchor);
        float halfWidth = card.DrawWidth > 1 ? card.DrawWidth / 2 : 110;
        local.X = Math.Clamp(local.X, halfWidth + 8, Math.Max(halfWidth + 8, DrawWidth - halfWidth - 8));
        local.Y -= 9;
        card.Move(local);
    }
}
