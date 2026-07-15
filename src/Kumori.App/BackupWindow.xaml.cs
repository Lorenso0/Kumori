using System.Diagnostics;
using System.IO;
using System.Windows;
using Kumori.Core;
using Kumori.Core.Settings;
using Kumori.Storage;

namespace Kumori.App;

public partial class BackupWindow : Window
{
    private readonly BackupService service = new();
    private readonly SettingsService settings;

    public BackupWindow(SettingsService settings)
    {
        this.settings = settings;
        InitializeComponent();
        Refresh();
    }

    private void Refresh()
    {
        try
        {
            BackupList.ItemsSource = service.List(settings.Current.Backup).Select(info => new Row(info)).ToArray();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            BackupList.ItemsSource = Array.Empty<Row>();
            StatusText.Text = $"Could not open the backup directory: {ex.Message}";
        }
    }

    private async void Create_Click(object sender, RoutedEventArgs e)
    {
        IsEnabled = false;
        try
        {
            StatusText.Text = "Creating consistent database snapshot...";
            var path = await Task.Run(() => service.Create(settings.Current.Backup));
            StatusText.Text = $"Created {path}";
            Refresh();
        }
        catch (Exception ex)
        {
            StatusText.Text = ex.Message;
        }
        finally
        {
            IsEnabled = true;
        }
    }

    private async void Restore_Click(object sender, RoutedEventArgs e)
    {
        if (BackupList.SelectedItem is not Row row)
        {
            StatusText.Text = "Select a backup first.";
            return;
        }
        if (!KumoriDialog.Confirm(this, "Restore this backup the next time Kumori starts?", "Restore backup", MessageBoxImage.Warning)) return;

        IsEnabled = false;
        try
        {
            await Task.Run(() => service.StageRestore(row.Info.Path));
            StatusText.Text = "Restore staged. Exit and reopen Kumori to apply it. External paths and automatic integrations will remain disabled until you review them.";
        }
        catch (Exception ex)
        {
            StatusText.Text = ex.Message;
        }
        finally
        {
            IsEnabled = true;
        }
    }

    private void Open_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var directory = string.IsNullOrWhiteSpace(settings.Current.Backup.Directory)
                ? AppPaths.BackupsDir
                : Path.GetFullPath(settings.Current.Backup.Directory);
            Directory.CreateDirectory(directory);
            Process.Start(new ProcessStartInfo { FileName = directory, UseShellExecute = true });
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            StatusText.Text = $"Could not open the backup directory: {ex.Message}";
        }
    }

    private sealed class Row(BackupInfo info)
    {
        public BackupInfo Info { get; } = info;
        public string Display => $"{DisplayDateTime.FormatLocalDateTime(Info.CreatedAt)}   {FormatSize(Info.SizeBytes)}   {Path.GetFileName(Info.Path)}";

        private static string FormatSize(long bytes) => bytes switch
        {
            < 1024 => $"{bytes} B",
            < 1_048_576 => $"{bytes / 1024d:0.0} KB",
            _ => $"{bytes / 1_048_576d:0.0} MB",
        };
    }
}
