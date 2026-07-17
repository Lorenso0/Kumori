using System.Windows;
using System.Windows.Media;
using Kumori.Native;
using DrawingColor = System.Drawing.Color;
using MediaColor = System.Windows.Media.Color;

namespace Kumori.App;

public partial class App
{
    private void ApplyTrayTheme()
    {
        if (_tray is null)
            return;

        _tray.SetTheme(new TrayMenuTheme(
            ResourceColor("Brush.PanelBackground", DrawingColor.FromArgb(21, 16, 19)),
            ResourceColor("Brush.ControlHoverBackground", DrawingColor.FromArgb(38, 24, 32)),
            ResourceColor("Brush.TextPrimary", DrawingColor.FromArgb(247, 241, 244)),
            ResourceColor("Brush.TextMuted", DrawingColor.FromArgb(157, 128, 140)),
            ResourceColor("Brush.AccentPink", DrawingColor.FromArgb(232, 85, 141)),
            ResourceColor("Brush.SubtleBorder", DrawingColor.FromArgb(128, 90, 109))));
    }

    internal static DrawingColor ToTrayColor(MediaColor color) =>
        DrawingColor.FromArgb(color.R, color.G, color.B);

    private static DrawingColor ResourceColor(string key, DrawingColor fallback)
    {
        var resource = Application.Current?.TryFindResource(key);
        return resource switch
        {
            SolidColorBrush brush => ToTrayColor(brush.Color),
            MediaColor color => ToTrayColor(color),
            _ => fallback,
        };
    }
}
