using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Kumori.Core;
using Kumori.Core.Settings;
using Kumori.Tracking;
using Brush = System.Windows.Media.Brush;
using Color = System.Windows.Media.Color;
using Path = System.IO.Path;

namespace Kumori.App.Skins;

public partial class SkinEditorPage : UserControl
{
    private static readonly Color[] builtInSwatches =
    [
        Color.FromRgb(0xFF, 0xC0, 0x00),
        Color.FromRgb(0x00, 0xCA, 0x00),
        Color.FromRgb(0x12, 0x7C, 0xFF),
        Color.FromRgb(0xF2, 0x18, 0x39),
        Color.FromRgb(0x91, 0x84, 0xD9),
        Color.FromRgb(0xFF, 0x66, 0xAA),
        Color.FromRgb(0x00, 0xE5, 0xD0),
        Color.FromRgb(0xFF, 0xFF, 0xFF),
    ];

    private static List<(string Filename, byte[] Bytes)> elementClipboard = [];

    private readonly SettingsService settings;
    private readonly ILazerSkinRealmService realmService;
    private readonly List<FileSystemWatcher> externalWatchers = [];
    private readonly Dictionary<(string Section, string Key), IniRow> iniRows = [];
    private IReadOnlyList<LazerSkinInfo> allSkins = [];
    private IReadOnlyList<SkinElementCategory> categories = [];
    private LazerSkinCatalog? catalog;
    private LazerSkinInfo? currentSkin;
    private SkinElementEntry? selectedEntry;
    private LazerSkinFileInfo? iniFile;
    private SkinIniDocument? iniDocument;
    private bool initialized;
    private bool loading;
    private bool busy;
    private int busyDepth;
    private bool suppressSkinSelection;
    private bool suppressEditorEvents;
    private bool suppressRawEvents;
    private bool iniDirty;
    private bool rawDirty;
    private bool backupCreated;
    private string? backupRoot;
    private int gameplayRefreshVersion;
    private Color currentColor = Colors.White;
    private IniRow? activeIniColorRow;
    private bool colorPickerTargetsElement;
    private CancellationTokenSource? gameplayRenderCancellation;
    private SliderPreviewKey? cachedSliderPreviewKey;
    private BitmapSource? cachedSliderPreview;
    private IInputElement? focusBeforeBusy;

    public SkinEditorPage(SettingsService settings, ILazerSkinRealmService? realmService = null)
    {
        this.settings = settings;
        this.realmService = realmService ?? new LazerSkinRealmService();
        InitializeComponent();
        HideEmptyElementsToggle.IsChecked = settings.Current.SkinEditor.HideEmptyElements;
        IntegratedSkinColorPicker.ColourChanged += SkinColorPicker_ColourChanged;
        IntegratedSkinColorPicker.CloseRequested += () => SkinColorPickerPopup.IsOpen = false;
        BuildSwatches();
        SizeChanged += (_, _) => ApplyResponsiveLayout();
        Unloaded += (_, _) =>
        {
            gameplayRenderCancellation?.Cancel();
            DisposeExternalWatchers();
        };
        ApplyResponsiveLayout();
    }

    public async Task EnsureLoadedAsync()
    {
        if (initialized || loading)
            return;
        initialized = true;
        await LoadCatalogAsync();
    }

    private async Task LoadCatalogAsync(Guid? preferredSkin = null)
    {
        if (loading) return;
        loading = true;
        SetBusy(true, "Loading skins from osu!lazer…");
        try
        {
            var rootOverride = settings.Current.SkinEditor.LazerRootOverride;
            catalog = await Task.Run(() => realmService.LoadCatalog(rootOverride));
            allSkins = catalog.Skins;
            RootPathText.Text = catalog.RootPath;
            ApplySkinFilter(preferredSkin ?? currentSkin?.Id);
            StatusText.Text = allSkins.Count == 0
                ? "No imported skins were found in this lazer library."
                : $"Loaded {allSkins.Count:N0} lazer skin(s).";
        }
        catch (Exception ex)
        {
            catalog = null;
            allSkins = [];
            SkinList.ItemsSource = allSkins;
            CompactSkinPicker.ItemsSource = allSkins;
            RootPathText.Text = "osu!lazer library unavailable";
            StatusText.Text = ex.Message;
        }
        finally
        {
            loading = false;
            SetBusy(false);
        }
    }

    private void ApplySkinFilter(Guid? preferredSkin = null)
    {
        var query = SkinSearchBox.Text.Trim();
        var filtered = allSkins
            .Where(skin => query.Length == 0
                || skin.DisplayName.Contains(query, StringComparison.OrdinalIgnoreCase))
            .ToArray();

        suppressSkinSelection = true;
        SkinList.ItemsSource = filtered;
        CompactSkinPicker.ItemsSource = allSkins;
        var selection = filtered.FirstOrDefault(skin => skin.Id == preferredSkin)
            ?? filtered.FirstOrDefault();
        SkinList.SelectedItem = selection;
        CompactSkinPicker.SelectedItem = allSkins.FirstOrDefault(skin => skin.Id == selection?.Id);
        suppressSkinSelection = false;

        if (selection is not null && selection.Id != currentSkin?.Id)
            _ = SelectSkinAsync(selection);
    }

    private async Task SelectSkinAsync(LazerSkinInfo skin)
    {
        if (skin.Id == currentSkin?.Id) return;
        if (!await ResolveDirtyStateAsync())
        {
            RestoreSkinSelection();
            return;
        }

        currentSkin = skin;
        selectedEntry = null;
        ElementPreview.Source = null;
        ElementPreviewHint.Visibility = Visibility.Visible;
        ImageEditorControls.IsEnabled = false;
        GameplaySkinName.Text = skin.DisplayName;
        categories = SkinElementCategorizer.Categorize(skin.Files);
        CategoryPicker.ItemsSource = categories
            .Select(category => new CategoryChoice(category))
            .ToArray();
        CategoryPicker.SelectedIndex = categories.Count > 0 ? 0 : -1;
        await LoadSkinIniAsync();
        await RefreshGameplayPreviewAsync();
        StatusText.Text = $"Editing {skin.DisplayName}. Changes preview live and save explicitly.";
        UpdateDirtyState();
    }

    private async Task LoadSkinIniAsync()
    {
        if (catalog is null || currentSkin is null) return;
        iniFile = currentSkin.Files.FirstOrDefault(file =>
            file.Filename.Equals("skin.ini", StringComparison.OrdinalIgnoreCase));
        try
        {
            iniDocument = iniFile is null
                ? SkinIniDocument.Create(currentSkin.Name, currentSkin.Creator)
                : SkinIniDocument.Parse(
                    await Task.Run(() => realmService.ReadFile(catalog.RootPath, iniFile.Hash)));
            iniDirty = false;
            rawDirty = false;
            BuildIniForm();
            SetRawText(iniDocument.ToText());
            SaveIniButton.IsEnabled = true;
            UpdateComboStrip();
        }
        catch (Exception ex)
        {
            iniDocument = null;
            iniRows.Clear();
            IniFormPanel.Children.Clear();
            RawIniText.Text = "";
            SaveIniButton.IsEnabled = false;
            StatusText.Text = $"Could not load skin.ini: {ex.Message}";
        }
    }

    private void BuildIniForm()
    {
        IniFormPanel.Children.Clear();
        iniRows.Clear();
        if (iniDocument is null) return;

        foreach (var (section, definitions) in SkinIniSchema.Sections())
        {
            var sectionPanel = new StackPanel();
            foreach (var definition in definitions)
            {
                var rowGrid = new Grid { Margin = new Thickness(0, 2, 0, 2) };
                rowGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(26) });
                rowGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(205) });
                rowGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                rowGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

                var active = new CheckBox
                {
                    IsChecked = iniDocument.HasValue(definition.Section, definition.Key),
                    VerticalAlignment = VerticalAlignment.Center,
                    ToolTip = "Include this setting in skin.ini",
                };
                var label = new TextBlock
                {
                    Text = definition.Label,
                    VerticalAlignment = VerticalAlignment.Center,
                    TextWrapping = TextWrapping.Wrap,
                    Margin = new Thickness(4, 0, 9, 0),
                };
                var value = new TextBox
                {
                    Text = iniDocument.GetValue(definition.Section, definition.Key)
                        ?? definition.DefaultValue,
                    IsEnabled = active.IsChecked == true,
                    MinWidth = 120,
                    VerticalContentAlignment = VerticalAlignment.Center,
                };
                Grid.SetColumn(active, 0);
                Grid.SetColumn(label, 1);
                Grid.SetColumn(value, 2);
                rowGrid.Children.Add(active);
                rowGrid.Children.Add(label);
                rowGrid.Children.Add(value);

                Button? picker = null;
                Border? colorPreview = null;
                if (definition.Type is SkinIniValueType.Rgb or SkinIniValueType.Rgba)
                {
                    var initialColor = TryParseColor(value.Text, out var parsedColor)
                        ? parsedColor
                        : Colors.White;
                    colorPreview = new Border
                    {
                        Width = 22,
                        Height = 16,
                        CornerRadius = new CornerRadius(4),
                        BorderBrush = (Brush)FindResource("Brush.StrongBorder"),
                        BorderThickness = new Thickness(1),
                        Background = new SolidColorBrush(initialColor),
                    };
                    picker = new Button
                    {
                        Content = colorPreview,
                        Margin = new Thickness(6, 0, 0, 0),
                        Padding = new Thickness(4),
                        Width = 34,
                        Height = 28,
                        IsEnabled = active.IsChecked == true,
                        ToolTip = $"Choose {definition.Label.ToLowerInvariant()}",
                    };
                    Grid.SetColumn(picker, 3);
                    rowGrid.Children.Add(picker);
                }

                var row = new IniRow(definition, active, value, picker, colorPreview);
                iniRows[(definition.Section, definition.Key)] = row;
                active.Checked += (_, _) => IniActiveChanged(row);
                active.Unchecked += (_, _) => IniActiveChanged(row);
                value.TextChanged += (_, _) => IniValueChanged(row);
                if (picker is not null)
                    picker.Click += (_, _) => PickIniColor(row);
                sectionPanel.Children.Add(rowGrid);
            }

            IniFormPanel.Children.Add(new GroupBox
            {
                Header = section,
                Content = sectionPanel,
                Margin = new Thickness(0, 0, 0, 9),
                Padding = new Thickness(8),
            });
        }

        IniFormPanel.Children.Add(new TextBlock
        {
            Text = "Use Raw mode to edit repeated [Mania] sections, comments, unknown keys, and future settings.",
            Foreground = (Brush)FindResource("Brush.TextMuted"),
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(2, 2, 2, 8),
        });
    }

    private void IniActiveChanged(IniRow row)
    {
        if (suppressEditorEvents) return;
        row.Value.IsEnabled = row.Active.IsChecked == true;
        if (row.Picker is not null)
            row.Picker.IsEnabled = row.Active.IsChecked == true;
        ApplyFormRowsToDocument(validate: false);
    }

    private void IniValueChanged(IniRow row)
    {
        if (suppressEditorEvents || row.Active.IsChecked != true) return;
        UpdateIniColorPreview(row);
        ApplyFormRowsToDocument(validate: false);
    }

    private bool ApplyFormRowsToDocument(bool validate)
    {
        if (iniDocument is null) return false;
        foreach (var row in iniRows.Values)
        {
            if (row.Active.IsChecked != true)
            {
                iniDocument.RemoveValue(row.Definition.Section, row.Definition.Key);
                continue;
            }

            var value = row.Value.Text.Trim();
            if (validate
                && !SkinIniDocument.TryValidate(row.Definition, value, out var error))
            {
                StatusText.Text = $"{row.Definition.Label}: {error}";
                row.Value.Focus();
                row.Value.SelectAll();
                return false;
            }

            iniDocument.SetValue(row.Definition.Section, row.Definition.Key, value);
        }

        iniDirty = true;
        rawDirty = false;
        SetRawText(iniDocument.ToText());
        UpdateComboStrip();
        UpdateDirtyState();
        _ = RefreshGameplayPreviewAsync();
        return true;
    }

    private void SetRawText(string text)
    {
        suppressRawEvents = true;
        RawIniText.Text = text;
        suppressRawEvents = false;
    }

    private void RawIniText_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (suppressRawEvents || iniDocument is null) return;
        rawDirty = true;
        iniDirty = true;
        UpdateDirtyState();
    }

    private void IniModeTabs_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!ReferenceEquals(e.Source, IniModeTabs) || IniModeTabs.SelectedIndex != 0
            || !rawDirty || iniDocument is null)
            return;
        iniDocument = iniDocument.WithText(RawIniText.Text);
        rawDirty = false;
        BuildIniForm();
        UpdateComboStrip();
        _ = RefreshGameplayPreviewAsync();
    }

    private async Task<bool> SaveIniAsync()
    {
        if (catalog is null || currentSkin is null || iniDocument is null)
            return false;
        if (rawDirty)
        {
            iniDocument = iniDocument.WithText(RawIniText.Text);
            rawDirty = false;
        }
        else if (!ApplyFormRowsToDocument(validate: true))
        {
            return false;
        }

        if (!await EnsureBackupAsync()) return false;
        SetBusy(true, "Saving skin.ini…");
        try
        {
            var bytes = iniDocument.ToBytes();
            LazerSkinWriteResult result;
            if (iniFile is null)
            {
                result = await Task.Run(() => realmService.AddOrReplaceFile(
                    catalog.RootPath,
                    currentSkin.Id,
                    "skin.ini",
                    bytes,
                    expectedHash: null));
            }
            else
            {
                result = await Task.Run(() => realmService.CommitFile(
                    catalog.RootPath,
                    currentSkin.Id,
                    iniFile.Filename,
                    bytes,
                    iniFile.Hash));
            }

            if (!HandleWriteResult(result, "skin.ini")) return false;
            iniFile = new LazerSkinFileInfo("skin.ini", result.Hash, bytes.LongLength);
            iniDirty = false;
            StatusText.Text = result.Status == LazerSkinWriteStatus.Unchanged
                ? "skin.ini is unchanged."
                : "skin.ini saved to osu!lazer.";
            UpdateDirtyState();
            return true;
        }
        catch (Exception ex)
        {
            StatusText.Text = $"Could not save skin.ini: {ex.Message}";
            return false;
        }
        finally
        {
            SetBusy(false);
        }
    }

    private async Task ShowCategoryAsync(SkinElementCategory? category)
    {
        if (category is null)
        {
            ElementList.ItemsSource = Array.Empty<SkinElementEntry>();
            return;
        }

        var hideEmpty = HideEmptyElementsToggle.IsChecked == true;
        if (!hideEmpty)
            ElementList.ItemsSource = category.Files;

        foreach (var logicalEntry in category.Files)
        {
            var entriesToLoad = hideEmpty
                ? logicalEntry.PhysicalEntries
                : [logicalEntry];
            foreach (var entry in entriesToLoad.Where(entry => entry.IsImage && entry.Thumbnail is null))
            {
                try
                {
                    await EnsureEntryLoadedAsync(entry);
                }
                catch
                {
                    // Keep rendering the remaining cards.
                }
            }
        }

        if (hideEmpty)
        {
            var visible = category.Files.Where(entry => !entry.IsLogicallyEmpty).ToArray();
            ElementList.ItemsSource = visible;
            var hidden = category.Files.Count - visible.Length;
            if (hidden > 0)
                StatusText.Text = $"Hidden {hidden:N0} fully transparent element{(hidden == 1 ? "" : "s")}.";
        }
    }

    private async Task EnsureEntryLoadedAsync(SkinElementEntry entry)
    {
        if (entry.OriginalPixels is not null || catalog is null)
            return;
        var bytes = await Task.Run(() => realmService.ReadFile(catalog.RootPath, entry.Hash));
        var bitmap = SkinImageTools.Decode(bytes);
        entry.OriginalBytes = bytes;
        entry.OriginalPixels = SkinImageTools.Pixels(bitmap, out var stride);
        entry.HasVisiblePixels = SkinImageTools.HasVisiblePixels(entry.OriginalPixels);
        entry.Stride = stride;
        entry.PixelWidth = bitmap.PixelWidth;
        entry.PixelHeight = bitmap.PixelHeight;
        entry.Thumbnail = SkinImageTools.Render(entry);
    }

    private async Task SelectEntryAsync(SkinElementEntry entry)
    {
        selectedEntry = entry;
        SelectedElementName.Text = entry.Filename;
        SelectedElementMeta.Text = entry.HasPairedResolution
            ? $"{FormatSize(entry.TotalSizeBytes)} · 1× + 2× files · edits save to both"
            : $"{FormatSize(entry.File.SizeBytes)} · {entry.Hash[..Math.Min(10, entry.Hash.Length)]}";
        if (!entry.IsImage)
        {
            ElementPreview.Source = null;
            ElementPreviewHint.Text = entry.IsAudio
                ? "Audio file — use Open externally"
                : "This file is not an image";
            ElementPreviewHint.Visibility = Visibility.Visible;
            ImageEditorControls.IsEnabled = true;
            RecolorModePicker.IsEnabled = false;
            TargetColorPanel.IsEnabled = false;
            HueShiftPanel.IsEnabled = false;
            SaveElementButton.IsEnabled = false;
            ResetElementButton.IsEnabled = false;
            ExportElementButton.IsEnabled = false;
            OpenExternallyButton.IsEnabled = true;
            return;
        }

        try
        {
            await EnsureEntryLoadedAsync(entry);
            suppressEditorEvents = true;
            RecolorModePicker.SelectedIndex = entry.Mode switch
            {
                SkinRecolorMode.Colorize => 0,
                SkinRecolorMode.Tint => 1,
                _ => 2,
            };
            SetCurrentColor(entry.TintColor ?? Colors.White, updateEntry: false);
            HueShiftSlider.Value = entry.HueShiftDegrees;
            SaturationShiftSlider.Value = entry.SaturationMultiplier;
            LightnessShiftSlider.Value = entry.LightnessMultiplier;
            suppressEditorEvents = false;
            UpdateModePanels();
            RenderSelectedEntry();
            ImageEditorControls.IsEnabled = true;
            RecolorModePicker.IsEnabled = true;
            TargetColorPanel.IsEnabled = true;
            HueShiftPanel.IsEnabled = true;
            SaveElementButton.IsEnabled = true;
            ResetElementButton.IsEnabled = true;
            ExportElementButton.IsEnabled = true;
            OpenExternallyButton.IsEnabled = true;
            if (ReferenceEquals(InspectorPanel.Parent, CompactInspectorHost))
                WorkspaceTabs.SelectedItem = CompactEditorTab;
        }
        catch (Exception ex)
        {
            suppressEditorEvents = false;
            ImageEditorControls.IsEnabled = false;
            ElementPreviewHint.Text = "Could not decode this image";
            ElementPreviewHint.Visibility = Visibility.Visible;
            StatusText.Text = ex.Message;
        }
    }

    private void RenderSelectedEntry()
    {
        if (selectedEntry?.OriginalPixels is null) return;
        selectedEntry.SynchronizeEditsToVariants();
        selectedEntry.Thumbnail = SkinImageTools.Render(selectedEntry);
        selectedEntry.RaiseStateChanged();
        ElementPreview.Source = selectedEntry.Thumbnail;
        ElementPreviewHint.Visibility = Visibility.Collapsed;
        SelectedElementMeta.Text =
            $"{FormatSize(selectedEntry.TotalSizeBytes)}"
            + (selectedEntry.HasPairedResolution ? " · 1× + 2× files · edits save to both" : "")
            + (selectedEntry.HasEdits ? " · edited (unsaved)" : "");
        UpdateDirtyState();
        _ = RefreshGameplayPreviewAsync();
    }

    private async Task<bool> SaveEntryAsync(SkinElementEntry entry)
    {
        if (!entry.HasEdits || catalog is null || currentSkin is null)
            return true;
        if (!await EnsureBackupAsync()) return false;
        entry.SynchronizeEditsToVariants();
        foreach (var physicalEntry in entry.PhysicalEntries)
        {
            await EnsureEntryLoadedAsync(physicalEntry);
            var bitmap = SkinImageTools.Render(physicalEntry);
            var bytes = SkinImageTools.Encode(bitmap, physicalEntry.Filename);
            var result = await Task.Run(() => realmService.CommitFile(
                catalog.RootPath,
                currentSkin.Id,
                physicalEntry.Filename,
                bytes,
                physicalEntry.Hash));
            if (!HandleWriteResult(result, physicalEntry.Filename))
                return false;

            var replacement = new LazerSkinFileInfo(
                physicalEntry.Filename,
                result.Hash,
                bytes.LongLength);
            physicalEntry.ReplaceFile(replacement);
            var decoded = SkinImageTools.Decode(bytes);
            physicalEntry.OriginalBytes = bytes;
            physicalEntry.OriginalPixels = SkinImageTools.Pixels(decoded, out var stride);
            physicalEntry.HasVisiblePixels =
                SkinImageTools.HasVisiblePixels(physicalEntry.OriginalPixels);
            physicalEntry.Stride = stride;
            physicalEntry.PixelWidth = decoded.PixelWidth;
            physicalEntry.PixelHeight = decoded.PixelHeight;
            physicalEntry.Thumbnail = decoded;
        }

        entry.Reset();
        if (ReferenceEquals(entry, selectedEntry))
            await SelectEntryAsync(entry);
        return true;
    }

    private async Task<bool> SaveAllAsync()
    {
        if (currentSkin is null) return true;
        SetBusy(true, "Saving pending skin changes…");
        try
        {
            foreach (var entry in categories.SelectMany(category => category.Files)
                         .Where(entry => entry.HasEdits).ToArray())
            {
                if (!await SaveEntryAsync(entry))
                    return false;
            }

            if (iniDirty && !await SaveIniAsync())
                return false;
            StatusText.Text = "All pending skin changes are saved.";
            UpdateDirtyState();
            return true;
        }
        catch (Exception ex)
        {
            StatusText.Text = $"Could not save all changes: {ex.Message}";
            return false;
        }
        finally
        {
            SetBusy(false);
        }
    }

    private bool HandleWriteResult(LazerSkinWriteResult result, string filename)
    {
        if (result.Status == LazerSkinWriteStatus.Conflict)
        {
            StatusText.Text = $"{filename}: {result.Message}";
            return false;
        }
        if (result.Status == LazerSkinWriteStatus.Missing)
        {
            StatusText.Text = result.Message ?? $"{filename} no longer exists.";
            return false;
        }
        return true;
    }

    private async Task<bool> EnsureBackupAsync(string? requestedRoot = null)
    {
        var rootPath = requestedRoot ?? catalog?.RootPath;
        if (rootPath is null) return false;
        if (backupCreated && string.Equals(backupRoot, rootPath, StringComparison.OrdinalIgnoreCase))
            return true;
        try
        {
            StatusText.Text = "Creating a Realm restore point before the first write…";
            var path = await Task.Run(() =>
                realmService.CreateBackup(rootPath, AppPaths.LazerSkinBackupsDir));
            backupCreated = true;
            backupRoot = rootPath;
            PruneBackups();
            StatusText.Text = $"Realm restore point created: {Path.GetFileName(path)}";
            return true;
        }
        catch (Exception ex)
        {
            StatusText.Text = $"The write was cancelled because the Realm backup failed: {ex.Message}";
            KumoriDialog.Show(
                Window.GetWindow(this),
                StatusText.Text,
                "Skin editor backup failed",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
            return false;
        }
    }

    private void PruneBackups()
    {
        try
        {
            var keep = Math.Clamp(settings.Current.Backup.RetentionCount, 1, 365);
            foreach (var file in Directory.EnumerateFiles(AppPaths.LazerSkinBackupsDir, "client.realm.*.realm")
                         .Select(path => new FileInfo(path))
                         .OrderByDescending(file => file.CreationTimeUtc)
                         .Skip(keep))
                file.Delete();
        }
        catch
        {
            // Backup pruning is best-effort and never invalidates the new restore point.
        }
    }

    private async Task<bool> ResolveDirtyStateAsync()
    {
        if (!HasDirtyChanges) return true;
        var result = KumoriDialog.Show(
            Window.GetWindow(this),
            "This skin has unsaved changes.\n\nYes: save all\nNo: discard them\nCancel: stay on this skin",
            "Unsaved skin changes",
            MessageBoxButton.YesNoCancel,
            MessageBoxImage.Warning,
            MessageBoxResult.Cancel);
        if (result == MessageBoxResult.Cancel) return false;
        if (result == MessageBoxResult.Yes)
            return await SaveAllAsync();
        DiscardAllChanges();
        return true;
    }

    private void DiscardAllChanges()
    {
        foreach (var entry in categories.SelectMany(category => category.Files))
        {
            if (!entry.HasEdits) continue;
            entry.Reset();
            foreach (var physicalEntry in entry.PhysicalEntries)
                if (physicalEntry.OriginalPixels is not null)
                    physicalEntry.Thumbnail = SkinImageTools.Render(physicalEntry);
        }
        iniDirty = false;
        rawDirty = false;
    }

    private bool HasDirtyChanges =>
        iniDirty || categories.SelectMany(category => category.Files).Any(entry => entry.HasEdits);

    private void UpdateDirtyState()
    {
        var count = categories.SelectMany(category => category.Files).Count(entry => entry.HasEdits)
            + (iniDirty ? 1 : 0);
        SaveAllButton.IsEnabled = count > 0 && !busy;
        SaveAllButton.Content = count > 0 ? $"Save all ({count})" : "Save all";
    }

    private void UpdateModePanels()
    {
        var hueMode = selectedEntry?.Mode == SkinRecolorMode.HueSaturation;
        TargetColorPanel.Visibility = hueMode ? Visibility.Collapsed : Visibility.Visible;
        HueShiftPanel.Visibility = hueMode ? Visibility.Visible : Visibility.Collapsed;
    }

    private void SetCurrentColor(Color color, bool updateEntry = true)
    {
        currentColor = color;
        suppressEditorEvents = true;
        CurrentColorSwatch.Background = new SolidColorBrush(color);
        HexColorBox.Text = $"#{color.R:X2}{color.G:X2}{color.B:X2}";
        suppressEditorEvents = false;
        if (updateEntry && selectedEntry is not null)
        {
            selectedEntry.TintColor = color;
            RenderSelectedEntry();
        }
    }

    private void BuildSwatches()
    {
        SwatchPanel.Children.Clear();
        foreach (var color in builtInSwatches)
            SwatchPanel.Children.Add(CreateSwatch(color, custom: false, null));
        foreach (var hex in settings.Current.SkinEditor.CustomSwatches.ToArray())
        {
            if (TryParseColor(hex, out var color))
                SwatchPanel.Children.Add(CreateSwatch(color, custom: true, hex));
        }
    }

    private FrameworkElement CreateSwatch(Color color, bool custom, string? hex)
    {
        var button = new Button
        {
            Width = 27,
            Height = 27,
            Margin = new Thickness(0, 0, 5, 5),
            Padding = new Thickness(0),
            Background = new SolidColorBrush(color),
            ToolTip = custom ? $"{hex} · right-click to delete" : $"#{color.R:X2}{color.G:X2}{color.B:X2}",
        };
        button.Click += (_, _) => SetCurrentColor(color);
        if (custom)
        {
            button.MouseRightButtonUp += (_, args) =>
            {
                settings.Update(value => value.SkinEditor.CustomSwatches.RemoveAll(
                    item => item.Equals(hex, StringComparison.OrdinalIgnoreCase)));
                BuildSwatches();
                args.Handled = true;
            };
        }
        return button;
    }

    private void PickIniColor(IniRow row)
    {
        var initial = TryParseColor(row.Value.Text, out var parsed) ? parsed : Colors.White;
        activeIniColorRow = row;
        colorPickerTargetsElement = false;
        IntegratedSkinColorPicker.Open(
            ToPickerHex(initial, row.Definition.Type == SkinIniValueType.Rgba),
            row.Definition.Label,
            $"Edits [{row.Definition.Section}] {row.Definition.Key} and updates the gameplay preview live.",
            allowOpacity: row.Definition.Type == SkinIniValueType.Rgba);
        SkinColorPickerPopup.PlacementTarget = row.Picker;
        SkinColorPickerPopup.IsOpen = true;
    }

    private void SkinColorPicker_ColourChanged(string value)
    {
        if (!TryParseColor(value, out var color))
            return;

        if (colorPickerTargetsElement)
        {
            SetCurrentColor(Color.FromRgb(color.R, color.G, color.B));
            return;
        }

        if (activeIniColorRow is not { } row)
            return;

        row.Value.Text = row.Definition.Type == SkinIniValueType.Rgba
            ? $"{color.R},{color.G},{color.B},{color.A}"
            : $"{color.R},{color.G},{color.B}";
        UpdateIniColorPreview(row);
    }

    private static string ToPickerHex(Color color, bool includeAlpha) =>
        includeAlpha
            ? $"#{color.A:X2}{color.R:X2}{color.G:X2}{color.B:X2}"
            : $"#{color.R:X2}{color.G:X2}{color.B:X2}";

    private static void UpdateIniColorPreview(IniRow row)
    {
        if (row.ColorPreview is null || !TryParseColor(row.Value.Text, out var color))
            return;
        row.ColorPreview.Background = new SolidColorBrush(color);
    }

    private async Task RefreshGameplayPreviewAsync()
    {
        var version = ++gameplayRefreshVersion;
        gameplayRenderCancellation?.Cancel();
        if (currentSkin is null)
        {
            gameplayRenderCancellation = null;
            return;
        }
        var cancellation = gameplayRenderCancellation = new CancellationTokenSource();
        var cancellationToken = cancellation.Token;
        try
        {
            var background = await FindAndLoadAsync("skin-banner", "skin-preview", "menu-background");
            var circle = await FindAndLoadAsync("hitcircle");
            var overlay = await FindAndLoadAsync("hitcircleoverlay");
            var number = await FindAndLoadAsync("default-1");
            var approach = await FindAndLoadAsync("approachcircle");
            var ball = await FindAndLoadAsync("sliderb0", "sliderb", "sliderball");
            var followCircle = await FindAndLoadAsync("sliderfollowcircle");
            var reverseArrow = await FindAndLoadAsync("reversearrow");
            var cursor = await FindAndLoadAsync("cursor");
            var cursorTrail = await FindAndLoadAsync("cursortrail");
            var scorebar = await FindAndLoadAsync("scorebar-bg");
            var scorebarMarker = await FindAndLoadAsync("scorebar-ki", "scorebar-marker");
            if (version != gameplayRefreshVersion) return;

            var combos = Enumerable.Range(1, 8)
                .Select(index => ReadIniColor("Colours", $"Combo{index}"))
                .Where(color => color.HasValue)
                .Select(color => color!.Value)
                .ToArray();
            var comboHead = combos.Length > 0 ? combos[0] : builtInSwatches[0];
            var comboTail = combos.Length > 1 ? combos[1] : comboHead;
            var sliderBorder = ReadIniColor("Colours", "SliderBorder");
            var sliderTrack = ReadIniColor("Colours", "SliderTrackOverride");
            var sliderKey = new SliderPreviewKey(comboTail, sliderBorder, sliderTrack);
            BitmapSource sliderBody;
            if (cachedSliderPreviewKey == sliderKey && cachedSliderPreview is not null)
            {
                sliderBody = cachedSliderPreview;
            }
            else
            {
                sliderBody = await Task.Run(
                    () => LegacySliderRenderer.Render(
                        880,
                        505,
                        LegacySliderRenderer.SampleSCurve(190, 310, 670, 180),
                        50,
                        comboTail,
                        sliderBorder,
                        sliderTrack,
                        cancellationToken),
                    cancellationToken);
                cancellationToken.ThrowIfCancellationRequested();
                if (version != gameplayRefreshVersion)
                    return;
                cachedSliderPreviewKey = sliderKey;
                cachedSliderPreview = sliderBody;
            }

            GameplayBackground.Source = background?.Thumbnail;
            GameplaySliderBody.Source = sliderBody;
            GameplayHitcircle.Source = Tinted(circle, comboHead);
            GameplayOverlay.Source = overlay?.Thumbnail;
            GameplayNumber.Source = number?.Thumbnail;
            GameplayApproach.Source = Tinted(approach, comboHead);
            GameplayTailCircle.Source = Tinted(circle, comboTail);
            GameplayTailOverlay.Source = overlay?.Thumbnail;
            GameplayReverseArrow.Source = reverseArrow?.Thumbnail;
            GameplayFollowCircle.Source = followCircle?.Thumbnail;
            var allowBallTint = iniDocument?.GetValue("General", "AllowSliderBallTint") == "1";
            GameplaySliderBall.Source = allowBallTint ? Tinted(ball, comboTail) : ball?.Thumbnail;
            GameplayCursor.Source = cursor?.Thumbnail;
            GameplayCursorTrail.Source = cursorTrail?.Thumbnail;
            GameplayScorebar.Source = scorebar?.Thumbnail;
            GameplayScorebarMarker.Source = scorebarMarker?.Thumbnail;
            GameplayBackground.Visibility = SkinBackgroundToggle.IsChecked == true
                ? Visibility.Visible
                : Visibility.Collapsed;
        }
        catch (OperationCanceledException)
        {
            // A newer live preview superseded this render.
        }
        catch
        {
            // The scene is best-effort; individual unavailable elements remain blank.
        }
        finally
        {
            if (ReferenceEquals(gameplayRenderCancellation, cancellation))
                gameplayRenderCancellation = null;
            cancellation.Dispose();
        }
    }

    private async Task<SkinElementEntry?> FindAndLoadAsync(params string[] stems)
    {
        var entry = categories.SelectMany(category => category.Files)
            .FirstOrDefault(file =>
            {
                var name = Path.GetFileNameWithoutExtension(file.Filename);
                if (name.EndsWith("@2x", StringComparison.OrdinalIgnoreCase))
                    name = name[..^3];
                return stems.Any(stem => name.Equals(stem, StringComparison.OrdinalIgnoreCase));
            });
        if (entry is not null && entry.IsImage)
            await EnsureEntryLoadedAsync(entry);
        return entry;
    }

    private static BitmapSource? Tinted(SkinElementEntry? entry, Color color)
    {
        if (entry?.OriginalPixels is null) return entry?.Thumbnail;
        var pixels = SkinImageTools.RenderPixels(entry);
        SkinImageTools.ApplyMultiplicativeTint(pixels, color);
        var bitmap = SkinImageTools.ToBitmap(pixels, entry.PixelWidth, entry.PixelHeight, entry.Stride);
        bitmap.Freeze();
        return bitmap;
    }

    private Color? ReadIniColor(string section, string key)
    {
        if (iniDocument is null) return null;
        if (rawDirty)
            iniDocument = iniDocument.WithText(RawIniText.Text);
        var raw = iniDocument.GetValue(section, key);
        return TryParseColor(raw, out var color) ? color : null;
    }

    private void UpdateComboStrip()
    {
        ComboStrip.Children.Clear();
        for (var index = 1; index <= 8; index++)
        {
            var raw = iniDocument?.GetValue("Colours", $"Combo{index}");
            if (!TryParseColor(raw, out var color)) continue;
            ComboStrip.Children.Add(new Border
            {
                Width = 20,
                Height = 20,
                Margin = new Thickness(0, 0, 5, 0),
                CornerRadius = new CornerRadius(10),
                Background = new SolidColorBrush(color),
                BorderBrush = (Brush)FindResource("Brush.SubtleBorder"),
                BorderThickness = new Thickness(1),
                ToolTip = $"Combo{index}: {raw}",
            });
        }
    }

    private async Task ImportPathsAsync(IEnumerable<string> paths)
    {
        var files = paths.Where(File.Exists)
            .Select(path => (Path.GetFileName(path), File.ReadAllBytes(path)))
            .ToArray();
        await ImportBytesAsync(files);
    }

    private async Task ImportBytesAsync(IEnumerable<(string Filename, byte[] Bytes)> files)
    {
        if (catalog is null || currentSkin is null) return;
        var batch = files.ToArray();
        if (batch.Length == 0) return;
        if (!await EnsureBackupAsync()) return;
        SetBusy(true, $"Importing {batch.Length} file(s)…");
        var changed = 0;
        try
        {
            foreach (var (filename, bytes) in batch)
            {
                var existing = categories.SelectMany(category => category.Files)
                        .FirstOrDefault(file => file.Filename.Equals(filename, StringComparison.OrdinalIgnoreCase))
                        ?.File
                    ?? (iniFile?.Filename.Equals(filename, StringComparison.OrdinalIgnoreCase) == true
                        ? iniFile
                        : null);
                var result = await Task.Run(() => realmService.AddOrReplaceFile(
                    catalog.RootPath,
                    currentSkin.Id,
                    filename,
                    bytes,
                    existing?.Hash));
                if (HandleWriteResult(result, filename) && result.Changed)
                    changed++;
            }
            StatusText.Text = $"Imported {changed} file(s).";
            await ReloadCurrentSkinAsync();
        }
        catch (Exception ex)
        {
            StatusText.Text = $"Import failed: {ex.Message}";
        }
        finally
        {
            SetBusy(false);
        }
    }

    private async Task ReloadCurrentSkinAsync()
    {
        var id = currentSkin?.Id;
        currentSkin = null;
        await LoadCatalogAsync(id);
    }

    private async Task StartExternalEditAsync(SkinElementEntry entry)
    {
        if (catalog is null || currentSkin is null) return;
        var rootPath = catalog.RootPath;
        var skinId = currentSkin.Id;
        if (entry.IsImage)
            await EnsureEntryLoadedAsync(entry);
        else if (entry.OriginalBytes is null)
            entry.OriginalBytes = await Task.Run(() => realmService.ReadFile(rootPath, entry.Hash));
        var directory = Path.Combine(
            AppPaths.RuntimeDir,
            "skin-editor",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, Path.GetFileName(entry.Filename));
        var initial = entry.IsImage && entry.HasEdits
            ? SkinImageTools.Encode(SkinImageTools.Render(entry), entry.Filename)
            : entry.OriginalBytes!;
        await File.WriteAllBytesAsync(path, initial);

        var watcher = new FileSystemWatcher(directory, Path.GetFileName(path))
        {
            NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.Size,
            EnableRaisingEvents = true,
        };
        long generation = 0;
        watcher.Changed += (_, _) =>
        {
            var currentGeneration = Interlocked.Increment(ref generation);
            Dispatcher.InvokeAsync(async () =>
            {
                await Task.Delay(300);
                if (currentGeneration != Interlocked.Read(ref generation)) return;
                byte[]? bytes = null;
                for (var attempt = 0; attempt < 5 && bytes is null; attempt++)
                {
                    try { bytes = await File.ReadAllBytesAsync(path); }
                    catch (IOException) { await Task.Delay(120); }
                }
                if (bytes is null || !await EnsureBackupAsync(rootPath)) return;
                var result = await Task.Run(() => realmService.CommitFile(
                    rootPath,
                    skinId,
                    entry.Filename,
                    bytes,
                    entry.Hash));
                if (!HandleWriteResult(result, entry.Filename)) return;
                var replacement = new LazerSkinFileInfo(entry.Filename, result.Hash, bytes.LongLength);
                entry.ReplaceFile(replacement);
                entry.OriginalBytes = bytes;
                entry.Reset();
                if (entry.IsImage)
                {
                    var decoded = SkinImageTools.Decode(bytes);
                    entry.OriginalPixels = SkinImageTools.Pixels(decoded, out var stride);
                    entry.HasVisiblePixels = SkinImageTools.HasVisiblePixels(entry.OriginalPixels);
                    entry.Stride = stride;
                    entry.PixelWidth = decoded.PixelWidth;
                    entry.PixelHeight = decoded.PixelHeight;
                    entry.Thumbnail = decoded;
                }
                if (ReferenceEquals(entry, selectedEntry))
                    await SelectEntryAsync(entry);
                StatusText.Text = $"External save synced: {entry.Filename}";
                await RefreshGameplayPreviewAsync();
            });
        };
        externalWatchers.Add(watcher);
        Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
        StatusText.Text = $"Opened {entry.Filename} externally. Saves auto-sync to osu!lazer.";
    }

    private void DisposeExternalWatchers()
    {
        foreach (var watcher in externalWatchers)
            watcher.Dispose();
        externalWatchers.Clear();
    }

    private void ApplyResponsiveLayout()
    {
        if (ActualWidth <= 0) return;
        var compact = ActualWidth <= 940;
        var standard = ActualWidth is > 940 and <= 1220;
        SkinListColumn.Width = compact ? new GridLength(0) : new GridLength(standard ? 190 : 230);
        SkinListPanel.Visibility = compact ? Visibility.Collapsed : Visibility.Visible;
        CompactSkinPicker.Visibility = compact ? Visibility.Visible : Visibility.Collapsed;
        CompactSkinPicker.Width = ActualWidth <= 760 ? 155 : 210;
        ChangeFolderButton.Visibility = compact ? Visibility.Collapsed : Visibility.Visible;
        RefreshButton.Visibility = compact ? Visibility.Collapsed : Visibility.Visible;
        BackupButton.Visibility = compact ? Visibility.Collapsed : Visibility.Visible;
        CompactActionsButton.Visibility = compact ? Visibility.Visible : Visibility.Collapsed;
        if (compact && ReferenceEquals(InspectorPanel.Parent, EditorGrid))
        {
            EditorGrid.Children.Remove(InspectorPanel);
            CompactInspectorHost.Content = InspectorPanel;
        }
        else if (!compact && ReferenceEquals(InspectorPanel.Parent, CompactInspectorHost))
        {
            CompactInspectorHost.Content = null;
            EditorGrid.Children.Add(InspectorPanel);
            Grid.SetColumn(InspectorPanel, 4);
        }
        CompactEditorTab.Visibility = compact ? Visibility.Visible : Visibility.Collapsed;
        InspectorColumn.Width = compact ? new GridLength(0) : new GridLength(standard ? 270 : 310);
        GameplayRow.Height = new GridLength(ActualHeight <= 610 ? 150 : 230);
    }

    private void RestoreSkinSelection()
    {
        suppressSkinSelection = true;
        SkinList.SelectedItem = SkinList.Items.Cast<LazerSkinInfo>()
            .FirstOrDefault(skin => skin.Id == currentSkin?.Id);
        CompactSkinPicker.SelectedItem = allSkins.FirstOrDefault(skin => skin.Id == currentSkin?.Id);
        suppressSkinSelection = false;
    }

    private void SetBusy(bool busy, string? status = null)
    {
        if (status is not null) StatusText.Text = status;
        if (busy)
        {
            if (busyDepth++ == 0)
            {
                focusBeforeBusy = Keyboard.FocusedElement;
                BusyInputShield.Visibility = Visibility.Visible;
                BusyInputShield.Focus();
            }
        }
        else
        {
            busyDepth = Math.Max(0, busyDepth - 1);
            if (busyDepth == 0)
            {
                BusyInputShield.Visibility = Visibility.Collapsed;
                if (focusBeforeBusy is not null)
                    Keyboard.Focus(focusBeforeBusy);
                focusBeforeBusy = null;
            }
        }
        this.busy = busyDepth > 0;
        UpdateDirtyState();
    }

    private static string FormatSize(long bytes) => bytes switch
    {
        >= 1_048_576 => $"{bytes / 1_048_576d:0.0} MB",
        >= 1024 => $"{bytes / 1024d:0.0} KB",
        _ => $"{bytes:N0} B",
    };

    private static bool TryParseColor(string? raw, out Color color)
    {
        color = Colors.White;
        if (string.IsNullOrWhiteSpace(raw)) return false;
        var value = raw.Trim();
        if (value.StartsWith('#'))
        {
            var hasAlpha = value.Length == 9;
            var offset = hasAlpha ? 3 : 1;
            if (value.Length is not (7 or 9)
                || (hasAlpha
                    && !byte.TryParse(value[1..3], NumberStyles.HexNumber, null, out _))
                || !byte.TryParse(value[offset..(offset + 2)], NumberStyles.HexNumber, null, out var red)
                || !byte.TryParse(value[(offset + 2)..(offset + 4)], NumberStyles.HexNumber, null, out var green)
                || !byte.TryParse(value[(offset + 4)..(offset + 6)], NumberStyles.HexNumber, null, out var blue))
                return false;
            var alpha = hasAlpha
                ? byte.Parse(value[1..3], NumberStyles.HexNumber, CultureInfo.InvariantCulture)
                : byte.MaxValue;
            color = Color.FromArgb(alpha, red, green, blue);
            return true;
        }
        var parts = value.Split(',', StringSplitOptions.TrimEntries);
        if (parts.Length < 3
            || !byte.TryParse(parts[0], out var r)
            || !byte.TryParse(parts[1], out var g)
            || !byte.TryParse(parts[2], out var b))
            return false;
        var a = parts.Length >= 4 && byte.TryParse(parts[3], out var parsedAlpha)
            ? parsedAlpha
            : byte.MaxValue;
        color = Color.FromArgb(a, r, g, b);
        return true;
    }

    private async void SkinList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!suppressSkinSelection && SkinList.SelectedItem is LazerSkinInfo skin)
        {
            CompactSkinPicker.SelectedItem = allSkins.FirstOrDefault(value => value.Id == skin.Id);
            await SelectSkinAsync(skin);
        }
    }

    private async void CompactSkinPicker_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!suppressSkinSelection && CompactSkinPicker.SelectedItem is LazerSkinInfo skin)
        {
            SkinList.SelectedItem = SkinList.Items.Cast<LazerSkinInfo>()
                .FirstOrDefault(value => value.Id == skin.Id);
            await SelectSkinAsync(skin);
        }
    }

    private void SkinSearchBox_TextChanged(object sender, TextChangedEventArgs e) =>
        ApplySkinFilter(currentSkin?.Id);

    private async void CategoryPicker_SelectionChanged(object sender, SelectionChangedEventArgs e) =>
        await ShowCategoryAsync((CategoryPicker.SelectedItem as CategoryChoice)?.Category);

    private async void HideEmptyElements_Changed(object sender, RoutedEventArgs e)
    {
        if (HideEmptyElementsToggle is null)
            return;
        var hide = HideEmptyElementsToggle.IsChecked == true;
        settings.Update(value => value.SkinEditor.HideEmptyElements = hide);
        await ShowCategoryAsync((CategoryPicker.SelectedItem as CategoryChoice)?.Category);
    }

    private async void ElementList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (ElementList.SelectedItem is SkinElementEntry entry)
            await SelectEntryAsync(entry);
    }

    private async void RecolorModePicker_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (suppressEditorEvents || selectedEntry is null
            || RecolorModePicker.SelectedItem is not ComboBoxItem { Tag: string tag })
            return;
        selectedEntry.Mode = Enum.Parse<SkinRecolorMode>(tag);
        UpdateModePanels();
        RenderSelectedEntry();
        await Task.CompletedTask;
    }

    private void HueShift_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (suppressEditorEvents || selectedEntry is null) return;
        selectedEntry.HueShiftDegrees = HueShiftSlider.Value;
        selectedEntry.SaturationMultiplier = SaturationShiftSlider.Value;
        selectedEntry.LightnessMultiplier = LightnessShiftSlider.Value;
        RenderSelectedEntry();
    }

    private void HexColorBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (suppressEditorEvents || selectedEntry is null
            || !TryParseColor(HexColorBox.Text, out var color))
            return;
        SetCurrentColor(color);
    }

    private void PickColor_Click(object sender, RoutedEventArgs e)
    {
        activeIniColorRow = null;
        colorPickerTargetsElement = true;
        IntegratedSkinColorPicker.Open(
            ToPickerHex(currentColor, includeAlpha: false),
            "Element colour",
            "Choose a colour visually or enter a hex value. The element and gameplay preview update live.",
            allowOpacity: false);
        SkinColorPickerPopup.PlacementTarget = sender as FrameworkElement ?? CurrentColorSwatch;
        SkinColorPickerPopup.IsOpen = true;
    }

    private void SaveSwatch_Click(object sender, RoutedEventArgs e)
    {
        var hex = $"#{currentColor.R:X2}{currentColor.G:X2}{currentColor.B:X2}";
        settings.Update(value =>
        {
            if (!value.SkinEditor.CustomSwatches.Contains(hex, StringComparer.OrdinalIgnoreCase))
                value.SkinEditor.CustomSwatches.Add(hex);
        });
        BuildSwatches();
        StatusText.Text = $"Saved colour {hex}.";
    }

    private void ResetElement_Click(object sender, RoutedEventArgs e)
    {
        if (selectedEntry is null) return;
        selectedEntry.Reset();
        _ = SelectEntryAsync(selectedEntry);
    }

    private async void SaveElement_Click(object sender, RoutedEventArgs e)
    {
        if (selectedEntry is null) return;
        SetBusy(true, $"Saving {selectedEntry.Filename}…");
        try
        {
            if (await SaveEntryAsync(selectedEntry))
                StatusText.Text = $"{selectedEntry.Filename} saved to osu!lazer.";
        }
        catch (Exception ex)
        {
            StatusText.Text = $"Save failed: {ex.Message}";
        }
        finally
        {
            SetBusy(false);
        }
    }

    private void ExportElement_Click(object sender, RoutedEventArgs e)
    {
        if (selectedEntry?.Thumbnail is not BitmapSource bitmap) return;
        var dialog = new Microsoft.Win32.SaveFileDialog
        {
            Title = "Export skin element",
            Filter = "PNG image|*.png",
            FileName = Path.GetFileNameWithoutExtension(selectedEntry.Filename) + ".png",
        };
        if (dialog.ShowDialog(Window.GetWindow(this)) != true) return;
        File.WriteAllBytes(dialog.FileName, SkinImageTools.EncodePng(bitmap));
        StatusText.Text = $"Exported {dialog.FileName}.";
    }

    private async void OpenExternally_Click(object sender, RoutedEventArgs e)
    {
        if (selectedEntry is null) return;
        try { await StartExternalEditAsync(selectedEntry); }
        catch (Exception ex) { StatusText.Text = $"Could not open externally: {ex.Message}"; }
    }

    private async void SaveIni_Click(object sender, RoutedEventArgs e) => await SaveIniAsync();
    private async void SaveAll_Click(object sender, RoutedEventArgs e) => await SaveAllAsync();

    private async void Backup_Click(object sender, RoutedEventArgs e)
    {
        if (catalog is null) return;
        SetBusy(true, "Creating Realm backup…");
        try
        {
            var path = await Task.Run(() =>
                realmService.CreateBackup(catalog.RootPath, AppPaths.LazerSkinBackupsDir));
            backupCreated = true;
            backupRoot = catalog.RootPath;
            PruneBackups();
            StatusText.Text = $"Backup created: {path}";
        }
        catch (Exception ex)
        {
            StatusText.Text = $"Backup failed: {ex.Message}";
        }
        finally
        {
            SetBusy(false);
        }
    }

    private async void Refresh_Click(object sender, RoutedEventArgs e)
    {
        if (!await ResolveDirtyStateAsync()) return;
        var id = currentSkin?.Id;
        currentSkin = null;
        await LoadCatalogAsync(id);
    }

    private async void ChangeFolder_Click(object sender, RoutedEventArgs e)
    {
        if (!await ResolveDirtyStateAsync()) return;
        using var dialog = new System.Windows.Forms.FolderBrowserDialog
        {
            Description = "Select the osu!lazer storage folder containing client.realm and files",
            UseDescriptionForTitle = true,
            InitialDirectory = catalog?.RootPath ?? "",
        };
        if (dialog.ShowDialog() != System.Windows.Forms.DialogResult.OK) return;
        settings.Update(value => value.SkinEditor.LazerRootOverride = dialog.SelectedPath);
        DisposeExternalWatchers();
        currentSkin = null;
        backupCreated = false;
        backupRoot = null;
        await LoadCatalogAsync();
    }

    private void ImportFiles_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = "Import files into this skin",
            Multiselect = true,
            Filter = "Skin files|*.png;*.jpg;*.jpeg;*.wav;*.mp3;*.ogg;*.ini|All files|*.*",
        };
        if (dialog.ShowDialog(Window.GetWindow(this)) == true)
            _ = ImportPathsAsync(dialog.FileNames);
    }

    private async void CopyElements_Click(object sender, RoutedEventArgs e)
    {
        if (catalog is null) return;
        var selected = ElementList.SelectedItems.Cast<SkinElementEntry>().ToArray();
        var copied = new List<(string Filename, byte[] Bytes)>();
        foreach (var logicalEntry in selected)
        {
            logicalEntry.SynchronizeEditsToVariants();
            foreach (var entry in logicalEntry.PhysicalEntries)
            {
                if (logicalEntry.HasEdits && entry.IsImage)
                    await EnsureEntryLoadedAsync(entry);
                var bytes = logicalEntry.HasEdits && entry.IsImage
                    ? SkinImageTools.Encode(SkinImageTools.Render(entry), entry.Filename)
                    : await Task.Run(() => realmService.ReadFile(catalog.RootPath, entry.Hash));
                copied.Add((entry.Filename, bytes));
            }
        }
        elementClipboard = copied;
        StatusText.Text = $"Copied {copied.Count} physical file{(copied.Count == 1 ? "" : "s")} from "
                          + $"{selected.Length} element{(selected.Length == 1 ? "" : "s")}.";
    }

    private async void PasteElements_Click(object sender, RoutedEventArgs e) =>
        await ImportBytesAsync(elementClipboard);

    private void ElementList_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.C && Keyboard.Modifiers.HasFlag(ModifierKeys.Control))
        {
            CopyElements_Click(sender, new RoutedEventArgs());
            e.Handled = true;
        }
        else if (e.Key == Key.V && Keyboard.Modifiers.HasFlag(ModifierKeys.Control))
        {
            PasteElements_Click(sender, new RoutedEventArgs());
            e.Handled = true;
        }
    }

    private void ElementList_DragOver(object sender, DragEventArgs e)
    {
        e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop)
            ? DragDropEffects.Copy
            : DragDropEffects.None;
        e.Handled = true;
    }

    private async void ElementList_Drop(object sender, DragEventArgs e)
    {
        if (e.Data.GetData(DataFormats.FileDrop) is string[] paths)
            await ImportPathsAsync(paths);
    }

    private void GameplayBackground_Checked(object sender, RoutedEventArgs e)
    {
        if (GameplayBackground is not null)
            GameplayBackground.Visibility = SkinBackgroundToggle.IsChecked == true
                ? Visibility.Visible
                : Visibility.Collapsed;
    }

    private void WorkspaceTabs_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (ReferenceEquals(e.Source, WorkspaceTabs) && WorkspaceTabs.SelectedIndex == 1)
            UpdateComboStrip();
    }

    private void CompactActions_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { ContextMenu: { } menu } button)
        {
            menu.PlacementTarget = button;
            menu.IsOpen = true;
        }
    }

    private sealed record CategoryChoice(SkinElementCategory Category)
    {
        public string DisplayLabel =>
            $"{(Category.IsSubfolder ? "📁 " : "")}{Category.Name} ({Category.Files.Count})";

        public override string ToString() => DisplayLabel;
    }

    private sealed record IniRow(
        SkinIniKeyDefinition Definition,
        CheckBox Active,
        TextBox Value,
        Button? Picker,
        Border? ColorPreview);

    private readonly record struct SliderPreviewKey(
        Color ComboColour,
        Color? SliderBorder,
        Color? SliderTrackOverride);
}
