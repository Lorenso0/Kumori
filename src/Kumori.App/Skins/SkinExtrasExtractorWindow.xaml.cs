using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;
using System.Windows;
using Kumori.Core;
using Microsoft.Win32;

namespace Kumori.App.Skins;

public partial class SkinExtrasExtractorWindow : Window
{
    private readonly SkinExtrasExtractionService service = new();
    private readonly SkinExtraModeVisibility modeVisibility;
    private readonly Action<bool>? lazerFilterChanged;
    private SkinExtractionSource source;
    private List<FamilyChoice> choices = [];
    private bool lazerUsedOnly;
    private bool initializingLazerFilter;

    public SkinExtrasExtractorWindow(
        Window? owner,
        SkinExtractionSource source,
        SkinExtraModeVisibility? modeVisibility = null,
        Action<bool>? lazerFilterChanged = null)
    {
        Owner = owner;
        this.source = source;
        this.modeVisibility = modeVisibility ?? new SkinExtraModeVisibility();
        this.lazerFilterChanged = lazerFilterChanged;
        lazerUsedOnly = this.modeVisibility.LazerUsedOnly;
        InitializeComponent();
        initializingLazerFilter = true;
        LazerUsedOnlyCheckBox.IsChecked = lazerUsedOnly;
        initializingLazerFilter = false;
        ShowSource();
    }

    public IReadOnlyList<SkinExtraExtractionResult> Results { get; private set; } = [];

    private void ShowSource()
    {
        var families = service.Analyze(source)
            .Where(family => modeVisibility.AllowsArea(family.Definition.Area))
            .ToArray();
        choices = families.Select(family => new FamilyChoice(family)).ToList();
        SourceNameText.Text = string.IsNullOrWhiteSpace(source.Author)
            ? source.DisplayName
            : $"{source.DisplayName} — {source.Author}";
        SourcePathText.Text = source.SourceLabel;
        PackNameTextBox.Text = SkinExtraNaming.PackName(source.DisplayName, source.Author);
        RefreshCompatibilityFilter();
    }

    private void LazerFilter_Changed(object sender, RoutedEventArgs e)
    {
        if (initializingLazerFilter) return;
        lazerUsedOnly = LazerUsedOnlyCheckBox.IsChecked == true;
        lazerFilterChanged?.Invoke(lazerUsedOnly);
        RefreshCompatibilityFilter();
    }

    private void RefreshCompatibilityFilter()
    {
        foreach (var choice in choices) choice.SetLazerUsedOnly(lazerUsedOnly);
        var visible = choices.Where(choice => choice.HasVisibleContent).ToArray();
        FamilyList.ItemsSource = visible;
        DetectionSummaryText.Text =
            $"{visible.Length} reusable famil{(visible.Length == 1 ? "y" : "ies")} detected"
            + (lazerUsedOnly ? " for osu! lazer" : "");
        StatusText.Text = visible.Length == 0
            ? lazerUsedOnly
                ? "No assets used by the audited osu! lazer version were found."
                : "No supported image, audio, colour, or number-font families were found."
            : "Scoped skin.ini settings are included automatically. Identity fields are never copied.";
        ExtractButton.IsEnabled = visible.Length > 0;
    }

    private void OpenFolder_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog
        {
            Title = "Choose a folder of loose osu! skin files",
            Multiselect = false,
        };
        if (dialog.ShowDialog(this) != true) return;
        Load(() => service.ReadFolder(dialog.FolderName));
    }

    private void OpenOsk_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Title = "Choose an osu! skin archive",
            Filter = "osu! skin archives|*.osk;*.zip|All files|*.*",
            Multiselect = false,
        };
        if (dialog.ShowDialog(this) != true) return;
        Load(() => service.ReadOsk(dialog.FileName));
    }

    private void Load(Func<SkinExtractionSource> loader)
    {
        try
        {
            source = loader();
            ShowSource();
        }
        catch (Exception ex)
        {
            MessageBox.Show(
                this,
                $"Could not read this skin:\n\n{ex.Message}",
                "Skin extraction",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
        }
    }

    private void SelectAll_Click(object sender, RoutedEventArgs e)
    {
        foreach (var choice in choices.Where(choice => choice.HasVisibleContent))
            choice.IsSelected = true;
    }

    private void Clear_Click(object sender, RoutedEventArgs e)
    {
        foreach (var choice in choices.Where(choice => choice.HasVisibleContent))
            choice.IsSelected = false;
    }

    private async void Extract_Click(object sender, RoutedEventArgs e)
    {
        var selected = choices.Where(choice => choice.IsSelected && choice.HasVisibleContent)
            .Select(choice => choice.BuildFamily(lazerUsedOnly))
            .Where(family => family.Files.Count > 0 || family.IniPatch.Count > 0)
            .ToArray();
        if (selected.Length == 0)
        {
            StatusText.Text = "Select at least one family.";
            return;
        }
        IsEnabled = false;
        StatusText.Text = "Hashing assets and checking the Extras library…";
        var packName = PackNameTextBox.Text.Trim();
        try
        {
            Results = await Task.Run(() =>
                service.Extract(
                    source,
                    selected,
                    AppPaths.SkinExtrasDir,
                    packName,
                    lazerUsedOnly));
            var extracted = Results.Count(result =>
                result.Status == SkinExtraExtractionStatus.Extracted);
            var duplicates = Results.Count - extracted;
            var similar = Results.Count(result => result.SimilarPack is not null);
            StatusText.Text =
                $"{extracted} pack(s) extracted; {duplicates} exact duplicate(s) skipped"
                + (similar == 0 ? "." : $"; {similar} possible visual duplicate(s) flagged.");
            DialogResult = true;
        }
        catch (Exception ex)
        {
            StatusText.Text = $"Extraction failed: {ex.Message}";
            MessageBox.Show(
                this,
                StatusText.Text,
                "Skin extraction",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
        }
        finally
        {
            IsEnabled = true;
        }
    }

    private sealed class FamilyChoice : INotifyPropertyChanged
    {
        private bool isSelected = true;
        private bool lazerUsedOnly;

        public FamilyChoice(SkinExtractionFamily family)
        {
            Family = family;
            Files = new ObservableCollection<FileChoice>(
                family.Files.Select(file => new FileChoice(file, family.Definition.Id)));
        }
        public SkinExtractionFamily Family { get; }
        public ObservableCollection<FileChoice> Files { get; }
        public IReadOnlyList<FileChoice> VisibleFiles => Files
            .Where(file => !lazerUsedOnly
                           || file.Compatibility == SkinExtraCompatibility.LazerUsed)
            .ToArray();
        public bool HasVisibleContent =>
            VisibleFiles.Count > 0
            || Family.IniPatch.Any(entry => !lazerUsedOnly
                || SkinExtraLazerCompatibility.IsIniPatchUsed(Family.Definition.Id, entry));
        public string Name => Family.DisplayName;
        public string Area => Family.Definition.Area;
        public string Details =>
            $"{VisibleFiles.Count} asset{(VisibleFiles.Count == 1 ? "" : "s")}"
            + (VisibleIniCount == 0 ? "" : $" · {VisibleIniCount} skin.ini setting(s)")
            + (Family.FontRoles.Count == 0 ? "" : $" · {string.Join(", ", Family.FontRoles)}");
        private int VisibleIniCount => Family.IniPatch.Count(entry => !lazerUsedOnly
            || SkinExtraLazerCompatibility.IsIniPatchUsed(Family.Definition.Id, entry));
        public string Diagnostics
        {
            get
            {
                var completeness = SkinExtraCompleteness.Analyze(
                    Family.Definition.Id,
                    VisibleFiles.Select(file => file.File.Filename));
                return completeness.IsComplete
                    ? ""
                    : $"Incomplete: missing {completeness.MissingSummary}. "
                      + "You can import it as-is or complete it from another pack when applying it.";
            }
        }

        public void SetLazerUsedOnly(bool value)
        {
            foreach (var file in Files) file.ShowCompatibilityBadge = !value;
            if (lazerUsedOnly == value) return;
            lazerUsedOnly = value;
            OnPropertyChanged(nameof(VisibleFiles));
            OnPropertyChanged(nameof(HasVisibleContent));
            OnPropertyChanged(nameof(Details));
            OnPropertyChanged(nameof(Diagnostics));
        }

        public SkinExtractionFamily BuildFamily(bool filterForLazer) => new()
        {
            Definition = Family.Definition,
            Variant = Family.Variant,
            Files = Files
                .Where(file => file.IsSelected)
                .Where(file => !filterForLazer
                               || file.Compatibility == SkinExtraCompatibility.LazerUsed)
                .Select(file => file.File)
                .ToArray(),
            IniPatch = Family.IniPatch
                .Where(entry => !filterForLazer
                                || SkinExtraLazerCompatibility.IsIniPatchUsed(
                                    Family.Definition.Id,
                                    entry))
                .ToArray(),
            FontRoles = Family.FontRoles,
        };

        public bool IsSelected
        {
            get => isSelected;
            set
            {
                if (isSelected == value) return;
                isSelected = value;
                OnPropertyChanged(nameof(IsSelected));
            }
        }

        public event PropertyChangedEventHandler? PropertyChanged;
        private void OnPropertyChanged(string name) =>
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }

    private sealed class FileChoice : INotifyPropertyChanged
    {
        private bool isSelected = true;
        private bool showCompatibilityBadge;

        public FileChoice(SkinExtractionFile file, string familyId)
        {
            File = file;
            Compatibility = SkinExtraLazerCompatibility.Classify(file.Filename, familyId);
        }
        public SkinExtractionFile File { get; }
        public SkinExtraCompatibility Compatibility { get; }
        public string Name => File.Filename;
        public string CompatibilityBadge => !showCompatibilityBadge
            || Compatibility == SkinExtraCompatibility.LazerUsed
                ? ""
                : Compatibility == SkinExtraCompatibility.StableOnly
                    ? "Stable only"
                    : "Unverified";
        public string Size => File.Bytes.Length < 1024
            ? $"{File.Bytes.Length} B"
            : $"{File.Bytes.Length / 1024d:0.#} KB";
        public bool IsSelected
        {
            get => isSelected;
            set
            {
                if (isSelected == value) return;
                isSelected = value;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsSelected)));
            }
        }

        public bool ShowCompatibilityBadge
        {
            get => showCompatibilityBadge;
            set
            {
                if (showCompatibilityBadge == value) return;
                showCompatibilityBadge = value;
                PropertyChanged?.Invoke(
                    this,
                    new PropertyChangedEventArgs(nameof(CompatibilityBadge)));
            }
        }

        public event PropertyChangedEventHandler? PropertyChanged;
    }
}
