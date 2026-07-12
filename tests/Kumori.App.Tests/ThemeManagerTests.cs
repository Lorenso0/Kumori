using Xunit;

namespace Kumori.App.Tests;

public sealed class ThemeManagerTests
{
    [Theory]
    [InlineData("refined-kumori", "refined-kumori")]
    [InlineData("pulse", "pulse")]
    [InlineData("windows-fluent", "windows-fluent")]
    [InlineData("unknown", "refined-kumori")]
    [InlineData(null, "refined-kumori")]
    public void ResolveAlwaysReturnsSupportedTheme(string? input, string expected)
    {
        Assert.Equal(expected, ThemeManager.Resolve(input).Id);
    }

    [Fact]
    public void EveryThemeDictionaryExistsAndDefinesSemanticResources()
    {
        var root = FindRepositoryRoot();
        string[] required =
        [
            "Brush.AppBackground", "Brush.PanelBackground", "Brush.CardBackground",
            "Brush.AccentPink", "Brush.AccentPurple", "Brush.TextPrimary",
            "Brush.NavigationBackground", "Brush.TopBarBackground", "Brush.MetricBackground", "Radius.ThemeCard",
        ];

        foreach (var theme in ThemeManager.AvailableThemes)
        {
            var path = Path.Combine(root, "src", "Kumori.App", theme.ResourcePath.Replace('/', Path.DirectorySeparatorChar));
            Assert.True(File.Exists(path), $"Missing theme dictionary: {path}");
            var xaml = File.ReadAllText(path);
            Assert.All(required, key => Assert.Contains($"x:Key=\"{key}\"", xaml));
        }
    }

    private static string FindRepositoryRoot()
    {
        var current = AppContext.BaseDirectory;
        while (!string.IsNullOrEmpty(current))
        {
            if (File.Exists(Path.Combine(current, "Kumori.sln"))) return current;
            current = Directory.GetParent(current)?.FullName ?? string.Empty;
        }
        throw new DirectoryNotFoundException("Could not locate Kumori.sln.");
    }
}
