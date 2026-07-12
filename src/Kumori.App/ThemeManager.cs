using System.Windows;
using Kumori.Core.Settings;
using Kumori.Native;
using System.Windows.Interop;

namespace Kumori.App;

public sealed record ThemeDescriptor(string Id, string DisplayName, string Description, string ResourcePath);

public sealed class ThemeManager
{
    public const string DefaultThemeId = "refined-kumori";

    public static readonly IReadOnlyList<ThemeDescriptor> AvailableThemes =
    [
        new(DefaultThemeId, "Refined Kumori", "Deep burgundy surfaces with crisp pink and purple accents.", "Themes/Palettes/RefinedKumori.xaml"),
        new("pulse", "Pulse", "A more energetic, artwork-forward rhythm-game presentation.", "Themes/Palettes/Pulse.xaml"),
        new("windows-fluent", "Windows Fluent", "Layered charcoal surfaces with a restrained native Windows feel.", "Themes/Palettes/WindowsFluent.xaml"),
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
        var resources = application.Resources;

        var merged = resources.MergedDictionaries;
        for (var i = merged.Count - 1; i >= 0; i--)
        {
            if (merged[i].Source?.OriginalString.Contains("Themes/Palettes/", StringComparison.OrdinalIgnoreCase) == true)
            {
                merged.RemoveAt(i);
            }
        }

        // Palette dictionaries stay first so later shared dictionaries can consume
        // their semantic resources while application-level overrides still win.
        merged.Insert(0, new ResourceDictionary { Source = new Uri(selected.ResourcePath, UriKind.Relative) });
        Current = selected;
        DarkTitleBar.UseMica = selected.Id == "windows-fluent";

        foreach (Window window in application.Windows)
        {
            DarkTitleBar.Apply(new WindowInteropHelper(window).Handle);
        }

        if (persist && !string.Equals(_settings.Current.Appearance.ThemeId, selected.Id, StringComparison.Ordinal))
        {
            _settings.Update(settings => settings.Appearance.ThemeId = selected.Id);
        }

        ThemeChanged?.Invoke(this, selected);
    }
}
