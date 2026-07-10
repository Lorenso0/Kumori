using osu.Framework.Extensions.Color4Extensions;
using osuTK.Graphics;

namespace Kumori.ReplayViewer;

internal static class AdvancedAnalyzerColours
{
    public static readonly Color4 Panel = Color4Extensions.FromHex("#11131c");
    public static readonly Color4 Accent = Color4Extensions.FromHex("#8b5cf6");
    public static readonly Color4 Miss = Color4Extensions.FromHex("#ff4f7b");
    public static readonly Color4 Meh = Color4Extensions.FromHex("#ffd43b");
    public static readonly Color4 Ok = Color4Extensions.FromHex("#9bdc28");

    public static Color4 For(KumoriTimelineMarkerKind kind) => kind switch
    {
        KumoriTimelineMarkerKind.Miss => Miss,
        KumoriTimelineMarkerKind.SliderBreak => Miss.Opacity(0.72f),
        KumoriTimelineMarkerKind.Meh => Meh,
        KumoriTimelineMarkerKind.Ok => Ok,
        _ => Color4.White,
    };
}
