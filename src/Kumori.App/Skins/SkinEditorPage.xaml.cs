using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Kumori.Core;
using Kumori.Core.Settings;
using Kumori.Tracking;
using Color = System.Windows.Media.Color;
using Path = System.IO.Path;

namespace Kumori.App.Skins;

internal enum SkinEditorWorkspaceMode
{
    Elements,
    SkinIni,
}

internal enum SkinEditorCenterMode
{
    Asset,
    Gameplay,
    IniForm,
    IniRaw,
}

internal enum SkinEditorInspectorMode
{
    Context,
    Review,
}

internal enum SkinEditorCompactSurface
{
    Browse,
    Canvas,
    Properties,
}

internal enum SkinElementCompositionKind
{
    HitObject,
    Followpoints,
    Slider,
    Cursor,
    Spinner,
    Numbers,
    Scorebar,
    Context,
}

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
    private static readonly IReadOnlyList<System.Windows.Point>
        GameplaySliderPreviewPath = LegacySliderRenderer
            .SampleSCurve(127, 177, 676, 102, segments: 128);
    private static readonly IReadOnlyList<System.Windows.Point>
        ElementSliderPreviewPath = BuildElementSliderPreviewPath();
    private static readonly double GameplaySliderPreviewVelocity =
        SkinPreviewAnimation.PolylineLength(GameplaySliderPreviewPath)
        / SkinPreviewAnimation.SliderSpanMilliseconds;
    private static readonly double ElementSliderPreviewVelocity =
        SkinPreviewAnimation.PolylineLength(ElementSliderPreviewPath)
        / SkinPreviewAnimation.SliderSpanMilliseconds;

    private static List<(string Filename, byte[] Bytes)> elementClipboard = [];

    private readonly SettingsService settings;
    private readonly ILazerSkinRealmService realmService;
    private readonly ILazerSkinReloadService? reloadService;
    private readonly List<FileSystemWatcher> externalWatchers = [];
    private readonly Dictionary<(string Section, string Key), IniRow> iniRows = [];
    private readonly Dictionary<string, FrameworkElement> iniSectionPanels =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly object imageLoadGate = new();
    private readonly Dictionary<string, Task<LoadedSkinImage>> imageLoadTasks =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, Task<bool>> imageVisibilityLoadTasks =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, bool> imageVisibilityCache =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, SkinElementEntry> draftPreviewEntries =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly List<System.Windows.Controls.Image> selectedCompositionLayers = [];
    private readonly List<ElementCompositionVisual> elementCompositionVisuals = [];
    private readonly Dictionary<System.Windows.Controls.Image, PreviewVisualTransforms>
        previewVisualTransforms = [];
    private readonly List<System.Windows.Controls.Image> interactiveCursorTrailVisuals = [];
    private readonly List<InteractiveCursorSample> interactiveCursorSamples = [];
    private static readonly object ElementCompositionDecorationTag = new();
    private static readonly DropShadowEffect ElementCompositionSelectionGlow =
        CreateElementCompositionSelectionGlow();
    private SkinElementCompositionKind? renderedElementCompositionKind;
    private bool renderedCursorUsesSmoothTrail;
    private IReadOnlyDictionary<string, SkinDraftChange[]> draftChangesByStem =
        new Dictionary<string, SkinDraftChange[]>(StringComparer.OrdinalIgnoreCase);
    private IReadOnlySet<string> draftDeletedFilenames =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    private long indexedDraftRevision = -1;
    private long displayedDraftRevision = -1;
    private IReadOnlyList<LazerSkinInfo> allSkins = [];
    private IReadOnlyList<SkinElementCategory> categories = [];
    private LazerSkinCatalog? catalog;
    private LazerSkinInfo? currentSkin;
    private PendingSkinDuplicate? pendingDuplicate;
    private SkinElementEntry? selectedEntry;
    private SkinElementEntry? contextMenuEntry;
    private LazerSkinFileInfo? iniFile;
    private SkinIniDocument? iniDocument;
    private SkinDraftSession? draft;
    private bool initialized;
    private bool loading;
    private bool busy;
    private int busyDepth;
    private bool suppressSkinSelection;
    private bool suppressSkinCatalogFilter;
    private bool suppressElementSelection;
    private bool showFullElementRender = true;
    private string? extrasCategoryHintOverride;
    private SkinExtrasPickerWindow? embeddedExtras;
    private readonly SkinExtrasCatalogSyncService extrasSyncService;
    private SkinExtrasSyncProgress? lastExtrasSyncProgress;
    private Guid? readinessSkinId;
    private bool suppressEditorEvents;
    private bool suppressRawEvents;
    private bool iniDirty;
    private bool rawDirty;
    private bool backupCreated;
    private string? backupRoot;
    private string? elementBackupDirectory;
    private readonly HashSet<string> backedUpElements = new(StringComparer.OrdinalIgnoreCase);
    private int gameplayRefreshVersion;
    private int richPreviewVersion;
    private int categoryLoadVersion;
    private int selectedEntryLoadVersion;
    private readonly DispatcherTimer gameplayPreviewRefreshTimer;
    private readonly DispatcherTimer elementSearchTimer;
    private readonly DispatcherTimer elementRenderTimer;
    private readonly DispatcherTimer draftRecoveryTimer;
    private readonly SemaphoreSlim draftRecoveryWriteGate = new(1, 1);
    private Guid? persistedDraftSkinId;
    private long persistedDraftRevision = -1;
    private Color currentColor = Colors.White;
    private IniRow? activeIniColorRow;
    private IniRow? focusedIniRow;
    private bool colorPickerTargetsElement;
    private CancellationTokenSource? gameplayRenderCancellation;
    private SliderPreviewKey? cachedSliderPreviewKey;
    private BitmapSource? cachedSliderPreview;
    private readonly Dictionary<SkinElementCompositionKind, IReadOnlyList<ElementLayerSpec>>
        elementCompositionCache = [];
    private long cachedElementCompositionDraftRevision = -1;
    private readonly Stopwatch previewRenderClock = Stopwatch.StartNew();
    private double previewAnimationElapsed;
    private double previewLastRenderTime;
    private double previewFrameDelta;
    private double previewHealth = 1;
    private bool previewRenderingSubscribed;
    private bool previewAnimationsEnabled;
    private bool interactiveCursorActive;
    private double interactiveCursorLastSampleTime = double.NegativeInfinity;
    private double interactiveCursorScale = 1;
    private double interactiveCursorScaleFrom = 1;
    private double interactiveCursorScaleTarget = 1;
    private double interactiveCursorScaleStartTime;
    private int interactiveCursorDownCount;
    private System.Windows.Point? interactiveSmoothTrailAnchor;
    private System.Windows.Point interactiveCursorPosition;
    private Window? previewHostWindow;
    private IReadOnlyList<BitmapSource> sliderBallAnimationFrames = [];
    private IReadOnlyList<BitmapSource> sliderFollowAnimationFrames = [];
    private IReadOnlyList<BitmapSource> followpointAnimationFrames = [];
    private int previewAnimationFramerate = -1;
    private decimal previewLegacySkinVersion = 2.7m;
    private IInputElement? focusBeforeBusy;
    private SkinEditorWorkspaceMode workspaceMode = SkinEditorWorkspaceMode.Elements;
    private SkinEditorCenterMode elementCenterMode = SkinEditorCenterMode.Asset;
    private SkinEditorInspectorMode inspectorMode = SkinEditorInspectorMode.Context;
    private SkinEditorCompactSurface compactSurface = SkinEditorCompactSurface.Canvas;
    private ResponsiveLayoutState responsiveState =
        ResponsiveLayoutResolver.Resolve(1280, 800);

    public SkinEditorPage(
        SettingsService settings,
        ILazerSkinRealmService? realmService = null)
        : this(
            settings,
            realmService,
            reloadService: null)
    {
    }

    internal SkinEditorPage(
        SettingsService settings,
        ILazerSkinRealmService? realmService,
        ILazerSkinReloadService? reloadService)
    {
        this.settings = settings;
        this.realmService = realmService ?? new LazerSkinRealmService();
        this.reloadService = reloadService;
        extrasSyncService = SkinExtrasCatalogSyncService.Shared;
        extrasSyncService.ProgressChanged += ExtrasSyncService_ProgressChanged;
        extrasSyncService.LibraryChanged += ExtrasSyncService_LibraryChanged;
        elementSearchTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(180),
        };
        elementRenderTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(33),
        };
        InitializeComponent();
        previewAnimationsEnabled = settings.Current.SkinEditor.PreviewAnimationsEnabled;
        PreviewPlaybackToggle.IsChecked = previewAnimationsEnabled;
        UpdatePreviewPlaybackPresentation();
        elementSearchTimer.Tick += async (_, _) =>
        {
            elementSearchTimer.Stop();
            await ShowCategoryAsync((CategoryPicker.SelectedItem as CategoryChoice)?.Category);
        };
        elementRenderTimer.Tick += (_, _) =>
        {
            elementRenderTimer.Stop();
            RenderSelectedEntry(invalidateComposition: true);
        };
        gameplayPreviewRefreshTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(90),
        };
        gameplayPreviewRefreshTimer.Tick += async (_, _) =>
        {
            gameplayPreviewRefreshTimer.Stop();
            await RefreshGameplayPreviewAsync();
        };
        draftRecoveryTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(650),
        };
        draftRecoveryTimer.Tick += async (_, _) =>
        {
            draftRecoveryTimer.Stop();
            await PersistDraftRecoveryAsync();
        };
        HideEmptyElementsToggle.IsChecked = settings.Current.SkinEditor.HideEmptyElements;
        AutoBackupElementsMenuItem.IsChecked = settings.Current.SkinEditor.AutoBackupElements;
        UpdateElementBackupHint();
        ImageEditorControls.Visibility = Visibility.Collapsed;
        ElementActionFooter.Visibility = Visibility.Collapsed;
        NoElementInspectorHint.Visibility = Visibility.Visible;
        IntegratedSkinColorPicker.ColourChanged += SkinColorPicker_ColourChanged;
        IntegratedSkinColorPicker.CloseRequested += () => SkinColorPickerPopup.IsOpen = false;
        BuildSwatches();
        SizeChanged += (_, _) => ApplyResponsiveLayout();
        Loaded += SkinEditor_Loaded;
        IsVisibleChanged += (_, _) =>
        {
            if (!IsVisible)
                EndInteractiveCursorPreview();
            UpdatePreviewAnimationSubscription();
        };
        Unloaded += async (_, _) =>
        {
            EndInteractiveCursorPreview();
            StopPreviewRendering();
            if (previewHostWindow is not null)
                previewHostWindow.Deactivated -= PreviewHostWindow_Deactivated;
            previewHostWindow = null;
            gameplayPreviewRefreshTimer.Stop();
            elementSearchTimer.Stop();
            elementRenderTimer.Stop();
            gameplayRenderCancellation?.Cancel();
            draftRecoveryTimer.Stop();
            await PersistDraftRecoveryAsync();
            CloseExtrasWorkspace();
            DisposeExternalWatchers();
        };
        UpdateStudioState();
        ApplyResponsiveLayout();
        UpdateOnboardingState();
    }

    public async Task EnsureLoadedAsync()
    {
        BeginExtrasSynchronization(manual: false);
        if (initialized || loading)
            return;
        initialized = true;
        try
        {
            await Task.Run(EnsureExtrasDirectories);
        }
        catch (Exception ex)
        {
            StatusText.Text = $"Could not prepare the Extras library: {ex.Message}";
        }
        await LoadCatalogAsync();
    }

    private void BeginExtrasSynchronization(bool manual) =>
        _ = SynchronizeExtrasAsync(manual);

    private async Task SynchronizeExtrasAsync(bool manual)
    {
        try
        {
            await extrasSyncService.SynchronizeAsync(manual);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            await Dispatcher.InvokeAsync(() =>
                StatusText.Text = $"Extras update check failed: {ex.Message}");
        }
    }

    private void ExtrasSyncService_ProgressChanged(
        object? sender,
        SkinExtrasSyncProgress progress)
    {
        _ = Dispatcher.InvokeAsync(() =>
        {
            lastExtrasSyncProgress = progress;
            StatusText.Text = progress.Message;
            embeddedExtras?.UpdateCatalogSyncProgress(progress);
        });
    }

    private void ExtrasSyncService_LibraryChanged(object? sender, EventArgs e) =>
        _ = Dispatcher.InvokeAsync(() => embeddedExtras?.RefreshLibrary());

    private void CheckExtrasUpdates_Click(object sender, RoutedEventArgs e) =>
        BeginExtrasSynchronization(manual: true);

    private async Task LoadCatalogAsync(
        Guid? preferredSkin = null,
        bool forceReloadSelectedSkin = false)
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
            await ApplySkinFilterAsync(
                preferredSkin ?? currentSkin?.Id,
                forceReloadSelectedSkin);
            StatusText.Text = allSkins.Count == 0
                ? "No imported skins were found in this lazer library."
                : $"Loaded {allSkins.Count:N0} lazer skin(s).";
        }
        catch (Exception ex)
        {
            catalog = null;
            allSkins = [];
            CompactSkinPicker.ItemsSource = allSkins;
            CompactSkinPicker.Text = "";
            RootPathText.Text = "osu!lazer library unavailable";
            StatusText.Text = ex.Message;
        }
        finally
        {
            loading = false;
            SetBusy(false);
        }
    }

    private async Task ApplySkinFilterAsync(
        Guid? preferredSkin = null,
        bool forceReloadSelectedSkin = false)
    {
        CompactSkinPicker.ItemsSource = allSkins;
        var selection = preferredSkin is not null
            ? allSkins.FirstOrDefault(skin => skin.Id == preferredSkin.Value)
            : currentSkin is null
                ? null
                : allSkins.FirstOrDefault(skin => skin.Id == currentSkin.Id);
        SetSkinPickerSelection(selection);

        if (selection is not null
            && (forceReloadSelectedSkin || selection.Id != currentSkin?.Id))
            await SelectSkinAsync(selection, forceReloadSelectedSkin);
        else if (selection is null)
            ShowSkinChooser();
    }

    private void ShowSkinChooser()
    {
        ActiveSkinLabel.Text = "SELECT A SKIN";
        CompactSkinPicker.Text = "Select a skin…";
        UpdateOnboardingState();
    }

    private async Task SelectSkinAsync(
        LazerSkinInfo skin,
        bool forceReload = false,
        bool restoreRecoveredDraft = true)
    {
        if (!forceReload && skin.Id == currentSkin?.Id) return;
        if (!forceReload && !await ResolveDirtyStateAsync())
        {
            RestoreSkinSelection();
            return;
        }

        currentSkin = skin;
        WelcomePanel.Visibility = Visibility.Collapsed;
        SetSkinPickerSelection(skin);
        ActiveSkinLabel.Text = "ACTIVE SKIN";
        draft = new SkinDraftSession(skin.Id);
        indexedDraftRevision = -1;
        displayedDraftRevision = -1;
        draftPreviewEntries.Clear();
        draftChangesByStem =
            new Dictionary<string, SkinDraftChange[]>(StringComparer.OrdinalIgnoreCase);
        draftDeletedFilenames =
            new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        elementCompositionCache.Clear();
        cachedElementCompositionDraftRevision = draft.Revision;
        cachedSliderPreviewKey = null;
        cachedSliderPreview = null;
        backupCreated = false;
        backupRoot = null;
        elementBackupDirectory = null;
        backedUpElements.Clear();
        selectedEntry = null;
        ClearElementComposition();
        ElementPreviewHintText.Text = "Select an image element";
        ElementPreviewHint.Visibility = Visibility.Visible;
        SelectedElementName.Text = "No element selected";
        SelectedElementMeta.Text = "";
        SelectedElementUsage.Text = "Choose an asset from the library to preview and edit it.";
        ImageEditorControls.IsEnabled = false;
        ImageEditorControls.Visibility = Visibility.Collapsed;
        ElementActionFooter.Visibility = Visibility.Collapsed;
        NoElementInspectorHint.Visibility = Visibility.Visible;
        GameplaySkinName.Text = skin.DisplayName;
        categories = SkinElementCategorizer.Categorize(skin.Files);
        CategoryPicker.ItemsSource = categories
            .Select(category => new CategoryChoice(category))
            .ToArray();
        BuildSemanticGroupRail();
        CategoryPicker.SelectedIndex = categories.Count > 0 ? 0 : -1;
        await LoadSkinIniAsync();
        if (restoreRecoveredDraft)
            RestoreRecoveredDraft();
        await RefreshGameplayPreviewAsync();
        StatusText.Text = $"Editing {skin.DisplayName}. Preview freely, add edits to Changes, then save when ready.";
        InspectorSubtitleText.Text = skin.DisplayName;
        UpdateSkinReadiness();
        UpdateOnboardingState();
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
            RefreshIniFormAfterLayout(iniDocument);
            _ = RefreshRichPreviewsAsync();
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
        var preferredSection = (IniSectionList.SelectedItem as IniSectionChoice)?.Name
                               ?? focusedIniRow?.Definition.Section;
        IniFormPanel.Children.Clear();
        iniRows.Clear();
        iniSectionPanels.Clear();
        IniSectionList.ItemsSource = Array.Empty<IniSectionChoice>();
        if (iniDocument is null) return;

        var sectionChoices = new List<IniSectionChoice>();
        foreach (var (section, definitions) in SkinIniSchema.Sections())
        {
            var sectionPanel = new StackPanel();
            foreach (var definition in definitions)
            {
                var rowGrid = new Grid();
                rowGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(24) });
                rowGrid.ColumnDefinitions.Add(new ColumnDefinition
                {
                    Width = new GridLength(2, GridUnitType.Star),
                    MinWidth = 140,
                    MaxWidth = 280,
                });
                rowGrid.ColumnDefinitions.Add(new ColumnDefinition
                {
                    Width = new GridLength(3, GridUnitType.Star),
                    MinWidth = 100,
                });
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
                    ToolTip = definition.Label,
                    Margin = new Thickness(4, 3, 12, 3),
                };
                var value = new TextBox
                {
                    Text = iniDocument.GetValue(definition.Section, definition.Key)
                        ?? definition.DefaultValue,
                    IsEnabled = active.IsChecked == true,
                    MinWidth = 100,
                    MinHeight = 32,
                    Padding = new Thickness(8, 3, 8, 3),
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
                        BorderThickness = new Thickness(1),
                        Background = new SolidColorBrush(initialColor),
                    };
                    colorPreview.SetResourceReference(Border.BorderBrushProperty, "Brush.StrongBorder");
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
                value.GotKeyboardFocus += (_, _) => ShowIniContext(row);
                var rowBorder = new Border
                {
                    Child = rowGrid,
                    Style = (Style)FindResource("IniSettingRow"),
                };
                rowBorder.MouseEnter += (_, _) => ShowIniContext(row);
                if (picker is not null)
                    picker.Click += (_, _) => PickIniColor(row);
                sectionPanel.Children.Add(rowBorder);
            }

            var activeCount = definitions.Count(definition => iniDocument.HasValue(definition.Section, definition.Key));
            var sectionHost = new StackPanel
            {
                Margin = new Thickness(0),
            };
            sectionHost.Children.Add(sectionPanel);
            var sectionHint = new TextBlock
            {
                Text = "Use Raw mode for comments, unknown keys, repeated [Mania] sections, and future settings.",
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(2, 12, 2, 4),
                FontSize = 10,
            };
            sectionHint.SetResourceReference(TextBlock.ForegroundProperty, "Brush.TextMuted");
            sectionHost.Children.Add(sectionHint);
            iniSectionPanels[section] = sectionHost;
            sectionChoices.Add(new IniSectionChoice(section, activeCount));
        }

        IniSectionList.ItemsSource = sectionChoices;
        var initialSection = sectionChoices.FirstOrDefault(choice =>
            choice.Name.Equals(preferredSection, StringComparison.OrdinalIgnoreCase))
            ?? sectionChoices.FirstOrDefault(choice =>
                choice.Name.Equals("Colours", StringComparison.OrdinalIgnoreCase))
            ?? sectionChoices.FirstOrDefault();
        IniSectionList.SelectedItem = initialSection;
        if (initialSection is not null)
            ShowIniSection(initialSection.Name);
        ShowIniContext(iniRows.Values.FirstOrDefault(row =>
            initialSection is not null
            && row.Definition.Section.Equals(initialSection.Name, StringComparison.OrdinalIgnoreCase)));
    }

    private void RefreshIniFormAfterLayout(
        SkinIniDocument document,
        (string Section, string Key)? focusTarget = null)
    {
        if (!ReferenceEquals(iniDocument, document)
            || workspaceMode != SkinEditorWorkspaceMode.SkinIni
            || WorkspaceTabs.SelectedIndex != 1
            || IniModeTabs.SelectedIndex != 0)
            return;

        // Rebuild only after the workspace TabItem is selected. This makes the
        // first form visible immediately and prevents navigation from retaining
        // TextBoxes that belonged to a previous, detached section tree.
        BuildIniForm();
        IniFormScroll.UpdateLayout();
        if (focusTarget is { } target)
            FocusIniRow(target.Section, target.Key);
    }

    private void ShowIniContext(IniRow? row)
    {
        if (row is null) return;
        focusedIniRow = row;
        var metadata = SkinIniRichEditor.Describe(row.Definition);
        IniContextTitle.Text = row.Definition.Label;
        IniContextHelp.Text = metadata.Help;
        if (workspaceMode == SkinEditorWorkspaceMode.SkinIni
            && inspectorMode == SkinEditorInspectorMode.Context)
            InspectorSubtitleText.Text = row.Definition.Label;
        IniContextAffects.Children.Clear();
        foreach (var target in metadata.Affects)
        {
            var badge = new Border
            {
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(4),
                Padding = new Thickness(5, 1, 5, 1),
                Margin = new Thickness(0, 0, 4, 2),
                Child = new TextBlock { Text = target, FontSize = 9 },
            };
            badge.SetResourceReference(Border.BackgroundProperty, "Brush.ControlBackground");
            badge.SetResourceReference(Border.BorderBrushProperty, "Brush.SubtleBorder");
            IniContextAffects.Children.Add(badge);
        }
        IniContextVisual.Tag = metadata.Group;
        _ = RefreshRichPreviewsAsync();
    }

    private async Task RefreshRichPreviewsAsync()
    {
        if (iniDocument is null) return;
        var version = ++richPreviewVersion;
        var metadata = focusedIniRow is null
            ? new SkinIniRichMetadata(SkinIniVisualGroup.Identity, SkinIniPreviewKind.Information, "", [])
            : SkinIniRichEditor.Describe(focusedIniRow.Definition);
        var combos = Enumerable.Range(1, 8)
            .Select(index => ReadIniColor("Colours", $"Combo{index}"))
            .Select(color => color ?? Colors.Transparent)
            .ToArray();
        var previewColour = focusedIniRow is not null
                            && focusedIniRow.Definition.Type is SkinIniValueType.Rgb or SkinIniValueType.Rgba
                            && TryParseColor(focusedIniRow.Value.Text, out var focusedColour)
            ? focusedColour
            : combos.FirstOrDefault(color => color.A > 0, builtInSwatches[0]);

        IniContextHitObjectStage.Visibility = Visibility.Collapsed;
        IniContextSliderStage.Visibility = Visibility.Collapsed;
        IniContextAsset.Visibility = Visibility.Collapsed;
        IniContextColour.Visibility = Visibility.Collapsed;

        if (metadata.Preview is SkinIniPreviewKind.HitObjects or SkinIniPreviewKind.ComboPalette)
        {
            var hitCirclePrefix = iniDocument.GetValue("Fonts", "HitCirclePrefix");
            if (string.IsNullOrWhiteSpace(hitCirclePrefix))
                hitCirclePrefix = "default";
            var assets = await Task.WhenAll(
                FindAndLoadAsync("hitcircle"),
                FindAndLoadAsync("hitcircleoverlay"),
                FindAndLoadAsync($"{hitCirclePrefix}-1"),
                FindAndLoadAsync("approachcircle"));
            if (version != richPreviewVersion) return;
            var circle = assets[0];
            var overlay = assets[1];
            var number = assets[2];
            var approach = assets[3];

            IniContextHitCircle.Source = Tinted(circle, previewColour);
            IniContextHitApproach.Source = Tinted(approach, previewColour);
            IniContextHitNumber.Source = number?.Thumbnail;
            var overlayAbove = iniDocument.GetValue("General", "HitCircleOverlayAboveNumber") == "1";
            IniContextHitOverlayBelow.Source = overlay?.Thumbnail;
            IniContextHitOverlayBelow.Visibility = overlayAbove ? Visibility.Collapsed : Visibility.Visible;
            IniContextHitOverlayAbove.Source = overlay?.Thumbnail;
            IniContextHitOverlayAbove.Visibility = overlayAbove ? Visibility.Visible : Visibility.Collapsed;
            IniContextHitSwatch.Fill = new SolidColorBrush(previewColour);
            IniContextHitObjectStage.Visibility = Visibility.Visible;
            IniContextFallback.Visibility = circle is null ? Visibility.Visible : Visibility.Collapsed;
            return;
        }

        if (metadata.Preview == SkinIniPreviewKind.Slider)
        {
            IniContextSlider.Source = GameplaySliderBody.Source;
            IniContextSliderHead.Source = GameplayHitcircle.Source;
            IniContextSliderHeadOverlay.Source = GameplayOverlay.Source;
            IniContextSliderNumber.Source = GameplayNumber.Source;
            IniContextSliderTail.Source = GameplayTailCircle.Source;
            IniContextSliderTailOverlay.Source = GameplayTailOverlay.Source;
            IniContextSliderBall.Source = GameplaySliderBall.Source;
            IniContextSliderStage.Visibility = Visibility.Visible;
            IniContextFallback.Visibility = GameplaySliderBody.Source is null
                ? Visibility.Visible
                : Visibility.Collapsed;
            return;
        }

        var representative = metadata.Preview switch
        {
            SkinIniPreviewKind.Cursor => GameplayCursor.Source,
            SkinIniPreviewKind.Spinner => GameplaySpinnerCircle.Source
                                            ?? GameplaySpinnerTop.Source
                                            ?? GameplaySpinnerBackground.Source,
            SkinIniPreviewKind.Interface => GameplayScorebar.Source,
            SkinIniPreviewKind.Catch => GameplayTailCircle.Source,
            _ => null,
        };
        IniContextAsset.Source = representative;
        IniContextAsset.Visibility = representative is null ? Visibility.Collapsed : Visibility.Visible;
        IniContextFallback.Visibility = representative is null ? Visibility.Visible : Visibility.Collapsed;
    }

    private void HighlightGameplayGroup(SkinIniVisualGroup group)
    {
        var cards = new[]
        {
            GameplayHudCard,
            GameplayHitObjectCard,
            GameplaySliderCard,
            GameplaySpinnerCard,
            GameplayCursorCard,
        };
        foreach (var card in cards)
        {
            card.SetResourceReference(Border.BorderBrushProperty, "Brush.BorderSubtle");
            card.BorderThickness = new Thickness(1);
        }

        var highlighted = group switch
        {
            SkinIniVisualGroup.Slider => GameplaySliderCard,
            SkinIniVisualGroup.Cursor => GameplayCursorCard,
            SkinIniVisualGroup.Combo or SkinIniVisualGroup.HitObjects => GameplayHitObjectCard,
            SkinIniVisualGroup.Spinner => GameplaySpinnerCard,
            SkinIniVisualGroup.Interface => GameplayHudCard,
            _ => null,
        };
        if (highlighted is not null)
        {
            highlighted.SetResourceReference(Border.BorderBrushProperty, "Brush.AccentPink");
            highlighted.BorderThickness = new Thickness(2);
        }
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
        InvalidateIniCompositionCache();
        SetRawText(iniDocument.ToText());
        UpdateComboStrip();
        UpdateDirtyState();
        _ = RefreshGameplayPreviewAsync();
        _ = RefreshRichPreviewsAsync();
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
        if (!ReferenceEquals(e.Source, IniModeTabs))
            return;
        IniFormModeButton.IsChecked = IniModeTabs.SelectedIndex == 0;
        IniRawModeButton.IsChecked = IniModeTabs.SelectedIndex == 1;
        if (IniModeTabs.SelectedIndex != 0 || iniDocument is null)
            return;
        if (rawDirty)
        {
            iniDocument = iniDocument.WithText(RawIniText.Text);
            rawDirty = false;
            InvalidateIniCompositionCache();
        }
        if (workspaceMode == SkinEditorWorkspaceMode.SkinIni)
            RefreshIniFormAfterLayout(iniDocument);
        UpdateComboStrip();
        _ = RefreshGameplayPreviewAsync();
        _ = RefreshRichPreviewsAsync();
    }

    private async Task<bool> SaveIniAsync()
    {
        if (catalog is null || currentSkin is null || iniDocument is null)
            return false;
        if (rawDirty)
        {
            iniDocument = iniDocument.WithText(RawIniText.Text);
            rawDirty = false;
            InvalidateIniCompositionCache();
        }
        else if (!ApplyFormRowsToDocument(validate: true))
        {
            return false;
        }

        if (draft is null) return false;
        var bytes = iniDocument.ToBytes();
        draft.Stage(
            "skin.ini",
            iniFile?.Hash,
            bytes,
            "skin.ini");
        iniDirty = false;
        StatusText.Text = "skin.ini added to Changes. Save to osu!lazer when ready.";
        UpdateDirtyState();
        return true;
    }

    private async Task ShowCategoryAsync(SkinElementCategory? category)
    {
        var version = ++categoryLoadVersion;
        if (category is null)
        {
            ElementList.ItemsSource = Array.Empty<SkinElementEntry>();
            ElementEmptyState.Visibility = Visibility.Visible;
            ElementEmptyTitle.Text = "Choose a category";
            ElementEmptyHint.Text = "Select an asset family above to browse its elements.";
            return;
        }

        var hideEmpty = HideEmptyElementsToggle.IsChecked == true;
        var query = ElementSearchBox.Text.Trim();
        // Let the list render immediately. Loading every full-resolution PNG before
        // assigning ItemsSource made category switches look like the app had hung.
        var candidates = category.Files
            .Where(entry => query.Length == 0
                || entry.Filename.Contains(query, StringComparison.OrdinalIgnoreCase))
            .ToArray();
        ElementList.ItemsSource = candidates;
        ElementEmptyState.Visibility = candidates.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        ElementEmptyTitle.Text = query.Length == 0 ? "Nothing here yet" : "No matching elements";
        ElementEmptyHint.Text = query.Length == 0
            ? "Browse Extras or import files to fill this category."
            : "Try a different search, or clear the search box.";

        // The virtualized list requests thumbnails as cards enter the viewport.
        // Only the hide-empty filter needs every image decoded up front.
        if (!hideEmpty)
            return;

        var entriesToInspect = candidates
            .SelectMany(logicalEntry => logicalEntry.PhysicalEntries)
            .Where(entry => entry.IsImage && entry.HasVisiblePixels is null)
            .Distinct()
            .ToArray();
        foreach (var batch in entriesToInspect.Chunk(4))
        {
            await Task.WhenAll(batch.Select(async entry =>
            {
                try
                {
                    await EnsureEntryVisibilityAsync(entry);
                }
                catch
                {
                    // A broken image should not block the remaining filter results.
                }
            }));
            if (version != categoryLoadVersion)
                return;
        }

        if (version != categoryLoadVersion)
            return;
        var visible = category.Files
            .Where(entry => !hideEmpty || !entry.IsLogicallyEmpty)
            .Where(entry => query.Length == 0
                || entry.Filename.Contains(query, StringComparison.OrdinalIgnoreCase))
            .ToArray();
        ElementList.ItemsSource = visible;
        ElementEmptyState.Visibility = visible.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        ElementEmptyTitle.Text = query.Length == 0 ? "Nothing here yet" : "No matching elements";
        ElementEmptyHint.Text = query.Length == 0
            ? "Browse Extras or import files to fill this category."
            : "Try a different search, or clear the search box.";
        var hidden = category.Files.Count - visible.Length;
        if (hidden > 0)
            StatusText.Text = $"Showing {visible.Length:N0} of {category.Files.Count:N0} elements.";
    }

    private async void ElementThumbnail_Loaded(object sender, RoutedEventArgs e)
    {
        if (sender is not System.Windows.Controls.Image
            {
                DataContext: SkinElementEntry { IsImage: true, Thumbnail: null } entry,
            })
            return;

        try
        {
            await EnsureEntryLoadedAsync(entry);
        }
        catch
        {
            // A broken thumbnail should not interrupt scrolling or other cards.
        }
    }

    private void BuildSemanticGroupRail()
    {
        SemanticGroupRail.Children.Clear();
        foreach (var group in SkinElementSemanticGroups.All)
        {
            var available = categories
                .Where(category => group.Categories.Contains(category.Name, StringComparer.OrdinalIgnoreCase))
                .ToArray();
            if (available.Length == 0) continue;
            var button = new Button
            {
                Content = group.Name,
                Tag = available[0],
                Style = (Style)FindResource("Button.Chrome"),
                Padding = new Thickness(8, 3, 8, 3),
                Margin = new Thickness(0, 0, 5, 4),
                ToolTip = string.Join(", ", available.Select(category => category.Name)),
            };
            button.Click += SemanticGroup_Click;
            SemanticGroupRail.Children.Add(button);
        }
    }

    private void SemanticGroup_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: SkinElementCategory category }) return;
        CategoryPicker.SelectedItem = CategoryPicker.Items.Cast<CategoryChoice>()
            .FirstOrDefault(choice => ReferenceEquals(choice.Category, category));
    }

    private async Task EnsureEntryLoadedAsync(SkinElementEntry entry)
    {
        if (entry.OriginalPixels is not null || catalog is null)
            return;
        var root = catalog.RootPath;
        var hash = entry.Hash;
        var loadKey = $"{entry.Filename}\0{hash}";
        Task<LoadedSkinImage> loadTask;
        lock (imageLoadGate)
        {
            if (imageLoadTasks.TryGetValue(loadKey, out var existingLoad))
            {
                loadTask = existingLoad;
            }
            else
            {
                loadTask = Task.Run(() =>
                {
                    var bytes = realmService.ReadFile(root, hash);
                    var bitmap = SkinImageTools.Decode(bytes);
                    var pixels = SkinImageTools.Pixels(bitmap, out var stride);
                    var thumbnail = SkinImageTools.ToBitmap(
                        pixels,
                        bitmap.PixelWidth,
                        bitmap.PixelHeight,
                        stride);
                    thumbnail.Freeze();
                    return new LoadedSkinImage(
                        bytes,
                        pixels,
                        stride,
                        bitmap.PixelWidth,
                        bitmap.PixelHeight,
                        SkinCursorMiddlePolicy.HasRenderablePixels(
                            entry.Filename,
                            bitmap.PixelWidth,
                            bitmap.PixelHeight,
                            pixels),
                        thumbnail);
                });
                imageLoadTasks[loadKey] = loadTask;
            }
        }
        LoadedSkinImage loaded;
        try
        {
            loaded = await loadTask;
        }
        finally
        {
            lock (imageLoadGate)
                if (imageLoadTasks.GetValueOrDefault(loadKey) == loadTask)
                    imageLoadTasks.Remove(loadKey);
        }
        if (entry.OriginalPixels is not null)
            return;
        loaded.Thumbnail.Freeze();
        entry.OriginalBytes = loaded.Bytes;
        entry.OriginalPixels = loaded.Pixels;
        entry.HasVisiblePixels = loaded.HasVisiblePixels;
        lock (imageLoadGate)
            imageVisibilityCache[loadKey] = loaded.HasVisiblePixels;
        entry.Stride = loaded.Stride;
        entry.PixelWidth = loaded.Width;
        entry.PixelHeight = loaded.Height;
        entry.Thumbnail = loaded.Thumbnail;
    }

    private async Task EnsureEntryVisibilityAsync(SkinElementEntry entry)
    {
        if (entry.HasVisiblePixels is not null || catalog is null)
            return;

        var root = catalog.RootPath;
        var hash = entry.Hash;
        var loadKey = $"{entry.Filename}\0{hash}";
        Task<bool> loadTask;
        lock (imageLoadGate)
        {
            if (imageVisibilityCache.TryGetValue(loadKey, out var cached))
            {
                entry.HasVisiblePixels = cached;
                return;
            }
            if (!imageVisibilityLoadTasks.TryGetValue(loadKey, out loadTask!))
            {
                loadTask = Task.Run(() =>
                {
                    var bytes = realmService.ReadFile(root, hash);
                    var bitmap = SkinImageTools.Decode(bytes);
                    return SkinCursorMiddlePolicy.HasRenderablePixels(
                        entry.Filename,
                        bitmap.PixelWidth,
                        bitmap.PixelHeight,
                        SkinImageTools.Pixels(bitmap, out _));
                });
                imageVisibilityLoadTasks[loadKey] = loadTask;
            }
        }

        bool hasVisiblePixels;
        try
        {
            hasVisiblePixels = await loadTask;
        }
        finally
        {
            lock (imageLoadGate)
                if (imageVisibilityLoadTasks.GetValueOrDefault(loadKey) == loadTask)
                    imageVisibilityLoadTasks.Remove(loadKey);
        }

        lock (imageLoadGate)
            imageVisibilityCache[loadKey] = hasVisiblePixels;
        entry.HasVisiblePixels = hasVisiblePixels;
    }

    private sealed record LoadedSkinImage(
        byte[] Bytes,
        byte[] Pixels,
        int Stride,
        int Width,
        int Height,
        bool HasVisiblePixels,
        BitmapSource Thumbnail);

    private async Task SelectEntryAsync(SkinElementEntry entry)
    {
        var selectionVersion = ++selectedEntryLoadVersion;
        selectedEntry = entry;
        ImageEditorControls.Visibility = Visibility.Visible;
        ElementActionFooter.Visibility = Visibility.Visible;
        NoElementInspectorHint.Visibility = Visibility.Collapsed;
        SelectedElementName.Text = entry.Filename;
        InspectorSubtitleText.Text = entry.Filename;
        SelectedElementMeta.Text = entry.HasPairedResolution
            ? $"{FormatSize(entry.TotalSizeBytes)} · 1× + 2× files · edits save to both"
            : $"{FormatSize(entry.File.SizeBytes)} · {entry.Hash[..Math.Min(10, entry.Hash.Length)]}";
        SelectedElementUsage.Text = SkinElementSemanticGroups.UsageForFilename(entry.Filename);
        UpdateSelectedElementEffectiveStatus(entry);
        var deletionStaged = entry.PhysicalEntries.Any(physical =>
            draft?.Changes.Any(change =>
                change.IsDeletion
                && change.Filename.Equals(physical.Filename, StringComparison.OrdinalIgnoreCase)) == true);
        var transparencyStaged = entry.PhysicalEntries.Any(physical =>
            draft?.Changes.Any(change =>
                !change.IsDeletion
                && change.Filename.Equals(physical.Filename, StringComparison.OrdinalIgnoreCase)
                && SkinElementCategorizer.IsImage(change.Filename)
                && SkinImageTools.IsFullyTransparentImage(change.Bytes)) == true);
        DeleteElementButton.Content = deletionStaged ? "Deletion staged" : "Delete element…";
        DeleteElementButton.IsEnabled = !deletionStaged;
        MakeTransparentButton.Content = transparencyStaged
            ? "Transparency staged"
            : "Make transparent…";
        MakeTransparentButton.IsEnabled = entry.IsImage
            && !deletionStaged
            && !transparencyStaged
            && entry.PhysicalEntries.All(physical => Path.GetExtension(physical.Filename)
                .Equals(".png", StringComparison.OrdinalIgnoreCase));
        var related = RelatedIniTargetForEntry(entry);
        OpenElementIniLinkButton.Tag = related;
        OpenElementIniLinkButton.Visibility = related is null ? Visibility.Collapsed : Visibility.Visible;
        HighlightGameplayGroup(SkinElementSemanticGroups.ForCategory(SkinElementCategorizer.CategoryFor(entry.Filename)).Name switch
        {
            "Sliders" => SkinIniVisualGroup.Slider,
            "Cursor" => SkinIniVisualGroup.Cursor,
            "Hit objects" => SkinIniVisualGroup.HitObjects,
            "Spinner" => SkinIniVisualGroup.Spinner,
            "HUD & interface" => SkinIniVisualGroup.Interface,
            _ => SkinIniVisualGroup.Identity,
        });
        if (!entry.IsImage)
        {
            ClearElementComposition();
            GameplayLocalAssetPreview.Source = null;
            GameplayLocalPreviewPanel.Visibility = Visibility.Collapsed;
            ElementPreviewHintText.Text = entry.IsAudio
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
            DeleteElementButton.IsEnabled = !deletionStaged;
            MakeTransparentButton.IsEnabled = false;
            return;
        }

        try
        {
            await EnsureEntryLoadedAsync(entry);
            if (selectionVersion != selectedEntryLoadVersion || !ReferenceEquals(entry, selectedEntry))
                return;
            UpdateSelectedElementEffectiveStatus(entry);
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
            RenderSelectedEntry(updateComposition: false);
            await RenderElementCompositionAsync(entry, selectionVersion);
            if (selectionVersion != selectedEntryLoadVersion || !ReferenceEquals(entry, selectedEntry))
                return;
            ImageEditorControls.IsEnabled = true;
            RecolorModePicker.IsEnabled = true;
            TargetColorPanel.IsEnabled = true;
            HueShiftPanel.IsEnabled = true;
            SaveElementButton.IsEnabled = true;
            ResetElementButton.IsEnabled = true;
            ExportElementButton.IsEnabled = true;
            OpenExternallyButton.IsEnabled = true;
            DeleteElementButton.IsEnabled = !deletionStaged;
            MakeTransparentButton.IsEnabled = entry.PhysicalEntries.All(physical =>
                Path.GetExtension(physical.Filename).Equals(
                    ".png",
                    StringComparison.OrdinalIgnoreCase));
        }
        catch (Exception ex)
        {
            suppressEditorEvents = false;
            ImageEditorControls.IsEnabled = false;
            ElementPreviewHintText.Text = "Could not decode this image";
            ElementPreviewHint.Visibility = Visibility.Visible;
            StatusText.Text = ex.Message;
        }
    }

    private void UpdateSelectedElementEffectiveStatus(SkinElementEntry entry)
    {
        var component = LogicalStem(entry.Filename);
        var transparent = (entry.HasVisiblePixels == false
                ? new[] { component }
                : [])
            .Concat(draft?.Changes
                .Where(change => !change.IsDeletion
                                 && SkinElementCategorizer.IsImage(change.Filename)
                                 && SkinImageTools.IsFullyTransparentImage(change.Bytes))
                .Select(change => change.Filename)
                ?? [])
            .ToArray();
        var staged = draft?.Changes
            .Where(change => !change.IsDeletion)
            .Select(change => change.Filename)
            .ToArray() ?? [];
        var resolution = SkinStudioEffectiveAssetResolver.Resolve(
            component,
            EffectiveSkinFilenames(),
            transparent,
            staged);
        SelectedElementEffectiveLabel.Text = resolution.Label;
        SelectedElementEffectiveDetail.Text = resolution.Detail;
        SelectedElementEffectiveLabel.Foreground = TryFindResource(
            resolution.State is SkinStudioEffectiveAssetState.Missing
                or SkinStudioEffectiveAssetState.BlockedFallback
                or SkinStudioEffectiveAssetState.Transparent
                ? "Brush.AccentPink"
                : "Brush.Success") as System.Windows.Media.Brush
            ?? System.Windows.Media.Brushes.White;
    }

    private static (string Section, string Key)? RelatedIniTargetForEntry(SkinElementEntry entry) =>
        SkinElementSemanticGroups.ForCategory(SkinElementCategorizer.CategoryFor(entry.Filename)).Name switch
        {
            "Sliders" => ("Colours", "SliderBorder"),
            "Cursor" => ("General", "CursorCentre"),
            "Hit objects" => ("Colours", "Combo1"),
            "HUD & interface" => ("Colours", "MenuGlow"),
            _ => null,
        };

    private void OpenElementIniLink_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: ValueTuple<string, string> target }) return;
        SetIniCenterMode(SkinEditorCenterMode.IniForm);
        SetWorkspaceMode(SkinEditorWorkspaceMode.SkinIni);
        if (iniDocument is { } document)
            RefreshIniFormAfterLayout(document, target);
    }

    private void FocusIniRow(string section, string key)
    {
        if (!iniRows.TryGetValue((section, key), out var row)) return;
        var sectionChoice = IniSectionList.Items.Cast<IniSectionChoice>()
            .FirstOrDefault(choice => choice.Name.Equals(section, StringComparison.OrdinalIgnoreCase));
        if (sectionChoice is not null)
            IniSectionList.SelectedItem = sectionChoice;
        ShowIniSection(section);
        ShowIniContext(row);
        row.Value.Focus();
        row.Value.BringIntoView();
    }

    private void RenderSelectedEntry(
        bool invalidateComposition = false,
        bool updateComposition = true)
    {
        if (selectedEntry?.OriginalPixels is null) return;
        if (invalidateComposition)
            elementCompositionCache.Clear();
        selectedEntry.SynchronizeEditsToVariants();
        selectedEntry.Thumbnail = SkinImageTools.Render(selectedEntry);
        selectedEntry.RaiseStateChanged();
        if (updateComposition)
            UpdateSelectedCompositionLayers(showOriginal: false);
        GameplayLocalAssetPreview.Source = selectedEntry.Thumbnail;
        GameplayLocalPreviewPanel.Visibility =
            workspaceMode == SkinEditorWorkspaceMode.Elements
            && elementCenterMode == SkinEditorCenterMode.Gameplay
                ? Visibility.Visible
                : Visibility.Collapsed;
        ElementPreviewHint.Visibility = Visibility.Collapsed;
        SelectedElementMeta.Text =
            $"{FormatSize(selectedEntry.TotalSizeBytes)}"
            + (selectedEntry.HasPairedResolution ? " · 1× + 2× files · edits save to both" : "")
            + (selectedEntry.HasEdits ? " · edited (unsaved)" : "");
        UpdateDirtyState();
        RequestGameplayPreviewRefresh();
    }

    internal static SkinElementCompositionKind CompositionKindFor(string filename)
    {
        var stem = LogicalStem(filename);
        if (stem.StartsWith("followpoint", StringComparison.OrdinalIgnoreCase))
            return SkinElementCompositionKind.Followpoints;
        var numberSeparator = stem.LastIndexOf('-');
        if (numberSeparator > 0
            && int.TryParse(stem[(numberSeparator + 1)..], out _)
            && (stem.StartsWith("default-", StringComparison.OrdinalIgnoreCase)
                || stem.StartsWith("score-", StringComparison.OrdinalIgnoreCase)
                || stem.StartsWith("combo-", StringComparison.OrdinalIgnoreCase)
                || stem.StartsWith("scoreentry-", StringComparison.OrdinalIgnoreCase)))
            return SkinElementCompositionKind.Numbers;
        return SkinElementCategorizer.CategoryFor(filename) switch
        {
            "Hitcircles" => SkinElementCompositionKind.HitObject,
            "Sliders" => SkinElementCompositionKind.Slider,
            "Cursor" => SkinElementCompositionKind.Cursor,
            "Spinner" => SkinElementCompositionKind.Spinner,
            "Numbers" => SkinElementCompositionKind.Numbers,
            "Scorebar" => SkinElementCompositionKind.Scorebar,
            _ => SkinElementCompositionKind.Context,
        };
    }

    private async Task RenderElementCompositionAsync(
        SkinElementEntry entry,
        int selectionVersion)
    {
        EnsureElementCompositionCacheCurrent();
        var compositionKind = CompositionKindFor(entry.Filename);
        if (IsCacheableElementComposition(compositionKind)
            && elementCompositionCache.TryGetValue(compositionKind, out var cachedLayers))
        {
            if (!TryUpdateElementCompositionSelection(compositionKind, entry))
                RenderElementComposition(cachedLayers, entry, compositionKind);
            ElementPreviewHint.Visibility = Visibility.Collapsed;
            return;
        }

        ElementPreviewHintText.Text = "Constructing element…";
        ElementPreviewHint.Visibility = Visibility.Visible;
        IReadOnlyList<ElementLayerSpec> layers = compositionKind switch
        {
            SkinElementCompositionKind.HitObject => await BuildHitObjectCompositionAsync(),
            SkinElementCompositionKind.Followpoints => await BuildFollowpointCompositionAsync(entry),
            SkinElementCompositionKind.Slider => await BuildSliderCompositionAsync(),
            SkinElementCompositionKind.Cursor => await BuildCursorCompositionAsync(),
            SkinElementCompositionKind.Spinner => await BuildSpinnerCompositionAsync(),
            SkinElementCompositionKind.Numbers => await BuildNumberCompositionAsync(entry),
            SkinElementCompositionKind.Scorebar => await BuildScorebarCompositionAsync(),
            _ => await BuildContextCompositionAsync(entry),
        };
        if (selectionVersion != selectedEntryLoadVersion || !ReferenceEquals(entry, selectedEntry))
            return;

        if (IsCacheableElementComposition(compositionKind))
            elementCompositionCache[compositionKind] = layers;
        RenderElementComposition(layers, entry, compositionKind);
        ElementPreviewHint.Visibility = Visibility.Collapsed;
    }

    internal static bool IsCacheableElementComposition(
        SkinElementCompositionKind compositionKind) =>
        compositionKind is SkinElementCompositionKind.HitObject
            or SkinElementCompositionKind.Slider
            or SkinElementCompositionKind.Cursor
            or SkinElementCompositionKind.Spinner
            or SkinElementCompositionKind.Scorebar;

    private void EnsureElementCompositionCacheCurrent()
    {
        var revision = draft?.Revision ?? -1;
        if (cachedElementCompositionDraftRevision == revision)
            return;

        elementCompositionCache.Clear();
        cachedElementCompositionDraftRevision = revision;
    }

    private void InvalidateIniCompositionCache()
    {
        elementCompositionCache.Clear();
        cachedSliderPreviewKey = null;
        cachedSliderPreview = null;
        ResetPreviewAnimation();
    }

    private async Task<IReadOnlyList<ElementLayerSpec>> BuildHitObjectCompositionAsync()
    {
        var hitCirclePrefix = iniDocument?.GetValue("Fonts", "HitCirclePrefix");
        if (string.IsNullOrWhiteSpace(hitCirclePrefix))
            hitCirclePrefix = "default";
        var entries = await Task.WhenAll(
            FindAndLoadAsync("approachcircle"),
            FindAndLoadAsync("hitcircle"),
            FindAndLoadAsync("hitcircleoverlay"),
            FindAndLoadAsync($"{hitCirclePrefix}-1"));
        var combo = ReadIniColor("Colours", "Combo1") ?? builtInSwatches[6];
        return CompactLayers(
        [
            Layer(
                entries[0],
                320,
                240,
                330,
                330,
                Tinted(entries[0], combo),
                role: SkinPreviewAnimationRole.ApproachCircle),
            Layer(
                entries[1],
                320,
                240,
                194,
                194,
                Tinted(entries[1], combo),
                role: SkinPreviewAnimationRole.HitCircle),
            Layer(
                entries[2],
                320,
                240,
                194,
                194,
                role: SkinPreviewAnimationRole.HitCircle,
                roleIndex: 1),
            Layer(
                entries[3],
                320,
                240,
                72,
                92,
                role: SkinPreviewAnimationRole.HitCircle,
                roleIndex: 2),
        ]);
    }

    private async Task<IReadOnlyList<ElementLayerSpec>> BuildFollowpointCompositionAsync(
        SkinElementEntry selected)
    {
        var entries = await LoadCategoryEntriesAsync(selected, stem =>
            stem.StartsWith("followpoint", StringComparison.OrdinalIgnoreCase), 12);
        if (entries.Count == 0)
            entries = [selected];

        const double spacing = 32;
        var startPosition = new System.Windows.Point(80, 330);
        var endPosition = new System.Windows.Point(560, 150);
        var distanceVector = endPosition - startPosition;
        var distance = (int)distanceVector.Length;
        var rotation = Math.Atan2(
            distanceVector.Y,
            distanceVector.X) * 180 / Math.PI;
        var layers = new List<ElementLayerSpec>();
        var index = 0;
        for (var travelled = (int)(spacing * 1.5);
             travelled < distance - spacing;
             travelled += (int)spacing)
        {
            var progress = travelled / (double)distance;
            var position = startPosition + progress * distanceVector;
            var frame = entries[index % entries.Count];
            var selectedFrame = LogicalStem(frame.Filename).Equals(
                LogicalStem(selected.Filename),
                StringComparison.OrdinalIgnoreCase);
            layers.Add(Layer(
                frame,
                position.X,
                position.Y,
                48,
                48,
                contextOpacity: 0.46,
                highlightEligible: selectedFrame && !layers.Any(layer =>
                    layer.HighlightEligible
                    && LogicalStem(layer.Entry?.Filename ?? "").Equals(
                        LogicalStem(selected.Filename),
                        StringComparison.OrdinalIgnoreCase)),
                role: SkinPreviewAnimationRole.Followpoint,
                roleIndex: index,
                roleProgress: progress,
                rotationDegrees: rotation));
            index++;
        }
        return layers;
    }

    private async Task<IReadOnlyList<ElementLayerSpec>> BuildSliderCompositionAsync()
    {
        var hitCirclePrefix = iniDocument?.GetValue("Fonts", "HitCirclePrefix");
        if (string.IsNullOrWhiteSpace(hitCirclePrefix))
            hitCirclePrefix = "default";
        var startEndpoint = await ResolveSliderEndpointAsync("sliderstartcircle");
        var endEndpoint = await ResolveSliderEndpointAsync("sliderendcircle");
        var entries = await Task.WhenAll(
            FindAndLoadAsync($"{hitCirclePrefix}-1"),
            FindAndLoadAsync("sliderb0", "sliderb", "sliderball"),
            FindAndLoadAsync("sliderfollowcircle"),
            FindAndLoadAsync("reversearrow"),
            FindAndLoadAsync("approachcircle"));
        var combos = Enumerable.Range(1, 8)
            .Select(index => ReadIniColor("Colours", $"Combo{index}"))
            .Where(color => color.HasValue)
            .Select(color => color!.Value)
            .ToArray();
        var combo = combos.Length > 1 ? combos[1] : combos.FirstOrDefault(builtInSwatches[6]);
        var sliderBorder = ReadIniColor("Colours", "SliderBorder");
        var sliderTrack = ReadIniColor("Colours", "SliderTrackOverride");
        var sliderBody = await GetSliderBodyAsync(
            combo,
            sliderBorder,
            sliderTrack,
            CancellationToken.None);
        var geometry = SliderCompositionGeometryFor();
        return CompactLayers(
        [
            new ElementLayerSpec(
                null,
                sliderBody,
                320,
                240,
                geometry.BodyWidth,
                geometry.BodyHeight,
                0.82,
                false,
                0,
                SkinPreviewAnimationRole.None,
                0,
                0),
            Layer(
                entries[4],
                geometry.Start.X,
                geometry.Start.Y,
                geometry.CircleDiameter * 1.67,
                geometry.CircleDiameter * 1.67,
                Tinted(entries[4], combo),
                role: SkinPreviewAnimationRole.ApproachCircle),
            Layer(
                startEndpoint.Base,
                geometry.Start.X,
                geometry.Start.Y,
                geometry.CircleDiameter,
                geometry.CircleDiameter,
                Tinted(startEndpoint.Base, combo)),
            Layer(
                startEndpoint.Overlay,
                geometry.Start.X,
                geometry.Start.Y,
                geometry.CircleDiameter,
                geometry.CircleDiameter),
            Layer(entries[0], geometry.Start.X, geometry.Start.Y, 27, 36),
            Layer(
                endEndpoint.Base,
                geometry.End.X,
                geometry.End.Y,
                geometry.CircleDiameter,
                geometry.CircleDiameter,
                Tinted(endEndpoint.Base, combo)),
            Layer(
                endEndpoint.Overlay,
                geometry.End.X,
                geometry.End.Y,
                geometry.CircleDiameter,
                geometry.CircleDiameter),
            Layer(
                entries[3],
                geometry.End.X,
                geometry.End.Y,
                geometry.ReverseDiameter,
                geometry.ReverseDiameter,
                rotationDegrees: geometry.ReverseRotation,
                role: SkinPreviewAnimationRole.ReverseArrow),
            Layer(
                entries[2],
                geometry.Ball.X,
                geometry.Ball.Y,
                geometry.FollowDiameter,
                geometry.FollowDiameter,
                role: SkinPreviewAnimationRole.SliderFollowCircle),
            Layer(
                entries[1],
                geometry.Ball.X,
                geometry.Ball.Y,
                geometry.BallDiameter,
                geometry.BallDiameter,
                role: SkinPreviewAnimationRole.SliderBall),
        ]);
    }

    private async Task<(SkinElementEntry? Base, SkinElementEntry? Overlay)>
        ResolveSliderEndpointAsync(string component)
    {
        var filenames = EffectiveSkinFilenames();
        var resolvedBase = SkinStudioEffectiveAssetResolver.Resolve(component, filenames);
        var resolvedOverlay = SkinStudioEffectiveAssetResolver.Resolve(
            component + "overlay",
            filenames);
        return (
            await FindAndLoadAsync(resolvedBase.ResolvedComponent ?? component),
            await FindAndLoadAsync(resolvedOverlay.ResolvedComponent ?? component + "overlay"));
    }

    private IReadOnlyList<string> EffectiveSkinFilenames() =>
        currentSkin is null
            ? []
            : SkinDraftProjection.EffectiveFiles(
                    currentSkin.Files,
                    draft?.Changes ?? [])
                .Select(file => file.Filename)
                .ToArray();

    internal static SliderCompositionGeometry SliderCompositionGeometryFor(
        double bodyWidth = 590)
    {
        const double sourceWidth = 800;
        const double sourceHeight = 300;
        const double sliderRadius = 46;
        var bodyHeight = bodyWidth * sourceHeight / sourceWidth;
        var bodyLeft = (640 - bodyWidth) / 2;
        var bodyTop = (480 - bodyHeight) / 2;
        var path = LegacySliderRenderer.SampleSCurve(127, 177, 676, 102, segments: 128);
        System.Windows.Point Map(System.Windows.Point point) =>
            new(
                bodyLeft + point.X / sourceWidth * bodyWidth,
                bodyTop + point.Y / sourceHeight * bodyHeight);

        var start = Map(path[0]);
        var end = Map(path[^1]);
        var ball = Map(path[(int)Math.Round((path.Count - 1) * 0.58)]);
        var reverseVector = path[^2] - path[^1];
        var reverseRotation =
            Math.Atan2(reverseVector.Y, reverseVector.X) * 180 / Math.PI;
        var circleDiameter = sliderRadius * 2 * bodyWidth / sourceWidth;
        return new SliderCompositionGeometry(
            bodyWidth,
            bodyHeight,
            start,
            end,
            ball,
            circleDiameter,
            circleDiameter * 1.55,
            circleDiameter,
            circleDiameter * 0.82,
            reverseRotation);
    }

    private static IReadOnlyList<System.Windows.Point>
        BuildElementSliderPreviewPath()
    {
        const double bodyWidth = 590;
        const double sourceWidth = 800;
        const double sourceHeight = 300;
        var bodyHeight = bodyWidth * sourceHeight / sourceWidth;
        var bodyLeft = (SkinCursorPreview.CanvasWidth - bodyWidth) / 2;
        var bodyTop = (SkinCursorPreview.CanvasHeight - bodyHeight) / 2;
        return GameplaySliderPreviewPath
            .Select(point => new System.Windows.Point(
                bodyLeft + point.X / sourceWidth * bodyWidth,
                bodyTop + point.Y / sourceHeight * bodyHeight))
            .ToArray();
    }

    private async Task<IReadOnlyList<ElementLayerSpec>> BuildCursorCompositionAsync()
    {
        if (currentSkin is null)
            return [];
        var assets = SkinCursorPreview.Resolve(
            SkinDraftProjection.EffectiveFiles(
                    currentSkin.Files,
                    draft?.Changes ?? [])
                .Select(file => file.Filename));
        renderedCursorUsesSmoothTrail = assets.UsesSmoothTrail;
        var entries = await Task.WhenAll(
            FindAndLoadEffectiveFileAsync(assets.TrailFilename),
            FindAndLoadEffectiveFileAsync(assets.MiddleFilename),
            FindAndLoadEffectiveFileAsync(assets.CursorFilename));
        var composition = SkinCursorPreview.Compose(
            entries[2] is not null,
            entries[0] is not null,
            assets.UsesSmoothTrail,
            ShouldRenderPreviewImage(entries[1]));
        var layers = new List<ElementLayerSpec>(composition.Count);
        foreach (var layer in composition)
        {
            var entry = layer.Kind switch
            {
                SkinCursorPreviewLayerKind.Trail => entries[0],
                SkinCursorPreviewLayerKind.Middle => entries[1],
                SkinCursorPreviewLayerKind.Cursor => entries[2],
                _ => null,
            };
            layers.Add(Layer(
                entry,
                layer.CentreX,
                layer.CentreY,
                layer.MaxWidth,
                layer.MaxHeight,
                contextOpacity: layer.Opacity,
                highlightEligible: layer.Kind != SkinCursorPreviewLayerKind.Trail
                                   || layer.Equals(composition.Last(candidate =>
                                       candidate.Kind == SkinCursorPreviewLayerKind.Trail)),
                role: layer.Kind switch
                {
                    SkinCursorPreviewLayerKind.Trail =>
                        SkinPreviewAnimationRole.CursorTrail,
                    SkinCursorPreviewLayerKind.Middle =>
                        SkinPreviewAnimationRole.CursorMiddle,
                    _ => SkinPreviewAnimationRole.Cursor,
                },
                roleIndex: layer.Kind == SkinCursorPreviewLayerKind.Trail
                    ? layers.Count
                    : 0));
        }
        return CompactLayers(layers);
    }

    internal static IReadOnlyList<System.Windows.Point> BuildCursorCompositionTrailPoints(
        bool smooth)
        => SkinCursorPreview.TrailPoints(smooth)
            .Select(point => new System.Windows.Point(point.X, point.Y))
            .ToArray();

    private async Task<IReadOnlyList<ElementLayerSpec>> BuildSpinnerCompositionAsync()
    {
        var entries = await Task.WhenAll(
            FindAndLoadAsync("spinner-background"),
            FindAndLoadAsync("spinner-circle"),
            FindAndLoadAsync("spinner-glow"),
            FindAndLoadAsync("spinner-bottom"),
            FindAndLoadAsync("spinner-top"),
            FindAndLoadAsync("spinner-middle2"),
            FindAndLoadAsync("spinner-middle"),
            FindAndLoadAsync("spinner-approachcircle"),
            FindAndLoadAsync("spinner-spin"),
            FindAndLoadAsync("spinner-clear"),
            FindAndLoadAsync("spinner-metre"),
            FindAndLoadAsync("spinner-rpm"));
        var spinnerColour = ReadIniColor("Colours", "SpinnerBackground")
                            ?? Color.FromRgb(92, 112, 150);
        return CompactLayers(
        [
            Layer(entries[0], 320, 238, 360, 360, Tinted(entries[0], spinnerColour)),
            Layer(
                entries[1],
                320,
                238,
                360,
                360,
                role: SkinPreviewAnimationRole.SpinnerCircle),
            Layer(
                entries[2],
                320,
                238,
                360,
                360,
                role: SkinPreviewAnimationRole.SpinnerGlow),
            Layer(
                entries[3],
                320,
                238,
                360,
                360,
                role: SkinPreviewAnimationRole.SpinnerBottom),
            Layer(
                entries[4],
                320,
                238,
                360,
                360,
                role: SkinPreviewAnimationRole.SpinnerTop),
            Layer(
                entries[5],
                320,
                238,
                360,
                360,
                role: SkinPreviewAnimationRole.SpinnerMiddle2),
            Layer(
                entries[6],
                320,
                238,
                360,
                360,
                role: SkinPreviewAnimationRole.SpinnerMiddle),
            Layer(
                entries[7],
                320,
                238,
                420,
                420,
                role: SkinPreviewAnimationRole.SpinnerApproach),
            Layer(
                entries[10],
                116,
                245,
                200,
                420,
                role: SkinPreviewAnimationRole.SpinnerMetre),
            Layer(
                entries[9],
                320,
                122,
                250,
                72,
                role: SkinPreviewAnimationRole.SpinnerClear),
            Layer(
                entries[8],
                320,
                362,
                250,
                72,
                role: SkinPreviewAnimationRole.SpinnerSpin),
            Layer(entries[11], 488, 410, 118, 40),
        ]);
    }

    private async Task<IReadOnlyList<ElementLayerSpec>> BuildNumberCompositionAsync(
        SkinElementEntry selected)
    {
        var selectedCategory = SkinElementCategorizer.CategoryFor(selected.Filename);
        var numberEntries = categories
            .Where(category => category.Name.Equals(
                selectedCategory,
                StringComparison.OrdinalIgnoreCase))
            .SelectMany(item => item.Files)
            .Where(item => item.IsImage)
            .Select(item => (
                Entry: item,
                Glyph: NumberGlyphParts(LogicalStem(item.Filename))))
            .Where(item => item.Glyph is not null)
            .Select(item => (
                item.Entry,
                Prefix: item.Glyph!.Value.Prefix,
                Suffix: item.Glyph.Value.Suffix))
            .ToArray();

        if (!showFullElementRender)
        {
            var selectedParts = NumberGlyphParts(LogicalStem(selected.Filename));
            var selectedPrefix = selectedParts?.Prefix;
            numberEntries = numberEntries
                .Where(item => selectedPrefix is null
                               || item.Prefix.Equals(
                                   selectedPrefix,
                                   StringComparison.OrdinalIgnoreCase))
                .ToArray();
        }

        foreach (var batch in numberEntries.Chunk(6))
            await Task.WhenAll(batch.Select(item =>
                EnsureEntryLoadedAsync(item.Entry)));
        if (numberEntries.Length == 0)
            return [Layer(selected, 320, 240, 96, 140)];

        var groups = numberEntries
            .GroupBy(item => item.Prefix, StringComparer.OrdinalIgnoreCase)
            .OrderBy(group => NumberPrefixOrder(group.Key))
            .ThenBy(group => group.Key, StringComparer.OrdinalIgnoreCase)
            .Select(group => group
                .OrderBy(item => NumberSuffixOrder(item.Suffix))
                .ThenBy(item => item.Suffix, StringComparer.OrdinalIgnoreCase)
                .ToArray())
            .ToArray();
        var layers = new List<ElementLayerSpec>();
        var rowHeight = Math.Min(150d, 390d / groups.Length);
        var top = (480 - rowHeight * groups.Length) / 2;
        for (var row = 0; row < groups.Length; row++)
        {
            var group = groups[row];
            var y = groups.Length == 1
                ? 240
                : top + rowHeight / 2 + row * rowHeight;
            var glyphWidth = Math.Min(66d, 540d / group.Length);
            for (var column = 0; column < group.Length; column++)
            {
                var x = group.Length == 1
                    ? 320
                    : 48 + column * 544d / (group.Length - 1);
                layers.Add(Layer(
                    group[column].Entry,
                    x,
                    y,
                    glyphWidth,
                    Math.Min(122, rowHeight * 0.76),
                    contextOpacity: 0.5));
            }
        }
        return layers;
    }

    internal static (string Prefix, string Suffix)? NumberGlyphParts(string stem)
    {
        var separator = stem.LastIndexOf('-');
        if (separator <= 0 || separator == stem.Length - 1)
            return null;
        var suffix = stem[(separator + 1)..];
        if (!int.TryParse(suffix, out _)
            && suffix is not ("x" or "X" or "comma" or "dot" or "percent"))
            return null;
        return (stem[..separator], suffix);
    }

    private static int NumberPrefixOrder(string prefix) =>
        prefix.ToLowerInvariant() switch
        {
            "default" => 0,
            "score" => 1,
            "combo" => 2,
            "scoreentry" => 3,
            _ => 4,
        };

    private static int NumberSuffixOrder(string suffix) =>
        int.TryParse(suffix, out var digit)
            ? digit
            : suffix.ToLowerInvariant() switch
            {
                "x" => 10,
                "comma" => 11,
                "dot" => 12,
                "percent" => 13,
                _ => 14,
            };

    private async Task<IReadOnlyList<ElementLayerSpec>> BuildScorebarCompositionAsync()
    {
        var entries = await Task.WhenAll(
            FindAndLoadAsync("scorebar-bg"),
            FindAndLoadAsync("scorebar-colour"),
            FindAndLoadAsync("scorebar-ki", "scorebar-marker"));
        return CompactLayers(
        [
            Layer(entries[0], 320, 225, 560, 270, contextOpacity: 0.48),
            Layer(entries[1], 320, 225, 540, 250, contextOpacity: 0.62),
            Layer(
                entries[2],
                420,
                225,
                110,
                110,
                role: SkinPreviewAnimationRole.ScorebarMarker),
        ]);
    }

    private async Task<IReadOnlyList<ElementLayerSpec>> BuildContextCompositionAsync(
        SkinElementEntry selected)
    {
        var category = SkinElementCategorizer.CategoryFor(selected.Filename);
        var entries = categories
            .Where(item => item.Name.Equals(category, StringComparison.OrdinalIgnoreCase))
            .SelectMany(item => item.Files)
            .Where(item => item.IsImage)
            .OrderByDescending(item => ReferenceEquals(item, selected))
            .ThenBy(item => item.Filename, StringComparer.OrdinalIgnoreCase)
            .Take(9)
            .ToArray();
        foreach (var batch in entries.Chunk(4))
            await Task.WhenAll(batch.Select(EnsureEntryLoadedAsync));
        var layers = new List<ElementLayerSpec>();
        for (var index = 0; index < entries.Length; index++)
        {
            var column = index % 3;
            var row = index / 3;
            layers.Add(Layer(
                entries[index],
                150 + column * 170,
                125 + row * 120,
                112,
                92,
                contextOpacity: 0.42));
        }
        return layers;
    }

    private async Task<IReadOnlyList<SkinElementEntry>> LoadCategoryEntriesAsync(
        SkinElementEntry selected,
        Func<string, bool> predicate,
        int limit)
    {
        var category = SkinElementCategorizer.CategoryFor(selected.Filename);
        var entries = categories
            .Where(item => item.Name.Equals(category, StringComparison.OrdinalIgnoreCase))
            .SelectMany(item => item.Files)
            .Where(item => item.IsImage && predicate(LogicalStem(item.Filename)))
            .OrderBy(item => item.Filename, StringComparer.OrdinalIgnoreCase)
            .Take(limit)
            .ToArray();
        foreach (var batch in entries.Chunk(4))
            await Task.WhenAll(batch.Select(EnsureEntryLoadedAsync));
        return entries;
    }

    private void RenderElementComposition(
        IReadOnlyList<ElementLayerSpec> requestedLayers,
        SkinElementEntry selected,
        SkinElementCompositionKind compositionKind)
    {
        ClearElementComposition();
        var layers = requestedLayers
            .Where(layer => layer.Source is not null
                            && ShouldRenderPreviewImage(layer.Entry))
            .ToList();
        var selectedStem = LogicalStem(selected.Filename);
        var hasSelected = layers.Any(layer =>
            layer.HighlightEligible
            && layer.Entry is not null
            && LogicalStem(layer.Entry.Filename).Equals(
                selectedStem,
                StringComparison.OrdinalIgnoreCase));
        if (!showFullElementRender
            && ShouldRenderPreviewImage(selected)
            && !hasSelected)
            layers.Add(Layer(selected, 320, 240, 300, 300));

        foreach (var layer in layers)
            AddCompositionLayer(layer, selected: false);
        renderedElementCompositionKind = compositionKind;
        _ = TryUpdateElementCompositionSelection(compositionKind, selected);
        RenderPreviewFrame();
    }

    private bool TryUpdateElementCompositionSelection(
        SkinElementCompositionKind compositionKind,
        SkinElementEntry selected)
    {
        if (renderedElementCompositionKind != compositionKind
            || elementCompositionVisuals.Count == 0)
            return false;
        var selectedStem = LogicalStem(selected.Filename);
        if (!showFullElementRender
            && ShouldRenderPreviewImage(selected)
            && !elementCompositionVisuals.Any(visual =>
                IsSelectedCompositionLayer(visual.Layer, selectedStem)))
            return false;

        foreach (var decoration in ElementCompositionCanvas.Children
                     .OfType<FrameworkElement>()
                     .Where(child => ReferenceEquals(
                         child.Tag,
                         ElementCompositionDecorationTag))
                     .ToArray())
        {
            ElementCompositionCanvas.Children.Remove(decoration);
        }
        selectedCompositionLayers.Clear();
        foreach (var visual in elementCompositionVisuals)
        {
            var isSelected = !showFullElementRender
                             && IsSelectedCompositionLayer(visual.Layer, selectedStem);
            visual.Image.Source = isSelected
                ? selected.Thumbnail ?? visual.Layer.Source
                : visual.Layer.Source;
            visual.Image.Opacity = showFullElementRender || isSelected
                ? 1
                : visual.Layer.ContextOpacity;
            visual.Image.Effect = isSelected
                ? ElementCompositionSelectionGlow
                : null;
            Panel.SetZIndex(visual.Image, isSelected ? 500 : visual.BaseZIndex);
            if (!isSelected)
                continue;
            selectedCompositionLayers.Add(visual.Image);
            ElementCompositionCanvas.Children.Add(
                CreateElementCompositionOutline(visual));
        }
        if (showFullElementRender)
            return true;

        var badge = new Border
        {
            Tag = ElementCompositionDecorationTag,
            Padding = new Thickness(9, 4, 9, 4),
            CornerRadius = new CornerRadius(10),
            Background = new SolidColorBrush(Color.FromArgb(220, 24, 14, 20)),
            BorderThickness = new Thickness(1),
            Child = new TextBlock
            {
                Text = $"SELECTED LAYER  ·  {selected.Filename}",
                FontSize = 10,
                FontWeight = FontWeights.SemiBold,
                Foreground = new SolidColorBrush(Colors.White),
            },
        };
        badge.SetResourceReference(Border.BorderBrushProperty, "Brush.AccentPink");
        Canvas.SetLeft(badge, 16);
        Canvas.SetTop(badge, 16);
        Panel.SetZIndex(badge, 1000);
        ElementCompositionCanvas.Children.Add(badge);
        return true;
    }

    private void AddCompositionLayer(ElementLayerSpec layer, bool selected)
    {
        if (layer.Source is null)
            return;
        var sourceWidth = layer.Entry is null
            ? layer.Source.PixelWidth
            : layer.Entry.PixelWidth / (layer.Entry.IsHighResolution ? 2d : 1d);
        var sourceHeight = layer.Entry is null
            ? layer.Source.PixelHeight
            : layer.Entry.PixelHeight / (layer.Entry.IsHighResolution ? 2d : 1d);
        sourceWidth = Math.Max(1, sourceWidth);
        sourceHeight = Math.Max(1, sourceHeight);
        var scale = Math.Min(layer.MaxWidth / sourceWidth, layer.MaxHeight / sourceHeight);
        var width = Math.Max(1, sourceWidth * scale);
        var height = Math.Max(1, sourceHeight * scale);
        var image = selected && selectedCompositionLayers.Count == 0
            ? ElementPreview
            : new System.Windows.Controls.Image();
        image.Source = selected ? selectedEntry?.Thumbnail ?? layer.Source : layer.Source;
        image.Width = width;
        image.Height = height;
        image.Stretch = Stretch.Fill;
        image.Opacity = selected ? 1 : layer.ContextOpacity;
        image.Visibility = Visibility.Visible;
        image.RenderTransformOrigin = new System.Windows.Point(0.5, 0.5);
        image.RenderTransform = Math.Abs(layer.RotationDegrees) > 0.01
            ? new RotateTransform(layer.RotationDegrees)
            : Transform.Identity;
        image.Effect = selected
            ? new DropShadowEffect
            {
                Color = Color.FromRgb(236, 73, 142),
                BlurRadius = 26,
                ShadowDepth = 0,
                Opacity = 0.95,
            }
            : null;
        Canvas.SetLeft(image, layer.CentreX - width / 2);
        Canvas.SetTop(image, layer.CentreY - height / 2);
        Panel.SetZIndex(image, selected ? 500 : 10);
        ElementCompositionCanvas.Children.Add(image);
        elementCompositionVisuals.Add(new ElementCompositionVisual(
            image,
            layer,
            width,
            height,
            10));
        if (!selected)
            return;

        selectedCompositionLayers.Add(image);
        var outline = new Border
        {
            Width = width + 12,
            Height = height + 12,
            BorderThickness = new Thickness(2),
            CornerRadius = new CornerRadius(8),
            Background = System.Windows.Media.Brushes.Transparent,
            IsHitTestVisible = false,
        };
        outline.SetResourceReference(Border.BorderBrushProperty, "Brush.AccentPink");
        Canvas.SetLeft(outline, layer.CentreX - width / 2 - 6);
        Canvas.SetTop(outline, layer.CentreY - height / 2 - 6);
        Panel.SetZIndex(outline, 510);
        ElementCompositionCanvas.Children.Add(outline);
    }

    private static Border CreateElementCompositionOutline(
        ElementCompositionVisual visual)
    {
        var outline = new Border
        {
            Tag = ElementCompositionDecorationTag,
            Width = visual.Width + 12,
            Height = visual.Height + 12,
            BorderThickness = new Thickness(2),
            CornerRadius = new CornerRadius(8),
            Background = System.Windows.Media.Brushes.Transparent,
            IsHitTestVisible = false,
        };
        outline.SetResourceReference(
            Border.BorderBrushProperty,
            "Brush.AccentPink");
        Canvas.SetLeft(
            outline,
            visual.Layer.CentreX - visual.Width / 2 - 6);
        Canvas.SetTop(
            outline,
            visual.Layer.CentreY - visual.Height / 2 - 6);
        Panel.SetZIndex(outline, 510);
        return outline;
    }

    private static DropShadowEffect CreateElementCompositionSelectionGlow()
    {
        var glow = new DropShadowEffect
        {
            Color = Color.FromRgb(236, 73, 142),
            BlurRadius = 26,
            ShadowDepth = 0,
            Opacity = 0.95,
        };
        if (glow.CanFreeze)
            glow.Freeze();
        return glow;
    }

    private void UpdateSelectedCompositionLayers(bool showOriginal)
    {
        if (selectedEntry?.OriginalPixels is null)
            return;
        var source = showOriginal
            ? SkinImageTools.ToBitmap(
                selectedEntry.OriginalPixels,
                selectedEntry.PixelWidth,
                selectedEntry.PixelHeight,
                selectedEntry.Stride)
            : selectedEntry.Thumbnail;
        foreach (var image in selectedCompositionLayers)
            image.Source = source;
    }

    private void ClearElementComposition()
    {
        if (ElementCompositionCanvas is null)
            return;
        EndInteractiveCursorPreview(restoreComposition: false);
        foreach (var visual in elementCompositionVisuals)
            previewVisualTransforms.Remove(visual.Image);
        ElementCompositionCanvas.Children.Clear();
        selectedCompositionLayers.Clear();
        elementCompositionVisuals.Clear();
        renderedElementCompositionKind = null;
        ElementPreview.Source = null;
        ElementPreview.Visibility = Visibility.Collapsed;
    }

    private static bool IsSelectedCompositionLayer(
        ElementLayerSpec layer,
        string selectedStem) =>
        layer.HighlightEligible
        && layer.Entry is not null
        && LogicalStem(layer.Entry.Filename).Equals(
            selectedStem,
            StringComparison.OrdinalIgnoreCase);

    internal static bool ShouldRenderPreviewImage(SkinElementEntry? entry) =>
        entry?.HasVisiblePixels != false
        && (entry is null
            || !SkinCursorMiddlePolicy.IsOnePixelPlaceholder(
                entry.Filename,
                entry.PixelWidth,
                entry.PixelHeight));

    private static ElementLayerSpec Layer(
        SkinElementEntry? entry,
        double centreX,
        double centreY,
        double maxWidth,
        double maxHeight,
        BitmapSource? source = null,
        double contextOpacity = 0.34,
        bool highlightEligible = true,
        double rotationDegrees = 0,
        SkinPreviewAnimationRole role = SkinPreviewAnimationRole.None,
        int roleIndex = 0,
        double roleProgress = 0) =>
        new(
            entry,
            source ?? entry?.Thumbnail,
            centreX,
            centreY,
            maxWidth,
            maxHeight,
            contextOpacity,
            highlightEligible,
            rotationDegrees,
            role,
            roleIndex,
            roleProgress);

    private static IReadOnlyList<ElementLayerSpec> CompactLayers(
        IEnumerable<ElementLayerSpec> layers) =>
        layers.Where(layer => layer.Source is not null
                              && ShouldRenderPreviewImage(layer.Entry))
            .ToArray();

    private sealed record ElementLayerSpec(
        SkinElementEntry? Entry,
        BitmapSource? Source,
        double CentreX,
        double CentreY,
        double MaxWidth,
        double MaxHeight,
        double ContextOpacity,
        bool HighlightEligible,
        double RotationDegrees,
        SkinPreviewAnimationRole Role,
        int RoleIndex,
        double RoleProgress);

    private sealed record ElementCompositionVisual(
        System.Windows.Controls.Image Image,
        ElementLayerSpec Layer,
        double Width,
        double Height,
        int BaseZIndex);

    private sealed class PreviewVisualTransforms
    {
        public PreviewVisualTransforms(double baseRotation)
        {
            BaseRotation = baseRotation;
            Group = new TransformGroup
            {
                Children =
                {
                    Scale,
                    Rotate,
                    Translate,
                },
            };
        }

        public double BaseRotation { get; }
        public ScaleTransform Scale { get; } = new(1, 1);
        public RotateTransform Rotate { get; } = new();
        public TranslateTransform Translate { get; } = new();
        public TransformGroup Group { get; }
    }

    private readonly record struct InteractiveCursorSample(
        System.Windows.Point Position,
        double Time,
        double Scale,
        double Rotation);

    internal readonly record struct SliderCompositionGeometry(
        double BodyWidth,
        double BodyHeight,
        System.Windows.Point Start,
        System.Windows.Point End,
        System.Windows.Point Ball,
        double CircleDiameter,
        double FollowDiameter,
        double BallDiameter,
        double ReverseDiameter,
        double ReverseRotation);

    private void RequestElementRender()
    {
        if (!elementRenderTimer.IsEnabled)
            elementRenderTimer.Start();
    }

    private void RequestGameplayPreviewRefresh()
    {
        gameplayPreviewRefreshTimer.Stop();
        gameplayPreviewRefreshTimer.Start();
    }

    private async Task<bool> SaveEntryAsync(SkinElementEntry entry)
    {
        if (!entry.HasEdits || draft is null)
            return true;
        if (settings.Current.SkinEditor.AutoBackupElements
            && !await BackupElementFilesAsync(entry.PhysicalEntries.Select(physical => physical.File)))
            return false;
        entry.SynchronizeEditsToVariants();
        var staged = new List<SkinDraftChange>();
        var actionId = $"recolour:{Guid.NewGuid():N}";
        var actionLabel = $"Recolour · {LogicalStem(entry.Filename)}";
        foreach (var physicalEntry in entry.PhysicalEntries)
        {
            await EnsureEntryLoadedAsync(physicalEntry);
            var bitmap = SkinImageTools.Render(physicalEntry);
            var bytes = SkinImageTools.Encode(bitmap, physicalEntry.Filename);
            staged.Add(new SkinDraftChange(
                physicalEntry.Filename,
                physicalEntry.Hash,
                bytes,
                $"{physicalEntry.Filename} (recolour)",
                SkinDraftOperation.Upsert,
                actionId,
                actionLabel));
        }
        draft.StageRange(staged);
        StatusText.Text = $"{entry.Filename} added to Changes. Save to osu!lazer when ready.";
        UpdateDirtyState();
        return true;
    }

    private async Task<bool> SaveAllAsync(bool confirm = true)
    {
        if (catalog is null || currentSkin is null || draft is null)
            return true;
        if (pendingDuplicate is { } duplicate
            && duplicate.WorkingSkinId == currentSkin.Id)
        {
            return await ExportAndImportDuplicateAsync(duplicate, confirm);
        }
        if (draft.Count == 0)
            return true;
        var pending = draft.Changes.ToArray();
        var added = pending.Count(change => !change.IsDeletion && change.ExpectedHash is null);
        var replaced = pending.Count(change => !change.IsDeletion && change.ExpectedHash is not null);
        var deleted = pending.Count(change => change.IsDeletion);
        var preflight = await BuildCurrentPreflightAsync();
        var selectedCategoryName =
            (CategoryPicker.SelectedItem as CategoryChoice)?.Category.Name;
        var selectedFilename = selectedEntry?.Filename;
        if (confirm && KumoriDialog.Show(
                Window.GetWindow(this),
                $"Save {pending.Length} change{(pending.Length == 1 ? "" : "s")} to osu!lazer?\n\n"
                + $"{added} added · {replaced} replaced · {deleted} deleted\n\n"
                + preflight.Summary + "\n"
                + (preflight.Issues.Count == 0
                    ? ""
                    : string.Join("\n", preflight.Issues.Take(3)
                        .Select(issue => $"• {issue.Message}")) + "\n")
                + "\nKumori will create a restore point before writing anything.",
                "Save skin to osu!lazer",
                MessageBoxButton.YesNo,
                MessageBoxImage.Question,
                MessageBoxResult.Yes) != MessageBoxResult.Yes)
            return false;
        SetBusy(true, $"Applying {draft.Count} staged skin change(s)…");
        try
        {
            await Dispatcher.Yield(DispatcherPriority.Background);
            StatusText.Text = "Checking the current osu!lazer skin…";
            var latest = await Task.Run(() => realmService.LoadCatalog(catalog.RootPath));
            var latestSkin = latest.Skins.FirstOrDefault(skin => skin.Id == currentSkin.Id);
            if (latestSkin is null)
            {
                StatusText.Text = "Apply blocked: the selected skin no longer exists in osu!lazer.";
                return false;
            }

            var currentFiles = latestSkin.Files.ToDictionary(file => file.Filename, StringComparer.OrdinalIgnoreCase);
            var conflicts = draft.Changes.Where(change =>
            {
                var exists = currentFiles.TryGetValue(change.Filename, out var file);
                return change.ExpectedHash is null ? exists : !exists || !string.Equals(file!.Hash, change.ExpectedHash, StringComparison.OrdinalIgnoreCase);
            }).ToArray();
            if (conflicts.Length > 0)
            {
                var choice = KumoriDialog.Show(
                    Window.GetWindow(this),
                    $"{conflicts.Length} staged file(s) changed in osu!lazer outside this editor.\n\n"
                    + "Yes: reload the skin and discard this session\n"
                    + "No: discard only the conflicting drafts\n"
                    + "Cancel: keep every draft unchanged",
                    "Skin changed externally",
                    MessageBoxButton.YesNoCancel,
                    MessageBoxImage.Warning,
                    MessageBoxResult.Cancel);
                if (choice == MessageBoxResult.Yes)
                {
                    draft.AcceptApplied();
                    await Task.Run(() => SkinDraftRecovery.Clear(currentSkin.Id));
                    await ReloadCurrentSkinAsync();
                    StatusText.Text = "Reloaded the current osu!lazer skin; the draft session was discarded.";
                }
                else if (choice == MessageBoxResult.No)
                {
                    draft.AcceptCommitted(conflicts.Select(change => change.Filename));
                    StatusText.Text = $"Discarded {conflicts.Length} conflicting draft(s). The remaining changes are still staged.";
                    UpdateDirtyState();
                }
                else
                {
                    StatusText.Text = "Save cancelled; every pending change was kept.";
                }
                return false;
            }

            if (settings.Current.SkinEditor.AutoBackupElements)
            {
                StatusText.Text = "Backing up the original edited files…";
                var originals = draft.Changes
                    .Where(change => change.ExpectedHash is not null)
                    .Select(change => currentFiles.GetValueOrDefault(change.Filename))
                    .Where(file => file is not null)
                    .Cast<LazerSkinFileInfo>()
                    .ToArray();
                if (!await BackupElementFilesAsync(originals))
                    return false;
            }

            if (!await EnsureBackupAsync()) return false;
            StatusText.Text =
                $"Writing {pending.Length} skin change{(pending.Length == 1 ? "" : "s")}…";
            var staged = draft.Changes.ToArray();
            var mutations = staged.Select(change => new LazerSkinBatchMutation(
                change.Filename,
                change.Bytes,
                change.ExpectedHash,
                change.IsDeletion)).ToArray();
            var batch = await Task.Run(() => realmService.ApplyBatch(
                catalog.RootPath,
                currentSkin.Id,
                mutations));
            if (!batch.Succeeded)
            {
                var result = batch.Results.FirstOrDefault();
                if (result is not null)
                    HandleWriteResult(result, batch.FailedFilename ?? "skin file");
                StatusText.Text =
                    $"Nothing was applied. {batch.FailedFilename ?? "A staged file"} changed or disappeared; every draft was preserved.";
                UpdateDirtyState();
                return false;
            }

            var refreshedSkin = SkinEditorCatalogProjection.ApplyBatch(
                latestSkin,
                mutations,
                batch);
            draft.AcceptApplied();
            await Task.Run(() => SkinDraftRecovery.Clear(refreshedSkin.Id));
            StatusText.Text = "Refreshing the saved skin…";
            await RefreshAppliedSkinAsync(
                latest,
                refreshedSkin,
                selectedCategoryName,
                selectedFilename);
            var savedStatus =
                $"Saved to osu!lazer: {added} added, {replaced} replaced, {deleted} deleted. Restore point created.";
            if (reloadService is null)
            {
                StatusText.Text = savedStatus;
            }
            else
            {
                StatusText.Text = $"{savedStatus} Lazer reload queued.";
                var savedSkinId = refreshedSkin.Id;
                reloadService.RequestReload(
                    latest.RootPath,
                    savedSkinId,
                    result =>
                    {
                        if (currentSkin?.Id != savedSkinId || (draft?.Count ?? 0) != 0)
                            return;
                        StatusText.Text = $"{savedStatus} {result.Message}";
                    });
            }
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

    private async Task<SkinStudioPreflightReport> BuildCurrentPreflightAsync()
    {
        if (currentSkin is null || catalog is null)
            return SkinStudioEffectiveAssetResolver.BuildPreflight([]);
        var source = CreateExtrasCurrentSkinSource();
        var described = new List<SkinExtraManifestFile>();
        foreach (var filename in source.Filenames.Where(filename =>
                     SkinElementCategorizer.IsImage(filename)
                     || SkinElementCategorizer.IsAudio(filename)))
        {
            try
            {
                var bytes = await source.ReadFileAsync(filename, CancellationToken.None);
                if (bytes is not null)
                    described.Add(await Task.Run(() =>
                        SkinExtraFingerprint.Describe(filename, filename, bytes)));
            }
            catch
            {
                // Save performs strict hash/concurrency checks; preflight can
                // still report the assets it was able to inspect.
            }
        }
        return SkinStudioEffectiveAssetResolver.BuildPreflight(described);
    }

    private async Task<bool> ExportAndImportDuplicateAsync(
        PendingSkinDuplicate duplicate,
        bool confirm)
    {
        if (catalog is null || currentSkin is null || draft is null)
            return false;

        var changes = draft.Changes.ToArray();
        if (confirm && KumoriDialog.Show(
                Window.GetWindow(this),
                $"Finish the duplicate “{duplicate.Name}”?\n\n"
                + $"Kumori will export a complete .osk, import it into osu!lazer, "
                + "and switch this editor to the imported copy.",
                "Export and import duplicate",
                MessageBoxButton.YesNo,
                MessageBoxImage.Question,
                MessageBoxResult.Yes) != MessageBoxResult.Yes)
            return false;

        string? exportedPath = null;
        SetBusy(true, "Preparing duplicate skin package…");
        try
        {
            var latest = await Task.Run(() => realmService.LoadCatalog(catalog.RootPath));
            var source = latest.Skins.FirstOrDefault(skin =>
                skin.Id == duplicate.SourceSkinId);
            if (source is null)
            {
                StatusText.Text =
                    "Import blocked: the source skin no longer exists in osu!lazer. The duplicate draft was kept.";
                return false;
            }

            var currentFiles = source.Files.ToDictionary(
                file => file.Filename,
                StringComparer.OrdinalIgnoreCase);
            var conflicts = changes.Where(change =>
            {
                var exists = currentFiles.TryGetValue(change.Filename, out var file);
                return change.ExpectedHash is null
                    ? exists
                    : !exists
                      || !string.Equals(
                          file!.Hash,
                          change.ExpectedHash,
                          StringComparison.OrdinalIgnoreCase);
            }).ToArray();
            if (conflicts.Length > 0)
            {
                StatusText.Text =
                    $"Import blocked: {conflicts.Length} source file(s) changed in osu!lazer. "
                    + "The duplicate draft was kept; refresh it before exporting.";
                return false;
            }

            StatusText.Text = "Reading the source skin and applying the duplicate draft…";
            var files = await Task.Run(() => source.Files.ToDictionary(
                file => file.Filename,
                file => new LazerSkinImportFile(
                    file.Filename,
                    realmService.ReadFile(latest.RootPath, file.Hash)),
                StringComparer.OrdinalIgnoreCase));
            foreach (var change in changes)
            {
                if (change.IsDeletion)
                    files.Remove(change.Filename);
                else
                    files[change.Filename] = new LazerSkinImportFile(
                        change.Filename,
                        change.Bytes.ToArray());
            }
            if (files.Count == 0)
            {
                StatusText.Text =
                    "Import blocked: a skin package cannot be empty. The duplicate draft was kept.";
                return false;
            }

            var importFiles = files.Values
                .OrderBy(file => file.Filename, StringComparer.OrdinalIgnoreCase)
                .ToArray();
            StatusText.Text = "Exporting the finished duplicate as .osk…";
            exportedPath = await Task.Run(() => SkinOskPackage.Export(
                AppPaths.SkinEditorExportsDir,
                duplicate.Name,
                importFiles));

            if (!await EnsureBackupAsync())
                return false;
            StatusText.Text = "Auto-importing the .osk into osu!lazer…";
            var imported = await Task.Run(() => realmService.ImportSkin(
                latest.RootPath,
                duplicate.Name,
                duplicate.Creator,
                importFiles));

            var workingId = duplicate.WorkingSkinId;
            draft.AcceptApplied();
            await Task.Run(() => SkinDraftRecovery.Clear(workingId));
            pendingDuplicate = null;
            currentSkin = null;
            await LoadCatalogAsync(imported.Id, forceReloadSelectedSkin: true);

            var savedStatus =
                $"Imported {imported.DisplayName} into osu!lazer. Exported OSK: {exportedPath}";
            StatusText.Text = reloadService is null
                ? savedStatus
                : $"{savedStatus} Lazer reload queued.";
            if (reloadService is not null)
            {
                reloadService.RequestReload(
                    latest.RootPath,
                    imported.Id,
                    result =>
                    {
                        if (currentSkin?.Id == imported.Id)
                            StatusText.Text = $"{savedStatus} {result.Message}";
                    });
            }
            UpdateDirtyState();
            return true;
        }
        catch (Exception ex)
        {
            StatusText.Text = exportedPath is null
                ? $"Could not finish the duplicate: {ex.Message}"
                : $"The OSK was exported to {exportedPath}, but auto-import failed: {ex.Message}";
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
            var backupDirectory = Path.Combine(
                GetOrCreateElementBackupDirectory(),
                "realm");
            var path = await Task.Run(() =>
                realmService.CreateBackup(rootPath, backupDirectory));
            backupCreated = true;
            backupRoot = rootPath;
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

    private string GetOrCreateElementBackupDirectory(bool forceNew = false)
    {
        if (!forceNew && !string.IsNullOrWhiteSpace(elementBackupDirectory))
            return elementBackupDirectory;

        var sessionName =
            $"{DateTimeOffset.Now:yyyyMMdd-HHmmss-fff}-{Guid.NewGuid().ToString("N")[..6]}";
        elementBackupDirectory = Path.Combine(
            CurrentSkinBackupRoot(),
            sessionName);
        Directory.CreateDirectory(elementBackupDirectory);
        backedUpElements.Clear();
        return elementBackupDirectory;
    }

    private string CurrentSkinBackupRoot() => Path.Combine(
        AppPaths.SkinEditorBackupsDir,
        SafePathSegment(currentSkin?.Name ?? "unknown-skin"));

    private async Task<bool> BackupElementFilesAsync(IEnumerable<LazerSkinFileInfo> files)
    {
        if (catalog is null || currentSkin is null)
            return false;

        try
        {
            var directory = GetOrCreateElementBackupDirectory();
            var elementsDirectory = Path.Combine(directory, "elements");
            foreach (var file in files
                         .GroupBy(file => $"{file.Filename}|{file.Hash}", StringComparer.OrdinalIgnoreCase)
                         .Select(group => group.First()))
            {
                var key = $"{file.Filename}|{file.Hash}";
                if (backedUpElements.Contains(key))
                    continue;

                var bytes = await Task.Run(() =>
                    realmService.ReadFile(catalog.RootPath, file.Hash));
                var destination = Path.Combine(
                    elementsDirectory,
                    SafeBackupRelativePath(file.Filename));
                Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                await File.WriteAllBytesAsync(destination, bytes);
                backedUpElements.Add(key);
            }

            var manifest = Path.Combine(directory, "backup.txt");
            if (!File.Exists(manifest))
            {
                await File.WriteAllTextAsync(
                    manifest,
                    $"Skin: {currentSkin.DisplayName}{Environment.NewLine}"
                    + $"Skin ID: {currentSkin.Id}{Environment.NewLine}"
                    + $"Created: {DateTimeOffset.Now:O}{Environment.NewLine}"
                    + $"Source: {catalog.RootPath}{Environment.NewLine}");
            }
            return true;
        }
        catch (Exception ex)
        {
            StatusText.Text = $"The edit was not staged because its element backup failed: {ex.Message}";
            KumoriDialog.Show(
                Window.GetWindow(this),
                StatusText.Text,
                "Element backup failed",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
            return false;
        }
    }

    private static string SafeBackupRelativePath(string filename)
    {
        var segments = filename.Replace('\\', '/')
            .Split('/', StringSplitOptions.RemoveEmptyEntries)
            .Where(segment => segment is not "." and not "..")
            .Select(SafePathSegment)
            .Where(segment => segment.Length > 0)
            .ToArray();
        return segments.Length == 0 ? "unnamed-element" : Path.Combine(segments);
    }

    private static string SafePathSegment(string value)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var cleaned = new string(value
            .Trim()
            .Select(character => invalid.Contains(character) ? '_' : character)
            .ToArray());
        return string.IsNullOrWhiteSpace(cleaned) ? "unnamed-skin" : cleaned;
    }

    private async Task<bool> ResolveDirtyStateAsync()
    {
        if (!HasDirtyChanges) return true;
        var isDuplicateDraft = pendingDuplicate is { } duplicate
                               && duplicate.WorkingSkinId == currentSkin?.Id;
        var result = KumoriDialog.Show(
            Window.GetWindow(this),
            isDuplicateDraft
                ? "This duplicate has not been imported yet.\n\n"
                  + "Yes: export and import it\nNo: discard the duplicate\nCancel: keep editing"
                : "This skin has unsaved changes.\n\n"
                  + "Yes: save all\nNo: discard them\nCancel: stay on this skin",
            "Unsaved skin changes",
            MessageBoxButton.YesNoCancel,
            MessageBoxImage.Warning,
            MessageBoxResult.Cancel);
        if (result == MessageBoxResult.Cancel) return false;
        if (result == MessageBoxResult.Yes)
        {
            foreach (var entry in categories.SelectMany(category => category.Files)
                         .Where(entry => entry.HasEdits).ToArray())
                if (!await SaveEntryAsync(entry)) return false;
            if (iniDirty && !await SaveIniAsync()) return false;
            return await SaveAllAsync(confirm: false);
        }
        DiscardAllChanges();
        if (isDuplicateDraft)
            AbandonPendingDuplicate();
        return true;
    }

    private void DiscardAllChanges()
    {
        draft?.AcceptApplied();
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
        UpdateDirtyState();
    }

    private bool HasDirtyChanges =>
        (pendingDuplicate is { } duplicate
         && duplicate.WorkingSkinId == currentSkin?.Id)
        || (draft?.Count ?? 0) > 0
        || iniDirty
        || categories.SelectMany(category => category.Files).Any(entry => entry.HasEdits);

    private void UpdateDirtyState()
    {
        var stagedCount = draft?.Count ?? 0;
        var isDuplicateDraft = pendingDuplicate is { } duplicate
                               && duplicate.WorkingSkinId == currentSkin?.Id;
        var unstagedCount = categories.SelectMany(category => category.Files).Count(entry => entry.HasEdits)
            + (iniDirty ? 1 : 0);
        SaveAllButton.IsEnabled = (stagedCount > 0 || isDuplicateDraft) && !busy;
        SaveAllButton.Content = isDuplicateDraft
            ? "Export & import duplicate"
            : stagedCount > 0
                ? $"Save {stagedCount} to osu!lazer"
                : "Save to osu!lazer";
        ReviewApplyButton.IsEnabled = (stagedCount > 0 || isDuplicateDraft) && !busy;
        ReviewApplyButton.Content = SaveAllButton.Content;
        DraftReviewButton.Content = stagedCount > 0 ? $"Changes  {stagedCount}" : "Changes";
        DraftChangesLabel.Text = stagedCount == 0
            ? (unstagedCount == 0 ? "No changes waiting" : $"{unstagedCount} edit{(unstagedCount == 1 ? "" : "s")} not added yet")
            : $"{stagedCount} change{(stagedCount == 1 ? "" : "s")} ready to save";
        var draftRevision = draft?.Revision ?? -1;
        // Rebinding the review list and serialising recovery data after every
        // preview render made the editor increasingly sluggish after staging.
        if (displayedDraftRevision != draftRevision)
        {
            var changeItems = draft?.Changes
                .OrderBy(change => change.GroupLabel, StringComparer.OrdinalIgnoreCase)
                .ThenBy(change => change.Filename, StringComparer.OrdinalIgnoreCase)
                .ToArray()
                ?? [];
            var changeView = CollectionViewSource.GetDefaultView(changeItems);
            changeView.GroupDescriptions.Add(
                new PropertyGroupDescription(nameof(SkinDraftChange.GroupKey)));
            DraftChangesList.ItemsSource = changeView;
            displayedDraftRevision = draftRevision;
            UpdateSkinReadiness();
            SyncDraftRecovery();
        }
        UndoDraftButton.IsEnabled = draft?.CanUndo == true && !busy;
        RedoDraftButton.IsEnabled = draft?.CanRedo == true && !busy;
        HeaderUndoButton.IsEnabled = UndoDraftButton.IsEnabled;
        HeaderRedoButton.IsEnabled = RedoDraftButton.IsEnabled;
        DiscardAllDraftButton.IsEnabled = stagedCount > 0 && !busy;
        DraftEmptyState.Visibility = stagedCount == 0 ? Visibility.Visible : Visibility.Collapsed;
        if (selectedEntry is not null)
        {
            var deletionStaged = selectedEntry.PhysicalEntries.Any(physical =>
                draft?.Changes.Any(change =>
                    change.IsDeletion
                    && change.Filename.Equals(
                        physical.Filename,
                        StringComparison.OrdinalIgnoreCase)) == true);
            DeleteElementButton.Content = deletionStaged ? "Deletion staged" : "Delete element…";
            DeleteElementButton.IsEnabled = !deletionStaged && !busy;
            var transparencyStaged = selectedEntry.PhysicalEntries.Any(physical =>
                draft?.Changes.Any(change =>
                    !change.IsDeletion
                    && change.Filename.Equals(
                        physical.Filename,
                        StringComparison.OrdinalIgnoreCase)
                    && SkinElementCategorizer.IsImage(change.Filename)
                    && SkinImageTools.IsFullyTransparentImage(change.Bytes)) == true);
            MakeTransparentButton.Content = transparencyStaged
                ? "Transparency staged"
                : "Make transparent…";
            MakeTransparentButton.IsEnabled = !busy
                && !deletionStaged
                && !transparencyStaged
                && selectedEntry.IsImage
                && selectedEntry.PhysicalEntries.All(physical => Path.GetExtension(
                        physical.Filename)
                    .Equals(".png", StringComparison.OrdinalIgnoreCase));
            ImageEditorControls.IsEnabled = !deletionStaged;
        }
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
            RequestElementRender();
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
            var hitCirclePrefix = iniDocument?.GetValue("Fonts", "HitCirclePrefix");
            if (string.IsNullOrWhiteSpace(hitCirclePrefix))
                hitCirclePrefix = "default";
            var scorePrefix = iniDocument?.GetValue("Fonts", "ScorePrefix");
            if (string.IsNullOrWhiteSpace(scorePrefix))
                scorePrefix = "score";
            var sliderStartTask = ResolveSliderEndpointAsync("sliderstartcircle");
            var sliderEndTask = ResolveSliderEndpointAsync("sliderendcircle");
            string[][] previewRequests =
            [
                ["hitcircle"], ["hitcircleoverlay"],
                [$"{hitCirclePrefix}-1"], [$"{hitCirclePrefix}-2"], [$"{hitCirclePrefix}-3"],
                ["approachcircle"], ["sliderb0", "sliderb", "sliderball"],
                ["sliderfollowcircle"], ["reversearrow"], ["cursor"], ["cursortrail"],
                ["cursormiddle"], ["scorebar-bg"], ["scorebar-ki", "scorebar-marker"],
                ["spinner-background"], ["spinner-approachcircle"], ["spinner-circle"],
                ["spinner-bottom"], ["spinner-top"], ["spinner-glow"], ["spinner-middle2"],
                ["spinner-middle"], ["spinner-metre"], ["spinner-rpm"], ["spinner-spin"],
                ["spinner-clear"], [$"{scorePrefix}-2"], [$"{scorePrefix}-6"], [$"{scorePrefix}-7"],
            ];
            var previewEntries = new SkinElementEntry?[previewRequests.Length];
            foreach (var batch in Enumerable.Range(0, previewRequests.Length).Chunk(6))
            {
                await Task.WhenAll(batch.Select(async index =>
                    previewEntries[index] = await FindAndLoadAsync(previewRequests[index])));
                cancellationToken.ThrowIfCancellationRequested();
                if (version != gameplayRefreshVersion) return;
            }
            var circle = previewEntries[0];
            var overlay = previewEntries[1];
            var number1 = previewEntries[2];
            var number2 = previewEntries[3];
            var number3 = previewEntries[4];
            var approach = previewEntries[5];
            var ball = previewEntries[6];
            var followCircle = previewEntries[7];
            var reverseArrow = previewEntries[8];
            var cursor = previewEntries[9];
            var cursorTrail = previewEntries[10];
            var cursorMiddle = previewEntries[11];
            var scorebar = previewEntries[12];
            var scorebarMarker = previewEntries[13];
            var spinnerBackground = previewEntries[14];
            var spinnerApproach = previewEntries[15];
            var spinnerCircle = previewEntries[16];
            var spinnerBottom = previewEntries[17];
            var spinnerTop = previewEntries[18];
            var spinnerGlow = previewEntries[19];
            var spinnerMiddle2 = previewEntries[20];
            var spinnerMiddle = previewEntries[21];
            var spinnerMetre = previewEntries[22];
            var spinnerRpm = previewEntries[23];
            var spinnerSpin = previewEntries[24];
            var spinnerClear = previewEntries[25];
            var score2 = previewEntries[26];
            var score6 = previewEntries[27];
            var score7 = previewEntries[28];
            var sliderStart = await sliderStartTask;
            var sliderEnd = await sliderEndTask;
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
            var sliderBody = await GetSliderBodyAsync(
                comboTail,
                sliderBorder,
                sliderTrack,
                cancellationToken);
            var allowBallTint =
                iniDocument?.GetValue("General", "AllowSliderBallTint") == "1";
            previewAnimationFramerate = int.TryParse(
                iniDocument?.GetValue("General", "AnimationFramerate"),
                NumberStyles.Integer,
                CultureInfo.InvariantCulture,
                out var configuredFramerate)
                ? configuredFramerate
                : -1;
            previewLegacySkinVersion = decimal.TryParse(
                iniDocument?.GetValue("General", "Version"),
                NumberStyles.Float,
                CultureInfo.InvariantCulture,
                out var configuredVersion)
                ? configuredVersion
                : 2.7m;
            var animationFrames = await Task.WhenAll(
                LoadAnimationFrameSourcesAsync(
                    "sliderb",
                    "",
                    allowBallTint ? comboTail : null),
                LoadAnimationFrameSourcesAsync("sliderfollowcircle", "-"),
                LoadAnimationFrameSourcesAsync("followpoint", "-"));
            sliderBallAnimationFrames = animationFrames[0];
            sliderFollowAnimationFrames = animationFrames[1];
            followpointAnimationFrames = animationFrames[2];
            if (version != gameplayRefreshVersion)
                return;

            GameplayBackground.Source = null;
            GameplaySliderBody.Source = sliderBody;
            GameplayHitcircle.Source = Tinted(sliderStart.Base, comboHead);
            GameplayComboCircle1.Source = Tinted(circle, comboHead);
            GameplayComboCircle2.Source = Tinted(circle, combos.Length > 1 ? combos[1] : comboHead);
            GameplayComboCircle3.Source = Tinted(circle, combos.Length > 2 ? combos[2] : comboHead);
            GameplayComboSwatch1.Fill = new SolidColorBrush(comboHead);
            GameplayComboSwatch2.Fill = new SolidColorBrush(combos.Length > 1 ? combos[1] : comboHead);
            GameplayComboSwatch3.Fill = new SolidColorBrush(combos.Length > 2 ? combos[2] : comboHead);
            GameplayOverlay.Source = sliderStart.Overlay?.Thumbnail;
            GameplayComboOverlay1.Source = overlay?.Thumbnail;
            GameplayComboOverlay2.Source = overlay?.Thumbnail;
            GameplayComboOverlay3.Source = overlay?.Thumbnail;
            GameplayNumber.Source = number1?.Thumbnail;
            GameplayComboNumber1.Source = number1?.Thumbnail;
            GameplayComboNumber2.Source = number2?.Thumbnail;
            GameplayComboNumber3.Source = number3?.Thumbnail;
            GameplayApproach.Source = Tinted(approach, comboHead);
            GameplayStandaloneApproach.Source = Tinted(approach, comboHead);
            GameplayTailCircle.Source = Tinted(sliderEnd.Base, comboTail);
            GameplayTailOverlay.Source = sliderEnd.Overlay?.Thumbnail;
            GameplayReverseArrow.Source = reverseArrow?.Thumbnail;
            GameplayFollowCircle.Source =
                sliderFollowAnimationFrames.FirstOrDefault()
                ?? followCircle?.Thumbnail;
            GameplaySliderBall.Source =
                sliderBallAnimationFrames.FirstOrDefault()
                ?? (allowBallTint ? Tinted(ball, comboTail) : ball?.Thumbnail);
            GameplayCursor.Source = cursor?.Thumbnail;
            GameplayCursorTrail.Source = cursorTrail?.Thumbnail;
            GameplayCursorTrailFar.Source = cursorTrail?.Thumbnail;
            GameplayCursorMiddle.Source = cursorMiddle?.Thumbnail;
            PlaceCursorLayer(GameplayCursorTrailFar, cursorTrail, 350, 112, 0.28);
            PlaceCursorLayer(GameplayCursorTrail, cursorTrail, 395, 102, 0.58);
            PlaceCursorLayer(GameplayCursorMiddle, cursorMiddle, 440, 94, 1);
            PlaceCursorLayer(GameplayCursor, cursor, 440, 94, 1);
            GameplayScorebar.Source = scorebar?.Thumbnail;
            GameplayScorebarMarker.Source = scorebarMarker?.Thumbnail;
            var spinnerColour = ReadIniColor("Colours", "SpinnerBackground") ?? Color.FromRgb(92, 112, 150);
            GameplaySpinnerFallbackFill.Fill = new SolidColorBrush(Color.FromArgb(72, spinnerColour.R, spinnerColour.G, spinnerColour.B));
            GameplaySpinnerFallbackRing.Stroke = new SolidColorBrush(spinnerColour);
            GameplaySpinnerBackground.Source = Tinted(spinnerBackground, spinnerColour);
            GameplaySpinnerApproach.Source = spinnerApproach?.Thumbnail;
            GameplaySpinnerCircle.Source = spinnerCircle?.Thumbnail;
            GameplaySpinnerBottom.Source = spinnerBottom?.Thumbnail;
            GameplaySpinnerTop.Source = spinnerTop?.Thumbnail;
            GameplaySpinnerGlow.Source = Tinted(spinnerGlow, Color.FromRgb(3, 151, 255));
            GameplaySpinnerMiddle2.Source = spinnerMiddle2?.Thumbnail;
            GameplaySpinnerMiddle.Source = spinnerMiddle?.Thumbnail;
            GameplaySpinnerMetre.Source = spinnerMetre?.Thumbnail;
            GameplaySpinnerRpm.Source = spinnerRpm?.Thumbnail;
            GameplaySpinnerSpin.Source = spinnerSpin?.Thumbnail;
            GameplaySpinnerClear.Source = spinnerClear?.Thumbnail;
            GameplaySpinnerSpmDigit2.Source = score2?.Thumbnail;
            GameplaySpinnerSpmDigit6.Source = score6?.Thumbnail;
            GameplaySpinnerSpmDigit7.Source = score7?.Thumbnail;
            PlaceSpinnerLayer(GameplaySpinnerBackground, spinnerBackground, 0.625, 320, 248);
            PlaceSpinnerLayer(GameplaySpinnerCircle, spinnerCircle, 0.625, 320, 248);
            PlaceSpinnerLayer(GameplaySpinnerGlow, spinnerGlow, 0.625, 320, 248);
            PlaceSpinnerLayer(GameplaySpinnerBottom, spinnerBottom, 0.625, 320, 248);
            PlaceSpinnerLayer(GameplaySpinnerTop, spinnerTop, 0.625, 320, 248);
            PlaceSpinnerLayer(GameplaySpinnerMiddle2, spinnerMiddle2, 0.625, 320, 248);
            PlaceSpinnerLayer(GameplaySpinnerMiddle, spinnerMiddle, 0.625, 320, 248);
            PlaceSpinnerLayer(GameplaySpinnerApproach, spinnerApproach, 0.625 * 1.86, 320, 248);
            PlaceSpinnerLayer(GameplaySpinnerSpin, spinnerSpin, 0.625, 320, 364);
            PlaceSpinnerLayer(GameplaySpinnerClear, spinnerClear, 0.625, 320, 144);
            PlaceSpinnerLayerFromTopLeft(GameplaySpinnerMetre, spinnerMetre, 0.625, 0, 29);
            PlaceSpinnerLayerFromTopLeft(GameplaySpinnerRpm, spinnerRpm, 0.625, 233, 445);
            var scoreOverlap = double.TryParse(
                iniDocument?.GetValue("Fonts", "ScoreOverlap"),
                NumberStyles.Float,
                CultureInfo.InvariantCulture,
                out var parsedScoreOverlap)
                ? parsedScoreOverlap
                : 0;
            var hasSpinnerDigits = PlaceSpinnerScore(
                [(GameplaySpinnerSpmDigit2, score2), (GameplaySpinnerSpmDigit6, score6), (GameplaySpinnerSpmDigit7, score7)],
                scoreOverlap);
            GameplaySpinnerSpmDigits.Visibility = hasSpinnerDigits ? Visibility.Visible : Visibility.Collapsed;
            GameplaySpinnerSpmFallbackText.Visibility = hasSpinnerDigits ? Visibility.Collapsed : Visibility.Visible;
            // osu! only selects the legacy new-style renderer when spinner-top
            // exists and spinner-background does not. A background always wins
            // and selects the old-style renderer.
            var spinnerStyle = LegacySpinnerPreview.Resolve(
                hasBackground: spinnerBackground is not null,
                hasTop: spinnerTop is not null);
            var usesNewStyleSpinner = spinnerStyle == LegacySpinnerPreviewStyle.New;
            var usesOldStyleSpinner = spinnerStyle == LegacySpinnerPreviewStyle.Old;
            GameplayOldSpinnerLayers.Visibility = usesOldStyleSpinner ? Visibility.Visible : Visibility.Collapsed;
            GameplayNewSpinnerLayers.Visibility = usesNewStyleSpinner ? Visibility.Visible : Visibility.Collapsed;
            GameplaySpinnerFallbackLayers.Visibility =
                usesOldStyleSpinner || usesNewStyleSpinner ? Visibility.Collapsed : Visibility.Visible;
            GameplaySpinnerModeText.Text = usesNewStyleSpinner
                ? "new-style · glow → bottom → top → middle2 → middle · pre-spin state"
                : usesOldStyleSpinner
                    ? "old-style · background → circle → metre · pre-spin state"
                    : "No legacy spinner body in this skin · osu! uses the active default skin";
            GameplaySpinnerSpinFallback.Visibility = spinnerSpin is null ? Visibility.Visible : Visibility.Collapsed;
            GameplaySpinnerClearFallback.Visibility = Visibility.Collapsed;
            GameplayBackground.Visibility = Visibility.Collapsed;
            ResetPreviewAnimation();
            _ = RefreshRichPreviewsAsync();
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

    private static BitmapSource RenderGameplaySliderBody(
        Color comboColour,
        Color? sliderBorder,
        Color? sliderTrack,
        CancellationToken cancellationToken)
    {
        // Render from a higher-resolution copy of the exact logical path. Scaling
        // the sampled points (rather than only their endpoints) preserves the
        // curve while giving WPF enough real edge coverage to downsample cleanly.
        const double scale = 2;
        var path = LegacySliderRenderer.SampleSCurve(127, 177, 676, 102, segments: 128)
            .Select(point => new System.Windows.Point(point.X * scale, point.Y * scale))
            .ToArray();
        return LegacySliderRenderer.Render(
            (int)(800 * scale),
            (int)(300 * scale),
            path,
            46 * scale,
            comboColour,
            sliderBorder,
            sliderTrack,
            cancellationToken);
    }

    private async Task<BitmapSource> GetSliderBodyAsync(
        Color comboColour,
        Color? sliderBorder,
        Color? sliderTrack,
        CancellationToken cancellationToken)
    {
        var key = new SliderPreviewKey(comboColour, sliderBorder, sliderTrack);
        if (cachedSliderPreviewKey == key && cachedSliderPreview is not null)
            return cachedSliderPreview;

        var rendered = await Task.Run(
            () => RenderGameplaySliderBody(
                comboColour,
                sliderBorder,
                sliderTrack,
                cancellationToken),
            cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        cachedSliderPreviewKey = key;
        cachedSliderPreview = rendered;
        return rendered;
    }

    private async Task<IReadOnlyList<BitmapSource>> LoadAnimationFrameSourcesAsync(
        string baseStem,
        string separator,
        Color? tint = null)
    {
        if (currentSkin is null)
            return [];
        var filenames = SkinDraftProjection.EffectiveFiles(
                currentSkin.Files,
                draft?.Changes ?? [])
            .Select(file => file.Filename);
        var resolved = SkinPreviewAnimation.ResolveFrames(
            filenames,
            baseStem,
            separator);
        if (resolved.Count == 0)
            return [];

        var frames = new List<BitmapSource>(resolved.Count);
        foreach (var filename in resolved)
        {
            try
            {
                var entry = await FindAndLoadEffectiveFileAsync(filename);
                var source = tint.HasValue
                    ? Tinted(entry, tint.Value)
                    : entry?.Thumbnail;
                if (source is null)
                    break;
                frames.Add(source);
            }
            catch
            {
                break;
            }
        }
        return frames;
    }

    private async Task<SkinElementEntry?> FindAndLoadAsync(params string[] stems)
    {
        EnsureDraftPreviewIndex();
        foreach (var stem in stems)
        {
            var matchingChanges = draftChangesByStem.GetValueOrDefault(stem) ?? [];
            var staged = matchingChanges.Where(change => !change.IsDeletion)
                .OrderByDescending(change => SkinElementCategorizer.IsHighResolution(change.Filename))
                .FirstOrDefault();
            if (staged is not null && SkinElementCategorizer.IsImage(staged.Filename))
                return GetOrCreateDraftPreviewEntry(staged);
            if (matchingChanges.Any(change => change.IsDeletion))
                return null;
        }

        var entry = categories.SelectMany(category => category.Files)
            .SelectMany(file => file.PhysicalEntries)
            .Where(file => !draftDeletedFilenames.Contains(file.Filename))
            .OrderByDescending(file => file.IsHighResolution)
            .FirstOrDefault(file =>
            {
                var name = LogicalStem(file.Filename);
                return stems.Any(stem => name.Equals(stem, StringComparison.OrdinalIgnoreCase));
            });
        if (entry is not null && entry.IsImage)
            await EnsureEntryLoadedAsync(entry);
        return entry;
    }

    private async Task<SkinElementEntry?> FindAndLoadEffectiveFileAsync(string? filename)
    {
        if (filename is null)
            return null;
        EnsureDraftPreviewIndex();
        var staged = draft?.Changes.FirstOrDefault(change =>
            change.Filename.Equals(filename, StringComparison.OrdinalIgnoreCase));
        if (staged is not null)
            return staged.IsDeletion || !SkinElementCategorizer.IsImage(staged.Filename)
                ? null
                : GetOrCreateDraftPreviewEntry(staged);

        var entry = categories
            .SelectMany(category => category.Files)
            .SelectMany(file => file.PhysicalEntries)
            .FirstOrDefault(file => file.Filename.Equals(
                filename,
                StringComparison.OrdinalIgnoreCase));
        if (entry is not null && entry.IsImage)
            await EnsureEntryLoadedAsync(entry);
        return entry;
    }

    private void EnsureDraftPreviewIndex()
    {
        var revision = draft?.Revision ?? -1;
        if (indexedDraftRevision == revision)
            return;

        var changes = draft?.Changes.ToArray() ?? [];
        draftChangesByStem = changes
            .GroupBy(change => LogicalStem(change.Filename), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(
                group => group.Key,
                group => group.ToArray(),
                StringComparer.OrdinalIgnoreCase);
        draftDeletedFilenames = changes
            .Where(change => change.IsDeletion)
            .Select(change => change.Filename)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        draftPreviewEntries.Clear();
        indexedDraftRevision = revision;
    }

    private static string LogicalStem(string filename)
    {
        var name = Path.GetFileNameWithoutExtension(filename);
        return name.EndsWith("@2x", StringComparison.OrdinalIgnoreCase)
            ? name[..^3]
            : name;
    }

    private SkinElementEntry GetOrCreateDraftPreviewEntry(SkinDraftChange change)
    {
        if (draftPreviewEntries.TryGetValue(change.Filename, out var cached))
            return cached;

        var entry = new SkinElementEntry(new LazerSkinFileInfo(
            change.Filename,
            change.ExpectedHash ?? "staged",
            change.Bytes.LongLength));
        var bitmap = SkinImageTools.Decode(change.Bytes);
        entry.OriginalBytes = change.Bytes;
        entry.OriginalPixels = SkinImageTools.Pixels(bitmap, out var stride);
        entry.HasVisiblePixels = SkinCursorMiddlePolicy.HasRenderablePixels(
            entry.Filename,
            bitmap.PixelWidth,
            bitmap.PixelHeight,
            entry.OriginalPixels);
        entry.Stride = stride;
        entry.PixelWidth = bitmap.PixelWidth;
        entry.PixelHeight = bitmap.PixelHeight;
        entry.Thumbnail = SkinImageTools.Render(entry);
        draftPreviewEntries[change.Filename] = entry;
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

    private static void PlaceCursorLayer(
        System.Windows.Controls.Image image,
        SkinElementEntry? entry,
        double centreX,
        double centreY,
        double opacity)
    {
        if (entry?.Thumbnail is null
            || !ShouldRenderPreviewImage(entry)
            || entry.PixelWidth <= 0
            || entry.PixelHeight <= 0)
        {
            image.Visibility = Visibility.Collapsed;
            return;
        }

        var resolutionScale = entry.IsHighResolution ? 0.5 : 1;
        var width = Math.Clamp(entry.PixelWidth * resolutionScale, 1, 96);
        var height = Math.Clamp(entry.PixelHeight * resolutionScale, 1, 96);
        image.Width = width;
        image.Height = height;
        image.Opacity = opacity;
        image.Stretch = Stretch.Fill;
        Canvas.SetLeft(image, centreX - width / 2);
        Canvas.SetTop(image, centreY - height / 2);
        image.Visibility = Visibility.Visible;
    }

    private static void PlaceSpinnerLayer(
        System.Windows.Controls.Image image,
        SkinElementEntry? entry,
        double stableScale,
        double centreX,
        double centreY)
    {
        if (entry is null || entry.PixelWidth <= 0 || entry.PixelHeight <= 0)
            return;

        var resolutionScale = entry.IsHighResolution ? 2d : 1d;
        var width = entry.PixelWidth / resolutionScale * stableScale;
        var height = entry.PixelHeight / resolutionScale * stableScale;
        image.Width = width;
        image.Height = height;
        Canvas.SetLeft(image, centreX - width / 2);
        Canvas.SetTop(image, centreY - height / 2);
    }

    private static void PlaceSpinnerLayerFromTopLeft(
        System.Windows.Controls.Image image,
        SkinElementEntry? entry,
        double stableScale,
        double left,
        double top)
    {
        if (entry is null || entry.PixelWidth <= 0 || entry.PixelHeight <= 0)
            return;

        var resolutionScale = entry.IsHighResolution ? 2d : 1d;
        image.Width = entry.PixelWidth / resolutionScale * stableScale;
        image.Height = entry.PixelHeight / resolutionScale * stableScale;
        Canvas.SetLeft(image, left);
        Canvas.SetTop(image, top);
    }

    private static bool PlaceSpinnerScore(
        IReadOnlyList<(System.Windows.Controls.Image Image, SkinElementEntry? Entry)> digits,
        double scoreOverlap)
    {
        if (digits.Any(digit => digit.Entry is null
                               || digit.Entry.PixelWidth <= 0
                               || digit.Entry.PixelHeight <= 0))
            return false;

        const double stableScale = 0.625 * 0.9;
        var right = 400d;
        for (var index = digits.Count - 1; index >= 0; index--)
        {
            var (image, entry) = digits[index];
            var resolutionScale = entry!.IsHighResolution ? 2d : 1d;
            var width = entry.PixelWidth / resolutionScale * stableScale;
            var height = entry.PixelHeight / resolutionScale * stableScale;
            var left = right - width;
            image.Width = width;
            image.Height = height;
            Canvas.SetLeft(image, left);
            Canvas.SetTop(image, 448);
            right = left - scoreOverlap * stableScale;
        }

        return true;
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
            var combo = new Border
            {
                Width = 20,
                Height = 20,
                Margin = new Thickness(0, 0, 5, 0),
                CornerRadius = new CornerRadius(10),
                Background = new SolidColorBrush(color),
                BorderThickness = new Thickness(1),
                ToolTip = $"Combo{index}: {raw}",
            };
            combo.SetResourceReference(Border.BorderBrushProperty, "Brush.SubtleBorder");
            ComboStrip.Children.Add(combo);
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
        if (currentSkin is null || draft is null) return;
        var batch = files.ToArray();
        if (batch.Length == 0) return;
        var staged = new List<SkinDraftChange>();
        foreach (var (filename, bytes) in batch)
        {
            var existing = categories.SelectMany(category => category.Files)
                    .SelectMany(entry => entry.PhysicalEntries)
                    .FirstOrDefault(file => file.Filename.Equals(filename, StringComparison.OrdinalIgnoreCase))
                    ?.File
                ?? (iniFile?.Filename.Equals(filename, StringComparison.OrdinalIgnoreCase) == true
                    ? iniFile
                    : null);
            if (existing is not null
                && settings.Current.SkinEditor.AutoBackupElements
                && !await BackupElementFilesAsync([existing]))
                return;
            staged.Add(new SkinDraftChange(
                filename,
                existing?.Hash,
                bytes,
                existing is null ? $"{filename} (new import)" : $"{filename} (import replacement)"));
        }
        draft.StageRange(staged);
        StatusText.Text = $"Added {staged.Count} imported file(s) to Changes. Save to osu!lazer when ready.";
        UpdateDirtyState();
    }

    private async Task ReloadCurrentSkinAsync()
    {
        var id = currentSkin?.Id;
        currentSkin = null;
        await LoadCatalogAsync(id, forceReloadSelectedSkin: true);
    }

    private async Task RefreshAppliedSkinAsync(
        LazerSkinCatalog latestCatalog,
        LazerSkinInfo refreshedSkin,
        string? selectedCategoryName,
        string? selectedFilename)
    {
        allSkins = latestCatalog.Skins
            .Select(skin => skin.Id == refreshedSkin.Id ? refreshedSkin : skin)
            .ToArray();
        catalog = new LazerSkinCatalog(latestCatalog.RootPath, allSkins);
        CompactSkinPicker.ItemsSource = allSkins;
        await SelectSkinAsync(
            refreshedSkin,
            forceReload: true,
            restoreRecoveredDraft: false);

        if (selectedCategoryName is null)
            return;
        var categoryChoice = CategoryPicker.Items.Cast<CategoryChoice>()
            .FirstOrDefault(choice => choice.Category.Name.Equals(
                selectedCategoryName,
                StringComparison.OrdinalIgnoreCase));
        if (categoryChoice is null)
            return;

        CategoryPicker.SelectedItem = categoryChoice;
        var entry = selectedFilename is null
            ? null
            : categoryChoice.Category.Files.FirstOrDefault(candidate =>
                candidate.PhysicalEntries.Any(physical =>
                    physical.Filename.Equals(
                        selectedFilename,
                        StringComparison.OrdinalIgnoreCase)));
        if (entry is null)
            return;
        ElementList.SelectedItem = entry;
        await SelectEntryAsync(entry);
    }

    private async Task StartExternalEditAsync(SkinElementEntry entry)
    {
        if (catalog is null || currentSkin is null) return;
        if (settings.Current.SkinEditor.AutoBackupElements
            && !await BackupElementFilesAsync(entry.PhysicalEntries.Select(physical => physical.File)))
            return;
        if (entry.IsImage)
            await EnsureEntryLoadedAsync(entry);
        else if (entry.OriginalBytes is null)
            entry.OriginalBytes = await Task.Run(() => realmService.ReadFile(catalog.RootPath, entry.Hash));
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
                if (bytes is null || draft is null) return;
                draft.Stage(
                    entry.Filename,
                    entry.Hash,
                    bytes,
                    $"{entry.Filename} (external edit)");
                StatusText.Text = $"External edit added to Changes: {entry.Filename}. Save when ready.";
                UpdateDirtyState();
            });
        };
        externalWatchers.Add(watcher);
        Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
        StatusText.Text = $"Opened {entry.Filename} externally. Saves stage into this editing session.";
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
        responsiveState = ResponsiveLayoutResolver.Resolve(ActualWidth, ActualHeight);
        HeaderRow.Height = new GridLength(responsiveState.IsShort ? 54 : 64);
        StatusRow.Height = new GridLength(responsiveState.IsShort ? 24 : 26);
        HeaderTitlePanel.Visibility = responsiveState.IsCompact ? Visibility.Collapsed : Visibility.Visible;
        RootPathText.Visibility = responsiveState.IsShort ? Visibility.Collapsed : Visibility.Visible;
        ActiveSkinLabel.Visibility = responsiveState.IsShort
            ? Visibility.Collapsed
            : Visibility.Visible;
        CompactSurfaceBar.Visibility = responsiveState.IsCompact
                                       && FirstRunGuide.Visibility != Visibility.Visible
            ? Visibility.Visible
            : Visibility.Collapsed;
        CompactSkinPicker.MinWidth = responsiveState.IsCompact ? 145 : 180;
        CompactSkinPicker.MaxWidth = double.PositiveInfinity;
        EditorGrid.Margin = responsiveState.IsCompact
            ? new Thickness(8)
            : new Thickness(10);

        if (!responsiveState.IsCompact)
        {
            NavigatorColumn.Width = new GridLength(responsiveState.IsStandard ? 220 : 256);
            NavigatorGapColumn.Width = new GridLength(8);
            CenterColumn.Width = new GridLength(1, GridUnitType.Star);
            InspectorGapColumn.Width = new GridLength(8);
            InspectorColumn.Width = new GridLength(responsiveState.IsStandard ? 288 : 320);
        }

        UpdateStudioState();
    }

    private void SkinEditor_Loaded(object sender, RoutedEventArgs e)
    {
        var window = Window.GetWindow(this);
        if (!ReferenceEquals(previewHostWindow, window))
        {
            if (previewHostWindow is not null)
                previewHostWindow.Deactivated -= PreviewHostWindow_Deactivated;
            previewHostWindow = window;
            if (previewHostWindow is not null)
                previewHostWindow.Deactivated += PreviewHostWindow_Deactivated;
        }
        UpdatePreviewAnimationSubscription();
    }

    private void PreviewHostWindow_Deactivated(object? sender, EventArgs e) =>
        EndInteractiveCursorPreview();

    private void PreviewPlaybackToggle_Click(object sender, RoutedEventArgs e)
    {
        SetPreviewAnimationsEnabled(PreviewPlaybackToggle.IsChecked == true);
    }

    private void SetPreviewAnimationsEnabled(bool enabled)
    {
        previewAnimationsEnabled = enabled;
        settings.Update(value =>
            value.SkinEditor.PreviewAnimationsEnabled = previewAnimationsEnabled);
        UpdatePreviewPlaybackPresentation();
        UpdatePreviewAnimationSubscription();
        if (!previewAnimationsEnabled && !interactiveCursorActive)
            RenderPreviewFrame();
    }

    private void UpdatePreviewPlaybackPresentation()
    {
        if (PreviewPlaybackToggle is null)
            return;
        PreviewPlaybackToggle.IsChecked = previewAnimationsEnabled;
        PreviewPlaybackToggle.Content = previewAnimationsEnabled ? "Pause" : "Play";
        AutomationProperties.SetName(
            PreviewPlaybackToggle,
            previewAnimationsEnabled
                ? "Pause animated skin previews"
                : "Play animated skin previews");
    }

    private void ResetPreviewAnimation()
    {
        previewAnimationElapsed = 0;
        previewFrameDelta = 0;
        previewHealth = 1;
        previewLastRenderTime = previewRenderClock.Elapsed.TotalMilliseconds;
        interactiveCursorSamples.Clear();
        interactiveCursorLastSampleTime = double.NegativeInfinity;
        interactiveSmoothTrailAnchor = interactiveCursorActive
            ? interactiveCursorPosition
            : null;
        RenderPreviewFrame();
    }

    private void UpdatePreviewAnimationSubscription()
    {
        var shouldRender = SkinPreviewAnimation.ShouldRender(
            IsVisible,
            workspaceMode == SkinEditorWorkspaceMode.Elements
            && ExtrasWorkspace.Visibility != Visibility.Visible,
            previewAnimationsEnabled,
            interactiveCursorActive);
        if (shouldRender)
        {
            if (previewRenderingSubscribed)
                return;
            previewLastRenderTime = previewRenderClock.Elapsed.TotalMilliseconds;
            CompositionTarget.Rendering += PreviewCompositionTarget_Rendering;
            previewRenderingSubscribed = true;
        }
        else
        {
            StopPreviewRendering();
        }
    }

    private void StopPreviewRendering()
    {
        if (!previewRenderingSubscribed)
            return;
        CompositionTarget.Rendering -= PreviewCompositionTarget_Rendering;
        previewRenderingSubscribed = false;
    }

    private void PreviewCompositionTarget_Rendering(object? sender, EventArgs e)
    {
        var now = previewRenderClock.Elapsed.TotalMilliseconds;
        var delta = Math.Clamp(now - previewLastRenderTime, 0, 100);
        previewLastRenderTime = now;
        if (previewAnimationsEnabled)
        {
            previewAnimationElapsed += delta;
            previewFrameDelta = delta;
        }
        else
        {
            previewFrameDelta = 0;
        }
        RenderPreviewFrame(now);
    }

    private void RenderPreviewFrame(double? wallTime = null)
    {
        if (!IsLoaded || workspaceMode != SkinEditorWorkspaceMode.Elements)
            return;
        if (elementCenterMode == SkinEditorCenterMode.Gameplay)
            AnimateGameplayPreview(previewAnimationElapsed);
        else
            AnimateElementComposition(previewAnimationElapsed);
        if (interactiveCursorActive)
            RenderInteractiveCursor(
                wallTime ?? previewRenderClock.Elapsed.TotalMilliseconds);
        previewFrameDelta = 0;
    }

    private void AnimateGameplayPreview(double elapsed)
    {
        var approach = SkinPreviewAnimation.Approach(elapsed);
        ApplyPreviewTransform(
            GameplayStandaloneApproach,
            scale: approach.Scale);
        ApplyPreviewTransform(GameplayApproach, scale: approach.Scale);
        GameplayStandaloneApproach.Opacity = approach.Opacity;
        GameplayApproach.Opacity = approach.Opacity;

        var hitObject = SkinPreviewAnimation.HitObject(elapsed);
        ApplyPreviewTransform(GameplayHitcircle, scale: hitObject.Scale);
        ApplyPreviewTransform(GameplayOverlay, scale: hitObject.Scale);
        GameplayHitcircle.Opacity = hitObject.Opacity;
        GameplayOverlay.Opacity = hitObject.Opacity;
        var hitNumber = SkinPreviewAnimation.HitObject(
            elapsed,
            shortNumberFade: previewLegacySkinVersion > 1);
        ApplyPreviewTransform(GameplayNumber, scale: hitNumber.Scale);
        GameplayNumber.Opacity = hitNumber.Opacity;

        var slider = SkinPreviewAnimation.Slider(
            elapsed,
            legacyVersionOne: previewLegacySkinVersion <= 1);
        var sliderPosition = SkinPreviewAnimation.SamplePolyline(
            GameplaySliderPreviewPath,
            slider.Progress);
        var sliderRotation = SkinPreviewAnimation.PolylineRotation(
            GameplaySliderPreviewPath,
            slider.Progress,
            slider.Reversed);
        var sliderBallFlip =
            ReadPreviewBoolean("General", "SliderBallFlip", defaultValue: true)
            && slider.Reversed
                ? -1d
                : 1d;
        ApplyPreviewTransform(
            GameplaySliderBall,
            sliderPosition.X - 431,
            sliderPosition.Y - 145,
            rotation: sliderRotation,
            scaleX: sliderBallFlip);
        GameplaySliderBall.Opacity = slider.BallOpacity;
        ApplyPreviewTransform(
            GameplayFollowCircle,
            sliderPosition.X - 431,
            sliderPosition.Y - 145,
            scale: slider.FollowScale);
        GameplayFollowCircle.Opacity = slider.FollowOpacity;
        ApplyPreviewTransform(
            GameplayReverseArrow,
            scale: slider.ReverseScale,
            rotation: SkinPreviewAnimation.PolylineRotation(
                GameplaySliderPreviewPath,
                1,
                reversed: false) + 180 + slider.ReverseRotation);
        GameplayReverseArrow.Opacity = slider.ReverseOpacity;

        ApplyAnimationFrame(
            GameplaySliderBall,
            sliderBallAnimationFrames,
            elapsed,
            sliderBall: true,
            sliderVelocity: GameplaySliderPreviewVelocity);
        ApplyAnimationFrame(
            GameplayFollowCircle,
            sliderFollowAnimationFrames,
            elapsed,
            sliderBall: false);

        var cursor = SkinPreviewAnimation.Cursor(
            elapsed,
            800,
            210,
            ReadPreviewBoolean("General", "CursorExpand", defaultValue: true),
            ReadPreviewBoolean("General", "CursorRotate", defaultValue: true));
        var cursorCentre = ReadPreviewBoolean(
            "General",
            "CursorCentre",
            defaultValue: true);
        PlaceInteractiveVisual(
            GameplayCursor,
            440,
            94,
            cursor.Position,
            cursorCentre,
            cursor.Scale,
            cursor.Rotation);
        PlaceInteractiveVisual(
            GameplayCursorMiddle,
            440,
            94,
            cursor.Position,
            cursorCentre,
            1,
            0);
        var smoothCursor = GameplayCursorMiddle.Source is not null;
        AnimateGameplayTrail(
            GameplayCursorTrail,
            elapsed,
            smoothCursor ? 90 : SkinPreviewAnimation
                .DisjointTrailIntervalMilliseconds,
            395,
            102,
            0.58,
            smoothCursor);
        AnimateGameplayTrail(
            GameplayCursorTrailFar,
            elapsed,
            smoothCursor
                ? 260
                : SkinPreviewAnimation.DisjointTrailIntervalMilliseconds * 3,
            350,
            112,
            0.28,
            smoothCursor);

        var spinner = SkinPreviewAnimation.Spinner(
            elapsed,
            noBlink: ReadPreviewBoolean(
                "General",
                "SpinnerNoBlink",
                defaultValue: false));
        GameplayOldSpinnerLayers.Opacity = spinner.BodyOpacity;
        GameplayNewSpinnerLayers.Opacity = spinner.BodyOpacity;
        GameplaySpinnerFallbackLayers.Opacity = spinner.BodyOpacity;
        ApplyPreviewTransform(GameplaySpinnerCircle, rotation: spinner.Rotation);
        var spinnerTopRatio =
            GameplaySpinnerMiddle2.Source is not null ? 0.5 : 1;
        ApplyPreviewTransform(
            GameplaySpinnerTop,
            scale: spinner.BodyScale,
            rotation: spinner.Rotation * spinnerTopRatio);
        ApplyPreviewTransform(
            GameplaySpinnerBottom,
            scale: spinner.BodyScale,
            rotation: spinner.Rotation * spinnerTopRatio / 3);
        ApplyPreviewTransform(
            GameplaySpinnerMiddle2,
            scale: spinner.BodyScale,
            rotation: spinner.Rotation);
        ApplyPreviewTransform(
            GameplaySpinnerMiddle,
            scale: spinner.BodyScale);
        ApplyPreviewTransform(
            GameplaySpinnerGlow,
            scale: spinner.BodyScale);
        GameplaySpinnerGlow.Opacity = spinner.GlowOpacity;
        ApplyPreviewTransform(
            GameplaySpinnerApproach,
            scale: spinner.ApproachScale);
        ApplyPreviewTransform(
            GameplaySpinnerMetre,
            scaleY: spinner.MetreFill);
        GameplaySpinnerMetre.RenderTransformOrigin =
            new System.Windows.Point(0.5, 1);
        GameplaySpinnerMetre.Opacity = spinner.BodyOpacity;
        var showClear = spinner.ClearOpacity > 0.001;
        GameplaySpinnerClear.Visibility = showClear
                                          && GameplaySpinnerClear.Source is not null
            ? Visibility.Visible
            : Visibility.Collapsed;
        GameplaySpinnerClearFallback.Visibility = showClear
                                                  && GameplaySpinnerClear.Source is null
            ? Visibility.Visible
            : Visibility.Collapsed;
        ApplyPreviewTransform(
            GameplaySpinnerClear,
            scale: spinner.ClearScale);
        GameplaySpinnerClear.Opacity = spinner.ClearOpacity;
        GameplaySpinnerClearFallback.Opacity = spinner.ClearOpacity;
        GameplaySpinnerSpin.Opacity = spinner.SpinOpacity;
        GameplaySpinnerSpinFallback.Opacity = spinner.SpinOpacity;

        previewHealth = SkinPreviewAnimation.SmoothHealth(
            previewHealth,
            SkinPreviewAnimation.HealthTarget(elapsed),
            previewFrameDelta);
        ApplyPreviewTransform(
            GameplayScorebarMarker,
            SkinPreviewAnimation.ScorebarOffsetFromHealth(previewHealth),
            0);
    }

    private void AnimateGameplayTrail(
        System.Windows.Controls.Image image,
        double elapsed,
        double age,
        double baseX,
        double baseY,
        double baseOpacity,
        bool smooth)
    {
        var state = SkinPreviewAnimation.Cursor(
            elapsed - age,
            800,
            210,
            ReadPreviewBoolean(
                "General",
                "CursorExpand",
                defaultValue: true),
            rotate: true);
        PlaceInteractiveVisual(
            image,
            baseX,
            baseY,
            state.Position,
            smooth || ReadPreviewBoolean(
                "General",
                "CursorCentre",
                defaultValue: true),
            state.Scale * (smooth
                ? 1
                : SkinPreviewAnimation.LegacyTrailTextureScale),
            ReadPreviewBoolean(
                "General",
                "CursorTrailRotate",
                defaultValue: true)
                ? state.Rotation
                : 0);
        image.Opacity = baseOpacity
                        * SkinPreviewAnimation.TrailOpacity(age, smooth);
    }

    private void AnimateElementComposition(double elapsed)
    {
        if (elementCompositionVisuals.Count == 0)
            return;
        var approach = SkinPreviewAnimation.Approach(elapsed);
        var slider = SkinPreviewAnimation.Slider(
            elapsed,
            legacyVersionOne: previewLegacySkinVersion <= 1);
        var sliderPosition = SkinPreviewAnimation.SamplePolyline(
            ElementSliderPreviewPath,
            slider.Progress);
        var sliderRotation = SkinPreviewAnimation.PolylineRotation(
            ElementSliderPreviewPath,
            slider.Progress,
            slider.Reversed);
        var spinner = SkinPreviewAnimation.Spinner(
            elapsed,
            noBlink: ReadPreviewBoolean(
                "General",
                "SpinnerNoBlink",
                defaultValue: false));
        var cursor = SkinPreviewAnimation.Cursor(
            elapsed,
            640,
            480,
            ReadPreviewBoolean("General", "CursorExpand", defaultValue: true),
            ReadPreviewBoolean("General", "CursorRotate", defaultValue: true));
        var smoothCursor = renderedCursorUsesSmoothTrail;
        var trailVisuals = elementCompositionVisuals
            .Where(visual =>
                visual.Layer.Role == SkinPreviewAnimationRole.CursorTrail)
            .ToArray();
        var followpointVisuals = elementCompositionVisuals
            .Where(visual =>
                visual.Layer.Role == SkinPreviewAnimationRole.Followpoint)
            .OrderBy(visual => visual.Layer.RoleIndex)
            .ToArray();
        var followpointProgressSpan = followpointVisuals.Length > 1
            ? followpointVisuals[^1].Layer.RoleProgress
              - followpointVisuals[0].Layer.RoleProgress
            : 0;
        var followpointVector =
            followpointVisuals.Length > 1
            && followpointProgressSpan > double.Epsilon
                ? new System.Windows.Vector(
                    (followpointVisuals[^1].Layer.CentreX
                     - followpointVisuals[0].Layer.CentreX)
                    / followpointProgressSpan,
                    (followpointVisuals[^1].Layer.CentreY
                     - followpointVisuals[0].Layer.CentreY)
                    / followpointProgressSpan)
                : default;
        var hasSpinnerMiddle2 = elementCompositionVisuals.Any(visual =>
            visual.Layer.Role == SkinPreviewAnimationRole.SpinnerMiddle2);
        var spinnerTopRatio = hasSpinnerMiddle2 ? 0.5 : 1;

        foreach (var visual in elementCompositionVisuals)
        {
            var image = visual.Image;
            var baseOpacity = CompositionBaseOpacity(visual);
            switch (visual.Layer.Role)
            {
                case SkinPreviewAnimationRole.ApproachCircle:
                    ApplyPreviewTransform(
                        image,
                        scale: approach.Scale);
                    image.Opacity = baseOpacity * approach.Opacity;
                    break;

                case SkinPreviewAnimationRole.HitCircle:
                    var hit = SkinPreviewAnimation.HitObject(
                        elapsed,
                        shortNumberFade:
                            visual.Layer.RoleIndex == 2
                            && previewLegacySkinVersion > 1);
                    ApplyPreviewTransform(image, scale: hit.Scale);
                    image.Opacity = baseOpacity * hit.Opacity;
                    break;

                case SkinPreviewAnimationRole.Followpoint:
                    var followpoint = SkinPreviewAnimation.Followpoint(
                        elapsed,
                        visual.Layer.RoleProgress);
                    ApplyPreviewTransform(
                        image,
                        -followpointVector.X
                        * 0.1
                        * (1 - followpoint.TravelProgress),
                        -followpointVector.Y
                        * 0.1
                        * (1 - followpoint.TravelProgress),
                        followpoint.Scale);
                    image.Opacity = baseOpacity * followpoint.Opacity;
                    ApplyAnimationFrame(
                        image,
                        followpointAnimationFrames,
                        followpoint.AnimationTime,
                        sliderBall: false);
                    break;

                case SkinPreviewAnimationRole.SliderBall:
                    ApplyPreviewTransform(
                        image,
                        sliderPosition.X - visual.Layer.CentreX,
                        sliderPosition.Y - visual.Layer.CentreY,
                        rotation: sliderRotation,
                        scaleX: ReadPreviewBoolean(
                            "General",
                            "SliderBallFlip",
                            defaultValue: true) && slider.Reversed
                                ? -1
                                : 1);
                    ApplyAnimationFrame(
                        image,
                        sliderBallAnimationFrames,
                        elapsed,
                        sliderBall: true,
                        sliderVelocity: ElementSliderPreviewVelocity);
                    image.Opacity = baseOpacity * slider.BallOpacity;
                    break;

                case SkinPreviewAnimationRole.SliderFollowCircle:
                    ApplyPreviewTransform(
                        image,
                        sliderPosition.X - visual.Layer.CentreX,
                        sliderPosition.Y - visual.Layer.CentreY,
                        slider.FollowScale);
                    ApplyAnimationFrame(
                        image,
                        sliderFollowAnimationFrames,
                        elapsed,
                        sliderBall: false);
                    image.Opacity = baseOpacity * slider.FollowOpacity;
                    break;

                case SkinPreviewAnimationRole.ReverseArrow:
                    ApplyPreviewTransform(
                        image,
                        scale: slider.ReverseScale,
                        rotation: slider.ReverseRotation);
                    image.Opacity = baseOpacity * slider.ReverseOpacity;
                    break;

                case SkinPreviewAnimationRole.Cursor:
                    if (!interactiveCursorActive)
                    {
                        PlaceInteractiveVisual(
                            image,
                            visual.Layer.CentreX,
                            visual.Layer.CentreY,
                            cursor.Position,
                            ReadPreviewBoolean(
                                "General",
                                "CursorCentre",
                                defaultValue: true),
                            cursor.Scale,
                            cursor.Rotation);
                        image.Opacity = baseOpacity;
                    }
                    break;

                case SkinPreviewAnimationRole.CursorMiddle:
                    if (!interactiveCursorActive)
                    {
                        PlaceInteractiveVisual(
                            image,
                            visual.Layer.CentreX,
                            visual.Layer.CentreY,
                            cursor.Position,
                            ReadPreviewBoolean(
                                "General",
                                "CursorCentre",
                                defaultValue: true),
                            1,
                            0);
                        image.Opacity = baseOpacity;
                    }
                    break;

                case SkinPreviewAnimationRole.CursorTrail:
                    if (!interactiveCursorActive)
                    {
                        var reverseIndex =
                            trailVisuals.Length - 1 - visual.Layer.RoleIndex;
                        var fade = smoothCursor
                            ? SkinPreviewAnimation.SmoothTrailFadeMilliseconds
                            : SkinPreviewAnimation.DisjointTrailFadeMilliseconds;
                        var age = Math.Max(0, reverseIndex)
                                  * (smoothCursor
                                      ? fade
                                        / Math.Max(
                                            1,
                                            trailVisuals.Length - 1)
                                      : SkinPreviewAnimation
                                          .DisjointTrailIntervalMilliseconds);
                        var trailState = SkinPreviewAnimation.Cursor(
                            elapsed - age,
                            640,
                            480,
                            ReadPreviewBoolean(
                                "General",
                                "CursorExpand",
                                defaultValue: true),
                            rotate: true);
                        PlaceInteractiveVisual(
                            image,
                            visual.Layer.CentreX,
                            visual.Layer.CentreY,
                            trailState.Position,
                            smoothCursor || ReadPreviewBoolean(
                                "General",
                                "CursorCentre",
                                defaultValue: true),
                            trailState.Scale
                            * SkinPreviewAnimation.LegacyTrailTextureScale,
                            ReadPreviewBoolean(
                                "General",
                                "CursorTrailRotate",
                                defaultValue: true)
                                ? trailState.Rotation
                                : 0);
                        image.Opacity = baseOpacity
                                        * SkinPreviewAnimation.TrailOpacity(
                                            age,
                                            smoothCursor);
                    }
                    break;

                case SkinPreviewAnimationRole.SpinnerCircle:
                    ApplyPreviewTransform(image, rotation: spinner.Rotation);
                    image.Opacity = baseOpacity * spinner.BodyOpacity;
                    break;
                case SkinPreviewAnimationRole.SpinnerGlow:
                    ApplyPreviewTransform(image, scale: spinner.BodyScale);
                    image.Opacity = baseOpacity
                                    * spinner.BodyOpacity
                                    * spinner.GlowOpacity;
                    break;
                case SkinPreviewAnimationRole.SpinnerBottom:
                    ApplyPreviewTransform(
                        image,
                        scale: spinner.BodyScale,
                        rotation: spinner.Rotation * spinnerTopRatio / 3);
                    image.Opacity = baseOpacity * spinner.BodyOpacity;
                    break;
                case SkinPreviewAnimationRole.SpinnerTop:
                    ApplyPreviewTransform(
                        image,
                        scale: spinner.BodyScale,
                        rotation: spinner.Rotation * spinnerTopRatio);
                    image.Opacity = baseOpacity * spinner.BodyOpacity;
                    break;
                case SkinPreviewAnimationRole.SpinnerMiddle2:
                    ApplyPreviewTransform(
                        image,
                        scale: spinner.BodyScale,
                        rotation: spinner.Rotation);
                    image.Opacity = baseOpacity * spinner.BodyOpacity;
                    break;
                case SkinPreviewAnimationRole.SpinnerMiddle:
                    ApplyPreviewTransform(image, scale: spinner.BodyScale);
                    image.Opacity = baseOpacity * spinner.BodyOpacity;
                    break;
                case SkinPreviewAnimationRole.SpinnerApproach:
                    ApplyPreviewTransform(image, scale: spinner.ApproachScale);
                    image.Opacity = baseOpacity * spinner.BodyOpacity;
                    break;
                case SkinPreviewAnimationRole.SpinnerMetre:
                    ApplyPreviewTransform(
                        image,
                        scaleY: spinner.MetreFill);
                    image.RenderTransformOrigin =
                        new System.Windows.Point(0.5, 1);
                    image.Opacity = baseOpacity * spinner.BodyOpacity;
                    break;
                case SkinPreviewAnimationRole.SpinnerSpin:
                    image.Opacity = baseOpacity * spinner.SpinOpacity;
                    break;
                case SkinPreviewAnimationRole.SpinnerClear:
                    image.Visibility = spinner.ClearOpacity > 0.001
                        ? Visibility.Visible
                        : Visibility.Collapsed;
                    ApplyPreviewTransform(image, scale: spinner.ClearScale);
                    image.Opacity = baseOpacity * spinner.ClearOpacity;
                    break;
                case SkinPreviewAnimationRole.ScorebarMarker:
                    previewHealth = SkinPreviewAnimation.SmoothHealth(
                        previewHealth,
                        SkinPreviewAnimation.HealthTarget(elapsed),
                        previewFrameDelta);
                    ApplyPreviewTransform(
                        image,
                        SkinPreviewAnimation.ScorebarOffsetFromHealth(
                            previewHealth),
                        0);
                    image.Opacity = baseOpacity;
                    break;
            }
        }
    }

    private double CompositionBaseOpacity(ElementCompositionVisual visual)
    {
        if (showFullElementRender || selectedEntry is null)
            return 1;
        return IsSelectedCompositionLayer(
            visual.Layer,
            LogicalStem(selectedEntry.Filename))
            ? 1
            : visual.Layer.ContextOpacity;
    }

    private void ApplyAnimationFrame(
        System.Windows.Controls.Image image,
        IReadOnlyList<BitmapSource> frames,
        double elapsed,
        bool sliderBall,
        double sliderVelocity = double.PositiveInfinity)
    {
        if (frames.Count <= 1)
            return;
        image.Source = frames[SkinPreviewAnimation.FrameIndex(
            elapsed,
            frames.Count,
            previewAnimationFramerate,
            sliderBall,
            sliderVelocity)];
    }

    private void ApplyPreviewTransform(
        System.Windows.Controls.Image image,
        double translateX = 0,
        double translateY = 0,
        double scale = 1,
        double rotation = 0,
        double scaleX = 1,
        double scaleY = 1)
    {
        if (!previewVisualTransforms.TryGetValue(image, out var transforms))
        {
            var baseRotation = image.RenderTransform switch
            {
                RotateTransform rotate => rotate.Angle,
                _ => 0,
            };
            transforms = new PreviewVisualTransforms(baseRotation);
            previewVisualTransforms[image] = transforms;
            image.RenderTransformOrigin = new System.Windows.Point(0.5, 0.5);
            image.RenderTransform = transforms.Group;
        }
        transforms.Scale.ScaleX = scale * scaleX;
        transforms.Scale.ScaleY = scale * scaleY;
        transforms.Rotate.Angle = transforms.BaseRotation + rotation;
        transforms.Translate.X = translateX;
        transforms.Translate.Y = translateY;
    }

    private bool ReadPreviewBoolean(
        string section,
        string key,
        bool defaultValue)
    {
        var value = iniDocument?.GetValue(section, key);
        if (string.IsNullOrWhiteSpace(value))
            return defaultValue;
        return value.Trim() is "1" or "true" or "True";
    }

    private void ElementCompositionSurface_MouseEnter(
        object sender,
        MouseEventArgs e)
    {
        UpdateInteractiveCursorPosition(e);
        TryBeginInteractiveCursorPreview();
    }

    private void ElementCompositionSurface_MouseMove(
        object sender,
        MouseEventArgs e)
    {
        UpdateInteractiveCursorPosition(e);
        if (!interactiveCursorActive)
            TryBeginInteractiveCursorPreview();
    }

    private void ElementCompositionSurface_MouseLeave(
        object sender,
        MouseEventArgs e) =>
        EndInteractiveCursorPreview();

    private void ElementCompositionSurface_MouseLeftButtonDown(
        object sender,
        MouseButtonEventArgs e) =>
        BeginInteractiveCursorPress();

    private void ElementCompositionSurface_MouseLeftButtonUp(
        object sender,
        MouseButtonEventArgs e) =>
        EndInteractiveCursorPress();

    private void ElementCompositionSurface_MouseRightButtonDown(
        object sender,
        MouseButtonEventArgs e) =>
        BeginInteractiveCursorPress();

    private void ElementCompositionSurface_MouseRightButtonUp(
        object sender,
        MouseButtonEventArgs e) =>
        EndInteractiveCursorPress();

    private void BeginInteractiveCursorPress()
    {
        if (!interactiveCursorActive)
            return;
        interactiveCursorDownCount++;
        if (interactiveCursorDownCount != 1)
            return;
        BeginInteractiveCursorScaleTransition(
            ReadPreviewBoolean(
                "General",
                "CursorExpand",
                defaultValue: true)
                ? 1.3
                : 1);
    }

    private void EndInteractiveCursorPress()
    {
        if (!interactiveCursorActive)
            return;
        interactiveCursorDownCount = Math.Max(
            0,
            interactiveCursorDownCount - 1);
        if (interactiveCursorDownCount == 0)
            BeginInteractiveCursorScaleTransition(1);
    }

    private void UpdateInteractiveCursorPosition(MouseEventArgs e)
    {
        if (ElementCompositionCanvas is null)
            return;
        var point = e.GetPosition(ElementCompositionCanvas);
        var nextPosition = new System.Windows.Point(
            Math.Clamp(point.X, 0, SkinCursorPreview.CanvasWidth),
            Math.Clamp(point.Y, 0, SkinCursorPreview.CanvasHeight));
        interactiveCursorPosition = nextPosition;
        if (interactiveCursorActive && InteractiveCursorUsesSmoothTrail())
        {
            AppendInteractiveSmoothTrail(
                nextPosition,
                previewRenderClock.Elapsed.TotalMilliseconds);
        }
    }

    private void TryBeginInteractiveCursorPreview()
    {
        if (interactiveCursorActive)
            return;
        var cursorVisual = elementCompositionVisuals.FirstOrDefault(visual =>
            visual.Layer.Role == SkinPreviewAnimationRole.Cursor
            && visual.Image.Source is not null
            && visual.Image.Visibility == Visibility.Visible);
        if (!SkinPreviewAnimation.CanActivateInteractiveCursor(
                elementCenterMode == SkinEditorCenterMode.Asset,
                renderedElementCompositionKind == SkinElementCompositionKind.Cursor,
                cursorVisual is not null))
            return;

        interactiveCursorActive = true;
        interactiveCursorSamples.Clear();
        interactiveCursorLastSampleTime = double.NegativeInfinity;
        interactiveCursorScale = 1;
        interactiveCursorScaleFrom = 1;
        interactiveCursorScaleTarget = 1;
        interactiveCursorScaleStartTime =
            previewRenderClock.Elapsed.TotalMilliseconds;
        interactiveCursorDownCount = 0;
        interactiveSmoothTrailAnchor = interactiveCursorPosition;
        ElementCompositionSurface.Cursor = Cursors.None;
        foreach (var decoration in ElementCompositionCanvas.Children
                     .OfType<FrameworkElement>()
                     .Where(child => ReferenceEquals(
                         child.Tag,
                         ElementCompositionDecorationTag)))
            decoration.Visibility = Visibility.Collapsed;

        var existingTrails = elementCompositionVisuals
            .Where(visual =>
                visual.Layer.Role == SkinPreviewAnimationRole.CursorTrail)
            .Select(visual => visual.Image)
            .ToArray();
        interactiveCursorTrailVisuals.AddRange(existingTrails);
        var smooth = elementCompositionVisuals.Any(visual =>
            visual.Layer.Role == SkinPreviewAnimationRole.CursorMiddle);
        var desiredCount = smooth ? 32 : 10;
        var sourceTrail = existingTrails.LastOrDefault();
        while (sourceTrail is not null
               && interactiveCursorTrailVisuals.Count < desiredCount)
        {
            var clone = new System.Windows.Controls.Image
            {
                Source = sourceTrail.Source,
                Width = sourceTrail.Width,
                Height = sourceTrail.Height,
                Stretch = Stretch.Fill,
                IsHitTestVisible = false,
                RenderTransformOrigin = new System.Windows.Point(0.5, 0.5),
            };
            Canvas.SetLeft(clone, Canvas.GetLeft(sourceTrail));
            Canvas.SetTop(clone, Canvas.GetTop(sourceTrail));
            Panel.SetZIndex(clone, 9);
            ElementCompositionCanvas.Children.Add(clone);
            interactiveCursorTrailVisuals.Add(clone);
        }
        UpdatePreviewAnimationSubscription();
        RenderInteractiveCursor(previewRenderClock.Elapsed.TotalMilliseconds);
    }

    private void RenderInteractiveCursor(double wallTime)
    {
        if (!interactiveCursorActive)
            return;
        var smooth = InteractiveCursorUsesSmoothTrail();
        interactiveCursorScale = SkinPreviewAnimation.CursorTransitionScale(
            interactiveCursorScaleFrom,
            interactiveCursorScaleTarget,
            wallTime - interactiveCursorScaleStartTime);
        var cursorRotation = ReadPreviewBoolean(
                                 "General",
                                 "CursorRotate",
                                 defaultValue: true)
            ? previewAnimationElapsed
              % SkinPreviewAnimation.CursorRevolutionMilliseconds
              / SkinPreviewAnimation.CursorRevolutionMilliseconds * 360
            : 0;
        if (!smooth
            && wallTime - interactiveCursorLastSampleTime
            >= SkinPreviewAnimation.DisjointTrailIntervalMilliseconds)
        {
            interactiveCursorSamples.Add(new InteractiveCursorSample(
                interactiveCursorPosition,
                wallTime,
                interactiveCursorScale,
                cursorRotation));
            interactiveCursorLastSampleTime = wallTime;
            TrimInteractiveCursorSamples();
        }
        var fade = smooth
            ? SkinPreviewAnimation.SmoothTrailFadeMilliseconds
            : SkinPreviewAnimation.DisjointTrailFadeMilliseconds;
        interactiveCursorSamples.RemoveAll(sample =>
            wallTime - sample.Time > fade);

        var cursorCentre = ReadPreviewBoolean(
            "General",
            "CursorCentre",
            defaultValue: true);
        foreach (var visual in elementCompositionVisuals.Where(visual =>
                     visual.Layer.Role is SkinPreviewAnimationRole.Cursor
                         or SkinPreviewAnimationRole.CursorMiddle))
        {
            PlaceInteractiveVisual(
                visual.Image,
                visual.Layer.CentreX,
                visual.Layer.CentreY,
                interactiveCursorPosition,
                cursorCentre,
                visual.Layer.Role == SkinPreviewAnimationRole.Cursor
                    ? interactiveCursorScale
                    : 1,
                visual.Layer.Role == SkinPreviewAnimationRole.Cursor
                    ? cursorRotation
                    : 0);
            visual.Image.Opacity = 1;
        }

        for (var index = 0; index < interactiveCursorTrailVisuals.Count; index++)
        {
            var image = interactiveCursorTrailVisuals[index];
            var sampleIndex = interactiveCursorSamples.Count - 1 - index;
            if (sampleIndex < 0)
            {
                image.Visibility = Visibility.Collapsed;
                continue;
            }
            var sample = interactiveCursorSamples[sampleIndex];
            image.Visibility = Visibility.Visible;
            PlaceInteractiveVisual(
                image,
                Canvas.GetLeft(image) + image.Width / 2,
                Canvas.GetTop(image) + image.Height / 2,
                sample.Position,
                smooth || cursorCentre,
                sample.Scale
                * SkinPreviewAnimation.LegacyTrailTextureScale,
                ReadPreviewBoolean(
                    "General",
                    "CursorTrailRotate",
                    defaultValue: true)
                    ? sample.Rotation
                    : 0);
            image.Opacity = SkinPreviewAnimation.TrailOpacity(
                wallTime - sample.Time,
                smooth);
        }
    }

    private bool InteractiveCursorUsesSmoothTrail() =>
        renderedCursorUsesSmoothTrail;

    private void BeginInteractiveCursorScaleTransition(double target)
    {
        var now = previewRenderClock.Elapsed.TotalMilliseconds;
        interactiveCursorScale = SkinPreviewAnimation.CursorTransitionScale(
            interactiveCursorScaleFrom,
            interactiveCursorScaleTarget,
            now - interactiveCursorScaleStartTime);
        interactiveCursorScaleFrom = interactiveCursorScale;
        interactiveCursorScaleTarget = target;
        interactiveCursorScaleStartTime = now;
    }

    private void AppendInteractiveSmoothTrail(
        System.Windows.Point position,
        double wallTime)
    {
        if (!interactiveSmoothTrailAnchor.HasValue)
        {
            interactiveSmoothTrailAnchor = position;
            return;
        }
        var sourceTrail = interactiveCursorTrailVisuals.LastOrDefault();
        var displayWidth = (sourceTrail?.Width ?? 52)
                           * SkinPreviewAnimation.LegacyTrailTextureScale;
        var interval = SkinPreviewAnimation.TrailInterval(displayWidth);
        var parts = SkinPreviewAnimation.SmoothTrailParts(
            interactiveSmoothTrailAnchor.Value,
            position,
            interval);
        if (parts.Count == 0)
            return;
        interactiveCursorScale = SkinPreviewAnimation.CursorTransitionScale(
            interactiveCursorScaleFrom,
            interactiveCursorScaleTarget,
            wallTime - interactiveCursorScaleStartTime);
        var rotation = ReadPreviewBoolean(
                           "General",
                           "CursorRotate",
                           defaultValue: true)
            ? previewAnimationElapsed
              % SkinPreviewAnimation.CursorRevolutionMilliseconds
              / SkinPreviewAnimation.CursorRevolutionMilliseconds * 360
            : 0;
        foreach (var part in parts)
        {
            interactiveCursorSamples.Add(new InteractiveCursorSample(
                part,
                wallTime,
                interactiveCursorScale,
                rotation));
        }
        interactiveSmoothTrailAnchor = parts[^1];
        interactiveCursorLastSampleTime = wallTime;
        TrimInteractiveCursorSamples();
    }

    private void TrimInteractiveCursorSamples()
    {
        const int maxTrailParts = 32;
        if (interactiveCursorSamples.Count <= maxTrailParts)
            return;
        interactiveCursorSamples.RemoveRange(
            0,
            interactiveCursorSamples.Count - maxTrailParts);
    }

    private void PlaceInteractiveVisual(
        System.Windows.Controls.Image image,
        double baselineCentreX,
        double baselineCentreY,
        System.Windows.Point position,
        bool centred,
        double scale,
        double rotation)
    {
        var baselineLeft = baselineCentreX - image.Width / 2;
        var baselineTop = baselineCentreY - image.Height / 2;
        var desiredLeft = centred ? position.X - image.Width / 2 : position.X;
        var desiredTop = centred ? position.Y - image.Height / 2 : position.Y;
        ApplyPreviewTransform(
            image,
            desiredLeft - baselineLeft,
            desiredTop - baselineTop,
            scale,
            rotation);
        image.RenderTransformOrigin = centred
            ? new System.Windows.Point(0.5, 0.5)
            : new System.Windows.Point(0, 0);
    }

    private void EndInteractiveCursorPreview(bool restoreComposition = true)
    {
        if (!interactiveCursorActive)
            return;
        interactiveCursorActive = false;
        interactiveCursorSamples.Clear();
        interactiveCursorLastSampleTime = double.NegativeInfinity;
        interactiveSmoothTrailAnchor = null;
        interactiveCursorScale = 1;
        interactiveCursorScaleFrom = 1;
        interactiveCursorScaleTarget = 1;
        interactiveCursorDownCount = 0;
        if (ElementCompositionSurface is not null)
            ElementCompositionSurface.Cursor = null;

        var compositionImages = elementCompositionVisuals
            .Select(visual => visual.Image)
            .ToHashSet();
        foreach (var image in interactiveCursorTrailVisuals
                     .Where(image => !compositionImages.Contains(image))
                     .ToArray())
        {
            previewVisualTransforms.Remove(image);
            ElementCompositionCanvas.Children.Remove(image);
        }
        interactiveCursorTrailVisuals.Clear();
        if (restoreComposition && selectedEntry is not null)
            TryUpdateElementCompositionSelection(
                SkinElementCompositionKind.Cursor,
                selectedEntry);
        UpdatePreviewAnimationSubscription();
        if (restoreComposition)
            AnimateElementComposition(previewAnimationElapsed);
    }

    private void SetWorkspaceMode(SkinEditorWorkspaceMode mode)
    {
        if (mode != SkinEditorWorkspaceMode.Elements)
            EndInteractiveCursorPreview();
        workspaceMode = mode;
        UpdateStudioState();
        UpdatePreviewAnimationSubscription();
        if (mode == SkinEditorWorkspaceMode.SkinIni)
        {
            UpdateComboStrip();
            if (iniDocument is not null)
                RefreshIniFormAfterLayout(iniDocument);
        }
    }

    private void SetElementCenterMode(SkinEditorCenterMode mode)
    {
        if (mode is not SkinEditorCenterMode.Asset and not SkinEditorCenterMode.Gameplay)
            return;
        if (mode != SkinEditorCenterMode.Asset)
            EndInteractiveCursorPreview();
        elementCenterMode = mode;
        UpdateStudioState();
        UpdatePreviewAnimationSubscription();
        RenderPreviewFrame();
    }

    private void SetIniCenterMode(SkinEditorCenterMode mode)
    {
        if (mode is not SkinEditorCenterMode.IniForm and not SkinEditorCenterMode.IniRaw)
            return;
        IniModeTabs.SelectedIndex = mode == SkinEditorCenterMode.IniForm ? 0 : 1;
        IniFormModeButton.IsChecked = mode == SkinEditorCenterMode.IniForm;
        IniRawModeButton.IsChecked = mode == SkinEditorCenterMode.IniRaw;
    }

    private void SetInspectorMode(SkinEditorInspectorMode mode)
    {
        inspectorMode = mode;
        if (mode == SkinEditorInspectorMode.Review && responsiveState.IsCompact)
            compactSurface = SkinEditorCompactSurface.Properties;
        UpdateStudioState();
    }

    private void UpdateStudioState()
    {
        var elements = workspaceMode == SkinEditorWorkspaceMode.Elements;
        WorkspaceTabs.SelectedIndex = elements ? 0 : 1;
        ElementsModeButton.IsChecked = elements;
        IniWorkspaceModeButton.IsChecked = !elements;
        ElementNavigatorContent.Visibility = elements ? Visibility.Visible : Visibility.Collapsed;
        IniNavigatorContent.Visibility = elements ? Visibility.Collapsed : Visibility.Visible;
        ElementInspectorContent.Visibility = elements ? Visibility.Visible : Visibility.Collapsed;
        IniInspectorContent.Visibility = elements ? Visibility.Collapsed : Visibility.Visible;

        AssetCanvasButton.IsChecked = elementCenterMode == SkinEditorCenterMode.Asset;
        GameplayCanvasButton.IsChecked = elementCenterMode == SkinEditorCenterMode.Gameplay;
        AssetCanvasPanel.Visibility = elementCenterMode == SkinEditorCenterMode.Asset
            ? Visibility.Visible
            : Visibility.Collapsed;
        GameplayPreviewPanel.Visibility = elementCenterMode == SkinEditorCenterMode.Gameplay
            ? Visibility.Visible
            : Visibility.Collapsed;
        GameplayLocalPreviewPanel.Visibility =
            elements
            && elementCenterMode == SkinEditorCenterMode.Gameplay
            && selectedEntry?.IsImage == true
                ? Visibility.Visible
                : Visibility.Collapsed;

        var reviewing = inspectorMode == SkinEditorInspectorMode.Review;
        ContextInspectorContent.Visibility = reviewing ? Visibility.Collapsed : Visibility.Visible;
        ReviewInspectorContent.Visibility = reviewing ? Visibility.Visible : Visibility.Collapsed;
        ReviewCloseButton.Visibility = reviewing ? Visibility.Visible : Visibility.Collapsed;
        InspectorTitleText.Text = reviewing
            ? "Staged changes"
            : elements ? "Element properties" : "Setting context";
        InspectorSubtitleText.Text = reviewing
            ? "Review this editing session before applying it."
            : elements
                ? selectedEntry?.Filename ?? "Select an element to edit it."
                : focusedIniRow?.Definition.Label ?? "Select a skin.ini setting.";

        CompactBrowseButton.Content = elements ? "Browse" : "Sections";
        CompactCanvasButton.Content = elements ? "Canvas" : "Editor";
        CompactPropertiesButton.Content = elements ? "Properties" : "Context";
        CompactBrowseButton.IsChecked = compactSurface == SkinEditorCompactSurface.Browse;
        CompactCanvasButton.IsChecked = compactSurface == SkinEditorCompactSurface.Canvas;
        CompactPropertiesButton.IsChecked = compactSurface == SkinEditorCompactSurface.Properties;

        if (!responsiveState.IsCompact)
        {
            NavigatorPanel.Visibility = Visibility.Visible;
            CenterPanel.Visibility = Visibility.Visible;
            InspectorPanel.Visibility = Visibility.Visible;
            return;
        }

        var showBrowse = compactSurface == SkinEditorCompactSurface.Browse;
        var showCanvas = compactSurface == SkinEditorCompactSurface.Canvas;
        var showProperties = compactSurface == SkinEditorCompactSurface.Properties;
        NavigatorPanel.Visibility = showBrowse ? Visibility.Visible : Visibility.Collapsed;
        CenterPanel.Visibility = showCanvas ? Visibility.Visible : Visibility.Collapsed;
        InspectorPanel.Visibility = showProperties ? Visibility.Visible : Visibility.Collapsed;
        NavigatorColumn.Width = showBrowse ? new GridLength(1, GridUnitType.Star) : new GridLength(0);
        NavigatorGapColumn.Width = new GridLength(0);
        CenterColumn.Width = showCanvas ? new GridLength(1, GridUnitType.Star) : new GridLength(0);
        InspectorGapColumn.Width = new GridLength(0);
        InspectorColumn.Width = showProperties ? new GridLength(1, GridUnitType.Star) : new GridLength(0);
    }

    private void WorkspaceMode_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: string tag }) return;
        SetWorkspaceMode(tag == "SkinIni"
            ? SkinEditorWorkspaceMode.SkinIni
            : SkinEditorWorkspaceMode.Elements);
    }

    private void ElementCenterMode_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: string tag }) return;
        SetElementCenterMode(tag == "Gameplay"
            ? SkinEditorCenterMode.Gameplay
            : SkinEditorCenterMode.Asset);
    }

    private void SliderEndCircleToggle_Click(object sender, RoutedEventArgs e)
    {
        var visibility = SliderEndCircleToggle.IsChecked == true
            ? Visibility.Visible
            : Visibility.Collapsed;
        GameplayTailCircle.Visibility = visibility;
        GameplayTailOverlay.Visibility = visibility;
    }

    private void IniCenterMode_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: string tag }) return;
        SetIniCenterMode(tag == "Raw"
            ? SkinEditorCenterMode.IniRaw
            : SkinEditorCenterMode.IniForm);
    }

    private void CompactSurface_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: string tag }) return;
        compactSurface = tag switch
        {
            "Browse" => SkinEditorCompactSurface.Browse,
            "Properties" => SkinEditorCompactSurface.Properties,
            _ => SkinEditorCompactSurface.Canvas,
        };
        UpdateStudioState();
    }

    private async void DraftReview_Click(object sender, RoutedEventArgs e)
    {
        SetInspectorMode(SkinEditorInspectorMode.Review);
        DraftPreflightText.Text = "Checking effective assets and lazer compatibility…";
        var report = await BuildCurrentPreflightAsync();
        DraftPreflightText.Text = report.Summary
            + (report.Issues.Count == 0
                ? ""
                : "\n" + string.Join("\n", report.Issues.Take(3)
                    .Select(issue => $"• {issue.Message}")));
    }

    private void ReviewClose_Click(object sender, RoutedEventArgs e) =>
        SetInspectorMode(SkinEditorInspectorMode.Context);

    private void RestoreSkinSelection()
    {
        SetSkinPickerSelection(currentSkin);
    }

    private void AbandonPendingDuplicate()
    {
        if (pendingDuplicate is not { } duplicate)
            return;
        pendingDuplicate = null;
        allSkins = allSkins
            .Where(skin => skin.Id != duplicate.WorkingSkinId)
            .ToArray();
        CompactSkinPicker.ItemsSource = allSkins;
        _ = Task.Run(() => SkinDraftRecovery.Clear(duplicate.WorkingSkinId));
    }

    private void SetSkinPickerSelection(LazerSkinInfo? skin)
    {
        suppressSkinSelection = true;
        suppressSkinCatalogFilter = true;
        var selection = skin is null
            ? null
            : allSkins.FirstOrDefault(candidate => candidate.Id == skin.Id) ?? skin;
        CompactSkinPicker.SelectedItem = selection;
        CompactSkinPicker.Text = selection?.DisplayName ?? "";
        suppressSkinCatalogFilter = false;
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

    private async void CompactSkinPicker_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!suppressSkinSelection && CompactSkinPicker.SelectedItem is LazerSkinInfo skin)
            await SelectSkinAsync(skin);
    }

    private void CompactSkinPicker_KeyUp(object sender, KeyEventArgs e)
    {
        if (suppressSkinCatalogFilter || loading)
            return;
        var query = CompactSkinPicker.Text.Trim();
        if (CompactSkinPicker.SelectedItem is LazerSkinInfo selected
            && string.Equals(query, selected.DisplayName, StringComparison.Ordinal))
            return;
        var matches = string.IsNullOrWhiteSpace(query)
            ? allSkins
            : allSkins.Where(skin => skin.Name.Contains(query, StringComparison.OrdinalIgnoreCase)
                || skin.Creator.Contains(query, StringComparison.OrdinalIgnoreCase)
                || skin.DisplayName.Contains(query, StringComparison.OrdinalIgnoreCase)).ToArray();
        CompactSkinPicker.ItemsSource = matches;
        CompactSkinPicker.IsDropDownOpen = true;
    }

    private void UpdateOnboardingState()
    {
        if (WelcomePanel is null || FirstRunGuide is null) return;
        WelcomePanel.Visibility = initialized && currentSkin is null
            ? Visibility.Visible
            : Visibility.Collapsed;
        FirstRunGuide.Visibility = currentSkin is not null
                                   && !settings.Current.SkinEditor.HasSeenSkinStudioGuide
            ? Visibility.Visible
            : Visibility.Collapsed;
        if (CompactSurfaceBar is not null)
            CompactSurfaceBar.Visibility = responsiveState.IsCompact
                                           && FirstRunGuide.Visibility != Visibility.Visible
                ? Visibility.Visible
                : Visibility.Collapsed;
    }

    private void ChooseExistingSkin_Click(object sender, RoutedEventArgs e)
    {
        if (allSkins.Count == 0)
        {
            StatusText.Text = "No lazer skins were found. Create one here, or import a skin in osu!lazer first.";
            return;
        }
        CompactSkinPicker.ItemsSource = allSkins;
        CompactSkinPicker.Text = "";
        CompactSkinPicker.Focus();
        CompactSkinPicker.IsDropDownOpen = true;
    }

    private void DismissFirstRunGuide_Click(object sender, RoutedEventArgs e)
    {
        settings.Update(value => value.SkinEditor.HasSeenSkinStudioGuide = true);
        UpdateOnboardingState();
    }

    private async void ImportSkinToExtras_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = "Import an osu! skin into Extras",
            Filter = "osu! skin archives|*.osk;*.zip|All files|*.*",
        };
        if (dialog.ShowDialog(Window.GetWindow(this)) != true) return;
        SetBusy(true, "Reading skin archive…");
        SkinExtractionSource source;
        try
        {
            source = await Task.Run(() => new SkinExtrasExtractionService().ReadOsk(dialog.FileName));
        }
        catch (Exception ex)
        {
            StatusText.Text = $"Could not read this skin: {ex.Message}";
            return;
        }
        finally
        {
            SetBusy(false);
        }

        EnsureExtrasDirectories();
        var extractor = new SkinExtrasExtractorWindow(
            Window.GetWindow(this),
            source,
            ExtrasModeVisibility(),
            PersistLazerExtrasFilter);
        if (extractor.ShowDialog() != true) return;
        var added = extractor.Results.Count(result =>
            result.Status == SkinExtraExtractionStatus.Extracted);
        var skipped = extractor.Results.Count - added;
        StatusText.Text = $"Imported into Extras: {added} reusable packs added, {skipped} exact duplicates skipped.";
    }

    private void UpdateSkinReadiness()
    {
        if (SkinReadinessPanel is null || SkinReadinessItems is null || currentSkin is null)
        {
            if (SkinReadinessPanel is not null)
                SkinReadinessPanel.Visibility = Visibility.Collapsed;
            return;
        }

        var effectiveFiles = currentSkin.Files.Select(file => file.Filename)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (draft is not null)
        {
            foreach (var change in draft.Changes)
            {
                if (change.IsDeletion) effectiveFiles.Remove(change.Filename);
                else effectiveFiles.Add(change.Filename);
            }
        }
        bool HasCategory(params string[] names) => effectiveFiles.Any(filename =>
            names.Contains(SkinElementCategorizer.CategoryFor(filename), StringComparer.OrdinalIgnoreCase));
        var definitions = new[]
        {
            new SkinReadinessDefinition("Cursor", "Cursor", HasCategory("Cursor")),
            new SkinReadinessDefinition("Hit objects", "Hitcircles", HasCategory("Hitcircles")),
            new SkinReadinessDefinition("Sliders", "Sliders", HasCategory("Sliders")),
            new SkinReadinessDefinition("Judgements", "Judgements", HasCategory("Judgements")),
            new SkinReadinessDefinition("HUD", "Interface", HasCategory("Scorebar", "Interface")),
            new SkinReadinessDefinition("Sounds", "Sounds", effectiveFiles.Any(SkinElementCategorizer.IsAudio)),
            new SkinReadinessDefinition("skin.ini", "SkinIni", effectiveFiles.Contains("skin.ini")),
        };
        var ready = definitions.Count(definition => definition.IsReady);
        SkinReadinessPanel.Header = $"Skin readiness  {ready}/{definitions.Length}";
        SkinReadinessPanel.Visibility = ready == definitions.Length
            ? Visibility.Collapsed
            : Visibility.Visible;
        if (readinessSkinId != currentSkin.Id)
        {
            readinessSkinId = currentSkin.Id;
            SkinReadinessPanel.IsExpanded = ready <= 2;
        }
        SkinReadinessItems.Children.Clear();
        foreach (var definition in definitions)
        {
            var button = new Button
            {
                Content = definition.IsReady ? $"✓ {definition.Label}" : $"+ {definition.Label}",
                Tag = definition.ExtrasHint,
                IsEnabled = !definition.IsReady,
                Margin = new Thickness(0, 0, 5, 5),
                Padding = new Thickness(7, 3, 7, 3),
                ToolTip = definition.IsReady
                    ? $"{definition.Label} is ready"
                    : $"Add {definition.Label} from Extras",
            };
            button.Click += ReadinessItem_Click;
            SkinReadinessItems.Children.Add(button);
        }
    }

    private void ReadinessItem_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string hint }) return;
        if (hint == "SkinIni")
        {
            SetWorkspaceMode(SkinEditorWorkspaceMode.SkinIni);
            return;
        }
        extrasCategoryHintOverride = hint;
        ReplaceFromExtras_Click(sender, e);
    }

    private async void CreateBlankSkin_Click(object sender, RoutedEventArgs e)
    {
        await CreateBlankSkinAsync(openExtrasAfterCreate: false);
    }

    private async void CreateSkinFromExtras_Click(object sender, RoutedEventArgs e) =>
        await CreateBlankSkinAsync(openExtrasAfterCreate: true);

    private async void DuplicateSkin_Click(object sender, RoutedEventArgs e)
    {
        if (catalog is null || currentSkin is null)
            return;

        var requested = KumoriDialog.Input(
                Window.GetWindow(this),
                "Create an editable copy. It stays staged in Kumori until Save exports "
                + "the complete OSK and auto-imports it into osu!lazer.",
                "Duplicate skin",
                $"{currentSkin.Name} copy")
            .Trim();
        await PrepareDuplicateAsync(requested);
    }

    private async Task<bool> PrepareDuplicateAsync(string requested)
    {
        if (string.IsNullOrWhiteSpace(requested))
            return false;
        if (requested.IndexOfAny(['\r', '\n']) >= 0)
        {
            KumoriDialog.Show(
                Window.GetWindow(this),
                "A skin name cannot contain a line break.",
                "Duplicate skin",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
            return false;
        }
        if (catalog is null || currentSkin is null || !await ResolveDirtyStateAsync())
            return false;
        var source = currentSkin;

        var existingNames = allSkins
            .Select(skin => skin.Name)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var name = requested;
        var suffix = 2;
        while (!existingNames.Add(name))
            name = $"{requested} ({suffix++})";

        var working = source with
        {
            Id = Guid.NewGuid(),
            Name = name,
        };
        pendingDuplicate = new PendingSkinDuplicate(
            working.Id,
            source.Id,
            name,
            source.Creator);
        allSkins = allSkins
            .Append(working)
            .OrderBy(skin => skin.DisplayName, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        CompactSkinPicker.ItemsSource = allSkins;
        await SelectSkinAsync(
            working,
            forceReload: true,
            restoreRecoveredDraft: false);

        if (iniDocument is null || draft is null)
        {
            AbandonPendingDuplicate();
            await SelectSkinAsync(
                source,
                forceReload: true,
                restoreRecoveredDraft: false);
            StatusText.Text =
                "Could not prepare the duplicate because its skin.ini could not be loaded.";
            return false;
        }

        iniDocument.SetValue("General", "Name", name);
        iniDocument.SetValue("General", "Author", source.Creator);
        SetRawText(iniDocument.ToText());
        BuildIniForm();
        draft.Stage(
            "skin.ini",
            iniFile?.Hash,
            iniDocument.ToBytes(),
            "skin.ini (duplicate identity)");
        ActiveSkinLabel.Text = "DUPLICATE DRAFT";
        GameplaySkinName.Text = working.DisplayName;
        InspectorSubtitleText.Text = working.DisplayName;
        StatusText.Text =
            $"Editing staged duplicate {working.DisplayName}. Save will export its OSK and auto-import it into osu!lazer.";
        UpdateDirtyState();
        return true;
    }

    private async Task CreateBlankSkinAsync(bool openExtrasAfterCreate)
    {
        if (catalog is null || !await ResolveDirtyStateAsync()) return;
        var name = KumoriDialog.Input(
                Window.GetWindow(this),
                openExtrasAfterCreate
                    ? "Create a new skin, then choose its first family from Extras."
                    : "Create a blank skin with an initial skin.ini. You can build it from your Extras library immediately.",
                openExtrasAfterCreate ? "New skin from Extras" : "New blank skin",
                "Untitled skin")
            .Trim();
        if (string.IsNullOrWhiteSpace(name))
            return;
        if (name.IndexOfAny(['\r', '\n']) >= 0)
        {
            KumoriDialog.Show(
                Window.GetWindow(this),
                "A skin name cannot contain a line break.",
                "New blank skin",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
            return;
        }

        var creator = Environment.UserName;
        var existingNames = allSkins.Select(skin => skin.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var baseName = name;
        var suffix = 2;
        while (!existingNames.Add(name))
            name = $"{baseName} ({suffix++})";

        SetBusy(true, "Creating blank skin…");
        try
        {
            var ini = SkinIniDocument.Create(name, creator);
            var created = await Task.Run(() =>
                realmService.CreateSkin(catalog.RootPath, name, creator, ini.ToBytes()));
            await LoadCatalogAsync(created.Id);
            StatusText.Text = openExtrasAfterCreate
                ? $"Created {created.DisplayName}. Choose your first Extras pack."
                : $"Created {created.DisplayName}. Add Extras to start building it.";
            if (openExtrasAfterCreate)
                ReplaceFromExtras_Click(this, new RoutedEventArgs());
        }
        catch (Exception ex)
        {
            StatusText.Text = $"Could not create a blank skin: {ex.Message}";
        }
        finally
        {
            SetBusy(false);
        }
    }

    private async void EditSkinIdentity_Click(object sender, RoutedEventArgs e)
    {
        if (catalog is null || currentSkin is null)
            return;
        var editingDuplicate = pendingDuplicate is { } duplicate
                               && duplicate.WorkingSkinId == currentSkin.Id;
        if (!editingDuplicate && !await ResolveDirtyStateAsync())
            return;
        var owner = Window.GetWindow(this);
        var name = KumoriDialog.Input(
            owner,
            "This updates the lazer skin name and the [General] Name in skin.ini together.",
            "Rename skin",
            currentSkin.Name).Trim();
        if (string.IsNullOrWhiteSpace(name) || name.IndexOfAny(['\r', '\n']) >= 0) return;
        var creator = KumoriDialog.Input(
            owner,
            "Set the author shown by osu!lazer and in skin.ini.",
            "Skin author",
            currentSkin.Creator).Trim();
        if (creator.IndexOfAny(['\r', '\n']) >= 0) return;

        var document = rawDirty && iniDocument is not null
            ? iniDocument.WithText(RawIniText.Text)
            : iniDocument ?? SkinIniDocument.Create(name, creator);
        document.SetValue("General", "Name", name);
        document.SetValue("General", "Author", creator);
        if (editingDuplicate && pendingDuplicate is { } pending && draft is not null)
        {
            iniDocument = document;
            rawDirty = false;
            iniDirty = false;
            draft.Stage(
                "skin.ini",
                iniFile?.Hash,
                document.ToBytes(),
                "skin.ini (duplicate identity)");
            currentSkin = currentSkin with { Name = name, Creator = creator };
            pendingDuplicate = pending with { Name = name, Creator = creator };
            allSkins = allSkins
                .Select(skin => skin.Id == currentSkin.Id ? currentSkin : skin)
                .OrderBy(skin => skin.DisplayName, StringComparer.OrdinalIgnoreCase)
                .ToArray();
            CompactSkinPicker.ItemsSource = allSkins;
            SetSkinPickerSelection(currentSkin);
            ActiveSkinLabel.Text = "DUPLICATE DRAFT";
            GameplaySkinName.Text = currentSkin.DisplayName;
            InspectorSubtitleText.Text = currentSkin.DisplayName;
            SetRawText(document.ToText());
            BuildIniForm();
            StatusText.Text =
                $"Updated the staged duplicate identity to {currentSkin.DisplayName}.";
            UpdateDirtyState();
            return;
        }

        SetBusy(true, "Saving skin identity…");
        try
        {
            if (!await EnsureBackupAsync()) return;
            var result = await Task.Run(() => realmService.UpdateSkinIdentity(
                catalog.RootPath,
                currentSkin.Id,
                name,
                creator,
                document.ToBytes(),
                iniFile?.Hash));
            if (!result.Changed)
            {
                HandleWriteResult(result, "skin identity");
                return;
            }
            StatusText.Text = $"Updated {name} and its skin.ini identity together.";
            await ReloadCurrentSkinAsync();
        }
        catch (Exception ex)
        {
            StatusText.Text = $"Could not update the skin identity: {ex.Message}";
        }
        finally
        {
            SetBusy(false);
        }
    }

    private void ResetCurrentCategory_Click(object sender, RoutedEventArgs e)
    {
        if (draft is null || CategoryPicker.SelectedItem is not CategoryChoice { Category: var category })
            return;
        var entries = category.Files.ToArray();
        var staged = entries.SelectMany(entry => entry.PhysicalEntries)
            .Select(entry => entry.Filename)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var count = draft.Changes.Count(change => staged.Contains(change.Filename));
        if (count == 0 && entries.All(entry => !entry.HasEdits))
        {
            StatusText.Text = $"{category.Name} already matches the current lazer skin.";
            return;
        }
        if (KumoriDialog.Show(
                Window.GetWindow(this),
                $"Discard {count} staged change(s) and local edits in {category.Name}?\n\nThis keeps every other category intact.",
                "Reset current category",
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning,
                MessageBoxResult.No) != MessageBoxResult.Yes)
            return;
        foreach (var filename in staged)
            draft.Remove(filename);
        foreach (var entry in entries)
            entry.Reset();
        if (selectedEntry is not null && entries.Contains(selectedEntry))
            _ = SelectEntryAsync(selectedEntry);
        StatusText.Text = $"Reset {category.Name}; other staged changes were kept.";
        UpdateDirtyState();
    }

    private void ElementSearchBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        elementSearchTimer.Stop();
        elementSearchTimer.Start();
    }

    private async void CategoryPicker_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        await ShowCategoryAsync((CategoryPicker.SelectedItem as CategoryChoice)?.Category);
        await ShowFullElementRenderAsync();
    }

    private void IniSectionList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (IniSectionList.SelectedItem is not IniSectionChoice choice)
            return;
        ShowIniSection(choice.Name);
    }

    private void ShowIniSection(string sectionName)
    {
        if (!iniSectionPanels.TryGetValue(sectionName, out var selected))
            return;

        IniFormPanel.Children.Clear();
        selected.Visibility = Visibility.Visible;
        IniFormPanel.Children.Add(selected);
        IniWorkspaceSectionTitle.Text = $"{sectionName} settings";
        IniFormScroll.ScrollToTop();
    }

    private async void HideEmptyElements_Changed(object sender, RoutedEventArgs e)
    {
        if (HideEmptyElementsToggle is null)
            return;
        var hide = HideEmptyElementsToggle.IsChecked == true;
        settings.Update(value => value.SkinEditor.HideEmptyElements = hide);
        await ShowCategoryAsync((CategoryPicker.SelectedItem as CategoryChoice)?.Category);
    }

    private void AutoBackupElements_Click(object sender, RoutedEventArgs e)
    {
        var enabled = AutoBackupElementsMenuItem.IsChecked;
        settings.Update(value => value.SkinEditor.AutoBackupElements = enabled);
        UpdateElementBackupHint();
        StatusText.Text = enabled
            ? "Automatic element backups enabled."
            : "Automatic element backups disabled. A Realm restore point is still created before Apply.";
    }

    private void UpdateElementBackupHint()
    {
        if (ElementBackupHint is null)
            return;
        ElementBackupHint.Text = settings.Current.SkinEditor.AutoBackupElements
            ? "Original files are backed up automatically before staging."
            : "Automatic element backups are off. Realm is still backed up before Apply.";
    }

    private async void ElementList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (suppressElementSelection)
            return;
        if (ElementList.SelectedItem is SkinElementEntry entry)
        {
            showFullElementRender = false;
            FullElementRenderButton.IsChecked = false;
            await SelectEntryAsync(entry);
        }
    }

    private async void FullElementRenderButton_Click(object sender, RoutedEventArgs e) =>
        await ShowFullElementRenderAsync();

    private async Task ShowFullElementRenderAsync()
    {
        showFullElementRender = true;
        FullElementRenderButton.IsChecked = true;
        suppressElementSelection = true;
        ElementList.UnselectAll();
        suppressElementSelection = false;

        ImageEditorControls.Visibility = Visibility.Collapsed;
        ElementActionFooter.Visibility = Visibility.Collapsed;
        NoElementInspectorHint.Visibility = Visibility.Visible;
        SelectedElementName.Text = "Full render";
        SelectedElementMeta.Text = "";
        SelectedElementUsage.Text =
            "Choose an individual asset to highlight and edit it.";

        var entry = (ElementList.ItemsSource as IEnumerable<SkinElementEntry>)?
            .FirstOrDefault(candidate => candidate.IsImage);
        if (entry is null)
        {
            selectedEntry = null;
            ClearElementComposition();
            ElementPreviewHintText.Text = "No image elements in this category";
            ElementPreviewHint.Visibility = Visibility.Visible;
            return;
        }

        var selectionVersion = ++selectedEntryLoadVersion;
        selectedEntry = entry;
        ElementPreviewHintText.Text = "Constructing full render…";
        ElementPreviewHint.Visibility = Visibility.Visible;
        try
        {
            await EnsureEntryLoadedAsync(entry);
            if (selectionVersion != selectedEntryLoadVersion
                || !ReferenceEquals(entry, selectedEntry)
                || !showFullElementRender)
                return;
            await RenderElementCompositionAsync(entry, selectionVersion);
        }
        catch (Exception ex)
        {
            if (selectionVersion != selectedEntryLoadVersion)
                return;
            ElementPreviewHintText.Text = "Could not construct this element";
            ElementPreviewHint.Visibility = Visibility.Visible;
            StatusText.Text = ex.Message;
        }
    }

    private async void RecolorModePicker_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (suppressEditorEvents || selectedEntry is null
            || RecolorModePicker.SelectedItem is not ComboBoxItem { Tag: string tag })
            return;
        selectedEntry.Mode = Enum.Parse<SkinRecolorMode>(tag);
        UpdateModePanels();
        RenderSelectedEntry(invalidateComposition: true);
        await Task.CompletedTask;
    }

    private void HueShift_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (suppressEditorEvents || selectedEntry is null) return;
        selectedEntry.HueShiftDegrees = HueShiftSlider.Value;
        selectedEntry.SaturationMultiplier = SaturationShiftSlider.Value;
        selectedEntry.LightnessMultiplier = LightnessShiftSlider.Value;
        RequestElementRender();
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

    private void RandomElementColor_Click(object sender, RoutedEventArgs e)
    {
        if (selectedEntry is null)
            return;
        SetCurrentColor(RandomBrightColor());
        RenderSelectedEntry(invalidateComposition: true);
    }

    private async void RandomizeAllElementColors_Click(object sender, RoutedEventArgs e)
    {
        if (currentSkin is null || draft is null)
            return;
        var sourceName = currentSkin.Name;
        var duplicateChoice = KumoriDialog.Show(
            Window.GetWindow(this),
            "Create a duplicate before applying Color chaos?\n\n"
            + $"Yes: create and edit “{sourceName} (Chaos)” (recommended)\n"
            + "No: apply Color chaos to the current skin\n"
            + "Cancel: make no changes\n\n"
            + "Every effective image receives its own random colour. Transparent pixels stay transparent. "
            + "Hitcircle combo colours, the slider border, and the slider inner track are randomized in skin.ini too.",
            "Color chaos",
            MessageBoxButton.YesNoCancel,
            MessageBoxImage.Warning,
            MessageBoxResult.Yes);
        if (duplicateChoice == MessageBoxResult.Cancel)
            return;
        if (duplicateChoice == MessageBoxResult.Yes
            && !await PrepareDuplicateAsync($"{sourceName} (Chaos)"))
            return;
        if (currentSkin is null || draft is null)
            return;

        SetBusy(true, "Randomizing every skin element…");
        try
        {
            if (iniDocument is null)
            {
                iniDocument = SkinIniDocument.Create(currentSkin.Name, currentSkin.Creator);
            }
            else if (rawDirty)
            {
                iniDocument = iniDocument.WithText(RawIniText.Text);
            }
            else if (!ApplyFormRowsToDocument(validate: true))
            {
                return;
            }

            var source = CreateExtrasCurrentSkinSource();
            var baseline = currentSkin.Files.ToDictionary(
                file => file.Filename,
                StringComparer.OrdinalIgnoreCase);
            var colours = new Dictionary<string, Color>(StringComparer.OrdinalIgnoreCase);
            var transformer = new SkinImageTransformService();
            var actionId = $"random-colours:{Guid.NewGuid():N}";
            const string actionLabel = "Color chaos · images + gameplay colours";
            var staged = new List<SkinDraftChange>();
            foreach (var filename in source.Filenames
                         .Where(SkinElementCategorizer.IsImage)
                         .OrderBy(name => name, StringComparer.OrdinalIgnoreCase))
            {
                var bytes = await source.ReadFileAsync(filename, CancellationToken.None);
                if (bytes is null)
                    continue;
                var stem = LogicalStem(filename);
                if (!colours.TryGetValue(stem, out var colour))
                    colours[stem] = colour = RandomBrightColor();
                try
                {
                    var transformed = await Task.Run(() => transformer.Apply(
                        bytes,
                        filename,
                        new SkinImageTransform(
                            SkinImageTransformMode.Colorize,
                            new SkinRgb(colour.R, colour.G, colour.B))));
                    baseline.TryGetValue(filename, out var original);
                    staged.Add(new SkinDraftChange(
                        filename,
                        original?.Hash,
                        transformed,
                        $"{filename} (random colour)",
                        SkinDraftOperation.Upsert,
                        actionId,
                        actionLabel));
                }
                catch
                {
                    // A malformed or unsupported image should not discard the
                    // successfully transformed elements around it.
                }
            }

            var iniColours = ColorChaosIniColours(Random.Shared);
            foreach (var (key, value) in iniColours)
                iniDocument.SetValue("Colours", key, value);
            staged.Add(new SkinDraftChange(
                "skin.ini",
                iniFile?.Hash,
                iniDocument.ToBytes(),
                "skin.ini (Color chaos gameplay colours)",
                SkinDraftOperation.Upsert,
                actionId,
                actionLabel));
            draft.StageRange(staged);
            iniDirty = false;
            rawDirty = false;
            SetRawText(iniDocument.ToText());
            BuildIniForm();
            UpdateComboStrip();
            StatusText.Text = $"Color chaos staged for {staged.Count - 1} image files plus hitcircle and slider colours.";
            UpdateDirtyState();
            await RefreshGameplayPreviewAsync();
            await RefreshRichPreviewsAsync();
            if (selectedEntry is not null)
                await SelectEntryAsync(selectedEntry);
        }
        finally
        {
            SetBusy(false);
        }
    }

    private static Color RandomBrightColor() => Color.FromRgb(
        (byte)Random.Shared.Next(48, 256),
        (byte)Random.Shared.Next(48, 256),
        (byte)Random.Shared.Next(48, 256));

    internal static IReadOnlyDictionary<string, string> ColorChaosIniColours(
        Random random)
    {
        ArgumentNullException.ThrowIfNull(random);
        var keys = Enumerable.Range(1, 8)
            .Select(index => $"Combo{index}")
            .Concat(["SliderBorder", "SliderTrackOverride"]);
        return keys.ToDictionary(
            key => key,
            _ =>
            {
                var colour = Color.FromRgb(
                    (byte)random.Next(48, 256),
                    (byte)random.Next(48, 256),
                    (byte)random.Next(48, 256));
                return $"{colour.R},{colour.G},{colour.B}";
            },
            StringComparer.OrdinalIgnoreCase);
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
        elementCompositionCache.Clear();
        _ = SelectEntryAsync(selectedEntry);
    }

    private async void SaveElement_Click(object sender, RoutedEventArgs e)
    {
        if (selectedEntry is null) return;
        SetBusy(true, "Staging skin change(s)…");
        try
        {
            var entries = EditScopePicker.SelectedIndex == 1
                ? ElementList.SelectedItems.Cast<SkinElementEntry>().Where(entry => entry.IsImage).ToArray()
                : [selectedEntry];
            if (entries.Length == 0) entries = [selectedEntry];
            foreach (var entry in entries.Where(entry => !ReferenceEquals(entry, selectedEntry)))
            {
                await EnsureEntryLoadedAsync(entry);
                entry.Mode = selectedEntry.Mode;
                entry.TintColor = selectedEntry.TintColor;
                entry.HueShiftDegrees = selectedEntry.HueShiftDegrees;
                entry.SaturationMultiplier = selectedEntry.SaturationMultiplier;
                entry.LightnessMultiplier = selectedEntry.LightnessMultiplier;
                entry.SynchronizeEditsToVariants();
                entry.Thumbnail = SkinImageTools.Render(entry);
                entry.RaiseStateChanged();
            }
            foreach (var entry in entries)
                if (!await SaveEntryAsync(entry)) return;
            StatusText.Text = entries.Length == 1
                ? $"{selectedEntry.Filename} added to Changes. Save to osu!lazer when ready."
                : $"Added {entries.Length} selected elements to Changes. Save to osu!lazer when ready.";
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

    private async void MakeTransparentButton_Click(object sender, RoutedEventArgs e)
    {
        if (selectedEntry is not null)
            await MakeElementsTransparentAsync([selectedEntry]);
    }

    private async void MakeContextElementTransparent_Click(object sender, RoutedEventArgs e)
    {
        var entries = ElementList.SelectedItems.Cast<SkinElementEntry>()
            .Where(entry => entry.IsImage)
            .ToArray();
        if (contextMenuEntry is { } context
            && !entries.Contains(context))
            entries = [context];
        await MakeElementsTransparentAsync(entries);
    }

    private async Task MakeElementsTransparentAsync(
        IReadOnlyList<SkinElementEntry> logicalEntries)
    {
        if (draft is null || logicalEntries.Count == 0)
            return;
        var entries = logicalEntries
            .Where(entry => entry.IsImage)
            .SelectMany(entry => entry.PhysicalEntries)
            .DistinctBy(entry => entry.Filename, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var unsupported = entries.Where(entry => !Path.GetExtension(entry.Filename)
                .Equals(".png", StringComparison.OrdinalIgnoreCase))
            .ToArray();
        if (unsupported.Length > 0)
        {
            KumoriDialog.Show(
                Window.GetWindow(this),
                "Transparency requires PNG assets. Convert or replace these files with PNG first:\n\n"
                + string.Join("\n", unsupported.Take(8).Select(entry => $"• {entry.Filename}")),
                "Cannot make element transparent",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
            return;
        }
        if (KumoriDialog.Show(
                Window.GetWindow(this),
                $"Make {logicalEntries.Count} selected element"
                + (logicalEntries.Count == 1 ? "" : "s")
                + " fully transparent?\n\nThe files remain present and therefore continue to override osu!'s fallbacks. "
                + "Use Delete when you want the files removed and fallback behavior restored.",
                "Make skin element transparent",
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning,
                MessageBoxResult.No) != MessageBoxResult.Yes)
            return;

        SetBusy(true, "Creating transparent skin element…");
        try
        {
            if (settings.Current.SkinEditor.AutoBackupElements
                && !await BackupElementFilesAsync(entries.Select(entry => entry.File)))
                return;
            var actionId = $"transparent:{Guid.NewGuid():N}";
            var actionLabel = logicalEntries.Count == 1
                ? $"Make transparent · {LogicalStem(logicalEntries[0].Filename)}"
                : $"Make transparent · {logicalEntries.Count} elements";
            var changes = new List<SkinDraftChange>();
            foreach (var entry in entries)
            {
                await EnsureEntryLoadedAsync(entry);
                var bytes = SkinImageTools.CreateTransparentPng(
                    entry.PixelWidth,
                    entry.PixelHeight);
                changes.Add(new SkinDraftChange(
                    entry.Filename,
                    entry.Hash,
                    bytes,
                    $"{entry.Filename} (transparent)",
                    SkinDraftOperation.Upsert,
                    actionId,
                    actionLabel));
            }
            draft.StageRange(changes);
            StatusText.Text = $"Staged {entries.Length} transparent PNG file"
                + (entries.Length == 1 ? "." : "s.");
            UpdateDirtyState();
            await RefreshGameplayPreviewAsync();
            await RefreshRichPreviewsAsync();
            if (selectedEntry is not null)
                UpdateSelectedElementEffectiveStatus(selectedEntry);
        }
        catch (Exception ex)
        {
            StatusText.Text = $"Could not make the element transparent: {ex.Message}";
        }
        finally
        {
            SetBusy(false);
        }
    }

    private async void DeleteContextElement_Click(object sender, RoutedEventArgs e)
    {
        if (contextMenuEntry is not { } entry)
            return;
        if (!ReferenceEquals(selectedEntry, entry))
            await SelectEntryAsync(entry);
        DeleteElement_Click(sender, e);
    }

    private async void DeleteElement_Click(object sender, RoutedEventArgs e)
    {
        if (selectedEntry is null || draft is null)
            return;

        var physicalEntries = selectedEntry.PhysicalEntries.ToList();
        var impact = SkinStudioEffectiveAssetResolver.DescribeDeletion(
            selectedEntry.Filename,
            EffectiveSkinFilenames());
        if (impact.HasDependency)
        {
            var dependencyChoice = KumoriDialog.Show(
                Window.GetWindow(this),
                impact.Summary
                + "\n\nYes: remove the dependent custom base too, restoring osu!'s normal hitcircle fallback."
                + "\nNo: delete only the selected overlay (the fallback remains blocked)."
                + "\nCancel: make no changes.",
                "Slider endpoint dependency",
                MessageBoxButton.YesNoCancel,
                MessageBoxImage.Warning,
                MessageBoxResult.Yes);
            if (dependencyChoice == MessageBoxResult.Cancel)
                return;
            if (dependencyChoice == MessageBoxResult.Yes)
            {
                var safeFallback = impact.SafeFallbackComponents
                    .ToHashSet(StringComparer.OrdinalIgnoreCase);
                physicalEntries.AddRange(categories
                    .SelectMany(category => category.Files)
                    .Where(entry => safeFallback.Contains(LogicalStem(entry.Filename)))
                    .SelectMany(entry => entry.PhysicalEntries));
                physicalEntries = physicalEntries
                    .DistinctBy(entry => entry.Filename, StringComparer.OrdinalIgnoreCase)
                    .ToList();
            }
        }
        var filenames = string.Join(
            Environment.NewLine,
            physicalEntries.Select(entry => $"• {entry.Filename}"));
        var result = KumoriDialog.Show(
            Window.GetWindow(this),
            $"Stage deletion of the following skin file{(physicalEntries.Count == 1 ? "" : "s")}?\n\n"
            + filenames
            + "\n\nNothing is removed until Save to osu!lazer is pressed. "
            + "You can discard or undo the staged deletion from Changes.",
            "Delete skin element",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning,
            MessageBoxResult.No);
        if (result != MessageBoxResult.Yes)
            return;

        SetBusy(true, "Backing up and staging deletion…");
        try
        {
            if (settings.Current.SkinEditor.AutoBackupElements
                && !await BackupElementFilesAsync(physicalEntries.Select(entry => entry.File)))
                return;

            var actionId = $"delete:{Guid.NewGuid():N}";
            draft.StageRange(physicalEntries.Select(entry => new SkinDraftChange(
                entry.Filename,
                entry.Hash,
                [],
                $"{entry.Filename} (delete)",
                SkinDraftOperation.Delete,
                actionId,
                $"Delete · {LogicalStem(selectedEntry.Filename)}")));
            selectedEntry.Reset();
            DeleteElementButton.Content = "Deletion staged";
            DeleteElementButton.IsEnabled = false;
            ImageEditorControls.IsEnabled = false;
            StatusText.Text = physicalEntries.Count == 1
                ? $"{selectedEntry.Filename} deletion staged."
                : $"{physicalEntries.Count} resolution files staged for deletion.";
            UpdateDirtyState();
            await RefreshGameplayPreviewAsync();
            await RefreshRichPreviewsAsync();
            UpdateSelectedElementEffectiveStatus(selectedEntry);
        }
        finally
        {
            SetBusy(false);
        }
    }

    private async void SaveIni_Click(object sender, RoutedEventArgs e) => await SaveIniAsync();
    private async void SaveAll_Click(object sender, RoutedEventArgs e)
    {
        if (await SaveAllAsync() && (draft?.Count ?? 0) == 0)
            SetInspectorMode(SkinEditorInspectorMode.Context);
    }

    private void UndoDraft_Click(object sender, RoutedEventArgs e)
    {
        if (draft?.Undo() == true)
        {
            StatusText.Text = "Undid the last staged change.";
            UpdateDirtyState();
        }
    }

    private void RedoDraft_Click(object sender, RoutedEventArgs e)
    {
        if (draft?.Redo() == true)
        {
            StatusText.Text = "Restored the staged change.";
            UpdateDirtyState();
        }
    }

    private void DiscardDraftChange_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: SkinDraftChange change } || draft is null)
            return;
        var logicalEntry = categories.SelectMany(category => category.Files)
            .FirstOrDefault(entry => entry.PhysicalEntries.Any(file =>
                file.Filename.Equals(change.Filename, StringComparison.OrdinalIgnoreCase)));
        if (logicalEntry is not null)
        {
            foreach (var physicalEntry in logicalEntry.PhysicalEntries)
                draft.Remove(physicalEntry.Filename);
            logicalEntry.Reset();
            if (ReferenceEquals(logicalEntry, selectedEntry))
                _ = SelectEntryAsync(logicalEntry);
        }
        else if (!draft.Remove(change.Filename))
        {
            return;
        }
        StatusText.Text = $"Discarded staged change: {change.Filename}.";
        UpdateDirtyState();
    }

    private void DiscardDraftAction_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: System.Collections.IEnumerable items }
            || draft is null)
            return;
        var changes = items.Cast<object>().OfType<SkinDraftChange>().ToArray();
        if (changes.Length == 0
            || !draft.RemoveRange(changes.Select(change => change.Filename)))
            return;
        foreach (var entry in categories.SelectMany(category => category.Files)
                     .Where(entry => entry.PhysicalEntries.Any(file => changes.Any(change =>
                         change.Filename.Equals(file.Filename, StringComparison.OrdinalIgnoreCase)))))
            entry.Reset();
        StatusText.Text = $"Discarded action: {changes[0].GroupLabel}.";
        if (selectedEntry is not null)
            _ = SelectEntryAsync(selectedEntry);
        UpdateDirtyState();
    }

    private void DiscardAllDraftChanges_Click(object sender, RoutedEventArgs e)
    {
        if (draft is null || draft.Count == 0) return;
        if (KumoriDialog.Show(
                Window.GetWindow(this),
                $"Discard all {draft.Count} staged change(s)? Local un-staged edits are kept.",
                "Discard staged changes",
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning,
                MessageBoxResult.No) != MessageBoxResult.Yes)
            return;
        draft.AcceptApplied();
        StatusText.Text = "Discarded every staged change.";
        UpdateDirtyState();
    }

    private void RestoreRecoveredDraft()
    {
        if (catalog is null || currentSkin is null || draft is null || draft.Count > 0) return;
        var recovered = SkinDraftRecovery.Load(currentSkin.Id, catalog.RootPath);
        if (recovered.Count == 0) return;
        var current = currentSkin.Files.ToDictionary(file => file.Filename, StringComparer.OrdinalIgnoreCase);
        var compatible = recovered.Where(change =>
        {
            var exists = current.TryGetValue(change.Filename, out var file);
            return change.ExpectedHash is null ? !exists
                : exists && string.Equals(file!.Hash, change.ExpectedHash, StringComparison.OrdinalIgnoreCase);
        }).ToArray();
        if (compatible.Length == 0)
        {
            SkinDraftRecovery.Clear(currentSkin.Id);
            StatusText.Text = "An old draft could not be restored because its source files changed.";
            return;
        }
        var choice = KumoriDialog.Show(
            Window.GetWindow(this),
            $"Restore {compatible.Length} unapplied change(s) from the previous Studio session?\n\n"
            + (compatible.Length == recovered.Count
                ? "Their source files still match."
                : $"{recovered.Count - compatible.Length} change(s) were skipped because lazer changed those files."),
            "Restore staged changes",
            MessageBoxButton.YesNo,
            MessageBoxImage.Question,
            MessageBoxResult.Yes);
        if (choice == MessageBoxResult.Yes)
        {
            draft.StageRange(compatible);
            StatusText.Text = $"Restored {compatible.Length} staged change(s) from the previous session.";
        }
        else
        {
            SkinDraftRecovery.Clear(currentSkin.Id);
            StatusText.Text = "Discarded the saved Studio draft.";
        }
    }

    private void SyncDraftRecovery()
    {
        if (catalog is null || currentSkin is null || draft is null) return;
        if (persistedDraftSkinId == currentSkin.Id
            && persistedDraftRevision == draft.Revision)
            return;
        draftRecoveryTimer.Stop();
        draftRecoveryTimer.Start();
    }

    private async Task PersistDraftRecoveryAsync()
    {
        if (catalog is null || currentSkin is null || draft is null) return;
        var skinId = currentSkin.Id;
        var rootPath = catalog.RootPath;
        var revision = draft.Revision;
        var changes = draft.Changes.ToArray();
        try
        {
            await draftRecoveryWriteGate.WaitAsync();
            try
            {
                await Task.Run(() =>
                {
                    if (changes.Length == 0)
                        SkinDraftRecovery.Clear(skinId);
                    else
                        SkinDraftRecovery.Save(skinId, rootPath, changes);
                });
            }
            finally
            {
                draftRecoveryWriteGate.Release();
            }
            persistedDraftSkinId = skinId;
            persistedDraftRevision = revision;
            if (currentSkin?.Id == skinId && draft?.Revision != revision)
                SyncDraftRecovery();
        }
        catch (Exception)
        {
            // Recovery is best-effort; editing and applying must stay available.
        }
    }

    private void SkinEditor_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.LeftAlt && e.Key != Key.RightAlt || selectedEntry?.OriginalPixels is null)
            return;
        UpdateSelectedCompositionLayers(showOriginal: true);
        GameplayLocalAssetPreview.Source =
            selectedCompositionLayers.FirstOrDefault()?.Source
            ?? SkinImageTools.ToBitmap(
                selectedEntry.OriginalPixels,
                selectedEntry.PixelWidth,
                selectedEntry.PixelHeight,
                selectedEntry.Stride);
        ElementPreviewHint.Visibility = Visibility.Collapsed;
    }

    private void SkinEditor_PreviewKeyUp(object sender, KeyEventArgs e)
    {
        if (e.Key is Key.LeftAlt or Key.RightAlt)
            RenderSelectedEntry();
    }

    private async void Backup_Click(object sender, RoutedEventArgs e)
    {
        if (catalog is null || currentSkin is null) return;
        SetBusy(true, "Creating Realm backup…");
        try
        {
            var directory = GetOrCreateElementBackupDirectory(forceNew: true);
            if (!await BackupElementFilesAsync(currentSkin.Files))
                return;
            await Task.Run(() =>
                realmService.CreateBackup(catalog.RootPath, Path.Combine(directory, "realm")));
            backupCreated = true;
            backupRoot = catalog.RootPath;
            StatusText.Text = $"Skin backup created: {directory}";
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

    private void OpenSkinBackupFolder_Click(object sender, RoutedEventArgs e)
    {
        if (currentSkin is null)
            return;
        var directory = CurrentSkinBackupRoot();
        Directory.CreateDirectory(directory);
        Process.Start(new ProcessStartInfo
        {
            FileName = directory,
            UseShellExecute = true,
        });
    }

    private async void RestoreElementsFromBackup_Click(object sender, RoutedEventArgs e)
    {
        if (currentSkin is null || catalog is null || draft is null)
            return;
        var skinBackupRoot = Path.GetFullPath(CurrentSkinBackupRoot());
        Directory.CreateDirectory(skinBackupRoot);
        var sessions = SkinElementBackupCatalog.Scan(skinBackupRoot, currentSkin.Id);
        if (sessions.Count == 0)
        {
            KumoriDialog.Show(
                Window.GetWindow(this),
                "No complete element backups were found for this skin yet.",
                "No skin backups",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
            return;
        }

        var browser = new SkinBackupBrowserWindow(Window.GetWindow(this), sessions);
        if (browser.ShowDialog() != true || browser.Selection is not { } selection)
            return;
        var session = selection.Session.DirectoryPath;
        var backupFiles = selection.Files;

        SetBusy(true, "Staging backed-up skin elements…");
        try
        {
            var targetNames = backupFiles.Select(file => file.Filename)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            var currentFiles = currentSkin.Files
                .Where(file => targetNames.Contains(file.Filename))
                .ToArray();
            if (settings.Current.SkinEditor.AutoBackupElements
                && currentFiles.Length > 0
                && !await BackupElementFilesAsync(currentFiles))
                return;

            var baseline = currentSkin.Files.ToDictionary(
                file => file.Filename,
                StringComparer.OrdinalIgnoreCase);
            var actionId = $"restore:{Guid.NewGuid():N}";
            var actionLabel = $"Restore backup · {Path.GetFileName(session)}";
            var changes = new List<SkinDraftChange>();
            foreach (var file in backupFiles)
            {
                var bytes = await File.ReadAllBytesAsync(file.FullPath);
                baseline.TryGetValue(file.Filename, out var original);
                changes.Add(new SkinDraftChange(
                    file.Filename,
                    original?.Hash,
                    bytes,
                    $"{file.Filename} (restore from backup)",
                    SkinDraftOperation.Upsert,
                    actionId,
                    actionLabel));
                if (file.Filename.Equals("skin.ini", StringComparison.OrdinalIgnoreCase))
                {
                    iniDocument = SkinIniDocument.Parse(bytes);
                    SetRawText(iniDocument.ToText());
                    BuildIniForm();
                    iniDirty = false;
                    rawDirty = false;
                }
            }
            draft.StageRange(changes);
            StatusText.Text = $"Staged {changes.Count} file"
                + (changes.Count == 1 ? "" : "s")
                + " from backup. Review the Restore action in Changes.";
            UpdateDirtyState();
            await RefreshGameplayPreviewAsync();
            await RefreshRichPreviewsAsync();
            if (selectedEntry is not null)
                await SelectEntryAsync(selectedEntry);
        }
        catch (Exception ex)
        {
            StatusText.Text = $"Could not restore this backup: {ex.Message}";
        }
        finally
        {
            SetBusy(false);
        }
    }

    private async void DeleteSkin_Click(object sender, RoutedEventArgs e)
    {
        if (catalog is null || currentSkin is null)
            return;
        if (pendingDuplicate is { } duplicate
            && duplicate.WorkingSkinId == currentSkin.Id)
        {
            if (KumoriDialog.Show(
                    Window.GetWindow(this),
                    $"Discard the unimported duplicate “{duplicate.Name}”?\n\nThe source skin will not be deleted.",
                    "Discard duplicate draft",
                    MessageBoxButton.YesNo,
                    MessageBoxImage.Warning,
                    MessageBoxResult.No) != MessageBoxResult.Yes)
                return;
            var sourceId = duplicate.SourceSkinId;
            AbandonPendingDuplicate();
            currentSkin = null;
            await LoadCatalogAsync(sourceId, forceReloadSelectedSkin: true);
            return;
        }
        if (!await ResolveDirtyStateAsync() || currentSkin is null || catalog is null)
            return;

        var skin = currentSkin;
        if (KumoriDialog.Show(
                Window.GetWindow(this),
                $"Delete “{skin.DisplayName}” from osu!lazer?\n\n"
                + "Kumori will create a complete recovery backup before marking the skin for deletion.",
                "Delete skin",
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning,
                MessageBoxResult.No) != MessageBoxResult.Yes)
            return;

        var deleted = false;
        var deletedName = skin.DisplayName;
        string? recoveryDirectory = null;
        SetBusy(true, "Backing up and deleting skin…");
        try
        {
            var directory = GetOrCreateElementBackupDirectory(forceNew: true);
            recoveryDirectory = directory;
            if (!await BackupElementFilesAsync(skin.Files))
                return;
            await Task.Run(() => realmService.CreateBackup(
                catalog.RootPath,
                Path.Combine(directory, "realm")));
            deleted = await Task.Run(() => realmService.DeleteSkin(
                catalog.RootPath,
                skin.Id));
            if (!deleted)
            {
                StatusText.Text = "The skin no longer exists or was already pending deletion.";
                return;
            }
        }
        catch (Exception ex)
        {
            StatusText.Text = $"Could not delete the skin: {ex.Message}";
            return;
        }
        finally
        {
            SetBusy(false);
        }
        if (!deleted)
            return;
        currentSkin = null;
        draft = null;
        await LoadCatalogAsync();
        StatusText.Text = $"Deleted {deletedName}. Its recovery backup is in {recoveryDirectory}.";
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
        elementBackupDirectory = null;
        backedUpElements.Clear();
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

    private async void ReplaceFile_Click(object sender, RoutedEventArgs e)
    {
        var entry = contextMenuEntry
                    ?? ElementList.SelectedItem as SkinElementEntry
                    ?? selectedEntry;
        contextMenuEntry = null;
        if (entry is null || draft is null)
        {
            StatusText.Text = "Select an element to replace.";
            return;
        }

        var target = entry.File;
        var extension = Path.GetExtension(target.Filename);
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = $"Replace {target.Filename}",
            Multiselect = false,
            Filter = string.IsNullOrWhiteSpace(extension)
                ? "All files|*.*"
                : $"{extension.TrimStart('.').ToUpperInvariant()} files|*{extension}",
        };
        if (dialog.ShowDialog(Window.GetWindow(this)) != true)
            return;

        try
        {
            if (settings.Current.SkinEditor.AutoBackupElements
                && !await BackupElementFilesAsync([target]))
                return;

            var sourceBytes = await File.ReadAllBytesAsync(dialog.FileName);
            var change = SkinFileReplacementPlanner.Build(target, dialog.FileName, sourceBytes);
            draft.StageRange([change]);
            StatusText.Text =
                $"Added replacement for {target.Filename} to Changes. Save to osu!lazer when ready.";
            UpdateDirtyState();
        }
        catch (Exception ex)
        {
            StatusText.Text = $"Could not replace {target.Filename}: {ex.Message}";
        }
    }

    private static void EnsureExtrasDirectories()
    {
        Directory.CreateDirectory(AppPaths.SkinExtrasDir);

        var readme = Path.Combine(AppPaths.SkinExtrasDir, "README.txt");
        if (!File.Exists(readme))
        {
            File.WriteAllText(
                readme,
                "Kumori Skin Extras\r\n\r\n"
                + "Use Skin Studio > Actions > Extract skin to Extras to import an osu!lazer skin, folder, or .osk.\r\n"
                + "Generated packs use Extras\\osu\\[Area\\]<Family>\\<Variant?>\\<Skin Name — Author>\\extras.json.\r\n"
                + "The manifest records exact content fingerprints and only the skin.ini settings owned by that family.\r\n"
                + "Exact duplicate families are skipped; visually similar image packs are flagged.\r\n"
                + "In Skin Studio, choose Extras… and select the final pack folder.\r\n"
                + "Using a pack replaces only its family; unrelated assets and skin.ini settings stay intact.\r\n"
                + "Legacy category folders remain supported and are not moved automatically.\r\n");
        }
    }

    private void OpenExtrasFolder_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            EnsureExtrasDirectories();
            Process.Start(new ProcessStartInfo(AppPaths.SkinExtrasDir) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            StatusText.Text = $"Could not open the Extras folder: {ex.Message}";
        }
    }

    private async void ReplaceFromExtras_Click(object sender, RoutedEventArgs e)
    {
        if (catalog is null || currentSkin is null || draft is null)
            return;

        try
        {
            EnsureExtrasDirectories();
            var selectedCategory = (CategoryPicker.SelectedItem as CategoryChoice)?.Category.Name;
            var categoryHint = extrasCategoryHintOverride ?? (selectedCategory is not null
                               && SkinElementCategorizer.ExtraCategories.Contains(
                                   selectedCategory,
                                   StringComparer.OrdinalIgnoreCase)
                ? selectedCategory
                : "Cursor");
            extrasCategoryHintOverride = null;
            var extrasPreviewContext = await CreateExtrasPreviewContextAsync();
            var extrasCurrentIni = rawDirty && iniDocument is not null
                ? iniDocument.WithText(RawIniText.Text)
                : iniDocument;
            var extrasCurrentSkin = CreateExtrasCurrentSkinSource();
            CloseExtrasWorkspace();
            SkinExtrasPickerWindow? picker = null;
            picker = new SkinExtrasPickerWindow(
                Window.GetWindow(this),
                categoryHint,
                ExtrasModeVisibility(),
                PersistLazerExtrasFilter,
                extrasPreviewContext,
                extrasCurrentIni,
                extrasCurrentSkin,
                selection => StageExtrasSelectionAsync(
                    selection,
                    picker!.LazerUsedOnly,
                    Window.GetWindow(this)),
                (selections, progress) => StageExtrasSelectionsAsync(
                    selections,
                    picker!.LazerUsedOnly,
                    Window.GetWindow(this),
                    progress),
                previewAnimationsEnabled,
                SetPreviewAnimationsEnabled);
            embeddedExtras = picker;
            if (lastExtrasSyncProgress is not null)
                picker.UpdateCatalogSyncProgress(lastExtrasSyncProgress);
            picker.CloseRequested += (_, _) => CloseExtrasWorkspace();
            ExtrasHost.Content = picker;
            ExtrasWorkspace.Visibility = Visibility.Visible;
            UpdatePreviewAnimationSubscription();
            picker.Focus();
        }
        catch (Exception ex)
        {
            StatusText.Text = $"Could not open the Extras library: {ex.Message}";
            KumoriDialog.Show(
                Window.GetWindow(this),
                StatusText.Text,
                "Extras library failed",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
        }
    }

    private async Task<bool> StageExtrasSelectionAsync(
        SkinExtrasSelectionResult selection,
        bool lazerUsedOnly,
        Window? owner,
        MessageBoxResult? confirmationChoice = null)
    {
        if (catalog is null || currentSkin is null || draft is null)
            return false;

        try
        {
            var extrasRoot = Path.GetFullPath(AppPaths.SkinExtrasDir);
            var selectedPack = Path.GetFullPath(selection.PackDirectory);
            var relativePack = Path.GetRelativePath(extrasRoot, selectedPack);
            if (Path.IsPathRooted(relativePack)
                || relativePack.Equals("..", StringComparison.Ordinal)
                || relativePack.StartsWith($"..{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            {
                KumoriDialog.Show(
                    owner,
                    "Select a pack folder inside the Kumori Extras directory.",
                    "Choose an Extras pack",
                    MessageBoxButton.OK,
                    MessageBoxImage.Information);
                return false;
            }

            var manifest = selection.Manifest;
            var packName = manifest.DisplayName;
            var incoming = await Task.Run(() => manifest.Files.Select(file =>
            {
                var path = Path.GetFullPath(Path.Combine(
                    selectedPack,
                    file.TargetFilename.Replace('/', Path.DirectorySeparatorChar)));
                var relative = Path.GetRelativePath(selectedPack, path);
                if (Path.IsPathRooted(relative)
                    || relative.Equals("..", StringComparison.Ordinal)
                    || relative.StartsWith($"..{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                    || !File.Exists(path))
                    throw new InvalidDataException($"Pack asset is missing or unsafe: {file.TargetFilename}");
                var bytes = File.ReadAllBytes(path);
                var actual = SkinExtraFingerprint.Describe(
                    file.SourceFilename,
                    file.TargetFilename,
                    bytes);
                if (!SkinExtraFingerprint.EquivalentFileContent(actual, file))
                    throw new InvalidDataException(
                        $"Pack asset changed after indexing: {file.TargetFilename}");
                return new SkinExtraPackFile(file.TargetFilename, bytes);
            }).ToArray());
            if (iniDocument is null && manifest.IniPatch.Count > 0)
                throw new InvalidDataException("This pack needs skin.ini, but the current skin.ini could not be loaded.");
            if (iniDocument is not null)
            {
                if (rawDirty)
                {
                    iniDocument = iniDocument.WithText(RawIniText.Text);
                    rawDirty = false;
                }
                else if (!ApplyFormRowsToDocument(validate: true))
                {
                    return false;
                }
            }
            IReadOnlyList<SkinExtraPackFile> effectiveIncoming = incoming
                .Where(file => !lazerUsedOnly
                               || SkinExtraLazerCompatibility.IsLazerUsed(
                                   file.Filename,
                                   manifest.FamilyId))
                .Where(file => !SkinCursorMiddlePolicy.IsCursorFamily(manifest.FamilyId)
                               || !SkinCursorMiddlePolicy.IsCursorMiddle(file.Filename))
                .ToArray();
            if (manifest.FamilyId.Equals(
                    "osu.followpoints",
                    StringComparison.OrdinalIgnoreCase))
            {
                effectiveIncoming =
                    SkinFollowpointSequence.CompleteWithTransparentFrames(
                        effectiveIncoming);
            }
            effectiveIncoming = await Task.Run(() =>
                SkinExtraElementTinting.Apply(
                    manifest.FamilyId,
                    effectiveIncoming,
                    selection.ElementTints));
            var effectiveFiles = SkinDraftProjection.EffectiveFiles(
                currentSkin.Files,
                draft.Changes);
            var resolutionMismatches = SkinExtraResolutionPlanner.FindMismatches(
                effectiveFiles.Select(file => file.Filename),
                effectiveIncoming.Select(file => file.Filename));
            IReadOnlyList<SkinExtraPackFile> resolvedIncoming = effectiveIncoming;
            if (selection.ResolutionPolicy == SkinExtraResolutionPolicy.UpscaleToTwoX
                && resolutionMismatches.Count > 0)
            {
                var incomingByName = effectiveIncoming.ToDictionary(
                    file => file.Filename.Replace('\\', '/'),
                    StringComparer.OrdinalIgnoreCase);
                var generated = await Task.Run(() => resolutionMismatches.Select(mismatch =>
                {
                    var source = incomingByName[mismatch.OneXFilename];
                    return new SkinExtraPackFile(
                        mismatch.ExistingTwoXFilename,
                        SkinImageTools.Upscale2X(source.Bytes, mismatch.ExistingTwoXFilename));
                }).ToArray());
                resolvedIncoming = [.. effectiveIncoming, .. generated];
            }

            var plan = SkinExtraPackPlanner.BuildFamilyPlan(
                manifest,
                effectiveFiles,
                resolvedIncoming,
                iniDocument,
                includeIniPatch: true,
                lazerUsedOnly: lazerUsedOnly,
                replaceEntireFamily: selection.ReplaceEntireFamily,
                replaceSelectedLogicalElements: !selection.ReplaceEntireFamily);
            var changes = plan.Changes.ToList();

            var incomingNames = resolvedIncoming
                .Select(file => file.Filename)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            var currentFilesByName = effectiveFiles.ToDictionary(
                file => file.Filename,
                StringComparer.OrdinalIgnoreCase);
            var changedNames = changes
                .Select(change => change.Filename)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            foreach (var requestedName in selection.DeleteCurrentFiles ?? [])
            {
                var normalized = requestedName.Replace('\\', '/');
                if (!normalized.Equals(
                        Path.GetFileName(normalized),
                        StringComparison.Ordinal)
                    || incomingNames.Contains(normalized)
                    || changedNames.Contains(normalized)
                    || !currentFilesByName.TryGetValue(normalized, out var existing)
                    || SkinExtraFamilyRegistry.ForFile(normalized)?.Id.Equals(
                        manifest.FamilyId,
                        StringComparison.OrdinalIgnoreCase) != true)
                    continue;
                changes.Add(new SkinDraftChange(
                    existing.Filename,
                    existing.Hash,
                    [],
                    $"{existing.Filename} (current-skin layer excluded from selection)",
                    SkinDraftOperation.Delete));
                changedNames.Add(existing.Filename);
            }

            if (SkinCursorMiddlePolicy.IsCursorFamily(manifest.FamilyId))
            {
                var cursorPolicyChanges = SkinCursorMiddlePolicy.BuildChanges(
                    effectiveFiles,
                    selection.SmoothTrail);
                changes.AddRange(cursorPolicyChanges);
                foreach (var change in cursorPolicyChanges)
                    changedNames.Add(change.Filename);
            }

            var replacementNames = changes
                .Select(change => change.Filename)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            var effectiveChanges = changes
                .GroupBy(
                    change => change.Filename,
                    StringComparer.OrdinalIgnoreCase)
                .Select(group => group.Last())
                .ToArray();
            changes = SkinDraftProjection.NormalizeAgainstBaseline(
                    currentSkin.Files,
                    changes)
                .ToList();
            var actionId = $"extras:{Guid.NewGuid():N}";
            var actionLabel = $"Extras · {packName} ({manifest.FamilyName})";
            changes = changes.Select(change => change with
            {
                ActionId = actionId,
                ActionLabel = actionLabel,
            })
                .ToList();
            var removed = effectiveChanges.Count(change => change.IsDeletion);
            var replaced = effectiveChanges.Count(change =>
                !change.IsDeletion && currentFilesByName.ContainsKey(change.Filename));
            var added = effectiveChanges.Count(change =>
                !change.IsDeletion && !currentFilesByName.ContainsKey(change.Filename));
            var linkedSettings = plan.IniPatch.Count;
            var resolutionSummary = resolutionMismatches.Count == 0
                ? ""
                : selection.ResolutionPolicy == SkinExtraResolutionPolicy.UpscaleToTwoX
                    ? $"\n{resolutionMismatches.Count} matching @2x "
                      + (resolutionMismatches.Count == 1 ? "file" : "files")
                      + " will be generated automatically."
                    : $"\n{resolutionMismatches.Count} conflicting @2x "
                      + (resolutionMismatches.Count == 1 ? "file" : "files")
                      + " will be removed so the selected 1× files are used.";
            var cursorSummary = SkinCursorMiddlePolicy.IsCursorFamily(manifest.FamilyId)
                ? selection.SmoothTrail
                    ? "\nCursor middle: every variant will be removed and replaced by the transparent 1×1 Smooth Trail placeholder."
                    : "\nCursor middle: every variant will be removed."
                : "";

            var choice = confirmationChoice ?? KumoriDialog.Show(
                owner,
                $"Use {packName} for {manifest.FamilyName}?\n\n"
                + $"{selection.LogicalElementCount} selected element"
                + (selection.LogicalElementCount == 1 ? "" : "s")
                + $" · {resolvedIncoming.Count + (selection.SmoothTrail ? 1 : 0)} files"
                + (selection.SettingCount == 0 ? "" : $" · {selection.SettingCount} settings")
                + $"\n{replaced} replaced · {added} added"
                + (removed == 0 ? "" : $" · {removed} family-owned files removed")
                + (linkedSettings == 0 ? "" : $" · {linkedSettings} linked skin.ini settings")
                + resolutionSummary
                + cursorSummary
                + "\n\nChecked current-skin fallback layers are kept. Unchecked fallback layers are removed; all other neighboring assets and unrelated skin.ini settings remain unchanged.\n\n"
                + "Yes: create a backup now, then stage the replacement\n"
                + "No: stage without an immediate backup\n"
                + "Cancel: make no changes",
                $"Use {manifest.FamilyName} from Extras",
                MessageBoxButton.YesNoCancel,
                MessageBoxImage.Warning,
                MessageBoxResult.Yes);
            if (choice == MessageBoxResult.Cancel)
                return false;

            SetBusy(true, $"Preparing {packName}...");
            try
            {
                var touchedNames = replacementNames;
                var ownedCurrentFiles = currentSkin.Files.Where(file =>
                    touchedNames.Contains(file.Filename)).ToList();
                if (plan.IniPatch.Count > 0 && iniFile is not null)
                    ownedCurrentFiles.Add(iniFile);
                if (choice == MessageBoxResult.Yes && ownedCurrentFiles.Count > 0)
                {
                    var backupDirectory = GetOrCreateElementBackupDirectory(forceNew: true);
                    if (!await BackupElementFilesAsync(ownedCurrentFiles))
                        return false;
                    await Task.Run(() => realmService.CreateBackup(
                        catalog.RootPath,
                        Path.Combine(backupDirectory, "realm")));
                    backupCreated = true;
                    backupRoot = catalog.RootPath;
                }

                draft.ReplaceWhere(
                    change => replacementNames.Contains(change.Filename),
                    changes);
                if (plan.IniPatch.Count > 0 && iniDocument is not null)
                {
                    var patched = SkinIniDocument.Parse(iniDocument.ToBytes());
                    patched.ApplyPatch(plan.IniPatch);
                    iniDocument = patched;
                    draft.StageRange([new SkinDraftChange(
                        "skin.ini",
                        iniFile?.Hash,
                        iniDocument.ToBytes(),
                        $"skin.ini ({manifest.FamilyName} from {packName})",
                        SkinDraftOperation.Upsert,
                        actionId,
                        actionLabel)]);
                    iniDirty = false;
                    SetRawText(iniDocument.ToText());
                    BuildIniForm();
                    UpdateComboStrip();
                }
                await RefreshGameplayPreviewAsync();
                await RefreshRichPreviewsAsync();
                StatusText.Text =
                    $"{packName} staged for {manifest.FamilyName}: "
                    + $"{replaced} replaced, {added} added, {removed} removed"
                    + (linkedSettings == 0 ? "." : $", {linkedSettings} linked settings.");
                UpdateDirtyState();
                return true;
            }
            finally
            {
                SetBusy(false);
            }
        }
        catch (Exception ex)
        {
            StatusText.Text = $"Could not import the Extras pack: {ex.Message}";
            KumoriDialog.Show(
                owner,
                StatusText.Text,
                "Extras import failed",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
            return false;
        }
    }

    private async Task<bool> StageExtrasSelectionsAsync(
        IReadOnlyList<SkinExtrasSelectionResult> selections,
        bool lazerUsedOnly,
        Window? owner,
        IProgress<SkinExtrasBatchProgress>? progress = null)
    {
        if (selections.Count == 0)
            return false;
        var choice = KumoriDialog.Show(
            owner,
            $"Stage {selections.Count} randomly selected Extras packs?\n\n"
            + "Yes: create backups for affected existing files\n"
            + "No: stage without immediate per-pack backups\n"
            + "Cancel: make no changes",
            "Stage random mix",
            MessageBoxButton.YesNoCancel,
            MessageBoxImage.Warning,
            MessageBoxResult.Yes);
        if (choice == MessageBoxResult.Cancel)
            return false;
        for (var index = 0; index < selections.Count; index++)
        {
            var selection = selections[index];
            progress?.Report(new SkinExtrasBatchProgress(
                index,
                selections.Count,
                selection.Manifest.FamilyName,
                selection.Manifest.DisplayName));
            if (!await StageExtrasSelectionAsync(
                    selection,
                    lazerUsedOnly,
                    owner,
                    choice))
                return false;
            progress?.Report(new SkinExtrasBatchProgress(
                index + 1,
                selections.Count,
                selection.Manifest.FamilyName,
                selection.Manifest.DisplayName));
        }
        return true;
    }

    private void CloseExtrasWorkspace()
    {
        var picker = embeddedExtras;
        embeddedExtras = null;
        ExtrasWorkspace.Visibility = Visibility.Collapsed;
        ExtrasHost.Content = null;
        picker?.Dispose();
        UpdatePreviewAnimationSubscription();
    }

    private SkinExtrasCurrentSkinSource CreateExtrasCurrentSkinSource()
    {
        if (catalog is null || currentSkin is null)
            throw new InvalidOperationException("The current skin is not available for comparison.");

        var root = catalog.RootPath;
        return new SkinExtrasCurrentSkinSource(
            currentSkin.DisplayName,
            () => SkinDraftProjection.EffectiveFiles(currentSkin.Files, draft?.Changes ?? [])
                .Select(file => file.Filename)
                .ToArray(),
            async (filename, cancellationToken) =>
            {
                var staged = draft?.Changes.FirstOrDefault(change =>
                    change.Filename.Equals(filename, StringComparison.OrdinalIgnoreCase));
                if (staged is not null)
                    return staged.IsDeletion ? null : staged.Bytes.ToArray();
                var logicalEntry = categories
                    .SelectMany(category => category.Files)
                    .FirstOrDefault(entry => entry.PhysicalEntries.Any(physical =>
                        physical.Filename.Equals(
                            filename,
                            StringComparison.OrdinalIgnoreCase)));
                if (logicalEntry?.HasEdits == true)
                {
                    logicalEntry.SynchronizeEditsToVariants();
                    var physical = logicalEntry.PhysicalEntries.First(entry =>
                        entry.Filename.Equals(
                            filename,
                            StringComparison.OrdinalIgnoreCase));
                    await EnsureEntryLoadedAsync(physical);
                    return SkinImageTools.Encode(
                        SkinImageTools.Render(physical),
                        physical.Filename);
                }
                var current = currentSkin.Files.FirstOrDefault(file =>
                    file.Filename.Equals(filename, StringComparison.OrdinalIgnoreCase));
                if (current is null)
                    return null;
                return await Task.Run(
                    () => realmService.ReadFile(root, current.Hash),
                    cancellationToken);
            },
            () => iniDocument,
            () => draft?.Count > 0);
    }

    private async Task<SkinExtrasPreviewContext> CreateExtrasPreviewContextAsync()
    {
        var hitCircle = await FindAndLoadAsync("hitcircle");
        var hitCircleOverlay = await FindAndLoadAsync("hitcircleoverlay");
        var prefix = iniDocument?.GetValue("Fonts", "HitCirclePrefix");
        var leafPrefix = string.IsNullOrWhiteSpace(prefix)
            ? "default"
            : Path.GetFileName(prefix.Replace('\\', '/').TrimEnd('/'));
        var hitCircleNumber = await FindAndLoadAsync($"{leafPrefix}-1", "default-1");
        var comboColours = Enumerable.Range(1, 8)
            .Select(index => iniDocument?.GetValue("Colours", $"Combo{index}"))
            .Select(value => TryParseColor(value, out var colour) ? colour : (Color?)null)
            .Where(colour => colour.HasValue)
            .Select(colour => colour!.Value)
            .ToArray();

        return new SkinExtrasPreviewContext(
            hitCircle?.Thumbnail,
            hitCircleOverlay?.Thumbnail,
            hitCircleNumber?.Thumbnail,
            comboColours,
            iniDocument?.GetValue("General", "HitCircleOverlayAboveNumber") == "1");
    }

    private async void ExtractExtras_Click(object sender, RoutedEventArgs e)
    {
        if (catalog is null || currentSkin is null)
            return;
        SetBusy(true, $"Reading {currentSkin.DisplayName}…");
        SkinExtractionSource source;
        try
        {
            var rootFiles = currentSkin.Files.Where(file =>
                SkinExtrasExtractionService.IsRootSourceFile(file.Filename)).ToArray();
            var files = new List<SkinExtractionFile>(rootFiles.Length);
            foreach (var file in rootFiles)
            {
                var bytes = await Task.Run(() =>
                    realmService.ReadFile(catalog.RootPath, file.Hash));
                files.Add(new SkinExtractionFile(file.Filename, bytes));
            }
            source = new SkinExtrasExtractionService().BuildSource(
                currentSkin.DisplayName,
                $"osu!lazer · {currentSkin.DisplayName}",
                files);
        }
        catch (Exception ex)
        {
            StatusText.Text = $"Could not read the selected skin: {ex.Message}";
            return;
        }
        finally
        {
            SetBusy(false);
        }

        EnsureExtrasDirectories();
        var extractor = new SkinExtrasExtractorWindow(
            Window.GetWindow(this),
            source,
            ExtrasModeVisibility(),
            PersistLazerExtrasFilter);
        if (extractor.ShowDialog() != true) return;
        var extracted = extractor.Results.Count(result =>
            result.Status == SkinExtraExtractionStatus.Extracted);
        var duplicates = extractor.Results.Count - extracted;
        var similar = extractor.Results.Count(result => result.SimilarPack is not null);
        StatusText.Text =
            $"Extras extraction complete: {extracted} pack(s) added, {duplicates} exact duplicate(s) skipped"
            + (similar == 0 ? "." : $", {similar} possible visual duplicate(s) flagged.");
    }

    private async void AddCategoryToExtras_Click(object sender, RoutedEventArgs e)
    {
        var choice = CategoryPicker.SelectedItem as CategoryChoice;
        if (choice is null)
            return;
        await AddEntriesToExtrasAsync(choice.Category.Files, $"the {choice.Category.Name} category");
    }

    private async void AddSelectedElementsToExtras_Click(object sender, RoutedEventArgs e)
    {
        var entries = ElementList.SelectedItems.Cast<SkinElementEntry>().ToArray();
        if (entries.Length == 0)
        {
            StatusText.Text = "Select one or more elements to add them to Extras.";
            return;
        }
        await AddEntriesToExtrasAsync(
            entries,
            $"{entries.Length} selected element{(entries.Length == 1 ? "" : "s")}");
    }

    private async Task AddEntriesToExtrasAsync(
        IEnumerable<SkinElementEntry> entries,
        string scope)
    {
        if (catalog is null || currentSkin is null)
            return;

        var selectedFiles = entries
            .SelectMany(entry => entry.PhysicalEntries)
            .Select(entry => entry.File)
            .GroupBy(file => file.Filename, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .ToList();
        if (selectedFiles.Count == 0)
            return;

        SetBusy(true, $"Reading {scope} for Extras...");
        SkinExtractionSource source;
        try
        {
            var skinIni = currentSkin.Files.FirstOrDefault(file =>
                file.Filename.Equals("skin.ini", StringComparison.OrdinalIgnoreCase));
            if (skinIni is not null && !selectedFiles.Any(file =>
                    file.Filename.Equals(skinIni.Filename, StringComparison.OrdinalIgnoreCase)))
            {
                selectedFiles.Add(skinIni);
            }

            var root = catalog.RootPath;
            var files = await Task.Run(() => selectedFiles
                .Select(file => new SkinExtractionFile(
                    file.Filename,
                    realmService.ReadFile(root, file.Hash)))
                .ToArray());
            source = new SkinExtrasExtractionService().BuildSource(
                currentSkin.DisplayName,
                $"osu!lazer · {currentSkin.DisplayName} · {scope}",
                files);
        }
        catch (Exception ex)
        {
            StatusText.Text = $"Could not read {scope}: {ex.Message}";
            return;
        }
        finally
        {
            SetBusy(false);
        }

        EnsureExtrasDirectories();
        var extractor = new SkinExtrasExtractorWindow(
            Window.GetWindow(this),
            source,
            ExtrasModeVisibility(),
            PersistLazerExtrasFilter);
        if (extractor.ShowDialog() != true)
            return;

        var extracted = extractor.Results.Count(result =>
            result.Status == SkinExtraExtractionStatus.Extracted);
        var duplicates = extractor.Results.Count - extracted;
        StatusText.Text =
            $"Added {scope} to Extras: {extracted} pack(s) created, "
            + $"{duplicates} exact duplicate(s) skipped.";
    }

    private SkinExtraModeVisibility ExtrasModeVisibility() => new(
        settings.Current.SkinEditor.ShowCatchExtras,
        settings.Current.SkinEditor.ShowTaikoExtras,
        settings.Current.SkinEditor.ShowManiaExtras,
        settings.Current.SkinEditor.OnlyShowLazerExtras);

    private void PersistLazerExtrasFilter(bool enabled) =>
        settings.Update(value => value.SkinEditor.OnlyShowLazerExtras = enabled);

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

    private void ElementList_PreviewMouseRightButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.OriginalSource is not DependencyObject source)
            return;
        if (ItemsControl.ContainerFromElement(ElementList, source) is not ListBoxItem { DataContext: SkinElementEntry entry })
            return;
        contextMenuEntry = entry;
        if (ElementList.SelectedItems.Contains(entry))
            return;
        ElementList.SelectedItems.Clear();
        ElementList.SelectedItem = entry;
    }

    private void ElementContextMenu_Closed(object sender, RoutedEventArgs e) =>
        contextMenuEntry = null;

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

    private void WorkspaceTabs_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!ReferenceEquals(e.Source, WorkspaceTabs) || WorkspaceTabs.SelectedIndex != 1)
            return;

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

    private sealed record IniSectionChoice(string Name, int ActiveCount);

    private sealed record SkinReadinessDefinition(
        string Label,
        string ExtrasHint,
        bool IsReady);

    private sealed record PendingSkinDuplicate(
        Guid WorkingSkinId,
        Guid SourceSkinId,
        string Name,
        string Creator);

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
