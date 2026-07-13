using System.Windows;
using Kumori.Core.Settings;
using Kumori.Native;
using System.Windows.Interop;
using System.Windows.Media;
using MediaColor = System.Windows.Media.Color;
using MediaColorConverter = System.Windows.Media.ColorConverter;

namespace Kumori.App;

public sealed record ThemeDescriptor(string Id, string DisplayName, string Description, string ResourcePath);

public sealed class ThemeManager
{
    public const string DefaultThemeId = "refined-kumori";
    public const string CustomThemeId = "custom";

    public static readonly IReadOnlyList<ThemeDescriptor> AvailableThemes =
    [
        new(DefaultThemeId, "Refined Kumori", "Deep burgundy surfaces with crisp pink and purple accents.", "Themes/Palettes/RefinedKumori.xaml"),
        new("pulse", "Pulse", "A more energetic, artwork-forward rhythm-game presentation.", "Themes/Palettes/Pulse.xaml"),
        new("windows-fluent", "Windows Fluent", "Layered charcoal surfaces with a restrained native Windows feel.", "Themes/Palettes/WindowsFluent.xaml"),
        new(CustomThemeId, "Custom", "A personal palette that can be imported, exported, and shared.", "Themes/Palettes/Custom.xaml"),
    ];

    private readonly SettingsService _settings;

    public ThemeManager(SettingsService settings)
    {
        _settings = settings;
        Current = Resolve(settings.Current.Appearance.ThemeId);
    }

    public ThemeDescriptor Current { get; private set; }

    public event EventHandler<ThemeDescriptor>? ThemeChanged;

    public static ThemeDescriptor Resolve(string? themeId) =>
        AvailableThemes.FirstOrDefault(theme => string.Equals(theme.Id, themeId, StringComparison.OrdinalIgnoreCase))
        ?? AvailableThemes[0];

    public void ApplyCurrent() => Apply(_settings.Current.Appearance.ThemeId, persist: false);

    public void Apply(string? themeId, bool persist = true)
    {
        var selected = Resolve(themeId);
        var application = Application.Current;
        if (application is null)
        {
            Current = selected;
            return;
        }
        ApplyPalette(application, selected,
            selected.Id == CustomThemeId ? _settings.Current.Appearance.CustomTheme : null);

        if (persist && !string.Equals(_settings.Current.Appearance.ThemeId, selected.Id, StringComparison.Ordinal))
        {
            _settings.Update(settings => settings.Appearance.ThemeId = selected.Id);
        }

        ThemeChanged?.Invoke(this, selected);
    }

    public void PreviewCustom(CustomThemeSettings theme)
    {
        var application = Application.Current;
        if (application is null)
        {
            Current = Resolve(CustomThemeId);
            return;
        }

        ApplyPalette(application, Resolve(CustomThemeId), theme);
        ThemeChanged?.Invoke(this, Current);
    }

    private void ApplyPalette(Application application, ThemeDescriptor selected, CustomThemeSettings? customTheme)
    {
        var merged = application.Resources.MergedDictionaries;
        for (var i = merged.Count - 1; i >= 0; i--)
        {
            if (merged[i].Source?.OriginalString.Contains("Themes/Palettes/", StringComparison.OrdinalIgnoreCase) == true)
                merged.RemoveAt(i);
        }

        // Palette dictionaries stay first so later shared dictionaries can consume
        // their semantic resources while application-level overrides still win.
        var palette = new ResourceDictionary { Source = new Uri(selected.ResourcePath, UriKind.Relative) };
        if (selected.Id == CustomThemeId)
            ApplyCustomResources(palette, customTheme);
        merged.Insert(0, palette);

        Current = selected;
        DarkTitleBar.UseMica = selected.Id == "windows-fluent";
        foreach (Window window in application.Windows)
            DarkTitleBar.Apply(new WindowInteropHelper(window).Handle);
    }

    private static void ApplyCustomResources(ResourceDictionary palette, CustomThemeSettings? theme)
    {
        var colors = CustomThemePalette.Normalize(theme).Colors;
        foreach (var key in CustomThemePalette.ColorKeys)
        {
            var color = (MediaColor)MediaColorConverter.ConvertFromString(colors[key]);
            palette[$"Color.{key}"] = color;
            palette[$"Brush.{key}"] = FrozenBrush(color);
        }

        palette["Brush.BackgroundPrimary"] = FrozenBrush((MediaColor)palette["Color.AppBackground"]);
        palette["Brush.BackgroundSecondary"] = FrozenBrush((MediaColor)palette["Color.PanelBackground"]);
        palette["Brush.Border"] = FrozenBrush((MediaColor)palette["Color.SubtleBorder"]);
        palette["Brush.BorderSubtle"] = FrozenBrush((MediaColor)palette["Color.SubtleBorder"]);
        palette["Brush.Negative"] = FrozenBrush((MediaColor)palette["Color.Danger"]);
    }

    private static SolidColorBrush FrozenBrush(MediaColor color)
    {
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        return brush;
    }
}
