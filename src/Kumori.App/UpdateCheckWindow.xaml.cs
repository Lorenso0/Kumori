using System.Diagnostics;
using System.Reflection;
using System.Windows;
using Kumori.Core;
using Kumori.Native;

namespace Kumori.App;

public partial class UpdateCheckWindow : Window
{
    private readonly KumoriUpdateService updateService = new();
    private string releaseUrl = KumoriUpdateService.ReleasesUrl;

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
            var result = await updateService.CheckAsync();
            releaseUrl = result.ReleaseUrl;
            var availability = result.IsUpdateAvailable
                ? "An update is available."
                : "You are running the latest release.";
            var published = result.PublishedAt is { } publishedAt
                ? DisplayDateTime.FormatLocalDateTime(publishedAt)
                : "not queried (API-free check)";
            StatusText.Text =
                $"""
                Current version: {result.CurrentVersion}
                Latest release: {result.LatestName}
                Tag: {result.LatestTag}
                Published: {published}

                {availability}

                Published builds can download, verify, install, and relaunch from the automatic update prompt.
                You can also use Open releases to download manually.
                """;
        }
        catch (Exception ex)
        {
            StatusText.Text = $"Update check failed:\n{ex.Message}\n\nRelease page: {KumoriUpdateService.ReleasesUrl}";
        }
    }

    private void Open_Click(object sender, RoutedEventArgs e) =>
        Process.Start(new ProcessStartInfo { FileName = releaseUrl, UseShellExecute = true });

    private void Close_Click(object sender, RoutedEventArgs e) => Close();

    private static string CurrentVersion() =>
        Assembly.GetEntryAssembly()?.GetName().Version?.ToString() ?? "dev";
}
