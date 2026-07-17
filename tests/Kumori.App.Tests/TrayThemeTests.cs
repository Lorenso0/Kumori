using System.Windows.Media;
using Kumori.Native;
using DrawingColor = System.Drawing.Color;
using Xunit;

namespace Kumori.App.Tests;

public sealed class TrayThemeTests
{
    [Fact]
    public void WpfThemeColorBecomesOpaqueForWinFormsTrayRenderer()
    {
        var converted = App.ToTrayColor(Color.FromArgb(0xE0, 0x12, 0x34, 0x56));

        Assert.Equal(0xFF, converted.A);
        Assert.Equal(0x12, converted.R);
        Assert.Equal(0x34, converted.G);
        Assert.Equal(0x56, converted.B);
    }

    [Fact]
    public void TrayColorTableUsesThemeColorsForHoverChecksAndBorders()
    {
        var theme = new TrayMenuTheme(
            DrawingColor.Black,
            DrawingColor.DarkMagenta,
            DrawingColor.White,
            DrawingColor.Gray,
            DrawingColor.HotPink,
            DrawingColor.Purple);
        var table = new TrayMenuColorTable(theme);

        Assert.Equal(theme.Background, table.ToolStripDropDownBackground);
        Assert.Equal(theme.HoverBackground, table.MenuItemSelected);
        Assert.Equal(theme.Accent, table.CheckBackground);
        Assert.Equal(theme.Separator, table.MenuBorder);
    }
}
