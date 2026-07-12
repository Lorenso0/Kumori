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

    private void ImportFile_Click(object sender, RoutedEventArgs e)
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
        var path = SkinLibraryService.ImportFile(dialog.FileName);
        SkinLibraryService.Activate(_settings, path);
        StatusText.Text = $"Imported and activated {Path.GetFileName(path)}.";
        Refresh(path);
    }

    private void ImportFolder_Click(object sender, RoutedEventArgs e)
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
        var path = SkinLibraryService.ImportFolder(dialog.SelectedPath);
        SkinLibraryService.Activate(_settings, path);
        StatusText.Text = $"Imported and activated {Path.GetFileName(path)}.";
        Refresh(path);
    }

    private void Use_Click(object sender, RoutedEventArgs e)
    {
        if (SkinList.SelectedItem is not SkinRow row)
        {
            StatusText.Text = "Select a skin first.";
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
        ActivePathText.Text = SkinList.SelectedItem is SkinRow row ? row.Path : "";
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
        var rows = SkinLibraryService.List()
            .Select(item => new SkinRow(item))
            .ToArray();
        SkinList.ItemsSource = rows;
        SkinList.SelectedItem = rows.FirstOrDefault(row =>
            string.Equals(row.Path, selectPath ?? _settings.Current.ReplayViewer.SkinPath, StringComparison.OrdinalIgnoreCase));
        ActivePathText.Text = SkinList.SelectedItem is SkinRow row ? row.Path : "";
        if (string.IsNullOrWhiteSpace(StatusText.Text))
        {
            StatusText.Text = rows.Length == 0 ? "No skins imported yet." : $"{rows.Length} imported skin(s).";
        }
    }

    private sealed class SkinRow
    {
        public SkinRow(SkinLibraryItem item)
        {
            Name = item.Name;
            Path = item.Path;
            TypeText = item.IsFolder ? "Folder" : ".osk";
            SizeText = $"{item.SizeBytes / 1_048_576.0:0.0} MB";
        }

        public string Name { get; }
        public string Path { get; }
        public string TypeText { get; }
        public string SizeText { get; }
    }
}
