using System.IO;
using System.Windows;
using Kumori.Core.Settings;
using Kumori.Native;

namespace Kumori.App;

public partial class SkinLibraryWindow : Window
{
    private readonly SettingsService _settings;
    public event EventHandler? DismissRequested;

    public SkinLibraryWindow(SettingsService settings)
    {
        _settings = settings;
        InitializeComponent();
        Refresh();
    }

    private async void ImportFile_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = "Import osu! skin",
            Filter = "osu! skin archives (*.osk)|*.osk|All files (*.*)|*.*",
        };
        if (dialog.ShowDialog() != true)
        {
            return;
        }
        await ImportAndActivateAsync(() => SkinLibraryService.ImportFile(dialog.FileName));
    }

    private async void ImportFolder_Click(object sender, RoutedEventArgs e)
    {
        using var dialog = new System.Windows.Forms.FolderBrowserDialog
        {
            Description = "Import osu! skin folder",
            UseDescriptionForTitle = true,
        };
        if (dialog.ShowDialog() != System.Windows.Forms.DialogResult.OK)
        {
            return;
        }
        await ImportAndActivateAsync(() => SkinLibraryService.ImportFolder(dialog.SelectedPath));
    }

    private async Task ImportAndActivateAsync(Func<string> import)
    {
        IsEnabled = false;
        StatusText.Text = "Importing skin...";
        try
        {
            var path = await Task.Run(import);
            SkinLibraryService.Activate(_settings, path);
            StatusText.Text = $"Imported and activated {Path.GetFileName(path)}.";
            Refresh(path);
        }
        catch (Exception ex)
        {
            StatusText.Text = "Skin import failed.";
            KumoriDialog.Show(this, ex.Message, "Kumori", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            IsEnabled = true;
        }
    }

    private void Use_Click(object sender, RoutedEventArgs e)
    {
        if (SkinList.SelectedItem is not SkinRow row)
        {
            StatusText.Text = "Select a skin first.";
            return;
        }
        if (!row.IsAvailable)
        {
            StatusText.Text = "That skin source is unavailable. Kumori kept your selection and will use it again when the path returns.";
            return;
        }
        SkinLibraryService.Activate(_settings, row.Path);
        StatusText.Text = $"Active skin: {row.Name}";
    }

    private void Delete_Click(object sender, RoutedEventArgs e)
    {
        if (SkinList.SelectedItem is not SkinRow row)
        {
            StatusText.Text = "Select a skin first.";
            return;
        }
        if (!row.CanDelete)
        {
            StatusText.Text = "Argon Pro is built in and cannot be deleted.";
            return;
        }
        if (!KumoriDialog.Confirm(this, $"Delete {row.Name}?", "Kumori", MessageBoxImage.Warning))
        {
            return;
        }
        SkinLibraryService.DeleteImported(row.Path);
        if (string.Equals(_settings.Current.ReplayViewer.SkinPath, row.Path, StringComparison.OrdinalIgnoreCase))
        {
            _settings.Update(s => s.ReplayViewer.SkinPath = "");
        }
        StatusText.Text = "Skin deleted.";
        Refresh();
    }

    private void Close_Click(object sender, RoutedEventArgs e)
    {
        if (DismissRequested is not null) DismissRequested.Invoke(this, EventArgs.Empty);
        else Close();
    }

    private void SkinList_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        var row = SkinList.SelectedItem as SkinRow;
        ActivePathText.Text = row?.DisplayPath ?? "";
        DeleteButton.IsEnabled = row?.CanDelete == true;
        UseButton.IsEnabled = row?.IsAvailable == true;
        ActivePathText.ScrollToHorizontalOffset(0);
    }

    private void Window_MouseLeftButtonDown(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (e.ButtonState == System.Windows.Input.MouseButtonState.Pressed)
        {
            DragMove();
        }
    }

    private void Refresh(string? selectPath = null)
    {
        var rows = SkinLibraryService.List(_settings.Current.ReplayViewer.SkinPath)
            .Select(item => new SkinRow(item))
            .ToArray();
        SkinList.ItemsSource = rows;
        SkinList.SelectedItem = rows.FirstOrDefault(row =>
            SkinLibraryService.MatchesSelection(row.Path, selectPath ?? _settings.Current.ReplayViewer.SkinPath));
        var selected = SkinList.SelectedItem as SkinRow;
        ActivePathText.Text = selected?.DisplayPath ?? "";
        DeleteButton.IsEnabled = selected?.CanDelete == true;
        UseButton.IsEnabled = selected?.IsAvailable == true;
        if (string.IsNullOrWhiteSpace(StatusText.Text))
        {
            var importedCount = rows.Count(row => row.CanDelete);
            StatusText.Text = importedCount == 0
                ? "Argon Pro is ready. Import another skin to add more choices."
                : $"Argon Pro and {importedCount} imported skin(s) are available.";
        }
    }

    private sealed class SkinRow
    {
        public SkinRow(SkinLibraryItem item)
        {
            Name = item.Name;
            Path = item.Path;
            IsAvailable = item.IsAvailable;
            DisplayPath = item.IsBuiltIn
                ? "Included with osu!lazer"
                : item.IsAvailable ? item.Path : $"{item.Path} (unavailable)";
            TypeText = item.IsBuiltIn ? "Built-in"
                : !item.IsAvailable ? "Missing"
                : !item.IsImported ? "External"
                : item.IsFolder ? "Folder" : ".osk";
            SizeText = item.IsBuiltIn || !item.IsAvailable
                ? "—"
                : $"{item.SizeBytes / 1_048_576.0:0.0} MB";
            CanDelete = item.IsImported && item.IsAvailable;
        }

        public string Name { get; }
        public string Path { get; }
        public string DisplayPath { get; }
        public string TypeText { get; }
        public string SizeText { get; }
        public bool CanDelete { get; }
        public bool IsAvailable { get; }
    }
}
