using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using Kumori.Native;

namespace Kumori.App;

internal enum UpdateAvailableAction
{
    Later,
    ViewRelease,
    Install,
}

public partial class UpdateAvailableWindow : Window
{
    private readonly KumoriUpdateResult update;
    private readonly CancellationTokenSource downloadCancellation = new();
    private bool downloading;

    internal UpdateAvailableAction SelectedAction { get; private set; } = UpdateAvailableAction.Later;
    internal StagedKumoriUpdate? StagedUpdate { get; private set; }

    internal UpdateAvailableWindow(KumoriUpdateResult update)
    {
        this.update = update;
        InitializeComponent();
        VersionText.Text = update.LatestTag;
        var installSupported = update.CanAutoInstall && KumoriUpdateInstaller.IsSupportedInstallation;
        DescriptionText.Text = installSupported
            ? $"You are running {FormatVersion(update.CurrentVersion)}. Kumori {update.LatestTag} can be downloaded and installed now."
            : $"You are running {FormatVersion(update.CurrentVersion)}. Kumori {update.LatestTag} is available on the release page.";
        InstallButton.IsEnabled = installSupported;
        InstallHintText.Text = installSupported
            ? "The release checksum is verified before Kumori closes. The app then replaces itself and relaunches automatically."
            : "Automatic installation is unavailable for this build or release. You can still open the release page.";
        Closing += (_, e) =>
        {
            if (downloading)
            {
                downloadCancellation.Cancel();
            }
        };
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        DarkTitleBar.Apply(new WindowInteropHelper(this).Handle);
    }

    private async void Install_Click(object sender, RoutedEventArgs e)
    {
        if (downloading)
        {
            return;
        }

        downloading = true;
        SetButtonsEnabled(false);
        ProgressPanel.Visibility = Visibility.Visible;
        ErrorText.Visibility = Visibility.Collapsed;
        ProgressText.Text = "Preparing secure download...";
        DownloadProgress.IsIndeterminate = true;
        var progress = new Progress<KumoriUpdateDownloadProgress>(value =>
        {
            if (value.Percentage is { } percentage)
            {
                DownloadProgress.IsIndeterminate = false;
                DownloadProgress.Value = percentage;
                ProgressText.Text = $"Downloading update... {percentage}% ({FormatBytes(value.BytesReceived)} / {FormatBytes(value.TotalBytes!.Value)})";
            }
            else
            {
                DownloadProgress.IsIndeterminate = true;
                ProgressText.Text = $"Downloading update... {FormatBytes(value.BytesReceived)}";
            }
        });

        try
        {
            StagedUpdate = await new KumoriUpdateInstaller().StageAsync(update, progress, downloadCancellation.Token);
            ProgressText.Text = "Download verified. Restarting Kumori...";
            DownloadProgress.IsIndeterminate = false;
            DownloadProgress.Value = 100;
            SelectedAction = UpdateAvailableAction.Install;
            DialogResult = true;
        }
        catch (OperationCanceledException) when (downloadCancellation.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            ErrorText.Text = $"The update could not be installed: {ex.Message}";
            ErrorText.Visibility = Visibility.Visible;
            ProgressPanel.Visibility = Visibility.Collapsed;
            SetButtonsEnabled(true);
            downloading = false;
        }
    }

    private void Release_Click(object sender, RoutedEventArgs e)
    {
        if (downloading) return;
        SelectedAction = UpdateAvailableAction.ViewRelease;
        Close();
    }

    private void Later_Click(object sender, RoutedEventArgs e)
    {
        if (downloading)
        {
            downloadCancellation.Cancel();
            return;
        }
        SelectedAction = UpdateAvailableAction.Later;
        Close();
    }

    private void SetButtonsEnabled(bool enabled)
    {
        LaterButton.IsEnabled = enabled;
        ReleaseButton.IsEnabled = enabled;
        InstallButton.IsEnabled = enabled && update.CanAutoInstall && KumoriUpdateInstaller.IsSupportedInstallation;
        CloseButton.IsEnabled = enabled;
    }

    private static string FormatVersion(Version version) => $"{version.Major}.{version.Minor}.{version.Build}";

    private static string FormatBytes(long bytes) => bytes switch
    {
        >= 1024L * 1024 * 1024 => $"{bytes / (1024d * 1024 * 1024):0.0} GB",
        >= 1024L * 1024 => $"{bytes / (1024d * 1024):0.0} MB",
        >= 1024 => $"{bytes / 1024d:0.0} KB",
        _ => $"{bytes} B",
    };

    private void Window_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState == MouseButtonState.Pressed && !downloading)
        {
            DragMove();
        }
    }
}
