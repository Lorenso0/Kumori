using osu.Framework.Graphics;
using osu.Framework.Graphics.Containers;
using osu.Game.Rulesets.Osu;
using osu.Game.Rulesets.Osu.Replays;
using osu.Game.Rulesets.Osu.UI.ReplayAnalysis;

namespace Kumori.ReplayViewer;

internal partial class KumoriSelectedClickMarker : CompositeDrawable
{
    private const double display_length = 600;

    public KumoriSelectedClickMarker()
    {
        RelativeSizeAxes = Axes.Both;
    }

    public void Set(MissAnalysisEntry? entry, bool visible)
    {
        ClearInternal();
        if (!visible || entry?.InputFrame is not { } input)
            return;

        var markers = new ClickMarkerContainer();
        AddInternal(markers);
        markers.Add(new AnalysisFrameEntry(
            input.Time,
            display_length,
            input.Position,
            OsuAction.LeftButton));
    }
}
