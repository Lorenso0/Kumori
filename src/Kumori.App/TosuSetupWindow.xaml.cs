using System.IO;
using System.Windows;
using Kumori.Core;
using Kumori.Native;

namespace Kumori.App;

public partial class TosuSetupWindow : Window
{
    public TosuSetupWindow()
    {
        InitializeComponent();
        RefreshStatus("Ready.");
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        DarkTitleBar.Apply(new System.Windows.Interop.WindowInteropHelper(this).Handle);
    }

    private async void Install_Click(object sender, RoutedEventArgs e)
    {
        await RunAsync("Checking latest release...", async () =>
        {
            var result = await TosuManager.EnsureInstalledAsync(forceCheck: true);
            return result.InstalledOrUpdated
                ? $"Installed tosu {result.Version} at {result.ExecutablePath}"
                : $"tosu {result.Version} is already installed.";
        });
    }

    private async void Launch_Click(object sender, RoutedEventArgs e)
    {
        await RunAsync("Launching tosu...", async () =>
        {
            var process = await TosuManager.EnsureInstalledAndLaunchAsync();
            return $"tosu is running. PID {process.Id}";
        });
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();

    private async Task RunAsync(string starting, Func<Task<string>> action)
    {
        InstallButton.IsEnabled = false;
        LaunchButton.IsEnabled = false;
        Progress.IsIndeterminate = true;
        RefreshStatus(starting);
        try
        {
            var result = await action();
            Progress.IsIndeterminate = false;
            Progress.Value = 100;
            RefreshStatus(result);
        }
        catch (Exception ex)
        {
            Progress.IsIndeterminate = false;
            Progress.Value = 0;
            RefreshStatus(ex.Message);
        }
        finally
        {
            InstallButton.IsEnabled = true;
            LaunchButton.IsEnabled = true;
        }
    }

    private void RefreshStatus(string line)
    {
        StatusText.Text =
            $"""
            {line}

            Executable: {AppPaths.TosuExecutable}
            Installed: {File.Exists(AppPaths.TosuExecutable)}
            Version: {(File.Exists(AppPaths.TosuVersionFile) ? File.ReadAllText(AppPaths.TosuVersionFile).Trim() : "unknown")}
            Releases: {TosuManager.ReleasesUrl}
            Environment: {AppPaths.TosuEnvFile}
            """;
    }
}
