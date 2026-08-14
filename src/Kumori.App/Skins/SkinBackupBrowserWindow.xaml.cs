using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;

namespace Kumori.App.Skins;

internal partial class SkinBackupBrowserWindow : Window
{
    private IReadOnlyList<BackupFileChoice> fileChoices = [];

    internal SkinBackupBrowserWindow(
        Window? owner,
        IReadOnlyList<SkinElementBackupSession> sessions)
    {
        Owner = owner;
        InitializeComponent();
        BackupList.ItemsSource = sessions.Select(session =>
            new BackupSessionChoice(session)).ToArray();
        BackupList.SelectedIndex = sessions.Count > 0 ? 0 : -1;
        updateSelectionStatus();
    }

    internal SkinElementBackupSelection? Selection { get; private set; }

    private void BackupList_SelectionChanged(
        object sender,
        SelectionChangedEventArgs e)
    {
        if (BackupList.SelectedItem is not BackupSessionChoice selected)
        {
            fileChoices = [];
            BackupFilesList.ItemsSource = null;
            BackupDetailsText.Text = "Select a backup to inspect its files.";
            updateSelectionStatus();
            return;
        }

        fileChoices = selected.Session.Files
            .Select(file => new BackupFileChoice(file))
            .ToArray();
        BackupFilesList.ItemsSource = fileChoices;
        BackupDetailsText.Text = selected.Session.HasRealmRestorePoint
            ? "Complete skin snapshot with a Realm restore point"
            : "Element snapshot created before an edit";
        updateSelectionStatus();
    }

    private void SelectAll_Click(object sender, RoutedEventArgs e)
    {
        foreach (var file in fileChoices)
            file.IsSelected = true;
        updateSelectionStatus();
    }

    private void Clear_Click(object sender, RoutedEventArgs e)
    {
        foreach (var file in fileChoices)
            file.IsSelected = false;
        updateSelectionStatus();
    }

    private void FileChoice_Click(object sender, RoutedEventArgs e) =>
        updateSelectionStatus();

    private void Restore_Click(object sender, RoutedEventArgs e)
    {
        if (BackupList.SelectedItem is not BackupSessionChoice selected)
            return;
        var files = fileChoices
            .Where(file => file.IsSelected)
            .Select(file => file.File)
            .ToArray();
        if (files.Length == 0)
            return;

        Selection = new SkinElementBackupSelection(selected.Session, files);
        DialogResult = true;
    }

    private void updateSelectionStatus()
    {
        var selected = fileChoices.Count(file => file.IsSelected);
        SelectionStatusText.Text = selected == 0
            ? "Choose at least one previous file."
            : $"{selected} file{(selected == 1 ? "" : "s")} will be added to Changes.";
        RestoreButton.IsEnabled = selected > 0;
    }

    private sealed record BackupSessionChoice(SkinElementBackupSession Session)
    {
        public string CreatedText => Session.CreatedAt.ToLocalTime()
            .ToString("ddd, d MMM yyyy · HH:mm");
        public string SummaryText =>
            $"{Session.Files.Count} file{(Session.Files.Count == 1 ? "" : "s")}";
        public string KindText => Session.HasRealmRestorePoint
            ? "Full skin restore point"
            : "Before-edit snapshot";
    }

    private sealed class BackupFileChoice : INotifyPropertyChanged
    {
        private bool isSelected = true;

        public BackupFileChoice(SkinElementBackupFile file) => File = file;

        public event PropertyChangedEventHandler? PropertyChanged;
        public SkinElementBackupFile File { get; }
        public string Filename => File.Filename;
        public string ExtensionText
        {
            get
            {
                var extension = Path.GetExtension(File.Filename).TrimStart('.');
                return extension.Length == 0 ? "FILE" : extension.ToUpperInvariant();
            }
        }
        public string GroupText => SkinElementCategorizer.CategoryFor(File.Filename);
        public string SizeText => File.Size < 1024
            ? $"{File.Size} B"
            : File.Size < 1024 * 1024
                ? $"{File.Size / 1024d:0.#} KB"
                : $"{File.Size / (1024d * 1024d):0.#} MB";

        public bool IsSelected
        {
            get => isSelected;
            set
            {
                if (isSelected == value)
                    return;
                isSelected = value;
                PropertyChanged?.Invoke(
                    this,
                    new PropertyChangedEventArgs(nameof(IsSelected)));
            }
        }
    }
}
