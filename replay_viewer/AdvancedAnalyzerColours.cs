using osu.Framework.Extensions.Color4Extensions;
using osuTK.Graphics;

namespace Kumori.ReplayViewer;

internal static class AdvancedAnalyzerColours
{
    public static Color4 Panel { get; private set; } = Color4Extensions.FromHex("#1a0611");
    public static Color4 Accent { get; private set; } = Color4Extensions.FromHex("#ff2da8");
    public static Color4 Miss { get; private set; } = Color4Extensions.FromHex("#ff477f");
    public static Color4 Meh { get; private set; } = Color4Extensions.FromHex("#ffe044");
    public static Color4 Ok { get; private set; } = Color4Extensions.FromHex("#00e878");

    public static void Configure(string? themeId)
    {
        (Panel, Accent, Miss, Meh, Ok) = themeId switch
        {
            "pulse" => colours("#20102d", "#ff4eb8", "#ff668a", "#ffd95a", "#52f09a"),
            "windows-fluent" => colours("#202125", "#e84d9a", "#ef7185", "#f3c85b", "#65d58a"),
            _ => colours("#1a0611", "#ff2da8", "#ff477f", "#ffe044", "#00e878"),
        };
    }

    private static (Color4 Panel, Color4 Accent, Color4 Miss, Color4 Meh, Color4 Ok) colours(
        string panel, string accent, string miss, string meh, string ok) =>
        (Color4Extensions.FromHex(panel), Color4Extensions.FromHex(accent), Color4Extensions.FromHex(miss), Color4Extensions.FromHex(meh), Color4Extensions.FromHex(ok));

    public static Color4 For(KumoriTimelineMarkerKind kind) => kind switch
    {
        KumoriTimelineMarkerKind.Miss => Miss,
        KumoriTimelineMarkerKind.SliderBreak => Miss.Opacity(0.72f),
        KumoriTimelineMarkerKind.Meh => Meh,
        KumoriTimelineMarkerKind.Ok => Ok,
        _ => Color4.White,
    };
}
