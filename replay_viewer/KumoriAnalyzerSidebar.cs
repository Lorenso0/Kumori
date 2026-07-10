using osu.Game.Screens.Play.HUD;

namespace Kumori.ReplayViewer;

internal partial class KumoriAnalyzerSidebar : ReplaySettingsOverlay
{
    public const float COMPACT_WIDTH = 218;

    public KumoriAnalyzerSidebar()
        : base(anchorLeft: true)
    {
    }

    protected override void Update()
    {
        base.Update();

        // ReplaySettingsOverlay has a fixed private expanded width. This
        // analyzer-only wrapper is always expanded, so constrain its actual
        // draw and input width without changing lazer's shared component.
        Width = COMPACT_WIDTH;
    }
}
