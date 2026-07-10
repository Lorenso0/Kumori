using System.Diagnostics;
using System.Net.Http;
using System.Reflection;
using System.Text.Json;
using System.Windows;
using Kumori.Native;

namespace Kumori.App;

public partial class UpdateCheckWindow : Window
{
    private const string ReleasesUrl = "https://github.com/Lorenso0/Kumori/releases";
    private const string LatestApiUrl = "https://api.github.com/repos/Lorenso0/Kumori/releases/latest";
    private static readonly HttpClient Http = new();

    static UpdateCheckWindow()
    {
        Http.DefaultRequestHeaders.UserAgent.ParseAdd("Kumori");
    }

    public UpdateCheckWindow()
    {
        InitializeComponent();
        StatusText.Text = $"Current version: {CurrentVersion()}\n\nPress Check to query the latest GitHub release.";
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        DarkTitleBar.Apply(new System.Windows.Interop.WindowInteropHelper(this).Handle);
    }

    private async void Check_Click(object sender, RoutedEventArgs e)
    {
        StatusText.Text = "Checking latest release...";
        try
        {
            using var response = await Http.GetAsync(LatestApiUrl);
            response.EnsureSuccessStatusCode();
            await using var stream = await response.Content.ReadAsStreamAsync();
            using var doc = await JsonDocument.ParseAsync(stream);
            var tag = doc.RootElement.TryGetProperty("tag_name", out var tagElement)
                ? tagElement.GetString() ?? "unknown"
                : "unknown";
            var name = doc.RootElement.TryGetProperty("name", out var nameElement)
                ? nameElement.GetString() ?? tag
                : tag;
            var published = doc.RootElement.TryGetProperty("published_at", out var publishedElement)
                ? publishedElement.GetString() ?? "unknown"
                : "unknown";
            StatusText.Text =
                $"""
                Current version: {CurrentVersion()}
                Latest release: {name}
                Tag: {tag}
                Published: {published}

                Kumori can check releases now. Automatic replacement/install of Kumori itself should wait until the app is code-signed and the release manifest is stable.
                """;
        }
        catch (Exception ex)
        {
            StatusText.Text = $"Update check failed:\n{ex.Message}\n\nRelease page: {ReleasesUrl}";
        }
    }

    private void Open_Click(object sender, RoutedEventArgs e) =>
        Process.Start(new ProcessStartInfo { FileName = ReleasesUrl, UseShellExecute = true });

    private void Close_Click(object sender, RoutedEventArgs e) => Close();

    private static string CurrentVersion() =>
        Assembly.GetEntryAssembly()?.GetName().Version?.ToString() ?? "dev";
}
