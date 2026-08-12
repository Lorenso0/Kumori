using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Interop;
using System.Windows.Threading;
using Kumori.Core;
using Kumori.Core.Settings;
using Kumori.Storage;
using Kumori.Tracking;
using Microsoft.Win32;
using WpfBrush = System.Windows.Media.Brush;
using WpfBrushes = System.Windows.Media.Brushes;
using WpfColor = System.Windows.Media.Color;
using WpfColorConverter = System.Windows.Media.ColorConverter;

namespace Kumori.App.Skins;

public sealed class SkinStudioElementItem
{
    public required string Label { get; init; }
    public required string ComponentName { get; init; }
    public required string SourceLabel { get; init; }
    public required bool IsAudio { get; init; }
    public SkinStudioPreviewScene? PreviewScene { get; init; }
    public required SkinStudioSemanticPreviewDescriptor SemanticPreview { get; init; }
    public BitmapSource? Thumbnail { get; init; }
}

public sealed class SkinStudioSkinChoice
{
    public required string DisplayName { get; init; }
    public required string Detail { get; init; }
    public LazerSkinInfo? InstalledSkin { get; init; }
    public SkinDraftManifest? Draft { get; init; }

    public override string ToString() => DisplayName;
}

internal sealed class SkinStudioIniGroupChoice
{
    public required SkinIniVisualGroup Group { get; init; }
    public required string DisplayName { get; init; }
    public required IReadOnlyList<SkinIniKeyDefinition> Definitions { get; init; }
    public override string ToString() => DisplayName;
}

public partial class SkinStudioLauncherPage : UserControl, IDisposable
{
    private static readonly WpfColor[] builtInColourSwatches =
    [
        WpfColor.FromRgb(0xFF, 0xC0, 0x00),
        WpfColor.FromRgb(0x00, 0xCA, 0x00),
        WpfColor.FromRgb(0x12, 0x7C, 0xFF),
        WpfColor.FromRgb(0xF2, 0x18, 0x39),
        WpfColor.FromRgb(0x91, 0x84, 0xD9),
        WpfColor.FromRgb(0xFF, 0x66, 0xAA),
        WpfColor.FromRgb(0x00, 0xE5, 0xD0),
        WpfColor.FromRgb(0xFF, 0xFF, 0xFF),
    ];

    private readonly SettingsService settings;
    private readonly Func<Task> openLegacyEditor;
    private readonly ILazerSkinReloadService? reloadService;
    private readonly SemaphoreSlim readinessGate = new(1, 1);
    private readonly SkinStudioWorkspaceController workspace;
    private string? executablePath;
    private bool probeComplete;
    private bool initialized;
    private LazerSkinCatalog? installedSkinCatalog;
    private bool installedSkinCatalogLoaded;
    private bool suppressDraftSelection;
    private bool suppressElementSelection;
    private bool rendererPlaying;
    private SkinStudioRendererPipeClient? renderer;
    private CancellationTokenSource? rendererReloadCancellation;
    private bool suppressAutoMotionChange;
    private bool suppressSceneSelection;
    private readonly DispatcherTimer skinIniSaveTimer;
    private readonly DispatcherTimer liveSyncTimer;
    private readonly DispatcherTimer rendererEventTimer;
    private readonly DispatcherTimer rendererColourPreviewTimer;
    private readonly DispatcherTimer rendererScalePreviewTimer;
    private bool rendererEventPollActive;
    private bool rendererColourPreviewSending;
    private bool rendererScalePreviewSending;
    private bool rendererScalePreviewPending;
    private (SkinStudioRendererColourTarget Target, WpfColor Colour, string? Component)?
        pendingRendererColourPreview;
    private SkinIniDocument? integratedSkinIni;
    private string integratedSkinIniLastSaved = "";
    private bool suppressSkinIniEvents;
    private TextBox? activeSkinIniColourBox;
    private SkinIniValueType activeSkinIniColourType;
    private SkinStudioRendererColourTarget? activeRendererColourTarget;
    private System.Windows.Point rendererColourPopupAnchor = new(0.5, 0.5);
    private System.Windows.Rect rendererColourAvoidBounds =
        new(0.4, 0.4, 0.2, 0.2);
    private bool rendererColourPopupActive;
    private readonly Dictionary<string, (CheckBox Active, TextBox Value, Border? Preview)>
        skinIniInputs = new(StringComparer.OrdinalIgnoreCase);
    private string? rendererContractPath;
    private SkinStudioElementItem? selectedItem;
    private string compactSurface = "Canvas";
    private bool workspaceChosen;
    private SkinExtrasPickerWindow? extrasPicker;
    private SkinStudioWorkspaceController? extrasPreviewWorkspace;
    private CancellationTokenSource? extrasPreviewCancellation;
    private readonly SemaphoreSlim extrasPreviewGate = new(1, 1);
    private string? extrasPreviewPackKey;
    private FrameworkElement? rendererTarget;
    private Guid thumbnailCacheDraftId;
    private long thumbnailCacheRevision = -1;
    private bool workspaceRefreshQueued;
    private readonly SemaphoreSlim liveSyncGate = new(1, 1);
    private Guid? lastLiveSyncedDraftId;
    private long lastLiveSyncedRevision = -1;
    private readonly Dictionary<string, BitmapSource?> thumbnailCache =
        new(StringComparer.OrdinalIgnoreCase);
    private Window? ownerWindow;
    private bool suspendedForAppDeactivation;
    private bool disposed;

    public SkinStudioLauncherPage(
        SettingsService settings,
        Func<Task> openLegacyEditor)
        : this(settings, openLegacyEditor, null)
    {
    }

    internal SkinStudioLauncherPage(
        SettingsService settings,
        Func<Task> openLegacyEditor,
        ILazerSkinReloadService? reloadService)
    {
        this.settings = settings;
        this.openLegacyEditor = openLegacyEditor;
        this.reloadService = reloadService;
        workspace = new SkinStudioWorkspaceController(SkinStudioPaths.DefaultWorkspace);
        workspace.StateChanged += Workspace_StateChanged;
        InitializeComponent();
        SkinStudioColorPicker.ColourChanged += SkinStudioColorPicker_ColourChanged;
        SkinStudioColorPicker.CloseRequested += () => SkinStudioColorPickerPopup.IsOpen = false;
        SkinStudioColorPickerPopup.Opened += SkinStudioColorPickerPopup_Opened;
        SkinStudioColorPickerPopup.Closed += SkinStudioColorPickerPopup_Closed;
        skinIniSaveTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(90),
        };
        skinIniSaveTimer.Tick += (_, _) => saveIntegratedSkinIni();
        liveSyncTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(350),
        };
        liveSyncTimer.Tick += LiveSyncTimer_Tick;
        rendererEventTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(100),
        };
        rendererEventTimer.Tick += RendererEventTimer_Tick;
        rendererEventTimer.Start();
        rendererColourPreviewTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(16),
        };
        rendererColourPreviewTimer.Tick += RendererColourPreviewTimer_Tick;
        rendererScalePreviewTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(24),
        };
        rendererScalePreviewTimer.Tick += RendererScalePreviewTimer_Tick;
        buildColourSwatches();
        rendererTarget = MainRendererMount;
        LayoutUpdated += (_, _) => updateRendererPlacement();
        StudioHost.StudioExited += StudioHost_StudioExited;
        ScenePicker.ItemsSource = Enum.GetValues<SkinStudioPreviewScene>();
        ScenePicker.SelectedItem = SkinStudioPreviewScene.Showcase;
        SizeChanged += (_, _) => applyResponsiveLayout();
        Loaded += SkinStudioLauncherPage_Loaded;
        Unloaded += SkinStudioLauncherPage_Unloaded;
        IsVisibleChanged += SkinStudioLauncherPage_IsVisibleChanged;
    }

    private void SkinStudioLauncherPage_Loaded(object sender, RoutedEventArgs e)
    {
        var window = Window.GetWindow(this);
        if (ReferenceEquals(window, ownerWindow))
            return;
        detachOwnerWindow();
        ownerWindow = window;
        if (ownerWindow is null)
            return;
        ownerWindow.Deactivated += OwnerWindow_Deactivated;
        ownerWindow.Activated += OwnerWindow_Activated;
    }

    private void SkinStudioLauncherPage_Unloaded(object sender, RoutedEventArgs e) =>
        detachOwnerWindow();

    private async void SkinStudioLauncherPage_IsVisibleChanged(
        object sender,
        DependencyPropertyChangedEventArgs e)
    {
        if (disposed)
            return;
        if (!IsVisible)
        {
            await StopAsync();
            return;
        }
        if (workspaceChosen && renderer is null)
            await EnsureReadyAsync();
    }

    private async void OwnerWindow_Deactivated(object? sender, EventArgs e)
    {
        if (!IsVisible || renderer is null)
            return;

        // Let Windows finish transferring foreground ownership before checking
        // the new window. Ordinary Alt-Tab keeps the paused renderer visible;
        // only a true fullscreen foreground application suspends the child.
        await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
        if (!isForegroundApplicationFullscreen())
            return;

        suspendedForAppDeactivation = true;
        await sendRendererAsync(new SkinStudioRendererRequest
        {
            Command = SkinStudioRendererCommandKind.SetActive,
            Active = false,
        }, reportFailure: false);
    }

    private async void OwnerWindow_Activated(object? sender, EventArgs e)
    {
        if (!suspendedForAppDeactivation || !IsVisible || renderer is null)
            return;
        suspendedForAppDeactivation = false;
        await sendRendererAsync(new SkinStudioRendererRequest
        {
            Command = SkinStudioRendererCommandKind.SetActive,
            Active = CanvasPane.Visibility == Visibility.Visible,
        }, reportFailure: false);
    }

    private void detachOwnerWindow()
    {
        if (ownerWindow is null)
            return;
        ownerWindow.Deactivated -= OwnerWindow_Deactivated;
        ownerWindow.Activated -= OwnerWindow_Activated;
        ownerWindow = null;
    }

    private bool isForegroundApplicationFullscreen()
    {
        var foreground = GetForegroundWindow();
        var ownerHandle = ownerWindow is null
            ? 0
            : new WindowInteropHelper(ownerWindow).Handle;
        if (foreground == 0 || foreground == ownerHandle
            || !GetWindowRect(foreground, out var windowRect))
            return false;

        var monitor = MonitorFromWindow(foreground, monitor_defaulttonearest);
        var monitorInfo = new MonitorInfo
        {
            Size = Marshal.SizeOf<MonitorInfo>(),
        };
        if (monitor == 0 || !GetMonitorInfo(monitor, ref monitorInfo))
            return false;

        const int tolerance = 2;
        return Math.Abs(windowRect.Left - monitorInfo.Monitor.Left) <= tolerance
               && Math.Abs(windowRect.Top - monitorInfo.Monitor.Top) <= tolerance
               && Math.Abs(windowRect.Right - monitorInfo.Monitor.Right) <= tolerance
               && Math.Abs(windowRect.Bottom - monitorInfo.Monitor.Bottom) <= tolerance;
    }

    public async Task EnsureReadyAsync()
    {
        if (!initialized)
        {
            workspace.Initialize();
            initialized = true;
            RootPathText.Text = workspace.WorkspacePath;
            refreshWorkspacePresentation();
        }
        if (!installedSkinCatalogLoaded)
            await loadInstalledSkinCatalogAsync();
        if (!workspaceChosen)
        {
            WelcomePanel.Visibility = Visibility.Visible;
            return;
        }
        if (StudioHost.IsStudioRunning && renderer is not null)
            return;

        await readinessGate.WaitAsync();
        try
        {
            if (StudioHost.IsStudioRunning && renderer is not null)
                return;

            showStarting(
                "Starting the lazer renderer",
                "Preparing the isolated draft and renderer-only lazer surface.",
                10);
            executablePath ??= SkinStudioExecutableResolver.Resolve();
            if (executablePath is null)
            {
                showFailure(
                    "Native renderer is not installed",
                    "Build or install the native-tools bundle. The legacy editor remains available.");
                return;
            }

            if (!probeComplete)
            {
                var probe = await probeAsync(executablePath);
                if (probe.ContractVersion != SkinStudioLaunchContract.CurrentVersion
                    || probe.RendererContractVersion != SkinStudioRendererLaunchContract.CurrentVersion
                    || !probe.RendererOnly)
                {
                    throw new InvalidDataException(
                        "The bundled Skin Studio does not support the required renderer-only contract.");
                }
                if (!probe.EmbeddedHost.Equals("child-hwnd-v1", StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("The native bundle cannot be embedded in Kumori.");
                probeComplete = true;
            }

            showStarting("Starting the lazer renderer", "Launching the pinned lazer runtime.", 35);
            var contract = createRendererContract();
            renderer = new SkinStudioRendererPipeClient(contract.CommandPipeName);
            await StudioHost.StartRendererAsync(executablePath, rendererContractPath!);
            showStarting("Loading skin into lazer", "Importing the complete draft into the renderer.", 60);
            var loaded = await sendRendererAsync(new SkinStudioRendererRequest
            {
                Command = SkinStudioRendererCommandKind.LoadDraft,
                DraftId = workspace.CurrentDraft.DraftId,
                DraftRevision = workspace.CurrentRevision,
            });
            if (loaded?.Accepted != true)
                throw new InvalidOperationException(loaded?.Message ?? "The renderer did not load the draft.");
            showStarting("Preparing gameplay preview", "Positioning the fully loaded skin at the overview scene.", 90);
            await seekRendererAsync(SkinStudioPreviewScene.Showcase);
            await sendRendererAsync(new SkinStudioRendererRequest
            {
                Command = SkinStudioRendererCommandKind.SetPreviewScale,
                CursorScale = CursorSizeSlider.Value,
                ObjectScale = ObjectSizeSlider.Value,
            }, reportFailure: false);
            showStudio();
            setStatus(
                "Renderer ready",
                $"{workspace.CurrentDraft.Name} · revision {workspace.CurrentRevision}",
                "Success");
        }
        catch (Exception ex)
        {
            renderer = null;
            showFailure("Could not start the lazer renderer", ex.Message);
        }
        finally
        {
            readinessGate.Release();
        }
    }

    private async Task<StudioProbe> probeAsync(string executable)
    {
        var startInfo = new ProcessStartInfo(executable)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        startInfo.ArgumentList.Add("--probe");
        using var process = Process.Start(startInfo)
                            ?? throw new InvalidOperationException("The native Skin Studio probe did not start.");
        var outputTask = process.StandardOutput.ReadToEndAsync();
        var errorTask = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(20));
        var output = await outputTask;
        var error = await errorTask;
        if (process.ExitCode != 0)
            throw new InvalidOperationException(string.IsNullOrWhiteSpace(error) ? "Native probe failed." : error.Trim());
        using var document = JsonDocument.Parse(output);
        var root = document.RootElement;
        return new StudioProbe(
            root.GetProperty("contract_version").GetInt32(),
            root.GetProperty("renderer_contract_version").GetInt32(),
            root.GetProperty("renderer_only").GetBoolean(),
            root.GetProperty("embedded_host").GetString() ?? "");
    }

    private SkinStudioRendererLaunchContract createRendererContract()
    {
        var session = Guid.NewGuid();
        var appearance = settings.Current.Appearance;
        var contract = new SkinStudioRendererLaunchContract
        {
            WorkspacePath = workspace.WorkspacePath,
            DraftId = workspace.CurrentDraft.DraftId,
            DraftRevision = workspace.CurrentRevision,
            ThemeId = appearance.ThemeId,
            CustomTheme = CustomThemePalette.Normalize(appearance.CustomTheme).Colors,
            SessionId = session,
            CommandPipeName = $"kumori-skin-renderer-{session:N}",
        }.Normalize();
        rendererContractPath = Path.Combine(
            SkinStudioPaths.ContractsDirectory,
            $"renderer-{session:N}.json");
        contract.Save(rendererContractPath);
        return contract;
    }

    private void Workspace_StateChanged(object? sender, EventArgs e)
    {
        scheduleLiveSync();
        if (workspaceRefreshQueued)
            return;
        workspaceRefreshQueued = true;
        _ = Dispatcher.BeginInvoke(() =>
        {
            workspaceRefreshQueued = false;
            if (!disposed)
                refreshWorkspacePresentation();
        }, System.Windows.Threading.DispatcherPriority.Background);
    }

    private void refreshWorkspacePresentation()
    {
        refreshSkinChoices();

        if (CategoryPicker.ItemsSource is null)
        {
            CategoryPicker.ItemsSource = availableCategories();
            CategoryPicker.SelectedIndex = 0;
        }
        refreshElementList();
        refreshInspector();
        ChangesText.Text = workspace.CurrentDraft.Changes.Count == 0
            ? "No staged changes"
            : $"{workspace.CurrentDraft.Changes.Count} staged file change(s) · revision {workspace.CurrentRevision}";
        UndoButton.IsEnabled = workspace.CurrentDraft.CanUndo;
        RedoButton.IsEnabled = workspace.CurrentDraft.CanRedo;
        DiscardAllButton.IsEnabled = workspace.CurrentDraft.Changes.Count > 0;
        ReviewChangesButton.IsEnabled = workspace.CurrentDraft.Changes.Count > 0;
    }

    private void refreshSkinChoices()
    {
        suppressDraftSelection = true;
        var drafts = workspace.Drafts;
        var installedIds = installedSkinCatalog?.Skins
                               .Select(skin => skin.Id)
                               .ToHashSet()
                           ?? [];
        var choices = new List<SkinStudioSkinChoice>();
        if (installedSkinCatalog is not null)
        {
            choices.AddRange(installedSkinCatalog.Skins.Select(skin =>
                new SkinStudioSkinChoice
                {
                    DisplayName = skin.Name,
                    Detail = string.IsNullOrWhiteSpace(skin.Creator)
                        ? "Installed in osu!lazer"
                        : $"{skin.Creator} · installed in osu!lazer",
                    InstalledSkin = skin,
                }));
        }
        choices.AddRange(drafts
            .Where(draft => draft.SourceLazerSkinId is null
                            || !installedIds.Contains(draft.SourceLazerSkinId.Value))
            .Select(draft => new SkinStudioSkinChoice
            {
                DisplayName = draft.Name,
                Detail = string.IsNullOrWhiteSpace(draft.Creator)
                    ? "Kumori draft"
                    : $"{draft.Creator} · Kumori draft",
                Draft = draft,
            }));
        DraftPicker.ItemsSource = choices;
        DraftPicker.SelectedItem = choices.FirstOrDefault(choice =>
            choice.Draft?.DraftId == workspace.CurrentDraft.DraftId
            || (workspace.CurrentDraft.SourceLazerSkinId is { } skinId
                && choice.InstalledSkin?.Id == skinId));
        suppressDraftSelection = false;
    }

    private async Task loadInstalledSkinCatalogAsync()
    {
        installedSkinCatalogLoaded = true;
        try
        {
            var realm = new LazerSkinRealmService();
            var rootOverride = settings.Current.SkinEditor.LazerRootOverride;
            installedSkinCatalog = await Task.Run(() => realm.LoadCatalog(rootOverride));
            RootPathText.Text = installedSkinCatalog.RootPath;
            refreshSkinChoices();
        }
        catch (Exception ex)
        {
            installedSkinCatalog = null;
            setStatus("Installed skins unavailable", ex.Message, "Danger");
            refreshSkinChoices();
        }
    }

    private void refreshElementList()
    {
        if (CategoryPicker.SelectedItem is not SkinStudioElementCategory category)
            return;
        var query = ElementSearchBox.Text.Trim();
        var files = workspace.Materialize();
        var showUnused = HideFallbackToggle.IsChecked == true;
        var elements = category.Title.Equals("Mania", StringComparison.OrdinalIgnoreCase)
            ? maniaElementsForCurrentSkin(category.Elements, files)
            : category.Elements;
        var previousManiaKeys = selectedItem?.SemanticPreview.ManiaKeyCount;
        var items = elements
            .Where(element => isModeVisible(element.SemanticPreview.Ruleset))
            .Where(element => string.IsNullOrWhiteSpace(query)
                              || element.Label.Contains(query, StringComparison.OrdinalIgnoreCase)
                              || element.ComponentName.Contains(query, StringComparison.OrdinalIgnoreCase))
            .Where(element => showUnused
                              || (workspace.IsSupplied(element.ComponentName)
                                  && workspace.IsUsedByLazer(element.ComponentName)))
            .Select(element => new SkinStudioElementItem
            {
                Label = element.Label,
                ComponentName = element.ComponentName,
                IsAudio = element.IsAudio,
                PreviewScene = element.PreviewScene,
                SemanticPreview = element.SemanticPreview,
                SourceLabel = !workspace.IsSupplied(element.ComponentName)
                    ? "FALLBACK"
                    : workspace.IsUsedByLazer(element.ComponentName)
                        ? "SKIN"
                        : "UNUSED",
                Thumbnail = thumbnailForCurrentRevision(files, element.ComponentName),
            })
            .ToArray();
        suppressElementSelection = true;
        ElementList.ItemsSource = items;
        selectedItem = items.FirstOrDefault(item =>
            item.ComponentName.Equals(workspace.SelectedComponent, StringComparison.OrdinalIgnoreCase)
            && (previousManiaKeys is null
                || item.SemanticPreview.ManiaKeyCount == previousManiaKeys));
        ElementList.SelectedItem = selectedItem;
        suppressElementSelection = false;
    }

    private static IReadOnlyList<SkinStudioElementDefinition> maniaElementsForCurrentSkin(
        IReadOnlyList<SkinStudioElementDefinition> source,
        IReadOnlyDictionary<string, byte[]> files)
    {
        var keyCounts = new[] { 4 };
        if (files.TryGetValue("skin.ini", out var bytes))
        {
            try
            {
                var configured = SkinIniDocument.Parse(bytes)
                    .GetSections("Mania")
                    .Where(section => section.ManiaKeys is >= 1 and <= 18)
                    .Select(section => section.ManiaKeys!.Value)
                    .Distinct()
                    .Order()
                    .ToArray();
                if (configured.Length > 0)
                    keyCounts = configured;
            }
            catch
            {
            }
        }
        return keyCounts.SelectMany(keys => source.Select(element =>
        {
            var semantic = SkinStudioSemanticPreviewCatalog.Resolve(
                element.ComponentName,
                element.FamilyId,
                keys);
            return element with
            {
                Label = $"{keys}K · {element.Label}",
                PreviewScene = semantic.Scene,
                ManiaKeyCount = keys,
            };
        })).ToArray();
    }

    private void refreshInspector()
    {
        var selected = selectedItem;
        var family = workspace.SelectedFamily;
        var hasSelection = selected is not null;
        SelectedAssetTitle.Text = selected?.Label ?? "Choose an element";
        SelectedAssetDetail.Text = selected is null
            ? "Select an element on the left to inspect and edit its complete file family."
            : $"{selected.ComponentName}\n{SkinDraftAssetService.VariantSummary(family)}"
              + $"\n{compatibilityLabel(selected.SemanticPreview.Compatibility)}"
              + (selected.SourceLabel == "FALLBACK"
                  ? " · using lazer fallback"
                  : selected.SourceLabel == "UNUSED"
                      ? " · stable-only asset"
                      : " · supplied by this skin");
        SelectedAssetPreview.Source = selected?.Thumbnail;
        ReplaceButton.IsEnabled = hasSelection;
        DeleteButton.IsEnabled = family.Count > 0;
        ResetButton.IsEnabled = hasSelection && workspace.CurrentDraft.Changes.Any(change =>
            SkinDraftAssetService.ComponentName(change.Filename).Equals(
                selected!.ComponentName,
                StringComparison.OrdinalIgnoreCase));
        CopyButton.IsEnabled = family.Count > 0;
        PasteButton.IsEnabled = hasSelection && workspace.HasClipboard;
        var hasImage = family.Any(asset => asset.IsImage);
        QuickRecolourModePicker.IsEnabled = hasImage;
        QuickEditScopePicker.IsEnabled = hasImage;
        ApplyColourButton.IsEnabled = hasImage;
        AuditionButton.Visibility = selected?.IsAudio == true
            ? Visibility.Visible
            : Visibility.Collapsed;
        NormalizeAudioButton.Visibility = selected?.IsAudio == true
            ? Visibility.Visible
            : Visibility.Collapsed;
        NormalizeAudioButton.IsEnabled = selected?.IsAudio == true && family.Any(asset => asset.IsAudio);
        AdvancedTransformButton.IsEnabled = hasImage;
    }

    private static BitmapSource? thumbnailFor(
        IReadOnlyDictionary<string, byte[]> files,
        string component)
    {
        var candidate = files.FirstOrDefault(pair =>
            SkinDraftAssetService.ComponentName(pair.Key).Equals(
                component,
                StringComparison.OrdinalIgnoreCase)
            && SkinMediaTypes.IsImage(pair.Key));
        if (candidate.Value is null)
            return null;
        try
        {
            using var stream = new MemoryStream(candidate.Value, writable: false);
            var image = new BitmapImage();
            image.BeginInit();
            image.CacheOption = BitmapCacheOption.OnLoad;
            image.StreamSource = stream;
            image.EndInit();
            image.Freeze();
            return image;
        }
        catch
        {
            return null;
        }
    }

    private BitmapSource? thumbnailForCurrentRevision(
        IReadOnlyDictionary<string, byte[]> files,
        string component)
    {
        if (thumbnailCacheDraftId != workspace.CurrentDraft.DraftId
            || thumbnailCacheRevision != workspace.CurrentRevision)
        {
            thumbnailCacheDraftId = workspace.CurrentDraft.DraftId;
            thumbnailCacheRevision = workspace.CurrentRevision;
            thumbnailCache.Clear();
        }
        if (!thumbnailCache.TryGetValue(component, out var thumbnail))
        {
            thumbnail = thumbnailFor(files, component);
            thumbnailCache[component] = thumbnail;
        }
        return thumbnail;
    }

    private async Task<SkinStudioRendererResponse?> sendRendererAsync(
        SkinStudioRendererRequest request,
        bool reportFailure = true)
    {
        var client = renderer;
        if (client is null)
            return null;
        try
        {
            var response = await client.SendAsync(request);
            if (!response.Accepted && reportFailure)
                setStatus("Renderer command failed", response.Message, "Danger");
            rendererPlaying = response.Playing;
            PlayPauseButton.Content = rendererPlaying ? "Pause" : "Play";
            return response;
        }
        catch (Exception ex)
        {
            if (reportFailure)
                setStatus("Renderer unavailable", ex.Message, "Danger");
            return null;
        }
    }

    private async void RendererEventTimer_Tick(object? sender, EventArgs e)
    {
        var client = renderer;
        if (rendererEventPollActive || client is null || !IsVisible || disposed)
            return;
        rendererEventPollActive = true;
        try
        {
            var response = await client.SendAsync(new SkinStudioRendererRequest
            {
                Command = SkinStudioRendererCommandKind.PollEvent,
            });
            if (response.Accepted
                && response.Event == SkinStudioRendererEventKind.ColourEditRequested
                && response.ColourTarget is { } target
                && response.ColourRed is { } red
                && response.ColourGreen is { } green
                && response.ColourBlue is { } blue)
            {
                openRendererColourEditor(
                    target,
                    WpfColor.FromRgb(red, green, blue),
                    response.AnchorX ?? 0.5,
                    response.AnchorY ?? 0.5,
                    response.AvoidLeft ?? 0.4,
                    response.AvoidTop ?? 0.4,
                    response.AvoidRight ?? 0.6,
                    response.AvoidBottom ?? 0.6);
            }
        }
        catch
        {
            // Polling is opportunistic. Lifecycle and explicit renderer
            // commands own user-facing recovery reporting.
        }
        finally
        {
            rendererEventPollActive = false;
        }
    }

    private void openRendererColourEditor(
        SkinStudioRendererColourTarget target,
        WpfColor initial,
        double anchorX,
        double anchorY,
        double avoidLeft,
        double avoidTop,
        double avoidRight,
        double avoidBottom)
    {
        var setting = rendererColourSetting(target);
        showSkinIniMode();
        var choice = (SkinIniGroupPicker.ItemsSource as IEnumerable<SkinStudioIniGroupChoice>)?
            .FirstOrDefault(item => item.Group == setting.Group);
        if (choice is not null)
        {
            suppressSkinIniEvents = true;
            SkinIniGroupPicker.SelectedItem = choice;
            suppressSkinIniEvents = false;
            rebuildSkinIniGroup();
        }

        activeSkinIniColourBox = null;
        activeRendererColourTarget = target;
        rendererColourPopupActive = true;
        rendererColourPopupAnchor = new System.Windows.Point(
            Math.Clamp(anchorX, 0, 1),
            Math.Clamp(anchorY, 0, 1));
        var left = Math.Clamp(Math.Min(avoidLeft, avoidRight), 0, 1);
        var top = Math.Clamp(Math.Min(avoidTop, avoidBottom), 0, 1);
        var right = Math.Clamp(Math.Max(avoidLeft, avoidRight), 0, 1);
        var bottom = Math.Clamp(Math.Max(avoidTop, avoidBottom), 0, 1);
        rendererColourAvoidBounds = new System.Windows.Rect(
            left,
            top,
            Math.Max(0.01, right - left),
            Math.Max(0.01, bottom - top));
        SkinStudioColorPicker.Open(
            colourHex(initial),
            setting.Label,
            $"Edits [Colours] {setting.Key}. The isolated draft and real lazer preview update live.",
            allowOpacity: false);
        SkinStudioColorPickerPopup.Placement = PlacementMode.Custom;
        SkinStudioColorPickerPopup.PlacementTarget = StudioHost;
        SkinStudioColorPickerPopup.CustomPopupPlacementCallback =
            placeRendererColourPopup;
        SkinStudioColorPickerPopup.HorizontalOffset = 0;
        SkinStudioColorPickerPopup.VerticalOffset = 0;
        SkinStudioColorPickerPopup.IsOpen = true;
        setStatus(
            $"Editing {setting.Label.ToLowerInvariant()}",
            "Drag the colour wheel; the loaded lazer drawable updates immediately while Kumori saves draft revisions in the background.",
            "AccentPink");
    }

    private CustomPopupPlacement[] placeRendererColourPopup(
        System.Windows.Size popupSize,
        System.Windows.Size targetSize,
        System.Windows.Point offset)
    {
        const double gap = 14;
        const double edge = 8;
        var anchor = new System.Windows.Point(
            rendererColourPopupAnchor.X * targetSize.Width,
            rendererColourPopupAnchor.Y * targetSize.Height);
        var avoid = new System.Windows.Rect(
            rendererColourAvoidBounds.X * targetSize.Width,
            rendererColourAvoidBounds.Y * targetSize.Height,
            rendererColourAvoidBounds.Width * targetSize.Width,
            rendererColourAvoidBounds.Height * targetSize.Height);
        var maximumX = Math.Max(edge, targetSize.Width - popupSize.Width - edge);
        var maximumY = Math.Max(edge, targetSize.Height - popupSize.Height - edge);
        var centredX = Math.Clamp(
            anchor.X - popupSize.Width / 2,
            edge,
            maximumX);
        var centredY = Math.Clamp(
            anchor.Y - popupSize.Height / 2,
            edge,
            maximumY);
        var right = new CustomPopupPlacement(
            new System.Windows.Point(avoid.Right + gap, centredY),
            PopupPrimaryAxis.Horizontal);
        var left = new CustomPopupPlacement(
            new System.Windows.Point(avoid.Left - popupSize.Width - gap, centredY),
            PopupPrimaryAxis.Horizontal);
        var above = new CustomPopupPlacement(
            new System.Windows.Point(centredX, avoid.Top - popupSize.Height - gap),
            PopupPrimaryAxis.Vertical);
        var below = new CustomPopupPlacement(
            new System.Windows.Point(centredX, avoid.Bottom + gap),
            PopupPrimaryAxis.Vertical);

        // Wide slider bodies are safest above or below. Compact circles are
        // safest beside their approach-circle bounds. Every fallback remains
        // outside the actual drawable rather than merely avoiding a viewport
        // edge.
        return avoid.Width > targetSize.Width * 0.35
            ? [above, below, right, left]
            : [right, left, below, above];
    }

    private void useStandardColourPopupPlacement(FrameworkElement target)
    {
        SkinStudioColorPickerPopup.Placement = PlacementMode.Bottom;
        SkinStudioColorPickerPopup.PlacementTarget = target;
        SkinStudioColorPickerPopup.CustomPopupPlacementCallback = null;
        SkinStudioColorPickerPopup.HorizontalOffset = -150;
        SkinStudioColorPickerPopup.VerticalOffset = 5;
    }

    private async void SkinStudioColorPickerPopup_Opened(
        object? sender,
        EventArgs e)
    {
        if (!rendererColourPopupActive)
            return;
        await sendRendererAsync(new SkinStudioRendererRequest
        {
            Command = SkinStudioRendererCommandKind.SetMenuCursorVisible,
            Active = false,
        }, reportFailure: false);
    }

    private async void SkinStudioColorPickerPopup_Closed(
        object? sender,
        EventArgs e)
    {
        if (!rendererColourPopupActive)
            return;

        saveIntegratedSkinIni();
        rendererColourPopupActive = false;
        activeRendererColourTarget = null;
        await sendRendererAsync(new SkinStudioRendererRequest
        {
            Command = SkinStudioRendererCommandKind.SetMenuCursorVisible,
            Active = true,
        }, reportFailure: false);
        StudioHost.FocusStudio();
    }

    private void queueRendererColourPreview(
        SkinStudioRendererColourTarget target,
        WpfColor colour,
        string? component = null)
    {
        pendingRendererColourPreview = (target, colour, component);
        if (!rendererColourPreviewTimer.IsEnabled)
            rendererColourPreviewTimer.Start();
    }

    private async void RendererColourPreviewTimer_Tick(
        object? sender,
        EventArgs e)
    {
        if (rendererColourPreviewSending)
            return;
        if (pendingRendererColourPreview is not { } preview)
        {
            rendererColourPreviewTimer.Stop();
            return;
        }

        pendingRendererColourPreview = null;
        rendererColourPreviewSending = true;
        try
        {
            await sendRendererAsync(new SkinStudioRendererRequest
            {
                Command = SkinStudioRendererCommandKind.SetPreviewColour,
                ColourTarget = preview.Target,
                Component = preview.Component,
                ColourRed = preview.Colour.R,
                ColourGreen = preview.Colour.G,
                ColourBlue = preview.Colour.B,
            }, reportFailure: false);
        }
        finally
        {
            rendererColourPreviewSending = false;
        }
    }

    private void PreviewSizeSlider_ValueChanged(
        object sender,
        RoutedPropertyChangedEventArgs<double> e)
    {
        if (CursorSizeValue is not null && CursorSizeSlider is not null)
            CursorSizeValue.Text = $"{CursorSizeSlider.Value:0.00}×";
        if (ObjectSizeValue is not null && ObjectSizeSlider is not null)
            ObjectSizeValue.Text = $"{ObjectSizeSlider.Value:0.00}×";
        if (!initialized || renderer is null)
            return;
        rendererScalePreviewPending = true;
        if (!rendererScalePreviewTimer.IsEnabled)
            rendererScalePreviewTimer.Start();
    }

    private async void RendererScalePreviewTimer_Tick(
        object? sender,
        EventArgs e)
    {
        if (rendererScalePreviewSending || !rendererScalePreviewPending)
            return;
        rendererScalePreviewTimer.Stop();
        rendererScalePreviewPending = false;
        rendererScalePreviewSending = true;
        try
        {
            await sendRendererAsync(new SkinStudioRendererRequest
            {
                Command = SkinStudioRendererCommandKind.SetPreviewScale,
                CursorScale = CursorSizeSlider.Value,
                ObjectScale = ObjectSizeSlider.Value,
            }, reportFailure: false);
        }
        finally
        {
            rendererScalePreviewSending = false;
            if (rendererScalePreviewPending)
                rendererScalePreviewTimer.Start();
        }
    }

    private static (
        string Key,
        string Label,
        SkinIniVisualGroup Group) rendererColourSetting(
        SkinStudioRendererColourTarget target)
    {
        var comboIndex = (int)target - (int)SkinStudioRendererColourTarget.Combo1 + 1;
        if (comboIndex is >= 1 and <= 8)
            return ($"Combo{comboIndex}", $"Combo colour {comboIndex}", SkinIniVisualGroup.Combo);
        return target switch
        {
            SkinStudioRendererColourTarget.SliderInner =>
                ("SliderTrackOverride", "Slider inner colour", SkinIniVisualGroup.Slider),
            SkinStudioRendererColourTarget.SliderOuter =>
                ("SliderBorder", "Slider outer colour", SkinIniVisualGroup.Slider),
            _ => throw new ArgumentOutOfRangeException(nameof(target)),
        };
    }

    private void queueRendererReload()
    {
        if (renderer is null)
            return;
        rendererReloadCancellation?.Cancel();
        rendererReloadCancellation?.Dispose();
        var cancellation = new CancellationTokenSource();
        rendererReloadCancellation = cancellation;
        var draftId = workspace.CurrentDraft.DraftId;
        var revision = workspace.CurrentRevision;
        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(16, cancellation.Token);
                await Dispatcher.InvokeAsync(() =>
                    setStatus(
                        "Updating lazer preview",
                        $"Initializing draft revision {revision}â€¦",
                        "TextMuted"));
                var response = await renderer!.SendAsync(new SkinStudioRendererRequest
                {
                    Command = SkinStudioRendererCommandKind.LoadDraft,
                    DraftId = draftId,
                    DraftRevision = revision,
                }, cancellation.Token);
                await Dispatcher.InvokeAsync(() =>
                {
                    if (response.Accepted)
                        setStatus("Preview refreshed", $"Revision {revision} loaded in lazer.", "Success");
                    else
                        setStatus("Preview kept its last good skin", response.Message, "Danger");
                });
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception ex)
            {
                await Dispatcher.InvokeAsync(() =>
                    setStatus("Preview kept its last good skin", ex.Message, "Danger"));
            }
        });
    }

    private async Task seekRendererAsync(SkinStudioPreviewScene scene)
    {
        suppressSceneSelection = true;
        ScenePicker.SelectedItem = scene;
        suppressSceneSelection = false;
        updateAutoMotionPresentation(scene);
        var response = await sendRendererAsync(new SkinStudioRendererRequest
        {
            Command = SkinStudioRendererCommandKind.Seek,
            Scene = scene,
        });
        if (response?.Accepted == true)
        {
            rendererPlaying = scene is SkinStudioPreviewScene.Showcase
                or SkinStudioPreviewScene.Spinner;
            PlayPauseButton.Content = rendererPlaying ? "Pause" : "Play";
            setAutoMotionChecked(rendererPlaying);
        }
    }

    private void updateAutoMotionPresentation(SkinStudioPreviewScene scene)
    {
        AutoMotionToggle.Visibility = scene is SkinStudioPreviewScene.Cursor
            or SkinStudioPreviewScene.Spinner
            ? Visibility.Visible
            : Visibility.Collapsed;
        AutoMotionToggle.Content = scene switch
        {
            SkinStudioPreviewScene.Cursor => "Auto-move cursor",
            SkinStudioPreviewScene.Spinner => "Loop spinner",
            _ => "Auto motion",
        };
    }

    private void setAutoMotionChecked(bool value)
    {
        suppressAutoMotionChange = true;
        AutoMotionToggle.IsChecked = value;
        suppressAutoMotionChange = false;
    }

    private void runWorkspaceAction(Action action, string success)
    {
        try
        {
            action();
            queueRendererReload();
            setStatus(success, $"Draft revision {workspace.CurrentRevision}", "Success");
        }
        catch (Exception ex)
        {
            setStatus("Skin edit stopped", ex.Message, "Danger");
        }
    }

    private async void DraftPicker_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (suppressDraftSelection || DraftPicker.SelectedItem is not SkinStudioSkinChoice selected)
            return;
        var reopenSkinIni = SkinIniInspectorView.Visibility == Visibility.Visible;
        try
        {
            saveIntegratedSkinIni();
            if (selected.InstalledSkin is { } installed)
            {
                var draft = workspace.Drafts.FirstOrDefault(candidate =>
                    candidate.SourceLazerSkinId == installed.Id);
                if (draft is null)
                {
                    integratedSkinIni = null;
                    await importInstalledSkinAsync(installed);
                }
                else if (draft.DraftId != workspace.CurrentDraft.DraftId)
                {
                    integratedSkinIni = null;
                    workspace.OpenDraft(draft.DraftId);
                }
                else
                    return;
            }
            else if (selected.Draft is { } draft
                     && draft.DraftId != workspace.CurrentDraft.DraftId)
            {
                integratedSkinIni = null;
                workspace.OpenDraft(draft.DraftId);
            }
            else
            {
                return;
            }

            if (!workspaceChosen)
                await enterWorkspaceAsync();
            else
            {
                resetWorkspaceToTop();
                showStarting(
                    "Loading skin into lazer",
                    $"Initializing {workspace.CurrentDraft.Name} before revealing the preview.",
                    45);
                rendererReloadCancellation?.Cancel();
                var response = await sendRendererAsync(new SkinStudioRendererRequest
                {
                    Command = SkinStudioRendererCommandKind.LoadDraft,
                    DraftId = workspace.CurrentDraft.DraftId,
                    DraftRevision = workspace.CurrentRevision,
                });
                if (response?.Accepted != true)
                    throw new InvalidOperationException(response?.Message ?? "The renderer did not load the skin.");
                showStarting("Preparing gameplay preview", "Positioning the fully initialized skin.", 90);
                await seekRendererAsync(SkinStudioPreviewScene.Showcase);
                showStudio();
            }
            if (reopenSkinIni)
                showSkinIniMode();
        }
        catch (Exception ex)
        {
            if (StudioHost.IsStudioRunning && renderer is not null)
                showStudio();
            setStatus("Could not open installed skin", ex.Message, "Danger");
            refreshSkinChoices();
            if (reopenSkinIni)
                showSkinIniMode();
        }
    }

    private void DraftPicker_DropDownOpened(object? sender, EventArgs e)
    {
        void scrollToFirstSkin()
        {
            if (DraftPicker.Template.FindName("PART_Popup", DraftPicker)
                    is not System.Windows.Controls.Primitives.Popup { Child: { } popupChild })
                return;
            FindVisualDescendant<ScrollViewer>(popupChild)?.ScrollToTop();
        }

        _ = Dispatcher.BeginInvoke(scrollToFirstSkin, DispatcherPriority.Loaded);
        _ = Dispatcher.BeginInvoke(scrollToFirstSkin, DispatcherPriority.ContextIdle);
    }

    private void resetWorkspaceToTop()
    {
        ElementSearchBox.Clear();
        CategoryPicker.SelectedIndex = 0;
        InspectorScrollViewer.ScrollToTop();
        _ = Dispatcher.BeginInvoke(() =>
        {
            InspectorScrollViewer.ScrollToTop();
            FindVisualDescendant<ScrollViewer>(ElementList)?.ScrollToTop();
        }, DispatcherPriority.Loaded);
    }

    private static T? FindVisualDescendant<T>(DependencyObject? root)
        where T : DependencyObject
    {
        if (root is null)
            return null;
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(root); index++)
        {
            var child = VisualTreeHelper.GetChild(root, index);
            if (child is T match)
                return match;
            if (FindVisualDescendant<T>(child) is { } descendant)
                return descendant;
        }
        return null;
    }

    private void CategoryPicker_SelectionChanged(object sender, SelectionChangedEventArgs e) => refreshElementList();
    private void ElementSearchBox_TextChanged(object sender, TextChangedEventArgs e) { if (initialized) refreshElementList(); }
    private void ElementFilter_Changed(object sender, RoutedEventArgs e) { if (initialized) refreshElementList(); }

    private async void ElementList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (suppressElementSelection || ElementList.SelectedItem is not SkinStudioElementItem item)
            return;
        selectedItem = item;
        workspace.Select(item.ComponentName);
        refreshInspector();
        await selectPreviewTargetAsync(item.SemanticPreview);
    }

    private IReadOnlyList<SkinStudioElementCategory> availableCategories() =>
        workspace.Categories
            .Where(category => category.Elements.Any(element =>
                isModeVisible(element.SemanticPreview.Ruleset)))
            .ToArray();

    private bool isModeVisible(SkinStudioRuleset ruleset) => ruleset switch
    {
        SkinStudioRuleset.Catch => settings.Current.SkinEditor.ShowCatchExtras,
        SkinStudioRuleset.Taiko => settings.Current.SkinEditor.ShowTaikoExtras,
        SkinStudioRuleset.Mania => settings.Current.SkinEditor.ShowManiaExtras,
        _ => true,
    };

    private static string compatibilityLabel(SkinExtraCompatibility compatibility) =>
        compatibility switch
        {
            SkinExtraCompatibility.LazerUsed => "Used by lazer",
            SkinExtraCompatibility.StableOnly => "Stable-only compatibility",
            _ => "Compatibility unverified",
        };

    private async Task selectPreviewTargetAsync(
        SkinStudioSemanticPreviewDescriptor target)
    {
        suppressSceneSelection = true;
        ScenePicker.SelectedItem = target.Scene;
        suppressSceneSelection = false;
        updateAutoMotionPresentation(target.Scene);
        var response = await sendRendererAsync(new SkinStudioRendererRequest
        {
            Command = SkinStudioRendererCommandKind.SelectPreviewTarget,
            PreviewTargetId = target.Id,
            FamilyId = target.FamilyId,
            Component = target.ComponentName,
            Ruleset = target.Ruleset,
            ManiaKeyCount = target.ManiaKeyCount,
        });
        if (response?.Accepted != true)
            return;
        rendererPlaying = response.Playing;
        PlayPauseButton.Content = rendererPlaying ? "Pause" : "Play";
        setAutoMotionChecked(rendererPlaying);
    }

    private void ChooseExistingStart_Click(object sender, RoutedEventArgs e)
    {
        DraftPicker.Focus();
        DraftPicker.IsDropDownOpen = true;
    }

    // Retained for the one-release fallback route, but the hybrid screen never calls it.
    private async void ChooseExistingStartPopup_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            setStatus("Loading installed skins", "Reading the osu!lazer catalog without changing it…", "TextMuted");
            var realm = new LazerSkinRealmService();
            var rootOverride = settings.Current.SkinEditor.LazerRootOverride;
            var catalog = await Task.Run(() => realm.LoadCatalog(rootOverride));
            if (catalog.Skins.Count == 0)
                throw new InvalidOperationException("No installed osu!lazer skins were found.");

            var list = new ListBox
            {
                ItemsSource = catalog.Skins,
                DisplayMemberPath = nameof(LazerSkinInfo.DisplayName),
                MinHeight = 320,
            };
            var open = new Button
            {
                Content = "Open as an isolated Kumori draft",
                Height = 36,
                Margin = new Thickness(0, 10, 0, 0),
                IsDefault = true,
            };
            var panel = new DockPanel { Margin = new Thickness(12) };
            DockPanel.SetDock(open, Dock.Bottom);
            panel.Children.Add(open);
            panel.Children.Add(list);
            var window = new Window
            {
                Title = "Choose an existing lazer skin",
                Owner = Window.GetWindow(this),
                Width = 620,
                Height = 520,
                Content = panel,
                WindowStartupLocation = WindowStartupLocation.CenterOwner,
            };
            open.Click += (_, _) =>
            {
                if (list.SelectedItem is not null)
                    window.DialogResult = true;
            };
            list.MouseDoubleClick += (_, _) =>
            {
                if (list.SelectedItem is not null)
                    window.DialogResult = true;
            };
            if (window.ShowDialog() != true || list.SelectedItem is not LazerSkinInfo skin)
                return;

            var snapshots = Path.Combine(workspace.WorkspacePath, "installed-snapshots");
            var destination = Path.Combine(snapshots, $"{skin.Id:N}.osk");
            await Task.Run(() => LazerInstalledSkinSnapshotService.CreateVerifiedOsk(
                skin,
                hash => realm.ReadFile(catalog.RootPath, hash),
                destination));
            workspace.ImportSkin(destination, skin.Name, skin.Creator);
            await enterWorkspaceAsync();
        }
        catch (Exception ex)
        {
            setStatus("Could not open installed skin", ex.Message, "Danger");
            KumoriDialog.Show(Window.GetWindow(this), ex.Message, "Choose skin",
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private async Task importInstalledSkinAsync(LazerSkinInfo skin)
    {
        var catalog = installedSkinCatalog
                      ?? throw new InvalidOperationException("The installed osu!lazer skin list is unavailable.");
        setStatus("Opening installed skin", $"Creating an isolated draft of {skin.Name}…", "TextMuted");
        var realm = new LazerSkinRealmService();
        var snapshots = Path.Combine(workspace.WorkspacePath, "installed-snapshots");
        var destination = Path.Combine(snapshots, $"{skin.Id:N}.osk");
        await Task.Run(() => LazerInstalledSkinSnapshotService.CreateVerifiedOsk(
            skin,
            hash => realm.ReadFile(catalog.RootPath, hash),
            destination));
        workspace.ImportSkin(destination, skin.Name, skin.Creator, skin.Id);
    }

    private async void CreateBlankStart_Click(object sender, RoutedEventArgs e)
    {
        workspace.CreateBlank();
        await enterWorkspaceAsync();
    }

    private async void CreateFromExtrasStart_Click(object sender, RoutedEventArgs e)
    {
        workspace.CreateBlank();
        await enterWorkspaceAsync();
        await openExtrasWorkspaceAsync();
    }

    private async void ImportToExtrasStart_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Title = "Import an osu! skin into Extras",
            Filter = "osu! skin archives|*.osk;*.zip|All files|*.*",
        };
        if (dialog.ShowDialog(Window.GetWindow(this)) != true)
            return;
        try
        {
            setStatus("Reading skin archive", dialog.FileName, "TextMuted");
            var source = await Task.Run(() => new SkinExtrasExtractionService().ReadOsk(dialog.FileName));
            Directory.CreateDirectory(AppPaths.SkinExtrasDir);
            var extractor = new SkinExtrasExtractorWindow(
                Window.GetWindow(this),
                source,
                extrasModeVisibility(),
                persistLazerExtrasFilter);
            if (extractor.ShowDialog() != true)
                return;
            var added = extractor.Results.Count(result =>
                result.Status == SkinExtraExtractionStatus.Extracted);
            setStatus("Imported into Extras", $"{added} reusable pack(s) added.", "Success");
        }
        catch (Exception ex)
        {
            setStatus("Extras import failed", ex.Message, "Danger");
        }
    }

    private async Task enterWorkspaceAsync()
    {
        workspaceChosen = true;
        WelcomePanel.Visibility = Visibility.Collapsed;
        resetWorkspaceToTop();
        refreshWorkspacePresentation();
        await EnsureReadyAsync();
    }

    private SkinExtraModeVisibility extrasModeVisibility() => new(
        settings.Current.SkinEditor.ShowCatchExtras,
        settings.Current.SkinEditor.ShowTaikoExtras,
        settings.Current.SkinEditor.ShowManiaExtras,
        settings.Current.SkinEditor.OnlyShowLazerExtras);

    private void persistLazerExtrasFilter(bool enabled) =>
        settings.Update(value => value.SkinEditor.OnlyShowLazerExtras = enabled);

    private void OverflowMenu_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { ContextMenu: { } menu } button)
            return;
        menu.PlacementTarget = button;
        menu.Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom;
        menu.IsOpen = true;
        e.Handled = true;
    }

    private async void NewDraft_Click(object sender, RoutedEventArgs e)
    {
        workspace.CreateBlank();
        if (!workspaceChosen)
            await enterWorkspaceAsync();
        else
        {
            queueRendererReload();
            setStatus("Created a new isolated draft", $"Draft revision {workspace.CurrentRevision}", "Success");
        }
    }

    private void ManageDrafts_Click(object sender, RoutedEventArgs e)
    {
        var list = new ListBox
        {
            ItemsSource = workspace.Drafts,
            DisplayMemberPath = nameof(SkinDraftManifest.Name),
            SelectedItem = workspace.Drafts.FirstOrDefault(draft =>
                draft.DraftId == workspace.CurrentDraft.DraftId),
            MinHeight = 240,
        };
        var duplicate = new Button { Content = "Duplicate", Width = 92, Margin = new Thickness(0, 0, 6, 0) };
        var rename = new Button { Content = "Rename", Width = 92, Margin = new Thickness(0, 0, 6, 0) };
        var delete = new Button { Content = "Delete", Width = 92, Margin = new Thickness(0, 0, 6, 0) };
        var restore = new Button { Content = "Restore deleted", Width = 112 };
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 10, 0, 0) };
        buttons.Children.Add(duplicate);
        buttons.Children.Add(rename);
        buttons.Children.Add(delete);
        buttons.Children.Add(restore);
        var panel = new DockPanel { Margin = new Thickness(12) };
        DockPanel.SetDock(buttons, Dock.Bottom);
        panel.Children.Add(buttons);
        panel.Children.Add(list);
        var window = new Window
        {
            Title = "Skin Studio drafts",
            Owner = Window.GetWindow(this),
            Width = 540,
            Height = 430,
            Content = panel,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
        };

        void refreshDraftList()
        {
            list.ItemsSource = null;
            list.ItemsSource = workspace.Drafts;
            list.SelectedItem = workspace.Drafts.FirstOrDefault(draft =>
                draft.DraftId == workspace.CurrentDraft.DraftId);
        }

        list.MouseDoubleClick += (_, _) =>
        {
            if (list.SelectedItem is SkinDraftManifest draft)
            {
                runWorkspaceAction(() => workspace.OpenDraft(draft.DraftId), "Opened draft");
                refreshDraftList();
            }
        };
        duplicate.Click += (_, _) =>
        {
            runWorkspaceAction(workspace.DuplicateCurrent, "Duplicated draft");
            refreshDraftList();
        };
        rename.Click += (_, _) =>
        {
            var identity = promptIdentity(workspace.CurrentDraft.Name, workspace.CurrentDraft.Creator);
            if (identity is null)
                return;
            runWorkspaceAction(
                () => workspace.RenameCurrent(identity.Value.Name, identity.Value.Creator),
                "Updated draft identity");
            refreshDraftList();
        };
        delete.Click += (_, _) =>
        {
            if (MessageBox.Show(window, $"Move '{workspace.CurrentDraft.Name}' to recoverable trash?",
                    "Delete draft", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
                return;
            runWorkspaceAction(workspace.DeleteCurrentRecoverably, "Moved draft to recoverable trash");
            refreshDraftList();
        };
        restore.Click += (_, _) =>
        {
            runWorkspaceAction(workspace.RestoreLatestDeleted, "Restored the latest deleted draft");
            refreshDraftList();
        };
        window.ShowDialog();
    }

    private void ImportSkin_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog { Filter = "osu! skin (*.osk;*.zip)|*.osk;*.zip" };
        if (dialog.ShowDialog(Window.GetWindow(this)) == true)
            runWorkspaceAction(() => workspace.ImportSkin(dialog.FileName), "Imported skin into an isolated draft");
    }

    private void ImportAssets_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Filter = "Skin media|*.png;*.jpg;*.jpeg;*.wav;*.mp3;*.ogg",
            Multiselect = true,
        };
        if (dialog.ShowDialog(Window.GetWindow(this)) == true)
            runWorkspaceAction(() => workspace.ImportFiles(dialog.FileNames), "Imported skin files");
    }

    private void Replace_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog { Filter = "Skin media|*.png;*.jpg;*.jpeg;*.wav;*.mp3;*.ogg" };
        if (dialog.ShowDialog(Window.GetWindow(this)) == true)
            runWorkspaceAction(() => workspace.ReplaceSelected(dialog.FileName), "Replaced selected family");
    }

    private void Delete_Click(object sender, RoutedEventArgs e) => runWorkspaceAction(workspace.DeleteSelected, "Deleted selected family");
    private void Reset_Click(object sender, RoutedEventArgs e) => runWorkspaceAction(workspace.ResetSelected, "Reset selected family");
    private void Copy_Click(object sender, RoutedEventArgs e) => runWorkspaceAction(workspace.CopySelected, "Copied selected family");
    private void Paste_Click(object sender, RoutedEventArgs e) => runWorkspaceAction(workspace.PasteSelected, "Pasted complete family");
    private void Undo_Click(object sender, RoutedEventArgs e) => runWorkspaceAction(workspace.Undo, "Undid latest revision");
    private void Redo_Click(object sender, RoutedEventArgs e) => runWorkspaceAction(workspace.Redo, "Redid latest revision");

    private void DiscardAll_Click(object sender, RoutedEventArgs e)
    {
        if (MessageBox.Show(Window.GetWindow(this), "Discard all staged changes? A verified backup will be created first.",
                "Discard Skin Studio changes", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
            return;
        runWorkspaceAction(() =>
        {
            workspace.CreateBackup("Before discarding all changes");
            workspace.DiscardAll();
        }, "Discarded all staged changes after backup");
    }

    private void ApplyColour_Click(object sender, RoutedEventArgs e)
    {
        if (QuickRecolourModePicker.SelectedItem is not ComboBoxItem modeItem
            || QuickEditScopePicker.SelectedItem is not ComboBoxItem scopeItem
            || !Enum.TryParse<SkinImageTransformMode>(modeItem.Tag?.ToString(), out var mode)
            || !Enum.TryParse<SkinImageTransformScope>(scopeItem.Tag?.ToString(), out var scope))
        {
            setStatus("Transform is incomplete", "Choose a recolour mode and edit scope.", "Danger");
            return;
        }
        transformSelected(mode, scope);
    }

    private void QuickColourBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (QuickColourSwatch is not null
            && tryParseColour(QuickColourBox.Text, out var colour))
        {
            QuickColourSwatch.Background = new SolidColorBrush(colour);
        }
    }

    private void PickColour_Click(object sender, RoutedEventArgs e)
    {
        activeRendererColourTarget = null;
        activeSkinIniColourBox = null;
        var initial = tryParseColour(QuickColourBox.Text, out var parsed)
            ? parsed
            : WpfColor.FromRgb(0xFF, 0xFF, 0xFF);
        SkinStudioColorPicker.Open(
            colourHex(initial),
            "Element colour",
            "Choose a colour visually or enter a hex value. Apply it with Colorize or Tint.",
            allowOpacity: false);
        useStandardColourPopupPlacement(
            sender as FrameworkElement ?? QuickColourSwatch);
        SkinStudioColorPickerPopup.IsOpen = true;
    }

    private void SkinStudioColorPicker_ColourChanged(string value)
    {
        if (!tryParseColour(value, out var colour))
            return;
        if (activeRendererColourTarget is { } rendererTarget)
        {
            applyRendererColour(rendererTarget, colour);
            return;
        }
        if (activeSkinIniColourBox is { } skinIniColourBox)
        {
            skinIniColourBox.Text = activeSkinIniColourType == SkinIniValueType.Rgba
                ? $"{colour.R},{colour.G},{colour.B},{colour.A}"
                : $"{colour.R},{colour.G},{colour.B}";
            return;
        }
        QuickColourBox.Text = colourHex(colour);
    }

    private void applyRendererColour(
        SkinStudioRendererColourTarget target,
        WpfColor colour)
    {
        queueRendererColourPreview(target, colour);
        if (integratedSkinIni is null)
            return;
        var setting = rendererColourSetting(target);
        var text = $"{colour.R},{colour.G},{colour.B}";
        integratedSkinIni.SetValue("Colours", setting.Key, text);

        if (skinIniInputs.TryGetValue(
                skinIniInputKey("Colours", setting.Key),
                out var input))
        {
            suppressSkinIniEvents = true;
            input.Active.IsChecked = true;
            input.Value.IsEnabled = true;
            input.Value.Text = text;
            if (input.Preview is not null)
                updateSkinIniColourPreview(input.Preview, text);
            suppressSkinIniEvents = false;
        }

        syncSkinIniRawText();
        if (!skinIniSaveTimer.IsEnabled)
            skinIniSaveTimer.Start();
        SkinIniLiveStatus.Text =
            $"{setting.Label} {text} · saving and refreshing lazer live…";
    }

    private void SaveColourSwatch_Click(object sender, RoutedEventArgs e)
    {
        if (!tryParseColour(QuickColourBox.Text, out var colour))
        {
            setStatus("Colour is invalid", "Enter a six-digit hex colour such as #FFB7D5.", "Danger");
            return;
        }
        var hex = colourHex(colour);
        settings.Update(value =>
        {
            if (!value.SkinEditor.CustomSwatches.Contains(
                    hex,
                    StringComparer.OrdinalIgnoreCase))
            {
                value.SkinEditor.CustomSwatches.Add(hex);
            }
        });
        buildColourSwatches();
        setStatus("Colour saved", $"Added {hex} to your swatches.", "Success");
    }

    private void buildColourSwatches()
    {
        QuickColourSwatchPanel.Children.Clear();
        foreach (var colour in builtInColourSwatches)
            QuickColourSwatchPanel.Children.Add(createColourSwatch(colour, custom: false, null));
        foreach (var hex in settings.Current.SkinEditor.CustomSwatches.ToArray())
        {
            if (tryParseColour(hex, out var colour))
                QuickColourSwatchPanel.Children.Add(createColourSwatch(colour, custom: true, hex));
        }
    }

    private FrameworkElement createColourSwatch(
        WpfColor colour,
        bool custom,
        string? savedHex)
    {
        var button = new Button
        {
            Width = 27,
            Height = 27,
            Margin = new Thickness(0, 0, 5, 5),
            Padding = new Thickness(0),
            Background = new SolidColorBrush(colour),
            ToolTip = custom
                ? $"{savedHex} · right-click to delete"
                : colourHex(colour),
        };
        button.Click += (_, _) => QuickColourBox.Text = colourHex(colour);
        if (custom)
        {
            button.MouseRightButtonUp += (_, args) =>
            {
                settings.Update(value => value.SkinEditor.CustomSwatches.RemoveAll(
                    item => item.Equals(savedHex, StringComparison.OrdinalIgnoreCase)));
                buildColourSwatches();
                args.Handled = true;
            };
        }
        return button;
    }

    private static bool tryParseColour(string? value, out WpfColor colour)
    {
        colour = default;
        if (string.IsNullOrWhiteSpace(value))
            return false;
        try
        {
            colour = (WpfColor)WpfColorConverter.ConvertFromString(value)!;
            return true;
        }
        catch (Exception ex) when (ex is FormatException or NotSupportedException)
        {
            return false;
        }
    }

    private static string colourHex(WpfColor colour) =>
        $"#{colour.R:X2}{colour.G:X2}{colour.B:X2}";

    private void NormalizeAudio_Click(object sender, RoutedEventArgs e) =>
        runWorkspaceAction(workspace.NormalizeSelectedAudio, "Normalized selected audio family");

    private void AdvancedTransform_Click(object sender, RoutedEventArgs e)
    {
        var mode = new ComboBox
        {
            ItemsSource = Enum.GetValues<SkinImageTransformMode>(),
            SelectedItem = SkinImageTransformMode.Tint,
            Margin = new Thickness(0, 0, 0, 8),
        };
        var scope = new ComboBox
        {
            ItemsSource = Enum.GetValues<SkinImageTransformScope>(),
            SelectedItem = SkinImageTransformScope.FullFamily,
            Margin = new Thickness(0, 0, 0, 8),
        };
        var colour = new TextBox { Text = QuickColourBox.Text, Height = 30, Margin = new Thickness(0, 0, 0, 8) };
        var frame = new TextBox { Text = "0", Height = 30, Margin = new Thickness(0, 0, 0, 8) };
        var apply = new Button { Content = "Apply atomic transform", Height = 34 };
        var panel = new StackPanel { Margin = new Thickness(14) };
        panel.Children.Add(new TextBlock { Text = "Transform mode", Margin = new Thickness(0, 0, 0, 4) });
        panel.Children.Add(mode);
        panel.Children.Add(new TextBlock { Text = "File-family scope", Margin = new Thickness(0, 0, 0, 4) });
        panel.Children.Add(scope);
        panel.Children.Add(new TextBlock { Text = "Colour", Margin = new Thickness(0, 0, 0, 4) });
        panel.Children.Add(colour);
        panel.Children.Add(new TextBlock { Text = "Animation frame (used only for frame-pair scope)", Margin = new Thickness(0, 0, 0, 4) });
        panel.Children.Add(frame);
        panel.Children.Add(apply);
        var window = new Window
        {
            Title = "Advanced image transform",
            Owner = Window.GetWindow(this),
            Width = 430,
            Height = 390,
            Content = panel,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            ResizeMode = ResizeMode.NoResize,
        };
        apply.Click += (_, _) =>
        {
            try
            {
                var parsed = (WpfColor)WpfColorConverter.ConvertFromString(colour.Text)!;
                var selectedScope = (SkinImageTransformScope)scope.SelectedItem;
                int? selectedFrame = selectedScope == SkinImageTransformScope.AnimationFramePair
                    ? int.Parse(frame.Text, System.Globalization.CultureInfo.InvariantCulture)
                    : null;
                runWorkspaceAction(
                    () => workspace.TransformSelected(
                        (SkinImageTransformMode)mode.SelectedItem,
                        new SkinRgb(parsed.R, parsed.G, parsed.B),
                        selectedScope,
                        selectedFrame),
                    "Applied scoped image transform");
                window.Close();
            }
            catch (Exception ex)
            {
                MessageBox.Show(window, ex.Message, "Transform stopped", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        };
        window.ShowDialog();
    }

    private void ReviewChanges_Click(object sender, RoutedEventArgs e)
    {
        var list = new ListBox
        {
            ItemsSource = workspace.CurrentDraft.Changes,
            DisplayMemberPath = nameof(SkinDraftFileChange.Filename),
            MinHeight = 280,
        };
        var discard = new Button
        {
            Content = "Discard selected change",
            Height = 34,
            Margin = new Thickness(0, 10, 0, 0),
        };
        var panel = new DockPanel { Margin = new Thickness(12) };
        DockPanel.SetDock(discard, Dock.Bottom);
        panel.Children.Add(discard);
        panel.Children.Add(list);
        var window = new Window
        {
            Title = $"Review changes — {workspace.CurrentDraft.Name}",
            Owner = Window.GetWindow(this),
            Width = 620,
            Height = 500,
            Content = panel,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
        };
        discard.Click += (_, _) =>
        {
            if (list.SelectedItem is not SkinDraftFileChange change)
                return;
            runWorkspaceAction(() => workspace.DiscardChange(change.Filename), $"Discarded {change.Filename}");
            list.ItemsSource = workspace.CurrentDraft.Changes;
            if (workspace.CurrentDraft.Changes.Count == 0)
                window.Close();
        };
        window.ShowDialog();
    }

    private void transformSelected(
        SkinImageTransformMode mode,
        SkinImageTransformScope scope = SkinImageTransformScope.FullFamily)
    {
        try
        {
            var colour = (WpfColor)WpfColorConverter.ConvertFromString(QuickColourBox.Text)!;
            var animationFrame = scope == SkinImageTransformScope.AnimationFramePair
                ? workspace.SelectedFamily
                    .Select(asset => asset.AnimationFrame)
                    .FirstOrDefault(frame => frame is not null)
                : null;
            runWorkspaceAction(
                () => workspace.TransformSelected(
                    mode,
                    new SkinRgb(colour.R, colour.G, colour.B),
                    scope,
                    animationFrame),
                mode == SkinImageTransformMode.Colorize ? "Applied solid colour" : "Applied luminance-preserving tint");
        }
        catch (Exception ex)
        {
            setStatus("Invalid colour", ex.Message, "Danger");
        }
    }

    private async void Audition_Click(object sender, RoutedEventArgs e)
    {
        if (selectedItem is null)
            return;
        await sendRendererAsync(new SkinStudioRendererRequest
        {
            Command = SkinStudioRendererCommandKind.AuditionSample,
            Component = selectedItem.ComponentName,
        });
    }

    private async void StopAudio_Click(object sender, RoutedEventArgs e) =>
        await sendRendererAsync(new SkinStudioRendererRequest { Command = SkinStudioRendererCommandKind.StopAudio });

    private async void PlayPause_Click(object sender, RoutedEventArgs e)
    {
        var response = await sendRendererAsync(new SkinStudioRendererRequest
        {
            Command = rendererPlaying ? SkinStudioRendererCommandKind.Pause : SkinStudioRendererCommandKind.Play,
        });
        if (response?.Accepted == true
            && ScenePicker.SelectedItem is SkinStudioPreviewScene.Cursor
                or SkinStudioPreviewScene.Spinner)
            setAutoMotionChecked(response.Playing);
    }

    private async void Restart_Click(object sender, RoutedEventArgs e)
    {
        suppressSceneSelection = true;
        ScenePicker.SelectedItem = SkinStudioPreviewScene.Showcase;
        suppressSceneSelection = false;
        updateAutoMotionPresentation(SkinStudioPreviewScene.Showcase);
        await sendRendererAsync(new SkinStudioRendererRequest { Command = SkinStudioRendererCommandKind.Restart });
    }

    private async void ScenePicker_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!initialized || suppressSceneSelection
            || ScenePicker.SelectedItem is not SkinStudioPreviewScene scene)
            return;
        await seekRendererAsync(scene);
    }

    private async void AutoMotionToggle_Changed(object sender, RoutedEventArgs e)
    {
        if (!initialized || suppressAutoMotionChange || renderer is null)
            return;
        var enabled = AutoMotionToggle.IsChecked == true;
        var response = await sendRendererAsync(new SkinStudioRendererRequest
        {
            Command = SkinStudioRendererCommandKind.SetAutoMotion,
            Active = enabled,
        });
        if (response?.Accepted != true)
            setAutoMotionChecked(!enabled);
    }

    private void ExportSelected_Click(object sender, RoutedEventArgs e)
    {
        using var dialog = new System.Windows.Forms.FolderBrowserDialog();
        if (dialog.ShowDialog() == System.Windows.Forms.DialogResult.OK)
            runWorkspaceAction(() => workspace.ExportSelected(dialog.SelectedPath), "Exported selected family");
    }

    private void ExportDraft_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new SaveFileDialog
        {
            Filter = "osu! skin (*.osk)|*.osk",
            FileName = sanitizeFilename(workspace.CurrentDraft.Name) + ".osk",
        };
        if (dialog.ShowDialog(Window.GetWindow(this)) != true)
            return;
        try
        {
            workspace.Export(dialog.FileName);
            setStatus("Export complete", dialog.FileName, "Success");
        }
        catch (Exception ex)
        {
            setStatus("Export failed", ex.Message, "Danger");
        }
    }

    private void Backup_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var backup = workspace.CreateBackup("Manual hybrid Studio backup");
            setStatus("Backup verified", backup.ArchivePath, "Success");
        }
        catch (Exception ex)
        {
            setStatus("Backup failed", ex.Message, "Danger");
        }
    }

    private void EditSkinIni_Click(object sender, RoutedEventArgs e) => showSkinIniMode();

    private void ElementsMode_Click(object sender, RoutedEventArgs e) => showElementsMode();

    private void showElementsMode()
    {
        saveIntegratedSkinIni();
        activeRendererColourTarget = null;
        activeSkinIniColourBox = null;
        SkinStudioColorPickerPopup.IsOpen = false;
        ElementInspectorView.Visibility = Visibility.Visible;
        SkinIniInspectorView.Visibility = Visibility.Collapsed;
        ElementsModeButton.IsChecked = true;
        SkinIniModeButton.IsChecked = false;
    }

    private void showSkinIniMode()
    {
        saveIntegratedSkinIni();
        var text = workspace.ReadSkinIni();
        integratedSkinIni = SkinIniDocument.ParseText(text);
        integratedSkinIniLastSaved = text;

        suppressSkinIniEvents = true;
        SkinIniRawText.Text = text;
        var order = new[]
        {
            SkinIniVisualGroup.Identity,
            SkinIniVisualGroup.Combo,
            SkinIniVisualGroup.HitObjects,
            SkinIniVisualGroup.Slider,
            SkinIniVisualGroup.Cursor,
            SkinIniVisualGroup.Spinner,
            SkinIniVisualGroup.Interface,
            SkinIniVisualGroup.Fonts,
            SkinIniVisualGroup.Animation,
            SkinIniVisualGroup.Catch,
        };
        var definitions = SkinIniSchema.Sections()
            .SelectMany(section => section.Keys)
            .GroupBy(definition => SkinIniRichEditor.Describe(definition).Group)
            .ToDictionary(group => group.Key, group => (IReadOnlyList<SkinIniKeyDefinition>)group.ToArray());
        var choices = order
            .Where(definitions.ContainsKey)
            .Select(group => new SkinStudioIniGroupChoice
            {
                Group = group,
                DisplayName = SkinIniRichEditor.DisplayName(group),
                Definitions = definitions[group],
            })
            .ToArray();
        SkinIniGroupPicker.ItemsSource = choices;
        SkinIniGroupPicker.SelectedItem = choices.FirstOrDefault(choice => choice.Group == SkinIniVisualGroup.Combo)
                                          ?? choices.FirstOrDefault();
        suppressSkinIniEvents = false;

        ElementInspectorView.Visibility = Visibility.Collapsed;
        SkinIniInspectorView.Visibility = Visibility.Visible;
        ElementsModeButton.IsChecked = false;
        SkinIniModeButton.IsChecked = true;
        showSkinIniFormMode();
        rebuildSkinIniGroup();
        SkinIniLiveStatus.Text = "Edits are saved to the isolated draft after a short pause.";
    }

    private void SkinIniGroupPicker_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (suppressSkinIniEvents)
            return;
        rebuildSkinIniGroup();
        if (SkinIniGroupPicker.SelectedItem is not SkinStudioIniGroupChoice choice || renderer is null)
            return;
        var scene = choice.Group switch
        {
            SkinIniVisualGroup.Combo or SkinIniVisualGroup.HitObjects or SkinIniVisualGroup.Fonts => SkinStudioPreviewScene.Circles,
            SkinIniVisualGroup.Slider => SkinStudioPreviewScene.Sliders,
            SkinIniVisualGroup.Cursor => SkinStudioPreviewScene.Cursor,
            SkinIniVisualGroup.Spinner => SkinStudioPreviewScene.Spinner,
            SkinIniVisualGroup.Interface => SkinStudioPreviewScene.Hud,
            _ => (SkinStudioPreviewScene?)null,
        };
        if (scene is { } selectedScene)
            _ = seekRendererAsync(selectedScene);
    }

    private void rebuildSkinIniGroup()
    {
        SkinIniFormPanel.Children.Clear();
        skinIniInputs.Clear();
        if (integratedSkinIni is null
            || SkinIniGroupPicker.SelectedItem is not SkinStudioIniGroupChoice choice)
            return;

        var heading = new TextBlock
        {
            Text = choice.DisplayName,
            FontSize = 15,
            FontWeight = FontWeights.SemiBold,
            Margin = new Thickness(0, 0, 0, 3),
        };
        SkinIniFormPanel.Children.Add(heading);
        var groupHelp = new TextBlock
        {
            Text = groupDescription(choice.Group),
            TextWrapping = TextWrapping.Wrap,
            FontSize = 10,
            Margin = new Thickness(0, 0, 0, 9),
        };
        groupHelp.SetResourceReference(TextBlock.ForegroundProperty, "Brush.TextMuted");
        SkinIniFormPanel.Children.Add(groupHelp);

        foreach (var definition in choice.Definitions)
            SkinIniFormPanel.Children.Add(createSkinIniSetting(definition));
        SkinIniFormPanel.Children.Add(new TextBlock
        {
            Text = "Raw mode keeps comments, unknown keys, and repeated sections intact.",
            TextWrapping = TextWrapping.Wrap,
            FontSize = 9,
            Margin = new Thickness(1, 5, 1, 0),
        });
        _ = Dispatcher.BeginInvoke(() => SkinIniFormScroll.ScrollToTop(), DispatcherPriority.Loaded);
    }

    private FrameworkElement createSkinIniSetting(SkinIniKeyDefinition definition)
    {
        var active = new CheckBox
        {
            Content = definition.Label,
            IsChecked = integratedSkinIni!.HasValue(definition.Section, definition.Key),
            FontWeight = FontWeights.SemiBold,
            VerticalAlignment = VerticalAlignment.Center,
        };
        var value = new TextBox
        {
            Text = integratedSkinIni.GetValue(definition.Section, definition.Key) ?? definition.DefaultValue,
            IsEnabled = active.IsChecked == true,
            Height = 30,
            VerticalContentAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 6, 0, 0),
        };
        var input = new Grid();
        input.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        input.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        input.Children.Add(value);

        Border? colourPreview = null;
        if (definition.Type is SkinIniValueType.Rgb or SkinIniValueType.Rgba)
        {
            colourPreview = new Border
            {
                Width = 24,
                Height = 20,
                CornerRadius = new CornerRadius(4),
                BorderThickness = new Thickness(1),
                Margin = new Thickness(0),
            };
            colourPreview.SetResourceReference(Border.BorderBrushProperty, "Brush.BorderSubtle");
            updateSkinIniColourPreview(colourPreview, value.Text);
            var pick = new Button
            {
                Content = colourPreview,
                Width = 38,
                Height = 30,
                Margin = new Thickness(6, 6, 0, 0),
                IsEnabled = active.IsChecked == true,
                ToolTip = $"Choose {definition.Label.ToLowerInvariant()}",
            };
            pick.SetResourceReference(StyleProperty, "SkinAction");
            pick.Click += (_, _) => openSkinIniColourPicker(value, definition.Type, pick);
            active.Checked += (_, _) => pick.IsEnabled = true;
            active.Unchecked += (_, _) => pick.IsEnabled = false;
            Grid.SetColumn(pick, 1);
            input.Children.Add(pick);
        }

        var metadata = SkinIniRichEditor.Describe(definition);
        var help = new TextBlock
        {
            Text = metadata.Help,
            TextWrapping = TextWrapping.Wrap,
            FontSize = 9,
            Margin = new Thickness(0, 5, 0, 0),
        };
        help.SetResourceReference(TextBlock.ForegroundProperty, "Brush.TextMuted");
        var panel = new StackPanel();
        panel.Children.Add(active);
        panel.Children.Add(input);
        panel.Children.Add(help);
        var host = new Border
        {
            Child = panel,
            Padding = new Thickness(9),
            Margin = new Thickness(0, 0, 0, 7),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(6),
        };
        host.SetResourceReference(Border.BorderBrushProperty, "Brush.BorderSubtle");
        host.SetResourceReference(Border.BackgroundProperty, "Brush.ControlBackground");

        void commitValue()
        {
            if (suppressSkinIniEvents || active.IsChecked != true || integratedSkinIni is null)
                return;
            if (!SkinIniDocument.TryValidate(definition, value.Text, out var error))
            {
                value.SetResourceReference(Control.BorderBrushProperty, "Brush.Danger");
                value.ToolTip = error;
                skinIniSaveTimer.Stop();
                SkinIniLiveStatus.Text = $"{definition.Label}: {error}";
                return;
            }
            value.SetResourceReference(Control.BorderBrushProperty, "Brush.BorderSubtle");
            value.ToolTip = null;
            integratedSkinIni.SetValue(definition.Section, definition.Key, value.Text);
            if (colourPreview is not null)
                updateSkinIniColourPreview(colourPreview, value.Text);
            syncSkinIniRawText();
            scheduleSkinIniSave();
        }

        active.Checked += (_, _) =>
        {
            value.IsEnabled = true;
            commitValue();
        };
        active.Unchecked += (_, _) =>
        {
            value.IsEnabled = false;
            if (suppressSkinIniEvents || integratedSkinIni is null)
                return;
            integratedSkinIni.RemoveValue(definition.Section, definition.Key);
            syncSkinIniRawText();
            scheduleSkinIniSave();
        };
        value.TextChanged += (_, _) => commitValue();
        skinIniInputs[skinIniInputKey(definition.Section, definition.Key)] =
            (active, value, colourPreview);
        return host;
    }

    private static string skinIniInputKey(string section, string key) =>
        $"{section}\u001f{key}";

    private static string groupDescription(SkinIniVisualGroup group) => group switch
    {
        SkinIniVisualGroup.Combo => "Combo colours and the hit-object behaviour that uses them.",
        SkinIniVisualGroup.Slider => "Slider colours, ball tinting, and reverse behaviour together.",
        SkinIniVisualGroup.Cursor => "Cursor origin, expansion, rotation, and trail behaviour.",
        SkinIniVisualGroup.HitObjects => "Hit-circle layering and hitsound behaviour.",
        SkinIniVisualGroup.Spinner => "Spinner colours, visibility, and sound response.",
        SkinIniVisualGroup.Interface => "Menu, song-select, and input-overlay colours.",
        SkinIniVisualGroup.Fonts => "Prefixes and spacing for hit-circle, combo, and score numbers.",
        SkinIniVisualGroup.Animation => "Playback rate for animated skin assets.",
        SkinIniVisualGroup.Catch => "Catch the Beat hyperdash colours.",
        _ => "Skin identity and version information.",
    };

    private void SkinIniFormMode_Click(object sender, RoutedEventArgs e)
    {
        if (integratedSkinIni is null)
            return;
        showSkinIniFormMode();
        rebuildSkinIniGroup();
    }

    private void showSkinIniFormMode()
    {
        SkinIniFormModeButton.IsChecked = true;
        SkinIniRawModeButton.IsChecked = false;
        SkinIniFormScroll.Visibility = Visibility.Visible;
        SkinIniRawText.Visibility = Visibility.Collapsed;
    }

    private void SkinIniRawMode_Click(object sender, RoutedEventArgs e)
    {
        if (integratedSkinIni is null)
            return;
        syncSkinIniRawText();
        SkinIniFormModeButton.IsChecked = false;
        SkinIniRawModeButton.IsChecked = true;
        SkinIniFormScroll.Visibility = Visibility.Collapsed;
        SkinIniRawText.Visibility = Visibility.Visible;
        SkinIniRawText.Focus();
    }

    private void SkinIniRawText_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (suppressSkinIniEvents || integratedSkinIni is null)
            return;
        integratedSkinIni = integratedSkinIni.WithText(SkinIniRawText.Text);
        scheduleSkinIniSave();
    }

    private void syncSkinIniRawText()
    {
        if (integratedSkinIni is null)
            return;
        var text = integratedSkinIni.ToText();
        if (SkinIniRawText.Text == text)
            return;
        suppressSkinIniEvents = true;
        SkinIniRawText.Text = text;
        suppressSkinIniEvents = false;
    }

    private void scheduleSkinIniSave()
    {
        skinIniSaveTimer.Stop();
        skinIniSaveTimer.Start();
        SkinIniLiveStatus.Text = "Saving and refreshing the lazer preview…";
    }

    private void saveIntegratedSkinIni()
    {
        skinIniSaveTimer.Stop();
        if (integratedSkinIni is null)
            return;
        var text = integratedSkinIni.ToText();
        if (text == integratedSkinIniLastSaved)
            return;
        try
        {
            workspace.SaveSkinIni(text);
            integratedSkinIniLastSaved = text;
            if (activeRendererColourTarget is not null)
            {
                SkinIniLiveStatus.Text =
                    $"Saved revision {workspace.CurrentRevision}; live preview is current.";
            }
            else
            {
                queueRendererReload();
                SkinIniLiveStatus.Text =
                    $"Saved revision {workspace.CurrentRevision}; lazer is refreshing.";
            }
        }
        catch (Exception ex)
        {
            SkinIniLiveStatus.Text = $"Could not save: {ex.Message}";
            setStatus("skin.ini edit stopped", ex.Message, "Danger");
        }
    }

    private void openSkinIniColourPicker(TextBox value, SkinIniValueType type, FrameworkElement target)
    {
        activeRendererColourTarget = null;
        activeSkinIniColourBox = value;
        activeSkinIniColourType = type;
        var initial = tryParseIniColour(value.Text, out var parsed)
            ? parsed
            : WpfColor.FromRgb(0xFF, 0xFF, 0xFF);
        SkinStudioColorPicker.Open(
            type == SkinIniValueType.Rgba
                ? $"#{initial.A:X2}{initial.R:X2}{initial.G:X2}{initial.B:X2}"
                : colourHex(initial),
            "skin.ini colour",
            "Choose the colour visually. It is stored in osu!'s comma-separated skin.ini format.",
            allowOpacity: type == SkinIniValueType.Rgba);
        useStandardColourPopupPlacement(target);
        SkinStudioColorPickerPopup.IsOpen = true;
    }

    private static bool tryParseIniColour(string? value, out WpfColor colour)
    {
        colour = default;
        var parts = value?.Split(',', StringSplitOptions.TrimEntries);
        if (parts is not { Length: 3 or 4 }
            || parts.Any(part => !byte.TryParse(part, out _)))
            return false;
        var channels = parts.Select(byte.Parse).ToArray();
        colour = channels.Length == 4
            ? WpfColor.FromArgb(channels[3], channels[0], channels[1], channels[2])
            : WpfColor.FromRgb(channels[0], channels[1], channels[2]);
        return true;
    }

    private static void updateSkinIniColourPreview(Border preview, string value)
    {
        preview.Background = new SolidColorBrush(
            tryParseIniColour(value, out var colour) ? colour : WpfColor.FromArgb(0, 0, 0, 0));
    }

    private void EditSkinIniLegacyPopup_UNUSED(object sender, RoutedEventArgs e)
    {
        var editor = new TextBox
        {
            AcceptsReturn = true,
            AcceptsTab = true,
            Text = workspace.ReadSkinIni(),
            FontFamily = new System.Windows.Media.FontFamily("Consolas"),
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
        };
        var save = new Button { Content = "Save and close", Height = 34, Margin = new Thickness(0, 8, 0, 0) };
        var panel = new DockPanel { Margin = new Thickness(12) };
        DockPanel.SetDock(save, Dock.Bottom);
        panel.Children.Add(save);
        panel.Children.Add(editor);
        var window = new Window
        {
            Title = $"skin.ini — {workspace.CurrentDraft.Name}",
            Owner = Window.GetWindow(this),
            Width = 760,
            Height = 680,
            Content = panel,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
        };
        var lastSavedText = editor.Text;
        var liveSaveTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(450) };
        void saveLiveEdit()
        {
            liveSaveTimer.Stop();
            if (editor.Text == lastSavedText)
                return;
            var next = editor.Text;
            runWorkspaceAction(() => workspace.SaveSkinIni(next), "Updated skin.ini live");
            lastSavedText = next;
        }
        liveSaveTimer.Tick += (_, _) => saveLiveEdit();
        editor.TextChanged += (_, _) =>
        {
            liveSaveTimer.Stop();
            liveSaveTimer.Start();
        };
        save.Click += (_, _) =>
        {
            saveLiveEdit();
            window.Close();
        };
        window.Closed += (_, _) => saveLiveEdit();
        window.ShowDialog();
    }

    private async void BrowseExtras_Click(object sender, RoutedEventArgs e) =>
        await openExtrasWorkspaceAsync();

    private async Task openExtrasWorkspaceAsync()
    {
        if (extrasPicker is not null)
            return;
        try
        {
            var materialized = new Dictionary<string, byte[]>(
                workspace.Materialize(),
                StringComparer.OrdinalIgnoreCase);
            if (!materialized.ContainsKey("skin.ini"))
                materialized["skin.ini"] = System.Text.Encoding.UTF8.GetBytes(workspace.ReadSkinIni());
            var source = new SkinExtrasCurrentSkinSource(
                workspace.CurrentDraft.Name,
                () => workspace.Materialize().Keys.ToArray(),
                (filename, _) => Task.FromResult(
                    workspace.Materialize().TryGetValue(filename, out var bytes) ? bytes : null),
                () => SkinIniDocument.ParseText(workspace.ReadSkinIni()),
                () => workspace.CurrentDraft.Changes.Count > 0);
            var picker = new SkinExtrasPickerWindow(
                Window.GetWindow(this),
                CategoryPicker.SelectedItem is SkinStudioElementCategory category ? category.Title : "Hit objects",
                modeVisibility: extrasModeVisibility(),
                lazerFilterChanged: persistLazerExtrasFilter,
                currentIni: SkinIniDocument.Parse(materialized["skin.ini"]),
                currentSkinSource: source,
                stageSelection: selection =>
                {
                    runWorkspaceAction(
                        () => workspace.ApplyExtrasSelection(selection),
                        $"Applied Extras/{selection.Manifest.DisplayName}");
                    return Task.FromResult(true);
                });
            extrasPicker = picker;
            extrasPreviewWorkspace = new SkinStudioWorkspaceController(workspace.WorkspacePath);
            var previewBase = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);
            if (materialized.TryGetValue("skin.ini", out var previewIni))
                previewBase["skin.ini"] = previewIni;
            extrasPreviewWorkspace.InitializeExtrasPreview(previewBase);
            extrasPreviewPackKey = null;
            pendingRendererColourPreview = null;
            picker.PreviewPackChanged += ExtrasPicker_PreviewPackChanged;
            picker.PreviewTintChanged += ExtrasPicker_PreviewTintChanged;
            picker.PreviewMotionChanged += ExtrasPicker_PreviewMotionChanged;
            picker.PreviewSmoothTrailChanged +=
                ExtrasPicker_PreviewSmoothTrailChanged;
            picker.ShowRendererTarget();
            rendererTarget = picker.RendererTarget;
            picker.CloseRequested += (_, _) => closeExtrasWorkspace();
            ExtrasHost.Content = picker;
            ExtrasWorkspace.Visibility = Visibility.Visible;
            _ = Dispatcher.BeginInvoke(updateRendererPlacement, DispatcherPriority.Loaded);
            picker.Focus();
            try
            {
                var packCount = await picker.EnsureLibraryLoadedAsync();
                if (ReferenceEquals(extrasPicker, picker))
                {
                    setStatus(
                        "Extras library loaded",
                        packCount == 0
                            ? $"No packs were found in {AppPaths.SkinExtrasDir}."
                            : $"{packCount} reusable pack(s) are ready.",
                        packCount == 0 ? "TextMuted" : "Success");
                }
            }
            catch (Exception ex)
            {
                if (ReferenceEquals(extrasPicker, picker))
                {
                    setStatus(
                        "Extras catalog failed",
                        ex.GetBaseException().Message,
                        "Danger");
                }
            }
        }
        catch (Exception ex)
        {
            setStatus("Could not open Extras", ex.Message, "Danger");
            extrasPicker = null;
            extrasPreviewWorkspace = null;
            rendererTarget = MainRendererMount;
        }
    }

    private void closeExtrasWorkspace()
    {
        var picker = extrasPicker;
        if (picker is null)
            return;
        picker.HideRendererTarget();
        extrasPreviewCancellation?.Cancel();
        extrasPreviewCancellation?.Dispose();
        extrasPreviewCancellation = null;
        extrasPreviewPackKey = null;
        pendingRendererColourPreview = null;
        extrasPreviewWorkspace = null;
        rendererTarget = MainRendererMount;
        ExtrasHost.Content = null;
        ExtrasWorkspace.Visibility = Visibility.Collapsed;
        extrasPicker = null;
        picker.Dispose();
        updateRendererPlacement();
        _ = restoreMainRendererAsync();
    }

    private async void ExtrasPicker_PreviewPackChanged(
        object? sender,
        SkinExtrasPreviewPackChangedEventArgs e)
    {
        var previewWorkspace = extrasPreviewWorkspace;
        if (previewWorkspace is null || renderer is null)
            return;
        var packKey = $"{e.Pack.DirectoryPath}|{e.Pack.Manifest.Fingerprint}|smooth:{e.SmoothTrail}";
        if (string.Equals(extrasPreviewPackKey, packKey, StringComparison.OrdinalIgnoreCase))
            return;
        extrasPreviewCancellation?.Cancel();
        extrasPreviewCancellation?.Dispose();
        var cancellation = new CancellationTokenSource();
        extrasPreviewCancellation = cancellation;
        try
        {
            var cursorPack = SkinCursorMiddlePolicy.IsCursorFamily(
                e.Pack.Manifest.FamilyId);
            if (!cursorPack)
                await Task.Delay(80, cancellation.Token);
            await extrasPreviewGate.WaitAsync(cancellation.Token);
            try
            {
                await Task.Run(
                    () => previewWorkspace.PrepareExtrasPreview(
                        e.Pack,
                        e.SmoothTrail),
                    cancellation.Token);
                cancellation.Token.ThrowIfCancellationRequested();
                var components = e.Pack.Manifest.Files
                    .Select(file => SkinDraftAssetService.ComponentName(file.TargetFilename))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToArray();
                var scene = SkinStudioExtrasPreview.SceneFor(
                    e.Pack.Manifest.FamilyId,
                    components);
                var response = await renderer.SendAsync(new SkinStudioRendererRequest
                {
                    Command = SkinStudioRendererCommandKind.LoadDraft,
                    DraftId = previewWorkspace.CurrentDraft.DraftId,
                    DraftRevision = previewWorkspace.CurrentRevision,
                    Scene = scene,
                    Component = e.Pack.Manifest.FamilyId,
                    Components = components,
                }, cancellation.Token);
                if (!response.Accepted)
                    throw new InvalidOperationException(response.Message);
                cancellation.Token.ThrowIfCancellationRequested();
                extrasPreviewPackKey = packKey;
            }
            finally
            {
                extrasPreviewGate.Release();
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            extrasPreviewPackKey = null;
            setStatus("Extras preview kept its last good render", ex.Message, "Danger");
        }
    }

    private void ExtrasPicker_PreviewTintChanged(
        object? sender,
        SkinExtrasPreviewTintChangedEventArgs e)
    {
        if (!ReferenceEquals(sender, extrasPicker) || renderer is null)
            return;
        queueRendererColourPreview(
            SkinStudioRendererColourTarget.ElementTint,
            e.Colour,
            e.ElementKey);
    }

    private async void ExtrasPicker_PreviewMotionChanged(
        object? sender,
        SkinExtrasPreviewMotionChangedEventArgs e)
    {
        if (!ReferenceEquals(sender, extrasPicker) || renderer is null)
            return;
        try
        {
            var response = await renderer.SendAsync(new SkinStudioRendererRequest
            {
                Command = SkinStudioRendererCommandKind.SetAutoMotion,
                Active = e.Active,
            });
            if (!response.Accepted)
                throw new InvalidOperationException(response.Message);
        }
        catch (Exception ex)
        {
            setStatus("Cursor motion was not changed", ex.Message, "Danger");
        }
    }

    private async void ExtrasPicker_PreviewSmoothTrailChanged(
        object? sender,
        SkinExtrasPreviewSmoothTrailChangedEventArgs e)
    {
        if (!ReferenceEquals(sender, extrasPicker) || renderer is null)
            return;
        try
        {
            var response = await renderer.SendAsync(new SkinStudioRendererRequest
            {
                Command = SkinStudioRendererCommandKind.SetSmoothTrail,
                Active = e.Active,
            });
            if (!response.Accepted)
                throw new InvalidOperationException(response.Message);
        }
        catch (Exception ex)
        {
            setStatus("Smooth Trail was not changed", ex.Message, "Danger");
        }
    }

    private async Task restoreMainRendererAsync()
    {
        if (renderer is null)
            return;
        var response = await sendRendererAsync(new SkinStudioRendererRequest
        {
            Command = SkinStudioRendererCommandKind.LoadDraft,
            DraftId = workspace.CurrentDraft.DraftId,
            DraftRevision = workspace.CurrentRevision,
        }, reportFailure: false);
        if (response?.Accepted == true)
        {
            if (selectedItem is { } selected)
                await selectPreviewTargetAsync(selected.SemanticPreview);
            else
                await seekRendererAsync(SkinStudioPreviewScene.Showcase);
        }
    }

    private async void Publish_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var playerRoot = string.IsNullOrWhiteSpace(settings.Current.SkinEditor.LazerRootOverride)
                ? LazerStorage.GetRoot()
                : settings.Current.SkinEditor.LazerRootOverride;
            if (string.IsNullOrWhiteSpace(playerRoot))
                throw new InvalidOperationException("No osu!lazer player root was detected.");
            var idle = new ClosedLazerIdleProbe().Probe(playerRoot);
            if (!idle.IsProvenIdle)
                throw new InvalidOperationException($"Close osu!lazer before publishing: {idle.Detail}");

            var draft = workspace.CurrentDraft;
            var expected = workspace.Materialize();
            var queue = Path.Combine(workspace.WorkspacePath, "publish-queue");
            var archive = workspace.Export(Path.Combine(
                queue,
                $"{sanitizeFilename(draft.Name)}-{DateTimeOffset.UtcNow:yyyyMMdd-HHmmss}.osk"));
            var staging = Path.Combine(queue, "import-staging", $"{Guid.NewGuid():N}.osk");
            Directory.CreateDirectory(Path.GetDirectoryName(staging)!);
            File.Copy(archive, staging);
            setStatus("Preparing publish", "Creating and verifying the lazer catalog backup…", "TextMuted");

            var preparation = await Task.Run(() =>
            {
                var realm = new LazerSkinRealmService();
                var before = realm.LoadCatalog(playerRoot);
                var backup = new LazerCatalogBackupService().CreateVerified(
                    playerRoot,
                    Path.Combine(workspace.WorkspacePath, "real-lazer-backups"),
                    "before-publish");
                return (Realm: realm, Before: before, Backup: backup);
            });
            Process.Start(new ProcessStartInfo(staging) { UseShellExecute = true });
            var imported = await new LazerSkinPublishVerificationService(preparation.Realm)
                .WaitForImportAsync(
                    playerRoot,
                    preparation.Before.Skins.Select(skin => skin.Id).ToHashSet(),
                    draft.Name,
                    draft.Creator,
                    expected,
                    TimeSpan.FromSeconds(120));
            try { File.Delete(staging); } catch { }
            reloadService?.RequestReload(playerRoot, imported.SkinId);
            setStatus(
                "Publish verified",
                $"{imported.Name} · {imported.FileCount} files · backup {preparation.Backup.DirectoryPath}",
                "Success");
        }
        catch (Exception ex)
        {
            setStatus("Publish stopped", ex.Message, "Danger");
        }
    }

    private void LiveSyncPermission_Changed(object sender, RoutedEventArgs e)
    {
        if (LiveSyncPermission.IsChecked == true)
        {
            setStatus(
                "Lazer live edit enabled",
                "Changes will sync to a separate Kumori Live Preview skin and reload when lazer is focused.",
                "Success");
            scheduleLiveSync(immediate: true);
        }
        else
        {
            liveSyncTimer.Stop();
            setStatus(
                "Lazer live edit paused",
                "Draft editing continues locally; no more changes will be written to the preview skin.",
                "TextMuted");
        }
    }

    private async void LiveSync_Click(object sender, RoutedEventArgs e)
    {
        if (LiveSyncPermission.IsChecked != true)
        {
            LiveSyncPermission.IsChecked = true;
            return;
        }
        liveSyncTimer.Stop();
        await syncLivePreviewAsync(force: true);
    }

    private void scheduleLiveSync(bool immediate = false)
    {
        if (disposed
            || LiveSyncPermission?.IsChecked != true
            || !workspaceChosen)
        {
            return;
        }
        liveSyncTimer.Stop();
        if (immediate)
        {
            _ = syncLivePreviewAsync(force: false);
            return;
        }
        liveSyncTimer.Start();
    }

    private async void LiveSyncTimer_Tick(object? sender, EventArgs e)
    {
        liveSyncTimer.Stop();
        await syncLivePreviewAsync(force: false);
    }

    private async Task syncLivePreviewAsync(bool force)
    {
        if (disposed || LiveSyncPermission.IsChecked != true)
            return;

        await liveSyncGate.WaitAsync();
        try
        {
            if (disposed || LiveSyncPermission.IsChecked != true)
                return;
            var draftId = workspace.CurrentDraft.DraftId;
            var revision = workspace.CurrentRevision;
            if (!force
                && lastLiveSyncedDraftId == draftId
                && lastLiveSyncedRevision == revision)
            {
                return;
            }

            var playerRoot = string.IsNullOrWhiteSpace(settings.Current.SkinEditor.LazerRootOverride)
                ? LazerStorage.GetRoot()
                : settings.Current.SkinEditor.LazerRootOverride;
            if (string.IsNullOrWhiteSpace(playerRoot))
                throw new InvalidOperationException("No osu!lazer player root was detected.");
            setStatus(
                "Updating lazer live preview",
                "Synchronizing the disposable preview copy and verifying its backup…",
                "TextMuted");
            var result = await Task.Run(() => new LivePreviewSyncService(
                    new SkinDraftWorkspaceService(workspace.WorkspacePath),
                    new LazerLivePreviewStore(),
                    new ClosedLazerIdleProbe(),
                    Path.Combine(workspace.WorkspacePath, "real-lazer-backups"))
                .Sync(
                    draftId,
                    playerRoot,
                    liveSyncPermission: true,
                    allowWhilePlayerRunning: true));
            lastLiveSyncedDraftId = draftId;
            lastLiveSyncedRevision = revision;
            reloadService?.RequestReload(
                playerRoot,
                result.SkinId,
                reload =>
                {
                    if (LiveSyncPermission.IsChecked == true
                        && workspace.CurrentDraft.DraftId == draftId)
                    {
                        setStatus(
                            "Lazer live preview synchronized",
                            $"{result.SkinName} · {result.ChangedFiles} changed files · {reload.Message}",
                            reload.Status == LazerSkinReloadStatus.ManualReloadRequired
                                ? "Danger"
                                : "Success");
                    }
                });
            setStatus(
                "Lazer live preview synchronized",
                result.Created
                    ? $"Select '{result.SkinName}' once in lazer; following edits will reload automatically."
                    : $"{result.SkinName} · {result.ChangedFiles} changed files · reload queued",
                "Success");

            if (workspace.CurrentDraft.DraftId != draftId
                || workspace.CurrentRevision != revision)
            {
                scheduleLiveSync();
            }
        }
        catch (Exception ex)
        {
            setStatus("Lazer live edit stopped", ex.Message, "Danger");
        }
        finally
        {
            liveSyncGate.Release();
        }
    }

    private async void Retry_Click(object sender, RoutedEventArgs e)
    {
        RetryButton.IsEnabled = false;
        OverlayRetryButton.IsEnabled = false;
        try
        {
            renderer = null;
            await StudioHost.StopAsync();
            await EnsureReadyAsync();
        }
        finally
        {
            RetryButton.IsEnabled = true;
            OverlayRetryButton.IsEnabled = true;
        }
    }

    private async void OpenLegacy_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            renderer = null;
            await StudioHost.StopAsync();
            await openLegacyEditor();
        }
        catch (Exception ex)
        {
            showFailure("Could not open the legacy editor", ex.Message);
        }
    }

    private void StudioHost_StudioExited(object? sender, SkinStudioProcessExitedEventArgs e)
    {
        renderer = null;
        showFailure(
            "The lazer renderer stopped",
            string.IsNullOrWhiteSpace(e.StandardError)
                ? $"The renderer exited with code {e.ExitCode}. Your draft and controls are still available."
                : e.StandardError);
    }

    private void showStarting(string title, string detail, double? progress = null)
    {
        compactStudioHost();
        StatusOverlay.Visibility = Visibility.Visible;
        OverlayTitle.Text = title;
        OverlayDetail.Text = detail;
        StartupProgress.Visibility = Visibility.Visible;
        StartupProgress.IsIndeterminate = progress is null;
        if (progress is { } value)
            StartupProgress.Value = Math.Clamp(value, 0, 100);
        OverlayRetryButton.Visibility = Visibility.Collapsed;
        setStatus(title, detail, "TextMuted");
    }

    private void showFailure(string title, string detail)
    {
        compactStudioHost();
        StatusOverlay.Visibility = Visibility.Visible;
        OverlayTitle.Text = title;
        OverlayDetail.Text = detail;
        StartupProgress.Visibility = Visibility.Collapsed;
        OverlayRetryButton.Visibility = Visibility.Visible;
        setStatus(title, detail, "Danger");
    }

    private void showStudio()
    {
        StatusOverlay.Visibility = Visibility.Collapsed;
        updateRendererPlacement();
    }

    private void compactStudioHost()
    {
        StudioHost.Width = 1;
        StudioHost.Height = 1;
        Canvas.SetLeft(StudioHost, 0);
        Canvas.SetTop(StudioHost, 0);
    }

    private void updateRendererPlacement()
    {
        var target = rendererTarget;
        if (StatusOverlay.Visibility == Visibility.Visible
            || target is null
            || !target.IsVisible
            || target.ActualWidth < 2
            || target.ActualHeight < 2
            || !StudioHost.IsStudioRunning)
        {
            compactStudioHost();
            return;
        }
        try
        {
            var topLeft = target.TranslatePoint(new System.Windows.Point(0, 0), RendererLayer);
            if (Math.Abs(Canvas.GetLeft(StudioHost) - topLeft.X) > 0.5
                || double.IsNaN(Canvas.GetLeft(StudioHost)))
                Canvas.SetLeft(StudioHost, topLeft.X);
            if (Math.Abs(Canvas.GetTop(StudioHost) - topLeft.Y) > 0.5
                || double.IsNaN(Canvas.GetTop(StudioHost)))
                Canvas.SetTop(StudioHost, topLeft.Y);
            if (Math.Abs(StudioHost.Width - target.ActualWidth) > 0.5)
                StudioHost.Width = target.ActualWidth;
            if (Math.Abs(StudioHost.Height - target.ActualHeight) > 0.5)
                StudioHost.Height = target.ActualHeight;
        }
        catch (InvalidOperationException)
        {
            compactStudioHost();
        }
    }

    private void setStatus(string title, string detail, string resourceKey)
    {
        StatusTitle.Text = title;
        StatusDetail.Text = detail;
        StatusDot.Fill = TryFindResource($"Brush.{resourceKey}") as WpfBrush ?? WpfBrushes.Gray;
    }

    private void applyResponsiveLayout()
    {
        if (ActualWidth <= 850)
        {
            CompactNavigation.Visibility = Visibility.Visible;
            applyCompactSurface();
        }
        else if (ActualWidth <= 1050)
        {
            CompactNavigation.Visibility = Visibility.Collapsed;
            NavigatorColumn.Width = new GridLength(210);
            NavigatorGapColumn.Width = new GridLength(8);
            CenterColumn.Width = new GridLength(1, GridUnitType.Star);
            InspectorGapColumn.Width = new GridLength(8);
            InspectorColumn.Width = new GridLength(280);
            NavigatorPane.Visibility = Visibility.Visible;
            CanvasPane.Visibility = Visibility.Visible;
            InspectorPane.Visibility = Visibility.Visible;
        }
        else
        {
            CompactNavigation.Visibility = Visibility.Collapsed;
            NavigatorColumn.Width = new GridLength(256);
            NavigatorGapColumn.Width = new GridLength(8);
            CenterColumn.Width = new GridLength(1, GridUnitType.Star);
            InspectorGapColumn.Width = new GridLength(8);
            InspectorColumn.Width = new GridLength(320);
            NavigatorPane.Visibility = Visibility.Visible;
            CanvasPane.Visibility = Visibility.Visible;
            InspectorPane.Visibility = Visibility.Visible;
        }
    }

    private async void CompactSurface_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: string surface })
        {
            compactSurface = surface;
            applyCompactSurface();
            await sendRendererAsync(new SkinStudioRendererRequest
            {
                Command = SkinStudioRendererCommandKind.SetActive,
                Active = surface == "Canvas",
            }, reportFailure: false);
        }
    }

    private void applyCompactSurface()
    {
        var browse = compactSurface == "Browse";
        var canvas = compactSurface == "Canvas";
        var properties = compactSurface == "Properties";
        NavigatorColumn.Width = browse ? new GridLength(1, GridUnitType.Star) : new GridLength(0);
        NavigatorGapColumn.Width = new GridLength(0);
        CenterColumn.Width = canvas ? new GridLength(1, GridUnitType.Star) : new GridLength(0);
        InspectorGapColumn.Width = new GridLength(0);
        InspectorColumn.Width = properties ? new GridLength(1, GridUnitType.Star) : new GridLength(0);
        NavigatorPane.Visibility = browse ? Visibility.Visible : Visibility.Collapsed;
        CanvasPane.Visibility = canvas ? Visibility.Visible : Visibility.Collapsed;
        InspectorPane.Visibility = properties ? Visibility.Visible : Visibility.Collapsed;
        CompactBrowseButton.IsEnabled = !browse;
        CompactCanvasButton.IsEnabled = !canvas;
        CompactPropertiesButton.IsEnabled = !properties;
    }

    private (string Name, string Creator)? promptIdentity(string currentName, string currentCreator)
    {
        var name = new TextBox { Text = currentName, Height = 30, Margin = new Thickness(0, 4, 0, 10) };
        var creator = new TextBox { Text = currentCreator, Height = 30, Margin = new Thickness(0, 4, 0, 12) };
        var save = new Button { Content = "Save", Width = 92, Height = 34, HorizontalAlignment = HorizontalAlignment.Right };
        var panel = new StackPanel { Margin = new Thickness(14) };
        panel.Children.Add(new TextBlock { Text = "Skin name" });
        panel.Children.Add(name);
        panel.Children.Add(new TextBlock { Text = "Creator" });
        panel.Children.Add(creator);
        panel.Children.Add(save);
        var window = new Window
        {
            Title = "Rename draft",
            Owner = Window.GetWindow(this),
            Width = 420,
            Height = 245,
            Content = panel,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            ResizeMode = ResizeMode.NoResize,
        };
        save.Click += (_, _) =>
        {
            if (string.IsNullOrWhiteSpace(name.Text) || string.IsNullOrWhiteSpace(creator.Text))
            {
                MessageBox.Show(window, "Enter both a skin name and creator.", "Draft identity",
                    MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }
            window.DialogResult = true;
        };
        return window.ShowDialog() == true
            ? (name.Text.Trim(), creator.Text.Trim())
            : null;
    }

    private static string sanitizeFilename(string value)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var sanitized = new string(value.Select(character => invalid.Contains(character) ? '_' : character).ToArray()).Trim();
        return string.IsNullOrWhiteSpace(sanitized) ? "Kumori-skin" : sanitized;
    }

    public async Task StopAsync()
    {
        rendererReloadCancellation?.Cancel();
        activeRendererColourTarget = null;
        SkinStudioColorPickerPopup.IsOpen = false;
        await readinessGate.WaitAsync();
        try
        {
            await sendRendererAsync(new SkinStudioRendererRequest
            {
                Command = SkinStudioRendererCommandKind.SetActive,
                Active = false,
            }, reportFailure: false);
            renderer = null;
            suspendedForAppDeactivation = false;
            await StudioHost.StopAsync();
        }
        finally
        {
            readinessGate.Release();
        }
    }

    public void Dispose()
    {
        disposed = true;
        detachOwnerWindow();
        saveIntegratedSkinIni();
        skinIniSaveTimer.Stop();
        liveSyncTimer.Stop();
        rendererEventTimer.Stop();
        rendererColourPreviewTimer.Stop();
        rendererScalePreviewTimer.Stop();
        if (extrasPicker is not null)
            closeExtrasWorkspace();
        rendererReloadCancellation?.Cancel();
        rendererReloadCancellation?.Dispose();
        rendererReloadCancellation = null;
        workspace.StateChanged -= Workspace_StateChanged;
        if (!string.IsNullOrWhiteSpace(rendererContractPath))
        {
            try { File.Delete(rendererContractPath); } catch { }
        }
    }

    private const uint monitor_defaulttonearest = 0x00000002;

    [DllImport("user32.dll")]
    private static extern nint GetForegroundWindow();

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetWindowRect(nint window, out RectInt rect);

    [DllImport("user32.dll")]
    private static extern nint MonitorFromWindow(nint window, uint flags);

    [DllImport("user32.dll", CharSet = CharSet.Auto)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetMonitorInfo(nint monitor, ref MonitorInfo monitorInfo);

    [StructLayout(LayoutKind.Sequential)]
    private struct RectInt
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
    private struct MonitorInfo
    {
        public int Size;
        public RectInt Monitor;
        public RectInt WorkArea;
        public uint Flags;
    }

    private sealed record StudioProbe(
        int ContractVersion,
        int RendererContractVersion,
        bool RendererOnly,
        string EmbeddedHost);
}

internal static class SkinStudioExecutableResolver
{
    public static string? Resolve()
    {
        var configured = Environment.GetEnvironmentVariable("KUMORI_SKIN_STUDIO_PATH");
        if (isExecutable(configured))
            return Path.GetFullPath(configured!);
        foreach (var candidate in new[]
                 {
                     Path.Combine(AppContext.BaseDirectory, "native-tools", "Kumori.SkinStudio.exe"),
                     Path.Combine(AppContext.BaseDirectory, "Kumori.SkinStudio.exe"),
                 })
        {
            if (isExecutable(candidate))
                return Path.GetFullPath(candidate);
        }
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory);
             directory is not null;
             directory = directory.Parent)
        {
            if (!File.Exists(Path.Combine(directory.FullName, "Kumori.sln")))
                continue;
            var repositoryBuild = new[] { "Debug", "Release" }
                .Select(configuration => Path.Combine(
                    directory.FullName,
                    "src", "Kumori.SkinStudio", "bin", configuration,
                    "net10.0", "win-x64", "Kumori.SkinStudio.exe"))
                .Where(isExecutable)
                .OrderByDescending(File.GetLastWriteTimeUtc)
                .FirstOrDefault();
            if (repositoryBuild is not null)
                return repositoryBuild;
        }
        var embedded = NativeToolsPayload.TryEnsureSkinStudioExtracted();
        return isExecutable(embedded) ? Path.GetFullPath(embedded!) : null;
    }

    private static bool isExecutable(string? path) =>
        !string.IsNullOrWhiteSpace(path)
        && Path.GetExtension(path).Equals(".exe", StringComparison.OrdinalIgnoreCase)
        && File.Exists(path);
}
