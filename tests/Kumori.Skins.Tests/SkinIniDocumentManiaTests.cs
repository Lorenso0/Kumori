using System.Text;
using Kumori.Skins;
using Xunit;

namespace Kumori.Skins.Tests;

public sealed class SkinIniDocumentManiaTests
{
    [Fact]
    public void Add_mania_section_creates_key_count_specific_editable_template()
    {
        var document = SkinIniDocument.ParseText(
            "; header\r\n[General]\r\nName: Kumori\r\n");

        Assert.True(document.AddManiaSection(4));
        Assert.False(document.AddManiaSection(4));

        var mania = Assert.Single(document.GetSections("Mania"));
        Assert.Equal(4, mania.ManiaKeys);
        Assert.Equal("136", mania.Values["ColumnStart"]);
        Assert.Equal("402", mania.Values["HitPosition"]);
        Assert.Equal("30,30,30,30", mania.Values["ColumnWidth"]);
        Assert.Equal("2,2,2,2,2", mania.Values["ColumnLineWidth"]);
        var rendered = Encoding.UTF8.GetString(document.ToBytes());
        Assert.Contains("; header\r\n[General]\r\nName: Kumori", rendered);
        Assert.DoesNotContain("\n", rendered.Replace("\r\n", ""));
    }

    [Fact]
    public void Remove_mania_section_preserves_surrounding_sections_and_line_endings()
    {
        var document = SkinIniDocument.ParseText(
            "[General]\r\nName: Kumori\r\n\r\n"
            + "[Mania]\r\nKeys: 7\r\nHitPosition: 402\r\n; local comment\r\n\r\n"
            + "[Colours]\r\nCombo1: 255,0,0\r\n");

        Assert.True(document.RemoveManiaSection(7));
        Assert.False(document.RemoveManiaSection(7));

        var rendered = Encoding.UTF8.GetString(document.ToBytes());
        Assert.Empty(document.GetSections("Mania"));
        Assert.Contains("[General]\r\nName: Kumori", rendered);
        Assert.Contains("[Colours]\r\nCombo1: 255,0,0", rendered);
        Assert.DoesNotContain("local comment", rendered);
        Assert.DoesNotContain("\n", rendered.Replace("\r\n", ""));
    }
}
