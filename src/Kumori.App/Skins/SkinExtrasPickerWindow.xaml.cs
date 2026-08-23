using System.Diagnostics;
using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Kumori.Core;
using Kumori.Native;
using ManagedBass;
using Microsoft.VisualBasic.FileIO;
using Microsoft.Win32;
using Brush = System.Windows.Media.Brush;
using Brushes = System.Windows.Media.Brushes;
using Color = System.Windows.Media.Color;
using Image = System.Windows.Controls.Image;
using Point = System.Windows.Point;

namespace Kumori.App.Skins;

public sealed record SkinExtrasPreviewContext(
    BitmapSource? HitCircle,
    BitmapSource? HitCircleOverlay,
    BitmapSource? HitCircleNumber,
    IReadOnlyList<Color> ComboColours,
    bool HitCircleOverlayAboveNumber)
{
    public static SkinExtrasPreviewContext Empty { get; } =
        new(null, null, null, [], false);
}

public sealed class SkinExtrasPreviewPackChangedEventArgs : EventArgs
{
    public SkinExtrasPreviewPackChangedEventArgs(
        SkinExtraPackDescriptor pack,
        bool smoothTrail)
    {
        Pack = pack;
        SmoothTrail = smoothTrail;
    }

    public SkinExtraPackDescriptor Pack { get; }
    public bool SmoothTrail { get; }
}

public sealed class SkinExtrasPreviewTintChangedEventArgs : EventArgs
{
    public SkinExtrasPreviewTintChangedEventArgs(string elementKey, Color colour)
    {
        ElementKey = elementKey;
        Colour = colour;
    }
    public string ElementKey { get; }
    public Color Colour { get; }
}

public sealed class SkinExtrasPreviewMotionChangedEventArgs : EventArgs
{
    public SkinExtrasPreviewMotionChangedEventArgs(bool active) => Active = active;
    public bool Active { get; }
}

public sealed class SkinExtrasPreviewSmoothTrailChangedEventArgs : EventArgs
{
    public SkinExtrasPreviewSmoothTrailChangedEventArgs(bool active) => Active = active;
    public bool Active { get; }
}

// The historical type name is retained for compatibility; this view is hosted
// directly inside Skin Studio rather than shown as an operating-system window.
public partial class SkinExtrasPickerWindow : UserControl, IDisposable
{
    private readonly Window? dialogOwner;
    private readonly string initialCategory;
    private readonly SkinExtraModeVisibility modeVisibility;
    private readonly Action<bool>? lazerFilterChanged;
    private readonly Action<bool>? previewAnimationsChanged;
    private readonly SkinExtrasPreviewContext previewContext;
    private readonly SkinIniDocument? initialCurrentIni;
    private readonly SkinExtrasCurrentSkinSource? currentSkinSource;
    private readonly Func<SkinExtrasSelectionResult, Task<bool>>? stageSelection;
    private readonly Func<
        IReadOnlyList<SkinExtrasSelectionResult>,
        IProgress<SkinExtrasBatchProgress>?,
        Task<bool>>? stageSelections;
    private readonly DispatcherTimer reloadTimer;
    private readonly DispatcherTimer searchTimer;
    private readonly DispatcherTimer audioProgressTimer;
    private readonly DispatcherTimer audioSequenceTimer;
    private readonly DispatcherTimer compareBlinkTimer;
    private readonly SemaphoreSlim catalogLoadGate = new(1, 1);
    private Task? catalogLoadTask;
    private readonly SemaphoreSlim packPreviewLoadGate = new(2, 2);
    private static readonly object BitmapCacheGate = new();
    private static readonly Dictionary<string, CachedBitmap> BitmapCache =
        new(StringComparer.OrdinalIgnoreCase);
    private static readonly ConditionalWeakTable<BitmapSource, VisibleCropCache>
        VisibleCrops = new();
    private static readonly ConditionalWeakTable<BitmapSource, TintBitmapCache>
        TintedBitmaps = new();
    private static readonly IReadOnlyList<Point> ExtrasSliderPreviewPath =
        LegacySliderRenderer.SampleSCurve(44, 143, 336, 67, segments: 96);
    private const long BitmapCacheBudget = 96L * 1024 * 1024;
    private static long bitmapCacheBytes;
    private static long bitmapCacheSequence;
    private static readonly object PreviewLayerOutlineTag = new();
    private readonly Dictionary<string, IReadOnlyDictionary<string, string>> currentPreviewFiles =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> expandedPackKeys = new(StringComparer.OrdinalIgnoreCase);
    private IReadOnlyList<SkinExtraPackPreview> allPacks = [];
    private SkinExtraPackPreview? selectedPack;
    private SkinExtraPackPreview? displayedPack;
    private IReadOnlyList<PackElementEntry> currentFallbackElements = [];
    private string? currentFallbackPackKey;
    private readonly Dictionary<string, Dictionary<string, bool>>
        fallbackSelectionsByPack = new(StringComparer.OrdinalIgnoreCase);
    private PackElementEntry? activeTintElement;
    private PackElementEntry? activePreviewHighlightElement;
    private int audioDevice = -1;
    private int audioStream;
    private string? playingAudioPath;
    private string? playingAudioLabel;
    private bool playingAudioLoops;
    private AudioTrackOption? selectedAudioTrack;
    private string? activeAudioFamilyId;
    private IReadOnlyList<AudioSequenceStep> audioSequenceSteps = [];
    private readonly List<int> audioSequenceStreams = [];
    private int audioSequenceIndex;
    private string? audioSequenceSourceLabel;
    private bool updatingAudioTrackSelection;
    private bool audioSeeking;
    private double audioDurationSeconds;
    private FileSystemWatcher? extrasWatcher;
    private CancellationTokenSource? previewCancellation;
    private readonly string previewTempRoot = Path.Combine(
        Path.GetTempPath(),
        "Kumori",
        "extras-preview",
        Guid.NewGuid().ToString("N"));
    private Canvas activePreviewCanvas = null!;
    private bool updatingFamilies;
    private bool initialFamilySelected;
    private bool lazerUsedOnly;
    private bool initializingLazerFilter;
    private bool loadingPacks;
    private Exception? lastCatalogLoadError;
    private bool staging;
    private bool disposed;
    private bool catalogSyncWasActive;
    private bool catalogCancelRequested;
    private int packLoadVersion;
    private int usageScanVersion;
    private int elementThumbnailLoadVersion;
    private int fallbackThumbnailLoadVersion;
    private IncompleteImportGuide? incompleteImportGuide;
    private readonly HashSet<SkinExtraPackPreview> loadingPackPreviews =
        new(ReferenceEqualityComparer.Instance);
    private readonly Dictionary<ScrollViewer, double> libraryScrollTargets =
        new(ReferenceEqualityComparer.Instance);
    private readonly Dictionary<Canvas, ExtrasCanvasAnimationState>
        extrasCanvasAnimationStates = new(ReferenceEqualityComparer.Instance);
    private readonly Stopwatch extrasPreviewAnimationClock = Stopwatch.StartNew();
    private bool libraryScrollRendering;
    private long libraryScrollFrameTimestamp;
    private bool previewAnimationsEnabled;
    private bool extrasPreviewRendering;
    private bool rendererTargetVisible;
    private double extrasPreviewElapsed;
    private double extrasPreviewLastRenderTime;
    private double extrasPreviewFrameDelta;
    private readonly SkinExtrasCatalogSyncService extrasSyncService =
        SkinExtrasCatalogSyncService.Shared;
    private SkinIniDocument? EffectiveCurrentIni =>
        currentSkinSource?.CurrentIni ?? initialCurrentIni;

    public SkinExtrasPickerWindow(
        Window? owner,
        string category,
        SkinExtraModeVisibility? modeVisibility = null,
        Action<bool>? lazerFilterChanged = null,
        SkinExtrasPreviewContext? previewContext = null,
        SkinIniDocument? currentIni = null,
        SkinExtrasCurrentSkinSource? currentSkinSource = null,
        Func<SkinExtrasSelectionResult, Task<bool>>? stageSelection = null,
        Func<
            IReadOnlyList<SkinExtrasSelectionResult>,
            IProgress<SkinExtrasBatchProgress>?,
            Task<bool>>? stageSelections = null,
        bool previewAnimationsEnabled = true,
        Action<bool>? previewAnimationsChanged = null)
    {
        dialogOwner = owner;
        initialCategory = category;
        this.modeVisibility = modeVisibility ?? new SkinExtraModeVisibility();
        this.lazerFilterChanged = lazerFilterChanged;
        this.previewContext = previewContext ?? SkinExtrasPreviewContext.Empty;
        initialCurrentIni = currentIni;
        this.currentSkinSource = currentSkinSource;
        this.stageSelection = stageSelection;
        this.stageSelections = stageSelections;
        this.previewAnimationsEnabled = previewAnimationsEnabled;
        this.previewAnimationsChanged = previewAnimationsChanged;
        lazerUsedOnly = this.modeVisibility.LazerUsedOnly;
        InitializeComponent();
        ElementColorPicker.ColourChanged += ElementColorPicker_ColourChanged;
        ElementColorPicker.CloseRequested += () =>
            ElementColorPickerPopup.IsOpen = false;
        ElementColorPickerPopup.Closed += (_, _) => activeTintElement = null;
        activePreviewCanvas = CursorTrailCanvas;
        CurrentSkinPreviewButton.IsEnabled = currentSkinSource is not null;
        ComparePreviewButton.IsEnabled = currentSkinSource is not null;
        UpdatePreviewPlaybackPresentation();
        initializingLazerFilter = true;
        LazerUsedOnlyCheckBox.IsChecked = lazerUsedOnly;
        initializingLazerFilter = false;
        HeaderTitleText.Text = "Extras Library";
        TitleText.Text = "Browse Skin Extras";
        SubtitleText.Text =
            "Choose an element family on the left, compare its packs, then stage one for the current skin.";
        reloadTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(350) };
        reloadTimer.Tick += (_, _) =>
        {
            reloadTimer.Stop();
            LoadPacks();
        };
        searchTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(180) };
        searchTimer.Tick += (_, _) =>
        {
            searchTimer.Stop();
            RefreshCurrentFamily();
        };
        audioProgressTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(200),
        };
        audioProgressTimer.Tick += (_, _) => UpdateAudioProgress();
        audioSequenceTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(500),
        };
        audioSequenceTimer.Tick += (_, _) =>
        {
            try
            {
                PlayNextAudioSequenceStep();
            }
            catch (Exception ex)
            {
                StopAudio();
                PackDetails.Text = $"Could not preview this hitsound set: {ex.Message}";
                PackNoticePanel.Visibility = Visibility.Visible;
            }
        };
        compareBlinkTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(420),
        };
        compareBlinkTimer.Tick += (_, _) =>
        {
            ResultPreviewPane.Opacity = ResultPreviewPane.Opacity < 0.5 ? 1 : 0;
        };
        Loaded += (_, _) => UpdateExtrasPreviewAnimationSubscription();
        IsVisibleChanged += (_, _) => UpdateExtrasPreviewAnimationSubscription();
        Unloaded += (_, _) => Dispose();
        SkinExtrasPersistentIndex.CacheRefreshed += PersistentIndex_CacheRefreshed;
        extrasSyncService.ProgressChanged += ExtrasSyncService_ProgressChanged;
        extrasSyncService.LibraryChanged += ExtrasSyncService_LibraryChanged;
        if (extrasSyncService.CurrentProgress is { } progress
            && ShouldDisplaySyncProgress(progress))
            UpdateCatalogSyncProgress(progress);
        if (!IsCatalogMutationActive())
            LoadPacks();
        StartWatching();
    }

    public string? SelectedPackDirectory { get; private set; }
    public SkinExtraPackManifest? SelectedManifest { get; private set; }
    public SkinExtrasSelectionResult? SelectionResult { get; private set; }
    public bool LazerUsedOnly => lazerUsedOnly;
    public event EventHandler? CloseRequested;
    public event EventHandler<SkinExtrasPreviewPackChangedEventArgs>? PreviewPackChanged;
    public event EventHandler<SkinExtrasPreviewTintChangedEventArgs>? PreviewTintChanged;
    public event EventHandler<SkinExtrasPreviewMotionChangedEventArgs>? PreviewMotionChanged;
    public event EventHandler<SkinExtrasPreviewSmoothTrailChangedEventArgs>?
        PreviewSmoothTrailChanged;

    public FrameworkElement RendererTarget => RendererMount;

    public void ShowRendererTarget()
    {
        rendererTargetVisible = true;
        previewCancellation?.Cancel();
        StopExtrasPreviewRendering();
        extrasCanvasAnimationStates.Clear();
        CursorTrailCanvas.Children.Clear();
        CurrentPreviewCanvas.Children.Clear();
        ResultPreviewCanvas.Children.Clear();
        CursorPreview.Visibility = Visibility.Collapsed;
        ComparisonPreview.Visibility = Visibility.Collapsed;
        EmptyPreview.Visibility = Visibility.Collapsed;
        RendererMount.Visibility = Visibility.Visible;
        NativePreviewControls.Visibility = selectedPack is { } selected
                                           && SkinCursorMiddlePolicy.IsCursorFamily(
                                               selected.Manifest.FamilyId)
            ? Visibility.Visible
            : Visibility.Collapsed;
        if (selectedPack is { } pack)
            RefreshElementList(pack);
    }

    public void HideRendererTarget()
    {
        rendererTargetVisible = false;
        NativePreviewControls.Visibility = Visibility.Collapsed;
        RendererMount.Visibility = Visibility.Collapsed;
        UpdateExtrasPreviewAnimationSubscription();
    }

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        reloadTimer.Stop();
        searchTimer.Stop();
        audioProgressTimer.Stop();
        audioSequenceTimer.Stop();
        compareBlinkTimer.Stop();
        StopLibraryScrollRendering();
        StopExtrasPreviewRendering();
        libraryScrollTargets.Clear();
        previewCancellation?.Cancel();
        previewCancellation?.Dispose();
        extrasWatcher?.Dispose();
        SkinExtrasPersistentIndex.CacheRefreshed -= PersistentIndex_CacheRefreshed;
        extrasSyncService.ProgressChanged -= ExtrasSyncService_ProgressChanged;
        extrasSyncService.LibraryChanged -= ExtrasSyncService_LibraryChanged;
        StopAudio();
        DeletePreviewTempDirectory();
    }

    public void RefreshLibrary() => ScheduleReload();

    public async Task<int> EnsureLibraryLoadedAsync()
    {
        await AwaitCurrentCatalogLoadAsync();
        if (lastCatalogLoadError is not null)
        {
            throw new InvalidOperationException(
                "The Extras catalog could not be loaded.",
                lastCatalogLoadError);
        }

        // A catalog update can briefly invalidate the in-memory index while
        // packs are being replaced. Do not let that transient empty snapshot
        // become the hybrid Studio's permanent library view. A verified disk
        // scan reuses the same index as the legacy editor and then repopulates
        // this view from the completed result.
        if (allPacks.Count == 0
            && Directory.EnumerateDirectories(AppPaths.SkinExtrasDir)
                .Any(path => !IsInternalLibraryPath(path)))
        {
            SubtitleText.Text = "Indexing the existing Extras library...";
            await Task.Run(() => SkinExtraPackIndex.Scan(AppPaths.SkinExtrasDir));
            LoadPacks();
            await AwaitCurrentCatalogLoadAsync();
        }
        return allPacks.Count;
    }

    private async Task AwaitCurrentCatalogLoadAsync()
    {
        while (true)
        {
            if (catalogLoadTask is null)
                LoadPacks();
            var pending = catalogLoadTask!;
            await pending;
            if (ReferenceEquals(pending, catalogLoadTask) && !loadingPacks)
                return;
        }
    }

    internal void UpdateCatalogSyncProgress(SkinExtrasSyncProgress progress)
    {
        if (!ShouldDisplaySyncProgress(progress))
        {
            ExtrasSyncStatusText.Text =
                "Using the local Extras library. Updates are manual.";
            return;
        }
        ExtrasSyncStatusText.Text = progress.Message;
        var running = IsSynchronizationRunningStage(progress.Stage);
        var foreground = ShouldPresentSyncInForeground(progress);
        CheckExtrasUpdatesButton.IsEnabled = false;
        CatalogSyncOverlay.Visibility = foreground
            ? Visibility.Visible
            : Visibility.Collapsed;

        if (running)
            catalogSyncWasActive = true;

        if (foreground)
        {
            CatalogSyncOverlayText.Text = progress.Message;
            CancelCatalogSyncButton.IsEnabled = !catalogCancelRequested;
            CancelCatalogSyncButton.Content = catalogCancelRequested
                ? "Canceling…"
                : "Cancel download";
            UpdateCatalogSyncProgressBar(progress);
        }
        else if (!running && catalogSyncWasActive)
        {
            catalogSyncWasActive = false;
            catalogCancelRequested = false;
            ScheduleReload();
        }
    }

    private void UpdateCatalogSyncProgressBar(SkinExtrasSyncProgress progress)
    {
        if (progress.Stage == SkinExtrasSyncStage.Downloading
            && progress.TotalBytes > 0)
        {
            CatalogSyncProgressBar.IsIndeterminate = false;
            CatalogSyncProgressBar.Value = Math.Clamp(
                100d * progress.BytesReceived / progress.TotalBytes,
                0,
                100);
            CatalogSyncProgressDetailText.Text =
                $"{FormatByteCount(progress.BytesReceived)} of "
                + $"{FormatByteCount(progress.TotalBytes)}"
                + PackProgressSuffix(progress);
            return;
        }

        if (progress.Stage == SkinExtrasSyncStage.Installing
            && progress.TotalPacks > 0)
        {
            CatalogSyncProgressBar.IsIndeterminate = false;
            CatalogSyncProgressBar.Value = Math.Clamp(
                100d * progress.CompletedPacks / progress.TotalPacks,
                0,
                100);
            CatalogSyncProgressDetailText.Text =
                $"{progress.CompletedPacks} of {progress.TotalPacks} packs installed";
            return;
        }

        CatalogSyncProgressBar.IsIndeterminate = true;
        CatalogSyncProgressDetailText.Text = progress.Stage switch
        {
            SkinExtrasSyncStage.Checking => "Connecting to the Extras catalog…",
            SkinExtrasSyncStage.Planning => "Comparing installed packs…",
            _ => "Preparing Extras…",
        };
    }

    private static string PackProgressSuffix(SkinExtrasSyncProgress progress) =>
        progress.TotalPacks > 0
            ? $" · pack {Math.Min(progress.CompletedPacks + 1, progress.TotalPacks)}"
              + $" of {progress.TotalPacks}"
            : "";

    private static string FormatByteCount(long bytes)
    {
        string[] units = ["B", "KB", "MB", "GB"];
        var value = Math.Max(0, bytes);
        var unit = 0;
        var display = (double)value;
        while (display >= 1024 && unit < units.Length - 1)
        {
            display /= 1024;
            unit++;
        }
        return unit == 0
            ? $"{value} {units[unit]}"
            : $"{display:0.0} {units[unit]}";
    }

    private void ExtrasSyncService_ProgressChanged(
        object? sender,
        SkinExtrasSyncProgress progress) =>
        _ = Dispatcher.InvokeAsync(() => UpdateCatalogSyncProgress(progress));

    private void ExtrasSyncService_LibraryChanged(object? sender, EventArgs e) =>
        _ = Dispatcher.InvokeAsync(RefreshLibrary);

    private void PersistentIndex_CacheRefreshed(object? sender, EventArgs e) =>
        _ = Dispatcher.InvokeAsync(RefreshLibrary);

    private async void CheckExtrasUpdates_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            await extrasSyncService.SynchronizeAsync(manual: true);
        }
        catch (Exception ex)
        {
            ExtrasSyncStatusText.Text = $"Extras update check failed: {ex.Message}";
            CheckExtrasUpdatesButton.IsEnabled = false;
        }
    }

    private void CancelCatalogSync_Click(object sender, RoutedEventArgs e)
    {
        if (!extrasSyncService.CancelActiveSynchronization()) return;
        catalogCancelRequested = true;
        CancelCatalogSyncButton.IsEnabled = false;
        CancelCatalogSyncButton.Content = "Canceling…";
        CatalogSyncOverlayText.Text = "Canceling Extras synchronization…";
        CatalogSyncProgressDetailText.Text =
            "Finishing the current safe file operation before stopping.";
    }

    private void DeletePreviewTempDirectory()
    {
        try
        {
            var allowedRoot = Path.GetFullPath(Path.Combine(
                    Path.GetTempPath(),
                    "Kumori",
                    "extras-preview"))
                .TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            var target = Path.GetFullPath(previewTempRoot);
            if (target.StartsWith(allowedRoot, StringComparison.OrdinalIgnoreCase)
                && Directory.Exists(target))
                Directory.Delete(target, recursive: true);
        }
        catch
        {
            // Preview files are disposable and can be reclaimed by the OS later.
        }
    }

    private void StartWatching()
    {
        extrasWatcher = new FileSystemWatcher(AppPaths.SkinExtrasDir)
        {
            IncludeSubdirectories = true,
            NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName
                           | NotifyFilters.LastWrite | NotifyFilters.Size,
            EnableRaisingEvents = true,
        };
        FileSystemEventHandler changed = (_, args) =>
        {
            if (!IsInternalLibraryPath(args.FullPath))
            {
                SkinExtrasPersistentIndex.InvalidateMemory(AppPaths.SkinExtrasDir);
                ScheduleReload();
            }
        };
        RenamedEventHandler renamed = (_, args) =>
        {
            if (!IsInternalLibraryPath(args.FullPath)
                && !IsInternalLibraryPath(args.OldFullPath))
            {
                SkinExtrasPersistentIndex.InvalidateMemory(AppPaths.SkinExtrasDir);
                ScheduleReload();
            }
        };
        extrasWatcher.Created += changed;
        extrasWatcher.Changed += changed;
        extrasWatcher.Deleted += changed;
        extrasWatcher.Renamed += renamed;
    }

    private void ScheduleReload()
    {
        if (extrasSyncService.CurrentProgress is { } progress
            && IsForegroundSyncStage(progress.Stage))
        {
            return;
        }
        Dispatcher.BeginInvoke(() =>
        {
            if (disposed) return;
            reloadTimer.Stop();
            reloadTimer.Start();
        });
    }

    internal static bool IsSynchronizationRunningStage(SkinExtrasSyncStage stage) =>
        stage is SkinExtrasSyncStage.Checking
            or SkinExtrasSyncStage.Planning
            or SkinExtrasSyncStage.Downloading
            or SkinExtrasSyncStage.Installing;

    internal static bool IsForegroundSyncStage(SkinExtrasSyncStage stage) =>
        stage is SkinExtrasSyncStage.Downloading
            or SkinExtrasSyncStage.Installing;

    internal static bool ShouldPresentSyncInForeground(
        SkinExtrasSyncProgress progress) =>
        progress.IsManual && IsForegroundSyncStage(progress.Stage);

    internal static bool ShouldDisplaySyncProgress(
        SkinExtrasSyncProgress progress) =>
        progress.IsManual;

    internal static bool IsInternalLibraryPath(string path)
    {
        var internalRoot = Path.GetFullPath(Path.Combine(AppPaths.SkinExtrasDir, ".kumori"))
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var candidate = Path.GetFullPath(path)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        return candidate.Equals(internalRoot, StringComparison.OrdinalIgnoreCase)
               || candidate.StartsWith(
                   internalRoot + Path.DirectorySeparatorChar,
                   StringComparison.OrdinalIgnoreCase);
    }

    private void CloseWindow_Click(object sender, RoutedEventArgs e)
    {
        if (IsForegroundCatalogSyncActive()) return;
        CloseRequested?.Invoke(this, EventArgs.Empty);
    }

    private void ExtrasPicker_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        var backShortcut = e.Key == Key.Escape
                           || e.Key == Key.Left
                           && Keyboard.Modifiers.HasFlag(ModifierKeys.Alt);
        if (!backShortcut) return;
        e.Handled = true;
        if (IsForegroundCatalogSyncActive()) return;
        CloseRequested?.Invoke(this, EventArgs.Empty);
    }

    private void LibraryList_PreviewMouseWheel(
        object sender,
        MouseWheelEventArgs e)
    {
        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Control))
            return;
        var viewer = sender as ScrollViewer
                     ?? FindVisualDescendant<ScrollViewer>(sender as DependencyObject);
        if (viewer is null || viewer.ScrollableHeight <= 0)
            return;

        var current = viewer.VerticalOffset;
        var previousTarget = libraryScrollTargets.GetValueOrDefault(viewer, current);
        var target = ExtrasLibraryScrollPhysics.TargetOffset(
            current,
            previousTarget,
            e.Delta,
            viewer.ScrollableHeight);
        if (Math.Abs(target - current) < ExtrasLibraryScrollPhysics.SettleDistance
            && Math.Abs(previousTarget - current)
            < ExtrasLibraryScrollPhysics.SettleDistance)
            return;

        libraryScrollTargets[viewer] = target;
        StartLibraryScrollRendering();
        e.Handled = true;
    }

    private void StartLibraryScrollRendering()
    {
        if (libraryScrollRendering)
            return;
        libraryScrollRendering = true;
        libraryScrollFrameTimestamp = Stopwatch.GetTimestamp();
        CompositionTarget.Rendering += LibraryScroll_Rendering;
    }

    private void StopLibraryScrollRendering()
    {
        if (!libraryScrollRendering)
            return;
        CompositionTarget.Rendering -= LibraryScroll_Rendering;
        libraryScrollRendering = false;
    }

    private void LibraryScroll_Rendering(object? sender, EventArgs e)
    {
        var now = Stopwatch.GetTimestamp();
        var elapsed = Stopwatch.GetElapsedTime(
            libraryScrollFrameTimestamp,
            now).TotalSeconds;
        libraryScrollFrameTimestamp = now;

        foreach (var (viewer, requestedTarget) in libraryScrollTargets.ToArray())
        {
            if (!viewer.IsLoaded)
            {
                libraryScrollTargets.Remove(viewer);
                continue;
            }

            var target = Math.Clamp(requestedTarget, 0, viewer.ScrollableHeight);
            var next = ExtrasLibraryScrollPhysics.NextOffset(
                viewer.VerticalOffset,
                target,
                elapsed);
            viewer.ScrollToVerticalOffset(next);
            if (ExtrasLibraryScrollPhysics.IsSettled(next, target))
            {
                viewer.ScrollToVerticalOffset(target);
                libraryScrollTargets.Remove(viewer);
            }
        }

        if (libraryScrollTargets.Count == 0)
            StopLibraryScrollRendering();
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

    private bool IsForegroundCatalogSyncActive() =>
        extrasSyncService.CurrentProgress is { } progress
        && ShouldPresentSyncInForeground(progress);

    private bool IsCatalogMutationActive() =>
        extrasSyncService.CurrentProgress is { } progress
        && IsForegroundSyncStage(progress.Stage);

    private void LoadPacks(string? preferredPackPath = null) =>
        catalogLoadTask = LoadPacksAsync(preferredPackPath);

    private async Task LoadPacksAsync(string? preferredPackPath)
    {
        var version = ++packLoadVersion;
        loadingPacks = true;
        lastCatalogLoadError = null;
        var selectedFamilyId = (FamilyList.SelectedItem as FamilyNavigationItem)?.FamilyId;
        var selectedPackPath = selectedPack?.DirectoryPath;
        var requestedLazerFilter = lazerUsedOnly;
        var expandedKeys = expandedPackKeys.ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (allPacks.Count == 0)
            SubtitleText.Text = "Loading the Extras library...";

        IReadOnlyList<SkinExtraPackPreview> loadedPacks;
        try
        {
            await catalogLoadGate.WaitAsync();
            try
            {
                if (disposed || version != packLoadVersion)
                    return;
                loadedPacks = await Task.Run(() =>
                    BuildPackCatalog(requestedLazerFilter, expandedKeys));
            }
            finally
            {
                catalogLoadGate.Release();
            }
        }
        catch (Exception ex)
        {
            if (!disposed && version == packLoadVersion)
            {
                loadingPacks = false;
                lastCatalogLoadError = ex;
                SubtitleText.Text = $"Could not load the Extras library: {ex.Message}";
            }
            return;
        }

        if (disposed || version != packLoadVersion)
            return;
        RememberCurrentFallbackSelections();
        var editStates = CapturePackEditStates(allPacks);
        RestorePackEditStates(loadedPacks, editStates);
        // Navigation can change while the catalog is scanning. Resolve it
        // again on the UI thread so a stale reload cannot jump the user back
        // to the family or pack that was selected when the scan began.
        var reloadSelection = ResolveReloadSelection(
            selectedFamilyId,
            selectedPackPath,
            (FamilyList.SelectedItem as FamilyNavigationItem)?.FamilyId,
            selectedPack?.DirectoryPath,
            preferredPackPath);
        allPacks = loadedPacks;

        var navigation = BuildFamilyNavigation(allPacks, modeVisibility, requestedLazerFilter);
        var familyView = CollectionViewSource.GetDefaultView(navigation);
        familyView.GroupDescriptions.Add(
            new PropertyGroupDescription(nameof(FamilyNavigationItem.Area)));
        updatingFamilies = true;
        FamilyList.ItemsSource = familyView;
        var familyToSelect = navigation.FirstOrDefault(item =>
                                 item.FamilyId.Equals(
                                     reloadSelection.FamilyId,
                                     StringComparison.OrdinalIgnoreCase))
                             ?? (!initialFamilySelected
                                 ? navigation.FirstOrDefault(item =>
                                     item.LegacyCategory.Equals(
                                         initialCategory,
                                         StringComparison.OrdinalIgnoreCase)
                                     && item.PackCount > 0)
                                 : null)
                             ?? navigation[0];
        FamilyList.SelectedItem = familyToSelect;
        updatingFamilies = false;
        initialFamilySelected = true;
        ShowFamily(familyToSelect, reloadSelection.PackPath);
        loadingPacks = false;
        _ = RefreshUsageBadgesAsync(version);
    }

    private async Task RefreshUsageBadgesAsync(int catalogVersion)
    {
        var source = currentSkinSource;
        var scanVersion = ++usageScanVersion;
        if (source is null)
        {
            foreach (var pack in allPacks)
                pack.SetUsage(0, pack.Manifest.Files.Count);
            return;
        }

        try
        {
            var targets = allPacks.SelectMany(pack => pack.Manifest.Files)
                .Select(file => file.TargetFilename.Replace('\\', '/'))
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            var current = new Dictionary<string, SkinExtraManifestFile>(
                StringComparer.OrdinalIgnoreCase);
            foreach (var filename in source.Filenames
                         .Select(name => name.Replace('\\', '/'))
                         .Where(targets.Contains))
            {
                var bytes = await source.ReadFileAsync(filename, CancellationToken.None);
                if (bytes is null)
                    continue;
                current[filename] = SkinExtraFingerprint.Describe(filename, filename, bytes);
            }
            if (disposed || catalogVersion != packLoadVersion || scanVersion != usageScanVersion)
                return;
            foreach (var pack in allPacks)
            {
                var matchedTargets = pack.Manifest.Files.Where(file =>
                    current.TryGetValue(file.TargetFilename.Replace('\\', '/'), out var installed)
                    && SkinExtraFingerprint.EquivalentFileContent(installed, file))
                    .Select(file => file.TargetFilename)
                    .ToHashSet(StringComparer.OrdinalIgnoreCase);
                var targetCount = pack.Manifest.Files
                    .Select(file => file.TargetFilename)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .Count();
                var settingsMatch = pack.Manifest.IniPatch.All(entry =>
                    DescribeIniPatchChange(entry, EffectiveCurrentIni).Length == 0);
                pack.SetUsage(matchedTargets.Count, targetCount, settingsMatch);
                foreach (var element in pack.Elements)
                    element.SetUsage(
                        element.Files.Count(file => matchedTargets.Contains(file.Name)),
                        element.Files.Count);
            }
            if (selectedPack is { } selected)
                UpdateSelectedPackUsage(selected);
        }
        catch
        {
            // Usage labels are helpful metadata; a locked Realm file must not
            // prevent the Extras catalog itself from being usable.
        }
    }

    private void UpdateSelectedPackUsage(SkinExtraPackPreview pack)
    {
        SelectedPackUsageText.Text = pack.UsageDetail;
        SelectedPackUsageBadge.Visibility = string.IsNullOrWhiteSpace(pack.UsageDetail)
            ? Visibility.Collapsed
            : Visibility.Visible;
    }

    internal static (string Badge, string Detail, bool IsInUse) DescribePackUsage(
        int matchingFiles,
        int totalFiles,
        bool settingsMatch)
    {
        var total = Math.Max(0, totalFiles);
        var matching = Math.Clamp(matchingFiles, 0, total);
        var allFilesMatch = total > 0 && matching == total;
        var isInUse = allFilesMatch && settingsMatch;
        if (isInUse)
            return ("IN USE", "IN USE", true);
        if (matching == 0)
            return ("", "", false);
        if (allFilesMatch)
            return ("", "FILES MATCH · SETTINGS DIFFER", false);
        return ("", $"{matching}/{total} FILES MATCH", false);
    }

    internal static (string? FamilyId, string? PackPath) ResolveReloadSelection(
        string? familyAtStart,
        string? packAtStart,
        string? currentFamily,
        string? currentPack,
        string? explicitlyPreferredPack) =>
        (
            currentFamily ?? familyAtStart,
            explicitlyPreferredPack ?? currentPack ?? packAtStart
        );

    private static IReadOnlyDictionary<string, PackEditState> CapturePackEditStates(
        IEnumerable<SkinExtraPackPreview> packs) =>
        packs.GroupBy(pack => pack.PackKey, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(
                group => group.Key,
                group =>
                {
                    var pack = group.Last();
                    return new PackEditState(
                        pack.Files.GroupBy(
                                file => file.Name,
                                StringComparer.OrdinalIgnoreCase)
                            .ToDictionary(
                            files => files.Key,
                            files => files.Last().IsSelected,
                            StringComparer.OrdinalIgnoreCase),
                        pack.Settings.GroupBy(
                                SettingIdentity,
                                StringComparer.OrdinalIgnoreCase)
                            .ToDictionary(
                            settings => settings.Key,
                            settings => settings.Last().IsSelected,
                            StringComparer.OrdinalIgnoreCase),
                        pack.Elements.Where(element => element.IsTinted)
                            .GroupBy(
                                element => element.Key,
                                StringComparer.OrdinalIgnoreCase)
                            .ToDictionary(
                                elements => elements.Key,
                                elements => elements.Last().TintRgb,
                                StringComparer.OrdinalIgnoreCase));
                },
                StringComparer.OrdinalIgnoreCase);

    private static void RestorePackEditStates(
        IEnumerable<SkinExtraPackPreview> packs,
        IReadOnlyDictionary<string, PackEditState> states)
    {
        foreach (var pack in packs)
        {
            if (!states.TryGetValue(pack.PackKey, out var state))
                continue;
            foreach (var file in pack.Files)
                file.IsSelected = SelectionAfterReload(
                    file.Name,
                    file.IsSelected,
                    state.FileSelections);
            foreach (var setting in pack.Settings)
                if (state.SettingSelections.TryGetValue(
                        SettingIdentity(setting),
                        out var selected))
                    setting.IsSelected = selected;
            foreach (var element in pack.Elements)
                if (state.ElementTints.TryGetValue(element.Key, out var tint))
                    element.SetTint(Color.FromRgb(tint.Red, tint.Green, tint.Blue));
            pack.NotifySelectionChanged();
        }
    }

    private static string SettingIdentity(PackSettingEntry setting) =>
        $"{setting.Patch.Section}\0{setting.Patch.ManiaKeys}\0{setting.Patch.Key}";

    internal static bool SelectionAfterReload(
        string filename,
        bool defaultSelection,
        IReadOnlyDictionary<string, bool> previousSelections) =>
        previousSelections.TryGetValue(filename, out var selected)
            ? selected
            : defaultSelection;

    private IReadOnlyList<SkinExtraPackPreview> BuildPackCatalog(
        bool requestedLazerFilter,
        IReadOnlySet<string> expandedKeys)
    {
        Directory.CreateDirectory(AppPaths.SkinExtrasDir);
        var libraryState = SkinExtrasLibraryStateStore.GetAll(AppPaths.SkinExtrasDir);
        var previews = SkinExtrasPersistentIndex.ScanCached(AppPaths.SkinExtrasDir)
            .SelectMany(descriptor => SplitDisplayFamilies(descriptor)
                .Select(split => TryCreatePack(
                    split,
                    1,
                    descriptor,
                    libraryState,
                    requestedLazerFilter)))
            .Where(pack => pack is not null)
            .Cast<SkinExtraPackPreview>()
            .ToArray();
        var managedByFingerprint = SkinExtrasRemoteRegistryStore
            .Read(AppPaths.SkinExtrasDir)
            .Installs.Values
            .GroupBy(item => item.Fingerprint, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(
                group => group.Key,
                group => group.OrderByDescending(item => item.Revision).First(),
                StringComparer.OrdinalIgnoreCase);
        foreach (var preview in previews)
        {
            preview.IsExpanded = expandedKeys.Contains(preview.PackKey);
            if (managedByFingerprint.TryGetValue(
                    preview.SourceDescriptor.Manifest.Fingerprint,
                    out var managed))
                preview.CatalogBadge = managed.Withdrawn
                    ? "Catalog withdrawn"
                    : managed.LocallyModified
                        ? "Catalog managed · locally modified"
                        : $"Catalog managed · revision {managed.Revision}";
        }
        return CollapseDuplicatePacks(previews)
            .Where(pack => modeVisibility.AllowsArea(pack.Manifest.Area))
            .OrderByDescending(pack => pack.State.Favorite)
            .ThenByDescending(pack => pack.State.LastUsedUtc)
            .ThenBy(pack => pack.Collection, StringComparer.OrdinalIgnoreCase)
            .ThenBy(pack => pack.Name, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static IReadOnlyList<SkinExtraPackPreview> CollapseDuplicatePacks(
        IEnumerable<SkinExtraPackPreview> packs)
    {
        var kept = new List<SkinExtraPackPreview>();
        foreach (var candidate in packs
                     .OrderByDescending(pack => pack.Manifest.Files.Count)
                     .ThenBy(pack => pack.DirectoryPath, StringComparer.OrdinalIgnoreCase))
        {
            var match = kept.FindIndex(existing =>
            {
                var sameFamilyAndVariant = existing.Manifest.FamilyId.Equals(
                                               candidate.Manifest.FamilyId,
                                               StringComparison.OrdinalIgnoreCase)
                                           && StringComparer.OrdinalIgnoreCase.Equals(
                                               existing.Manifest.Variant,
                                               candidate.Manifest.Variant);
                return sameFamilyAndVariant
                       && NumberFontCollapseScopeMatches(
                           existing.Manifest,
                           candidate.Manifest)
                       && ((EffectivePackContains(existing.Manifest, candidate.Manifest)
                            && (EffectivePackContains(candidate.Manifest, existing.Manifest)
                                || SamePackSource(existing.Manifest, candidate.Manifest)))
                           || EquivalentTransparentPlaceholderPacks(existing, candidate));
            });
            if (match < 0)
            {
                kept.Add(candidate);
                continue;
            }

            kept[match] = kept[match] with
            {
                DuplicateCount = kept[match].DuplicateCount + candidate.DuplicateCount,
            };
        }
        return kept;
    }

    internal static bool NumberFontCollapseScopeMatches(
        SkinExtraPackManifest left,
        SkinExtraPackManifest right)
    {
        if (!left.FamilyId.Equals("osu.number-font", StringComparison.OrdinalIgnoreCase)
            || !right.FamilyId.Equals("osu.number-font", StringComparison.OrdinalIgnoreCase))
            return true;

        return SameNumberFontSource(left, right)
               && left.FontRoles.ToHashSet(StringComparer.OrdinalIgnoreCase)
                   .SetEquals(right.FontRoles);
    }

    private static bool SameNumberFontSource(
        SkinExtraPackManifest left,
        SkinExtraPackManifest right)
    {
        var leftSkin = string.IsNullOrWhiteSpace(left.SourceSkin)
            ? left.DisplayName
            : left.SourceSkin;
        var rightSkin = string.IsNullOrWhiteSpace(right.SourceSkin)
            ? right.DisplayName
            : right.SourceSkin;
        return leftSkin.Equals(rightSkin, StringComparison.OrdinalIgnoreCase)
               && StringComparer.OrdinalIgnoreCase.Equals(
                   left.SourceAuthor ?? "",
                   right.SourceAuthor ?? "");
    }

    private static bool EquivalentTransparentPlaceholderPacks(
        SkinExtraPackPreview left,
        SkinExtraPackPreview right)
    {
        if (!left.Manifest.FamilyId.Equals(
                right.Manifest.FamilyId,
                StringComparison.OrdinalIgnoreCase)
            || !StringComparer.OrdinalIgnoreCase.Equals(
                left.Manifest.Variant,
                right.Manifest.Variant)
            || left.Files.Count == 0
            || left.Files.Count != right.Files.Count
            || !EffectivePackContains(
                CopyManifest(left.Manifest, files: []),
                CopyManifest(right.Manifest, files: []))
            || !EffectivePackContains(
                CopyManifest(right.Manifest, files: []),
                CopyManifest(left.Manifest, files: [])))
            return false;

        return left.Files.All(file =>
        {
            var other = right.Files.FirstOrDefault(candidate =>
                candidate.Name.Equals(file.Name, StringComparison.OrdinalIgnoreCase));
            return other is not null
                   && !file.IsAudio
                   && !other.IsAudio
                   && IsTransparentManifestFile(left.Manifest, file.Name)
                   && IsTransparentManifestFile(right.Manifest, other.Name);
        });
    }

    private static bool IsTransparentManifestFile(
        SkinExtraPackManifest manifest,
        string targetFilename) =>
        manifest.Files.FirstOrDefault(file => file.TargetFilename.Equals(
            targetFilename,
            StringComparison.OrdinalIgnoreCase))?.SimilarityHash?.Equals(
            "transparent",
            StringComparison.OrdinalIgnoreCase) == true;

    private static bool SamePackSource(
        SkinExtraPackManifest left,
        SkinExtraPackManifest right) =>
        left.DisplayName.Equals(right.DisplayName, StringComparison.OrdinalIgnoreCase)
        || (!string.IsNullOrWhiteSpace(left.SourceSkin)
            && left.SourceSkin.Equals(right.SourceSkin, StringComparison.OrdinalIgnoreCase));

    private static bool EffectivePackContains(
        SkinExtraPackManifest superset,
        SkinExtraPackManifest subset) =>
        subset.Files.All(file => superset.Files.Any(candidate =>
            candidate.TargetFilename.Equals(file.TargetFilename, StringComparison.OrdinalIgnoreCase)
            && SkinExtraFingerprint.EquivalentFileContent(candidate, file)))
        && subset.IniPatch.All(entry => superset.IniPatch.Any(candidate =>
            candidate.Section.Equals(entry.Section, StringComparison.OrdinalIgnoreCase)
            && candidate.Key.Equals(entry.Key, StringComparison.OrdinalIgnoreCase)
            && candidate.ManiaKeys == entry.ManiaKeys
            && SkinExtraFingerprint.IniValuesEqual(candidate.Value, entry.Value)));

    private static IReadOnlyList<FamilyNavigationItem> BuildFamilyNavigation(
        IReadOnlyList<SkinExtraPackPreview> packs,
        SkinExtraModeVisibility visibility,
        bool lazerUsedOnly)
    {
        var counts = packs.GroupBy(pack => pack.NavigationFamilyId, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.Count(), StringComparer.OrdinalIgnoreCase);
        var result = new List<FamilyNavigationItem>
        {
            new("*", "Library", "All Extras", "", packs.Count),
        };
        result.AddRange(SkinExtraFamilyRegistry.All
            .Where(family => !family.Id.Equals("osu.number-font", StringComparison.OrdinalIgnoreCase))
            .Where(family => visibility.AllowsArea(family.Area))
            .Where(family => !lazerUsedOnly
                             || SkinExtraLazerCompatibility
                                 .FamilyCanContainLazerUsedContent(family.Id))
            .Where(family => counts.GetValueOrDefault(family.Id) > 0)
            .Select(family => new FamilyNavigationItem(
                family.Id,
                NavigationArea(family.Id, family.Area),
                family.Name,
                family.LegacyCategory,
                counts.GetValueOrDefault(family.Id)))
            .DistinctBy(item => item.FamilyId, StringComparer.OrdinalIgnoreCase));
        var numberFonts = packs.Where(pack => pack.Manifest.FamilyId.Equals(
                "osu.number-font",
                StringComparison.OrdinalIgnoreCase))
            .ToArray();
        foreach (var (role, familyId, name) in new[]
                 {
                     ("Hitcircle", "osu.number-font.hitcircle", "Hitcircle numbers"),
                     ("Score", "osu.number-font.score", "Score numbers"),
                     ("Combo", "osu.number-font.combo", "Combo numbers"),
                 })
        {
            var count = numberFonts.Count(pack =>
                NumberFontHasRole(pack.Manifest, role));
            if (count > 0)
                result.Add(new FamilyNavigationItem(
                    familyId,
                    "osu!",
                    name,
                    "Numbers",
                    count));
        }
        var otherNumberFonts = numberFonts.Count(pack =>
            !NumberFontHasRole(pack.Manifest, "Hitcircle")
            && !NumberFontHasRole(pack.Manifest, "Score")
            && !NumberFontHasRole(pack.Manifest, "Combo"));
        if (otherNumberFonts > 0)
            result.Add(new FamilyNavigationItem(
                "osu.number-font.other",
                "osu!",
                "Other number fonts",
                "Numbers",
                otherNumberFonts));
        result.AddRange(packs
            .Where(pack => SkinExtraFamilyRegistry.ById(pack.Manifest.FamilyId) is null
                           && !pack.Manifest.FamilyId.Equals(
                               "osu.number-font",
                               StringComparison.OrdinalIgnoreCase))
            .GroupBy(pack => pack.NavigationFamilyId, StringComparer.OrdinalIgnoreCase)
            .Select(group => new FamilyNavigationItem(
                group.Key,
                group.First().Manifest.Area,
                group.First().NavigationFamilyName,
                "",
                group.Count())));
        return result;
    }

    private static string NavigationArea(string familyId, string defaultArea) =>
        familyId.Equals("osu.combo-colours", StringComparison.OrdinalIgnoreCase)
        || familyId.Equals("osu.slider-colours", StringComparison.OrdinalIgnoreCase)
            ? "Colors"
            : defaultArea;

    private void FamilyList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (updatingFamilies || FamilyList.SelectedItem is not FamilyNavigationItem family)
            return;
        ShowFamily(family);
    }

    private void SearchBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        searchTimer.Stop();
        searchTimer.Start();
    }

    private void LibraryFilter_Changed(object sender, RoutedEventArgs e) => RefreshCurrentFamily();

    private void LazerFilter_Changed(object sender, RoutedEventArgs e)
    {
        if (initializingLazerFilter) return;
        lazerUsedOnly = LazerUsedOnlyCheckBox.IsChecked == true;
        lazerFilterChanged?.Invoke(lazerUsedOnly);
        LoadPacks();
    }

    private void RefreshCurrentFamily()
    {
        if (FamilyList?.SelectedItem is FamilyNavigationItem family)
            ShowFamily(family, selectedPack?.DirectoryPath);
    }

    private void ShowFamily(FamilyNavigationItem family, string? preferredPackPath = null)
    {
        var query = SearchBox?.Text.Trim() ?? "";
        var packs = (family.FamilyId == "*"
                ? allPacks
                : allPacks.Where(pack => PackBelongsToNavigationFamily(
                    pack.Manifest,
                    pack.NavigationFamilyId,
                    family.FamilyId)))
            .Where(pack => FavoritesOnlyCheckBox?.IsChecked != true || pack.State.Favorite)
            .Where(pack => PackMatches(pack, query))
            .OrderByDescending(pack => pack.State.Favorite)
            .ThenByDescending(pack => pack.State.LastUsedUtc)
            .ThenBy(pack => pack.Name, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var view = CollectionViewSource.GetDefaultView(packs);
        if (family.FamilyId == "*")
            view.GroupDescriptions.Add(
                new PropertyGroupDescription(nameof(SkinExtraPackPreview.Collection)));
        PackList.ItemsSource = view;
        TitleText.Text = family.FamilyId == "*" ? "Browse Skin Extras" : family.Name;
        HeaderPackCountText.Text = $"{packs.Length} pack{(packs.Length == 1 ? "" : "s")}";
        SubtitleText.Text = family.FamilyId == "*"
            ? "Every detected family is available here. Choose a category on the left to narrow the library."
            : $"{family.Area} · {family.PackCount} available pack{(family.PackCount == 1 ? "" : "s")}";
        var preferred = packs.FirstOrDefault(pack => pack.DirectoryPath.Equals(
            preferredPackPath,
            StringComparison.OrdinalIgnoreCase));
        if (preferred is not null)
            SelectPack(preferred, forceDisplay: true);
        else if (packs.Length > 0)
            SelectPack(packs[0], forceDisplay: true);
        else
            ShowEmptyFamily(family);
    }

    internal static bool PackBelongsToNavigationFamily(
        SkinExtraPackManifest manifest,
        string navigationFamilyId,
        string requestedFamilyId)
    {
        if (!manifest.FamilyId.Equals(
                "osu.number-font",
                StringComparison.OrdinalIgnoreCase))
            return navigationFamilyId.Equals(
                requestedFamilyId,
                StringComparison.OrdinalIgnoreCase);

        return requestedFamilyId.ToLowerInvariant() switch
        {
            "osu.number-font.hitcircle" => NumberFontHasRole(manifest, "Hitcircle"),
            "osu.number-font.score" => NumberFontHasRole(manifest, "Score"),
            "osu.number-font.combo" => NumberFontHasRole(manifest, "Combo"),
            "osu.number-font.other" => !NumberFontHasRole(manifest, "Hitcircle")
                                       && !NumberFontHasRole(manifest, "Score")
                                       && !NumberFontHasRole(manifest, "Combo"),
            _ => false,
        };
    }

    private static bool NumberFontHasRole(
        SkinExtraPackManifest manifest,
        string role)
    {
        if (manifest.FontRoles.Contains(role, StringComparer.OrdinalIgnoreCase))
            return true;
        var prefixKey = role switch
        {
            "Hitcircle" => "HitCirclePrefix",
            "Score" => "ScorePrefix",
            "Combo" => "ComboPrefix",
            _ => "",
        };
        return prefixKey.Length > 0
               && manifest.IniPatch.Any(entry =>
                   entry.Section.Equals("Fonts", StringComparison.OrdinalIgnoreCase)
                   && entry.Key.Equals(prefixKey, StringComparison.OrdinalIgnoreCase));
    }

    private static bool PackMatches(SkinExtraPackPreview pack, string query)
    {
        if (string.IsNullOrWhiteSpace(query)) return true;
        var searchable = string.Join(
            '\n',
            pack.Name,
            pack.Manifest.SourceSkin,
            pack.Manifest.SourceAuthor,
            pack.Manifest.Area,
            pack.Manifest.FamilyName,
            pack.Manifest.Variant,
            string.Join(' ', pack.State.Tags),
            string.Join(' ', pack.Manifest.Files.Select(file => file.TargetFilename)));
        return query.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .All(term => searchable.Contains(term, StringComparison.OrdinalIgnoreCase));
    }

    private void ShowEmptyFamily(FamilyNavigationItem family)
    {
        selectedPack = null;
        displayedPack = null;
        currentFallbackElements = [];
        currentFallbackPackKey = null;
        foreach (var pack in allPacks)
        {
            pack.IsSelected = false;
            pack.IsExpanded = false;
        }
        expandedPackKeys.Clear();
        selectedAudioTrack = null;
        StopAudio();
        UsePackButton.IsEnabled = false;
        FavoriteButton.IsEnabled = false;
        updatingAudioTrackSelection = true;
        AudioCurrentTrackPicker.ItemsSource = null;
        AudioPackTrackPicker.ItemsSource = null;
        updatingAudioTrackSelection = false;
        AudioPlayerPreview.Visibility = Visibility.Collapsed;
        SelectedPackName.Text = $"No {family.Name} packs";
        SelectedPackThumbnail.Source = null;
        SelectedPackSummaryText.Text = "Try another family or adjust the filters";
        SelectedPackHealthBadge.Visibility = Visibility.Collapsed;
        SelectedPackCompletenessBadge.Visibility = Visibility.Collapsed;
        SelectedPackPath.Text = AppPaths.SkinExtrasDir;
        PackDetails.Text =
            "Use “Extract skin to Extras…” in Skin Studio, or add a compatible legacy pack to the Extras folder.";
        PackNoticePanel.Visibility = Visibility.Visible;
        PackFilesExpander.Header = "Files";
        PackFilesExpander.IsExpanded = false;
        PackFilesList.ItemsSource = null;
        PackSettingsExpander.Visibility = Visibility.Collapsed;
        PackSettingsList.ItemsSource = null;
        CursorTrailCanvas.Children.Clear();
        CurrentPreviewCanvas.Children.Clear();
        ResultPreviewCanvas.Children.Clear();
        extrasCanvasAnimationStates.Clear();
        CursorPreview.Visibility = Visibility.Collapsed;
        ComparisonPreview.Visibility = Visibility.Collapsed;
        PreviewModeBar.Visibility = Visibility.Hidden;
        EmptyPreview.Visibility = Visibility.Visible;
        UpdateExtrasPreviewAnimationSubscription();
    }

    private SkinExtraPackPreview? TryCreatePack(
        SkinExtraPackDescriptor descriptor,
        int duplicateCount,
        SkinExtraPackDescriptor? source = null,
        IReadOnlyDictionary<string, SkinExtrasLibraryItemState>? libraryState = null,
        bool requestedLazerFilter = false)
    {
        var sourceDescriptor = source ?? descriptor;
        descriptor = KeepRootSourceFiles(descriptor);
        descriptor = SkinExtraPackValidator.CanonicalizeDuplicateTargets(descriptor);
        descriptor = ReclassifySingleFamily(descriptor);
        descriptor = RemoveIgnoredIniPatch(descriptor);
        var directory = descriptor.DirectoryPath;
        var physicalManifest = descriptor.Manifest;
        var isDerivedView = IsDerivedPackView(sourceDescriptor.Manifest, physicalManifest);
        var stateKey = PackStateKey(sourceDescriptor.Manifest, physicalManifest);
        var state = libraryState?.GetValueOrDefault(stateKey);
        if (state is null && isDerivedView)
        {
            var legacyStateKey = LegacyPackStateKey(
                sourceDescriptor.Manifest,
                physicalManifest);
            state = libraryState?.GetValueOrDefault(legacyStateKey);
            if (state is not null)
            {
                var migrated = state;
                SkinExtrasLibraryStateStore.Update(
                    AppPaths.SkinExtrasDir,
                    stateKey,
                    target =>
                    {
                        target.Favorite = migrated.Favorite;
                        target.Tags = migrated.Tags.ToList();
                        target.LastUsedUtc = migrated.LastUsedUtc;
                        target.DisplayNameOverride = migrated.DisplayNameOverride;
                    });
            }
        }
        state ??= SkinExtrasLibraryStateStore.Get(AppPaths.SkinExtrasDir, stateKey);
        var visibleManifest = requestedLazerFilter
            ? SkinExtraLazerCompatibility.FilterManifest(physicalManifest)
            : physicalManifest;
        if (SkinCursorMiddlePolicy.IsCursorFamily(visibleManifest.FamilyId))
        {
            visibleManifest = CopyManifest(
                visibleManifest,
                files: visibleManifest.Files
                    .Where(file => !SkinCursorMiddlePolicy.IsCursorMiddle(
                        file.TargetFilename))
                    .ToList());
        }
        var files = visibleManifest.Files
            .Select(file => (
                Path: Path.Combine(directory, file.TargetFilename.Replace('/', Path.DirectorySeparatorChar)),
                file.TargetFilename))
            .Where(item => File.Exists(item.Path) && IsSupportedAsset(item.Path))
            .ToArray();
        if (files.Length == 0 && visibleManifest.IniPatch.Count == 0)
            return null;

        var imageNames = files.Where(file => SkinElementCategorizer.IsImage(file.Path))
            .Select(file => Path.GetFileName(file.Path))
            .ToArray();
        var imagePaths = imageNames.Select(name => files.First(file =>
                Path.GetFileName(file.Path).Equals(name, StringComparison.OrdinalIgnoreCase)).Path)
            .ToArray();
        var audioPaths = files.Where(file => SkinElementCategorizer.IsAudio(file.Path))
            .Select(file => file.Path)
            .OrderBy(path => Path.GetFileName(path), StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var physicalFiles = files.Select(file => new PackFileEntry(
                file.TargetFilename,
                SkinElementCategorizer.IsImage(file.Path) ? "Image" : "Audio",
                file.Path,
                FormatFileSize(new FileInfo(file.Path).Length),
                requestedLazerFilter
                    ? ""
                    : SkinExtraLazerCompatibility.Classify(
                        file.TargetFilename,
                        physicalManifest.FamilyId) switch
                    {
                        SkinExtraCompatibility.StableOnly => "Stable only",
                        SkinExtraCompatibility.Unknown => "Unverified",
                        _ => "",
                    },
                isSelectable: !IsTransparentFollowpointFile(physicalManifest, file.TargetFilename)))
            .OrderBy(file => file.Name, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        // Decoding an image for every logical element in every pack makes the
        // Extras dialog expensive to open. Element thumbnails load only for the
        // pack the user actually opens.
        var logicalElements = BuildLogicalElements(
            visibleManifest,
            physicalFiles,
            loadThumbnails: false);
        var settings = BuildSettingEntries(visibleManifest);
        var colourOnlyName = SkinExtraNaming.UsesColourOnlyName(visibleManifest.FamilyId);
        return new SkinExtraPackPreview(
            colourOnlyName || string.IsNullOrWhiteSpace(state.DisplayNameOverride)
                ? PackDisplayName(visibleManifest)
                : state.DisplayNameOverride,
            $"{visibleManifest.Area} / {visibleManifest.FamilyName}"
            + (string.IsNullOrWhiteSpace(visibleManifest.Variant)
                ? ""
                : $" / {visibleManifest.Variant}"),
            directory,
            files.Length,
            visibleManifest,
            descriptor,
            sourceDescriptor,
            stateKey,
            isDerivedView,
            state,
            duplicateCount,
            sourceDescriptor.Manifest.FamilyId.Equals(
                physicalManifest.FamilyId,
                StringComparison.OrdinalIgnoreCase)
                ? Math.Max(0, sourceDescriptor.Manifest.Files.Count - physicalManifest.Files.Count)
                : 0,
            requestedLazerFilter
                ? ""
                : SkinExtraLazerCompatibility.CompatibilityBadge(physicalManifest),
            null,
            null,
            null,
            [],
            imagePaths,
            audioPaths,
            logicalElements,
            settings,
            physicalFiles);
    }

    private IReadOnlyList<PackElementEntry> BuildLogicalElements(
        SkinExtraPackManifest manifest,
        IReadOnlyList<PackFileEntry> files,
        bool loadThumbnails)
    {
        var filenames = files.Select(file => file.Name).ToArray();
        return files.GroupBy(
                file => SkinExtraLogicalGrouping.Key(
                    manifest.FamilyId,
                    file.Name,
                    filenames),
                StringComparer.OrdinalIgnoreCase)
            .Select(group => new PackElementEntry(
                group.Key,
                SkinExtraLogicalGrouping.DisplayName(group.Key),
                group.OrderBy(file => file.Name, StringComparer.OrdinalIgnoreCase).ToArray(),
                loadThumbnails
                    ? LoadElementThumbnail(
                        manifest,
                        group.Key,
                        group.Select(file => file.Path)
                            .Where(SkinElementCategorizer.IsImage))
                    : null))
            .OrderBy(element => element.Name, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private void PackThumbnail_Loaded(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: SkinExtraPackPreview pack })
            _ = EnsurePackPreviewAsync(pack);
    }

    private async Task EnsurePackPreviewAsync(SkinExtraPackPreview pack)
    {
        if (pack.PreviewLoaded || !loadingPackPreviews.Add(pack))
            return;

        try
        {
            await packPreviewLoadGate.WaitAsync();
            BitmapSource? thumbnail;
            try
            {
                thumbnail = await Task.Run(() => LoadPackThumbnail(pack));
            }
            finally
            {
                packPreviewLoadGate.Release();
            }

            if (disposed)
                return;
            pack.SetDeferredThumbnail(thumbnail);
            if (ReferenceEquals(pack, selectedPack))
            {
                // The first detail render is deliberately delayed until the
                // selected pack's image work has happened away from the UI
                // thread. Re-enter once to compose the cached assets.
                displayedPack = null;
                DisplayPack(pack);
            }
        }
        finally
        {
            loadingPackPreviews.Remove(pack);
        }
    }

    private BitmapSource? LoadPackThumbnail(SkinExtraPackPreview pack)
    {
        if (pack.Manifest.FamilyId.Equals("osu.hitcircles", StringComparison.OrdinalIgnoreCase)
            && ComposeHitCircleThumbnail(pack.ImagePaths, previewContext) is { } hitCircle)
            return hitCircle;

        if (SkinCursorMiddlePolicy.IsCursorFamily(pack.Manifest.FamilyId))
        {
            var assets = SkinCursorPreview.Resolve(
                pack.ImagePaths.Select(path => Path.GetFileName(path)!));
            var cursorPath = assets.CursorFilename is null
                ? null
                : pack.ImagePaths.FirstOrDefault(path =>
                    Path.GetFileName(path).Equals(
                        assets.CursorFilename,
                        StringComparison.OrdinalIgnoreCase));
            if (cursorPath is not null)
                return LoadBitmap(cursorPath, 160);
        }

        return LoadRepresentativeThumbnail(pack.ImagePaths, 160);
    }

    private BitmapSource? LoadElementThumbnail(
        SkinExtraPackManifest manifest,
        string logicalKey,
        IEnumerable<string> paths)
    {
        var thumbnail = LoadRepresentativeThumbnail(paths, 72);
        if (thumbnail is null
            || !manifest.FamilyId.Equals("osu.hitcircles", StringComparison.OrdinalIgnoreCase)
            || !logicalKey.Equals("approachcircle", StringComparison.OrdinalIgnoreCase)
               && !logicalKey.Equals("hitcircle", StringComparison.OrdinalIgnoreCase))
            return thumbnail;
        var combo = previewContext.ComboColours.FirstOrDefault();
        if (combo == default)
            combo = Color.FromRgb(80, 220, 255);
        return TintBitmap(thumbnail, combo);
    }

    private async Task EnsureElementThumbnailsAsync(SkinExtraPackPreview pack)
    {
        var pending = pack.Elements
            .Where(element => element.Thumbnail is null)
            .ToArray();
        if (pending.Length == 0)
            return;

        var version = ++elementThumbnailLoadVersion;
        var thumbnails = await Task.Run(() => pending
            .Select(element => LoadElementThumbnail(
                pack.Manifest,
                element.Key,
                element.Files.Select(file => file.Path)
                    .Where(SkinElementCategorizer.IsImage)))
            .ToArray());
        if (disposed
            || version != elementThumbnailLoadVersion
            || !ReferenceEquals(pack, selectedPack))
            return;

        for (var index = 0; index < pending.Length; index++)
        {
            pending[index].SetThumbnail(thumbnails[index]);
        }
    }

    private IReadOnlyList<PackSettingEntry> BuildSettingEntries(
        SkinExtraPackManifest manifest) =>
        manifest.IniPatch
            .Select(entry =>
            {
                var required = manifest.FamilyId.Equals(
                    "osu.number-font",
                    StringComparison.OrdinalIgnoreCase)
                    || entry.Section.Equals("Mania", StringComparison.OrdinalIgnoreCase)
                       && entry.Value is not null
                       && manifest.Files.Any(file =>
                           SkinExtraLogicalGrouping.LogicalStem(file.TargetFilename).Equals(
                               SkinExtraLogicalGrouping.LogicalStem(entry.Value),
                               StringComparison.OrdinalIgnoreCase));
                return new PackSettingEntry(
                    entry,
                    required,
                    DescribeIniPatchChange(entry, EffectiveCurrentIni) is { Length: > 0 } change
                        ? change
                        : "Already matches the current skin");
            })
            .OrderBy(setting => setting.Section, StringComparer.OrdinalIgnoreCase)
            .ThenBy(setting => setting.Key, StringComparer.OrdinalIgnoreCase)
            .ToArray();

    internal static bool IsDerivedPackView(
        SkinExtraPackManifest source,
        SkinExtraPackManifest displayed) =>
        !source.FamilyId.Equals(displayed.FamilyId, StringComparison.OrdinalIgnoreCase)
        || !string.Equals(source.Variant, displayed.Variant, StringComparison.OrdinalIgnoreCase)
        || source.Files.Count != displayed.Files.Count
        || source.IniPatch.Count != displayed.IniPatch.Count;

    internal static string PackStateKey(
        SkinExtraPackManifest source,
        SkinExtraPackManifest displayed) =>
        IsDerivedPackView(source, displayed)
            ? $"view:{source.Id}:{displayed.FamilyId}:{displayed.Variant ?? ""}"
            : displayed.Fingerprint;

    internal static string LegacyPackStateKey(
        SkinExtraPackManifest source,
        SkinExtraPackManifest displayed) =>
        IsDerivedPackView(source, displayed)
            ? $"{source.Fingerprint}:{displayed.FamilyId}:{displayed.Variant ?? ""}"
            : displayed.Fingerprint;

    private static IReadOnlyList<SkinExtraPackDescriptor> SplitDisplayFamilies(
        SkinExtraPackDescriptor descriptor)
    {
        var pending = new List<SkinExtraPackDescriptor> { descriptor };
        if (descriptor.Manifest.FamilyId.Equals("osu.slider", StringComparison.OrdinalIgnoreCase))
        {
            var colourKeys = SkinExtraFamilyRegistry.ById("osu.slider-colours")!.IniKeys
                .Select(key => $"{key.Section}\0{key.Key}")
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            var colourPatch = descriptor.Manifest.IniPatch.Where(entry =>
                    colourKeys.Contains($"{entry.Section}\0{entry.Key}"))
                .ToList();
            var sliderPatch = descriptor.Manifest.IniPatch.Where(entry =>
                    !colourKeys.Contains($"{entry.Section}\0{entry.Key}"))
                .ToList();
            pending = colourPatch.Count == 0
                ? [descriptor]
                :
                [
                new SkinExtraPackDescriptor(
                    descriptor.DirectoryPath,
                    CopyManifest(descriptor.Manifest, iniPatch: sliderPatch),
                    descriptor.IsLegacy),
                new SkinExtraPackDescriptor(
                    descriptor.DirectoryPath,
                    CopyManifest(
                        descriptor.Manifest,
                        "osu.slider-colours",
                        "osu!",
                        "Slider colours",
                        [],
                        colourPatch),
                    descriptor.IsLegacy),
                ];
        }

        pending = pending.SelectMany(SplitAudioDisplayFamilies).ToList();

        var result = new List<SkinExtraPackDescriptor>();
        foreach (var candidate in pending)
        {
            if (!candidate.Manifest.FamilyId.Equals(
                    "osu.hitbursts",
                    StringComparison.OrdinalIgnoreCase))
            {
                result.Add(candidate);
                continue;
            }
            var stable = candidate.Manifest.Files.Where(file =>
                    SkinExtraFamilyRegistry.ForFile(file.TargetFilename)?.Id.Equals(
                        "osu.result-judgements",
                        StringComparison.OrdinalIgnoreCase) == true)
                .ToList();
            if (stable.Count == 0 || stable.Count == candidate.Manifest.Files.Count)
            {
                result.Add(candidate);
                continue;
            }
            var gameplay = candidate.Manifest.Files.Except(stable).ToList();
            result.Add(new SkinExtraPackDescriptor(
                candidate.DirectoryPath,
                CopyManifest(candidate.Manifest, files: gameplay),
                candidate.IsLegacy));
            result.Add(new SkinExtraPackDescriptor(
                candidate.DirectoryPath,
                CopyManifest(
                    candidate.Manifest,
                    "osu.result-judgements",
                    "osu!",
                    "Result judgements (stable)",
                    stable,
                    []),
                candidate.IsLegacy));
        }
        return result;
    }

    private static IEnumerable<SkinExtraPackDescriptor> SplitAudioDisplayFamilies(
        SkinExtraPackDescriptor descriptor)
    {
        if (!descriptor.Manifest.FamilyId.Equals("audio.other", StringComparison.OrdinalIgnoreCase)
            && !descriptor.Manifest.FamilyId.Equals(
                "audio.gameplay",
                StringComparison.OrdinalIgnoreCase))
        {
            yield return descriptor;
            yield break;
        }

        var groups = descriptor.Manifest.Files
            .GroupBy(
                file => SkinExtraFamilyRegistry.ForFile(file.TargetFilename)?.Id
                        ?? descriptor.Manifest.FamilyId,
                StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (groups.Length == 1
            && groups[0].Key.Equals(
                descriptor.Manifest.FamilyId,
                StringComparison.OrdinalIgnoreCase))
        {
            yield return descriptor;
            yield break;
        }

        foreach (var group in groups)
        {
            var definition = SkinExtraFamilyRegistry.ById(group.Key);
            if (definition is null)
                continue;
            yield return new SkinExtraPackDescriptor(
                descriptor.DirectoryPath,
                CopyManifest(
                    descriptor.Manifest,
                    definition.Id,
                    definition.Area,
                    definition.Name,
                    group.ToList(),
                    []),
                descriptor.IsLegacy);
        }
    }

    private static string PackDisplayName(SkinExtraPackManifest manifest)
    {
        if (SkinExtraNaming.UsesColourOnlyName(manifest.FamilyId))
            return SkinExtraNaming.DisplayNameForPack(manifest);
        if (!manifest.FamilyId.Equals("osu.number-font", StringComparison.OrdinalIgnoreCase))
            return manifest.DisplayName;
        var role = manifest.FontRoles.Count > 0
            ? string.Join(" + ", manifest.FontRoles)
            : manifest.Variant ?? "Number font";
        return $"{manifest.DisplayName} — {role}";
    }

    private static SkinExtraPackDescriptor KeepRootSourceFiles(
        SkinExtraPackDescriptor descriptor)
    {
        var rootFiles = descriptor.Manifest.Files
            .Where(file => SkinExtrasExtractionService.IsRootSourceFile(file.SourceFilename))
            .ToList();
        if (rootFiles.Count == descriptor.Manifest.Files.Count)
            return descriptor;
        return new SkinExtraPackDescriptor(
            descriptor.DirectoryPath,
            CopyManifest(descriptor.Manifest, files: rootFiles),
            descriptor.IsLegacy);
    }

    private static SkinExtraPackDescriptor ReclassifySingleFamily(
        SkinExtraPackDescriptor descriptor)
    {
        if (descriptor.Manifest.Files.Count == 0)
            return descriptor;
        var families = descriptor.Manifest.Files
            .Select(file => SkinExtraFamilyRegistry.ForFile(file.TargetFilename))
            .ToArray();
        if (families.Any(family => family is null))
            return descriptor;
        var family = families[0]!;
        if (families.Any(candidate =>
                !candidate!.Id.Equals(family.Id, StringComparison.OrdinalIgnoreCase))
            || family.Id.Equals(descriptor.Manifest.FamilyId, StringComparison.OrdinalIgnoreCase))
            return descriptor;

        var ownedSettings = family.IniKeys
            .Select(key => $"{key.Section}\0{key.Key}\0{key.ManiaKeys}")
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var patch = descriptor.Manifest.IniPatch.Where(entry =>
                ownedSettings.Contains($"{entry.Section}\0{entry.Key}\0{entry.ManiaKeys}"))
            .ToList();
        return new SkinExtraPackDescriptor(
            descriptor.DirectoryPath,
            CopyManifest(
                descriptor.Manifest,
                family.Id,
                family.Area,
                family.Name,
                descriptor.Manifest.Files,
                patch),
            descriptor.IsLegacy);
    }

    private static SkinExtraPackDescriptor RemoveIgnoredIniPatch(
        SkinExtraPackDescriptor descriptor)
    {
        if (!descriptor.Manifest.FamilyId.Equals(
                "interface.menu",
                StringComparison.OrdinalIgnoreCase))
            return descriptor;
        var patch = descriptor.Manifest.IniPatch
            .Where(entry => !entry.Section.Equals(
                                "Colours",
                                StringComparison.OrdinalIgnoreCase)
                            || !entry.Key.Equals(
                                "MenuGlow",
                                StringComparison.OrdinalIgnoreCase))
            .ToList();
        return patch.Count == descriptor.Manifest.IniPatch.Count
            ? descriptor
            : new SkinExtraPackDescriptor(
                descriptor.DirectoryPath,
                CopyManifest(descriptor.Manifest, iniPatch: patch),
                descriptor.IsLegacy);
    }

    private static SkinExtraPackManifest CopyManifest(
        SkinExtraPackManifest manifest,
        string? familyId = null,
        string? area = null,
        string? familyName = null,
        List<SkinExtraManifestFile>? files = null,
        List<SkinExtraIniPatchEntry>? iniPatch = null,
        string? fingerprint = null) =>
        new()
        {
            SchemaVersion = manifest.SchemaVersion,
            Id = manifest.Id,
            DisplayName = manifest.DisplayName,
            FamilyId = familyId ?? manifest.FamilyId,
            Area = area ?? manifest.Area,
            FamilyName = familyName ?? manifest.FamilyName,
            Variant = manifest.Variant,
            SourceSkin = manifest.SourceSkin,
            SourceAuthor = manifest.SourceAuthor,
            Fingerprint = fingerprint ?? manifest.Fingerprint,
            ExtractedAt = manifest.ExtractedAt,
            Files = files ?? manifest.Files.ToList(),
            IniPatch = iniPatch ?? manifest.IniPatch.ToList(),
            FontRoles = manifest.FontRoles.ToList(),
        };

    private static string FormatFileSize(long bytes) => bytes switch
    {
        < 1024 => $"{bytes} B",
        < 1024 * 1024 => $"{bytes / 1024d:0.#} KB",
        _ => $"{bytes / (1024d * 1024d):0.#} MB",
    };

    private static BitmapSource? LoadBitmap(string path, int maxLogicalPixelWidth = 0)
    {
        try
        {
            var fullPath = Path.GetFullPath(path);
            var info = new FileInfo(fullPath);
            var isHighDefinition = Path.GetFileNameWithoutExtension(fullPath)
                .EndsWith("@2x", StringComparison.OrdinalIgnoreCase);
            var cacheKey = $"{fullPath}\0{maxLogicalPixelWidth}";
            lock (BitmapCacheGate)
            {
                if (BitmapCache.TryGetValue(cacheKey, out var cached)
                    && cached.Length == info.Length
                    && cached.LastWriteUtcTicks == info.LastWriteTimeUtc.Ticks)
                {
                    BitmapCache[cacheKey] = cached with
                    {
                        LastAccess = ++bitmapCacheSequence,
                    };
                    return cached.Image;
                }
            }

            var decodePixelWidth = maxLogicalPixelWidth <= 0
                ? 0
                : maxLogicalPixelWidth * (isHighDefinition ? 2 : 1);
            BitmapSource bitmap = SkinImageTools.Decode(
                File.ReadAllBytes(fullPath),
                decodePixelWidth);
            if (isHighDefinition)
            {
                bitmap = new TransformedBitmap(bitmap, new ScaleTransform(0.5, 0.5));
                bitmap.Freeze();
            }

            var bytes = (long)bitmap.PixelWidth * bitmap.PixelHeight * 4;
            lock (BitmapCacheGate)
            {
                if (BitmapCache.Remove(cacheKey, out var previous))
                    bitmapCacheBytes -= previous.Bytes;
                BitmapCache[cacheKey] = new CachedBitmap(
                    bitmap,
                    info.Length,
                    info.LastWriteTimeUtc.Ticks,
                    bytes,
                    ++bitmapCacheSequence);
                bitmapCacheBytes += bytes;
                TrimBitmapCache();
            }
            return bitmap;
        }
        catch { return null; }
    }

    private static BitmapSource? LoadRepresentativeThumbnail(
        IEnumerable<string> paths,
        int maxLogicalPixelWidth)
    {
        BitmapSource? decodedFallback = null;
        foreach (var path in PreferredPreviewPaths(paths))
        {
            var bitmap = LoadBitmap(path, maxLogicalPixelWidth);
            if (bitmap is null)
                continue;
            decodedFallback ??= bitmap;
            if (VisibleCrop(bitmap) is { } visible)
                return visible;
        }
        return decodedFallback;
    }

    private static void TrimBitmapCache()
    {
        while (bitmapCacheBytes > BitmapCacheBudget && BitmapCache.Count > 1)
        {
            var oldest = BitmapCache.MinBy(pair => pair.Value.LastAccess);
            if (!BitmapCache.Remove(oldest.Key, out var removed))
                break;
            bitmapCacheBytes -= removed.Bytes;
        }
    }

    private static IEnumerable<string> CursorCandidates(IEnumerable<string> files)
    {
        yield return "cursor.png";
        yield return "cursor@2x.png";
        foreach (var name in files
                     .Select(Path.GetFileName)
                     .Where(name => name is not null
                                    && name.StartsWith("cursor-", StringComparison.OrdinalIgnoreCase))
                     .OrderBy(name => name, StringComparer.OrdinalIgnoreCase))
        {
            yield return name!;
        }
    }

    private static bool IsSupportedAsset(string path) =>
        SkinElementCategorizer.ImageExtensions.Contains(
            Path.GetExtension(path),
            StringComparer.OrdinalIgnoreCase)
        || SkinElementCategorizer.AudioExtensions.Contains(
            Path.GetExtension(path),
            StringComparer.OrdinalIgnoreCase);

    private static bool IsTransparentFollowpointFile(
        SkinExtraPackManifest manifest,
        string targetFilename) =>
        manifest.FamilyId.Equals("osu.followpoints", StringComparison.OrdinalIgnoreCase)
        && manifest.Files.FirstOrDefault(file => file.TargetFilename.Equals(
                targetFilename,
                StringComparison.OrdinalIgnoreCase))?.SimilarityHash?.Equals(
                "transparent",
                StringComparison.OrdinalIgnoreCase) == true;

    private void SelectPack(
        SkinExtraPackPreview pack,
        bool forceDisplay = false)
    {
        if (!ReferenceEquals(selectedPack, pack))
            StopAudio();
        foreach (var candidate in allPacks)
        {
            var isSelected = ReferenceEquals(candidate, pack);
            candidate.IsSelected = isSelected;
            if (isSelected)
                continue;
            candidate.IsExpanded = false;
            expandedPackKeys.Remove(candidate.PackKey);
        }
        selectedPack = pack;
        if (forceDisplay)
            displayedPack = null;
        DisplayPack(pack);
    }

    private void DisplayPack(SkinExtraPackPreview pack)
    {
        if (ReferenceEquals(displayedPack, pack))
            return;
        displayedPack = pack;
        ConfigurePreviewCanvas(CursorTrailCanvas, pack.Manifest.FamilyId);
        ConfigurePreviewCanvas(CurrentPreviewCanvas, pack.Manifest.FamilyId);
        ConfigurePreviewCanvas(ResultPreviewCanvas, pack.Manifest.FamilyId);
        _ = EnsurePackPreviewAsync(pack);
        RememberCurrentFallbackSelections();
        currentFallbackElements = [];
        currentFallbackPackKey = null;
        CurrentSkinPreviewLabel.Text = currentSkinSource?.HasStagedChanges == true
            ? "CURRENT + CHANGES"
            : "CURRENT SKIN";
        SelectedPackName.Text = pack.Name;
        SelectedPackThumbnail.Source = pack.Thumbnail;
        SelectedPackSummaryText.Text = string.IsNullOrWhiteSpace(pack.Manifest.SourceAuthor)
            ? pack.SelectionText
            : $"by {pack.Manifest.SourceAuthor} · {pack.SelectionText}";
        SelectedPackPath.Text = pack.DirectoryPath;
        UpdateSelectedPackUsage(pack);
        PreviewPackChanged?.Invoke(
            this,
            new SkinExtrasPreviewPackChangedEventArgs(
                pack.Descriptor,
                SmoothTrailCheckBox.IsChecked == true));
        _ = EnsureElementThumbnailsAsync(pack);
        var cursorFamily = SkinCursorMiddlePolicy.IsCursorFamily(
            pack.Manifest.FamilyId);
        NativePreviewControls.Visibility = rendererTargetVisible && cursorFamily
            ? Visibility.Visible
            : Visibility.Collapsed;
        NativeCursorMotionToggle.Visibility = cursorFamily
            ? Visibility.Visible
            : Visibility.Collapsed;
        NativeCursorMotionToggle.IsChecked = cursorFamily;
        // The embedded lazer renderer owns Extras previews while mounted. Do not
        // spend time composing the hidden WPF fallback behind its opaque surface.
        var hasVisualPreview = rendererTargetVisible
                               || (pack.PreviewLoaded && RenderPackOnlyPreview(pack));
        var hasAudioPreview = RenderAudioPreview(pack);
        PreviewCaption.Text =
            $"{pack.FileCountText} - {pack.Collection}"
            + (pack.Manifest.IniPatch.Count == 0
                ? ""
                : $" - {pack.Manifest.IniPatch.Count} scoped setting(s)")
            + (pack.DuplicateCount > 1 ? $" - {pack.DuplicateCount} overlapping copies collapsed" : "");
        var health = SkinExtraPackValidator.Validate(pack.Descriptor, verifyContent: false);
        var preflight = SkinStudioEffectiveAssetResolver.BuildPreflight(
            pack.Manifest.Files,
            pack.Manifest.FamilyId);
        var completeness = SkinExtraCompleteness.Analyze(
            pack.Manifest.FamilyId,
            pack.Files.Select(file => file.Name));
        SelectedPackHealthBadge.Visibility = Visibility.Visible;
        SelectedPackHealthText.Text = health.Issues.Count == 0 && preflight.Issues.Count == 0
            ? "Healthy"
            : health.Errors + preflight.Issues.Count(issue => issue.Severity == "Error") is var errors
              && errors > 0
                ? $"{errors} error{(errors == 1 ? "" : "s")}"
                : $"{health.Warnings + preflight.Issues.Count} warning(s)";
        SelectedPackHealthText.Foreground = TryFindResource(
            health.Issues.Count == 0 ? "Brush.Success" : "Brush.AccentPink") as Brush
            ?? Brushes.White;
        SelectedPackCompletenessBadge.Visibility = Visibility.Visible;
        SelectedPackCompletenessText.Text = completeness.IsComplete
            ? "Complete"
            : $"Missing {completeness.MissingSummary}";
        SelectedPackCompletenessText.Foreground = TryFindResource(
            completeness.IsComplete ? "Brush.Success" : "Brush.AccentPink") as Brush
            ?? Brushes.White;
        PackDetails.Text = "";
        PackNoticePanel.Visibility = Visibility.Collapsed;
        if (preflight.Issues.Count > 0)
        {
            PackDetails.Text = preflight.Summary + "\n"
                + string.Join("\n", preflight.Issues.Take(5)
                    .Select(issue => $"• {issue.Message}"));
            PackNoticePanel.Visibility = Visibility.Visible;
        }
        PackFilesExpander.Header =
            $"Elements ({pack.SelectedElementCount}) · Files "
            + $"({pack.SelectedFileCount}/{pack.FileCount})";
        SelectedPackSummaryText.Text = string.IsNullOrWhiteSpace(pack.Manifest.SourceAuthor)
            ? pack.SelectionText
            : $"by {pack.Manifest.SourceAuthor} · {pack.SelectionText}";
        // Keep the selected pack's elements in view. The preview is deliberately
        // capped so this list no longer has to start collapsed to save space.
        PackFilesExpander.IsExpanded = true;
        PackFilesList.ItemsSource = pack.Elements;
        EmptyPreview.Visibility = hasVisualPreview || hasAudioPreview
            ? Visibility.Collapsed
            : Visibility.Visible;
        var comparisonActive = currentSkinSource is not null
                               && (ComparePreviewButton.IsChecked == true
                                   || CurrentSkinPreviewButton.IsChecked == true);
        CursorPreview.Visibility = !comparisonActive && hasVisualPreview
            ? Visibility.Visible
            : Visibility.Collapsed;
        ComparisonPreview.Visibility = comparisonActive && hasVisualPreview
            ? Visibility.Visible
            : Visibility.Collapsed;
        if (comparisonActive)
        {
            CurrentPreviewCanvas.Children.Clear();
            ResultPreviewCanvas.Children.Clear();
        }
        PreviewModeBar.Visibility = rendererTargetVisible
            ? Visibility.Hidden
            : hasVisualPreview
            ? Visibility.Visible
            : Visibility.Hidden;
        FavoriteButton.IsEnabled = true;
        FavoriteButton.Content = pack.State.Favorite ? "★" : "☆";
        FavoriteButton.ToolTip = pack.State.Favorite
            ? "Remove this pack from favorites"
            : "Keep this pack at the top of its family";
        RenamePackButton.IsEnabled =
            !SkinExtraNaming.UsesColourOnlyName(pack.Manifest.FamilyId);
        RenamePackButton.ToolTip = RenamePackButton.IsEnabled
            ? "Rename this Extras pack"
            : "Colour packs are always named from their colour values";
        PackSettingsList.ItemsSource = pack.Settings;
        PackSettingsExpander.Header =
            $"Settings ({pack.SelectedSettingCount}/{pack.Settings.Count})";
        PackSettingsExpander.Visibility = pack.Settings.Count == 0
            ? Visibility.Collapsed
            : Visibility.Visible;
        CursorOptionsPanel.Visibility =
            SkinCursorMiddlePolicy.IsCursorFamily(pack.Manifest.FamilyId)
                ? Visibility.Visible
                : Visibility.Collapsed;
        UpdateSelectionUi(pack);
        ResetExtrasPreviewAnimation();
        UpdateExtrasPreviewAnimationSubscription();
        _ = RefreshComparisonPreviewAsync(pack);
    }

    private void PackFilesExpander_Expanded(object sender, RoutedEventArgs e)
    {
        if (selectedPack is not null)
            _ = EnsureElementThumbnailsAsync(selectedPack);
    }

    internal static string DescribeIniPatchChanges(
        SkinExtraPackManifest manifest,
        SkinIniDocument? currentIni)
    {
        var changes = new List<string>();
        foreach (var entry in manifest.IniPatch)
        {
            var change = DescribeIniPatchChange(entry, currentIni);
            if (change.Length == 0)
                continue;
            changes.Add(change);
        }

        return changes.Count == 0
            ? "No skin.ini values will change; the current values already match this pack."
            : "skin.ini values this pack will change:\n\n" + string.Join('\n', changes);
    }

    private static string DescribeIniPatchChange(
        SkinExtraIniPatchEntry entry,
        SkinIniDocument? currentIni)
    {
        string? currentValue;
        var section = entry.Section;
        if (entry.Section.Equals("Mania", StringComparison.OrdinalIgnoreCase)
            && entry.ManiaKeys is { } maniaKeys)
        {
            section = $"Mania {maniaKeys}K";
            var instance = currentIni?.GetSections("Mania")
                .FirstOrDefault(candidate => candidate.ManiaKeys == maniaKeys);
            currentValue = instance is not null
                           && instance.Values.TryGetValue(entry.Key, out var value)
                ? value
                : null;
        }
        else
        {
            currentValue = currentIni?.GetValue(entry.Section, entry.Key);
        }

        if (string.Equals(currentValue, entry.Value, StringComparison.OrdinalIgnoreCase))
            return "";
        return $"[{section}] {entry.Key}: {currentValue ?? "(not set)"} → "
               + (entry.Value ?? "(remove)");
    }

    private bool RenderAudioPreview(SkinExtraPackPreview pack)
    {
        activeAudioFamilyId = pack.Manifest.FamilyId;
        var cues = pack.Files
            .Where(file => file.IsSelected && file.IsAudio)
            .Select(file => new AudioAuditionCue(
                AudioPadLabel(file.Name),
                file.Name,
                BeforePath: null,
                AfterPath: file.Path))
            .ToArray();
        RenderAudioCues(cues, pack);
        return cues.Length > 0;
    }

    private void RenderAudioComparison(
        IReadOnlyDictionary<string, string> current,
        IReadOnlyDictionary<string, string> integrated,
        SkinExtraPackPreview pack)
    {
        if (!pack.Manifest.Area.Equals("Audio", StringComparison.OrdinalIgnoreCase))
            return;
        activeAudioFamilyId = pack.Manifest.FamilyId;
        var filenames = integrated.Keys.Where(filename =>
                SkinElementCategorizer.AudioExtensions.Contains(
                    Path.GetExtension(filename),
                    StringComparer.OrdinalIgnoreCase))
            .Concat(current.Keys.Where(filename =>
                SkinElementCategorizer.AudioExtensions.Contains(
                    Path.GetExtension(filename),
                    StringComparer.OrdinalIgnoreCase)))
            .Distinct(StringComparer.OrdinalIgnoreCase);
        var cues = filenames
            .Select(filename => new AudioAuditionCue(
                AudioPadLabel(filename),
                filename,
                current.GetValueOrDefault(filename),
                integrated.GetValueOrDefault(filename)))
            .ToArray();
        RenderAudioCues(cues, pack);
    }

    private void RenderAudioCues(
        IReadOnlyList<AudioAuditionCue> cues,
        SkinExtraPackPreview pack)
    {
        var selectedPath = selectedAudioTrack?.Path;
        var currentTracks = cues
            .Where(cue => cue.BeforePath is not null)
            .Select(cue => new AudioTrackOption(
                cue.Label,
                cue.Filename,
                cue.BeforePath!,
                "Current skin"))
            .ToArray();
        var packTracks = cues
            .Where(cue => cue.AfterPath is not null)
            .Select(cue => new AudioTrackOption(
                cue.Label,
                cue.Filename,
                cue.AfterPath!,
                "With selection"))
            .ToArray();

        var hasTracks = currentTracks.Length > 0 || packTracks.Length > 0;
        AudioPlayerPreview.Visibility = hasTracks
            ? Visibility.Visible
            : Visibility.Collapsed;
        AudioCurrentEmptyText.Visibility = currentTracks.Length == 0
            ? Visibility.Visible
            : Visibility.Collapsed;
        AudioPackEmptyText.Visibility = packTracks.Length == 0
            ? Visibility.Visible
            : Visibility.Collapsed;

        updatingAudioTrackSelection = true;
        AudioCurrentTrackPicker.ItemsSource = currentTracks;
        AudioPackTrackPicker.ItemsSource = packTracks;
        var selection = currentTracks
                            .Concat(packTracks)
                            .FirstOrDefault(track => string.Equals(
                                track.Path,
                                selectedPath,
                                StringComparison.OrdinalIgnoreCase))
                        ?? packTracks.FirstOrDefault()
                        ?? currentTracks.FirstOrDefault();
        AudioCurrentTrackPicker.SelectedItem =
            selection is not null && selection.SourceLabel == "Current skin"
                ? selection
                : null;
        AudioPackTrackPicker.SelectedItem =
            selection is not null && selection.SourceLabel == "With selection"
                ? selection
                : null;
        updatingAudioTrackSelection = false;

        ConfigureHitsoundAudition(pack, currentTracks, packTracks);

        if (selection is null)
        {
            selectedAudioTrack = null;
            StopAudio();
            SetAudioPlaybackUi(null);
            return;
        }

        SelectAudioTrack(selection, autoplay: false);
    }

    private void ConfigureHitsoundAudition(
        SkinExtraPackPreview pack,
        IReadOnlyList<AudioTrackOption> currentTracks,
        IReadOnlyList<AudioTrackOption> packTracks)
    {
        var usePack = packTracks.Count > 0;
        var layered = EffectiveLayeredHitSounds(pack, withSelection: usePack);
        var plan = SkinAudioScenarioAudition.Build(pack.Manifest.FamilyId, layered);
        if (plan is null)
        {
            AudioHitsoundMapText.Visibility = Visibility.Collapsed;
            AudioPlayPackButton.Visibility = Visibility.Collapsed;
            AudioPlayPackButton.IsEnabled = false;
            return;
        }

        AudioHitsoundMapText.Text = "osu! scenario · "
            + SkinAudioScenarioAudition.Describe(plan)
            + (SkinHitsoundAudition.IsHitsoundFamily(pack.Manifest.FamilyId)
                ? " · LayeredHitSounds " + (layered ? "on" : "off")
                : "");
        AudioHitsoundMapText.Visibility = Visibility.Visible;
        AudioPlayPackButton.Content = "Play osu! scenario";
        AudioPlayPackButton.Visibility = Visibility.Visible;
        AudioPlayPackButton.IsEnabled = ResolveAudioSequence(
            plan,
            usePack ? packTracks : currentTracks).Count > 0;
    }

    private bool EffectiveLayeredHitSounds(
        SkinExtraPackPreview pack,
        bool withSelection)
    {
        var value = EffectiveCurrentIni?.GetValue("General", "LayeredHitSounds");
        if (withSelection)
        {
            var patch = SelectedPatch(pack).LastOrDefault(entry =>
                entry.Section.Equals("General", StringComparison.OrdinalIgnoreCase)
                && entry.Key.Equals("LayeredHitSounds", StringComparison.OrdinalIgnoreCase));
            if (patch is not null)
                value = patch.Value;
        }
        return value is null
               || (!value.Trim().Equals("0", StringComparison.OrdinalIgnoreCase)
                   && !value.Trim().Equals("false", StringComparison.OrdinalIgnoreCase));
    }

    private static IReadOnlyList<AudioSequenceStep> ResolveAudioSequence(
        SkinHitsoundAuditionPlan plan,
        IReadOnlyList<AudioTrackOption> tracks)
    {
        var byComponent = tracks
            .GroupBy(
                track => Path.GetFileNameWithoutExtension(track.Filename),
                StringComparer.OrdinalIgnoreCase)
            .ToDictionary(
                group => group.Key,
                group => group.First(),
                StringComparer.OrdinalIgnoreCase);
        var result = new List<AudioSequenceStep>();
        foreach (var step in plan.Steps)
        {
            // The final component is the event-specific sample. Do not turn a
            // missing whistle/finish/clap into another normal hit.
            if (!byComponent.ContainsKey(step.Components[^1]))
                continue;
            var resolved = step.Components
                .Select(component => byComponent.GetValueOrDefault(component))
                .OfType<AudioTrackOption>()
                .DistinctBy(track => track.Path, StringComparer.OrdinalIgnoreCase)
                .ToArray();
            if (resolved.Length > 0)
                result.Add(new AudioSequenceStep(step.Label, resolved));
        }
        return result;
    }

    private void SelectAudioTrack(AudioTrackOption track, bool autoplay)
    {
        selectedAudioTrack = track;
        updatingAudioTrackSelection = true;
        if (track.SourceLabel == "Current skin")
        {
            AudioCurrentTrackPicker.SelectedItem = track;
            AudioPackTrackPicker.SelectedItem = null;
        }
        else
        {
            AudioCurrentTrackPicker.SelectedItem = null;
            AudioPackTrackPicker.SelectedItem = track;
        }
        updatingAudioTrackSelection = false;

        if (audioStream != 0
            && !string.Equals(
                playingAudioPath,
                track.Path,
                StringComparison.OrdinalIgnoreCase))
            StopAudio();
        else if (audioStream == 0)
            ResetAudioProgress();

        AudioPlayPauseButton.IsEnabled = true;
        SetAudioPlaybackUi(null);
        if (autoplay)
            PlayAudio(track.Path, $"{track.SourceLabel} · {track.Label}");
    }

    private AudioPlaybackRequest? SelectedAudioRequest()
    {
        return selectedAudioTrack is null
            ? null
            : new AudioPlaybackRequest(
                selectedAudioTrack.Path,
                $"{selectedAudioTrack.SourceLabel} · {selectedAudioTrack.Label}");
    }

    internal static string AudioPadLabel(string path)
    {
        var stem = Path.GetFileNameWithoutExtension(path).ToLowerInvariant();
        foreach (var prefix in new[] { "normal-", "soft-", "drum-" })
            if (stem.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                return $"{HumanizeAudioStem(prefix[..^1])} · "
                       + HumanizeAudioStem(stem[prefix.Length..]);
        return HumanizeAudioStem(stem);
    }

    private static string HumanizeAudioStem(string stem)
    {
        var readable = stem
            .Replace("combobreak", "combo break", StringComparison.Ordinal)
            .Replace("spinnerspin", "spinner spin", StringComparison.Ordinal)
            .Replace("spinnerbonus", "spinner bonus", StringComparison.Ordinal)
            .Replace("sectionpass", "section pass", StringComparison.Ordinal)
            .Replace("sectionfail", "section fail", StringComparison.Ordinal)
            .Replace("failsound", "fail sound", StringComparison.Ordinal)
            .Replace("hitnormal", "hit normal", StringComparison.Ordinal)
            .Replace("hitwhistle", "hit whistle", StringComparison.Ordinal)
            .Replace("hitfinish", "hit finish", StringComparison.Ordinal)
            .Replace("hitclap", "hit clap", StringComparison.Ordinal)
            .Replace('-', ' ')
            .Replace('_', ' ')
            .Trim();
        return readable.Length == 0
            ? "Audio"
            : char.ToUpperInvariant(readable[0]) + readable[1..];
    }

    private bool RenderPackOnlyPreview(SkinExtraPackPreview pack)
    {
        var selectedFiles = pack.Files.Where(file => file.IsSelected)
            .ToDictionary(file => file.Name, file => file.Path, StringComparer.OrdinalIgnoreCase);
        if (SkinCursorMiddlePolicy.IsCursorFamily(pack.Manifest.FamilyId)
            && SmoothTrailCheckBox.IsChecked == true)
            selectedFiles[SkinCursorMiddlePolicy.CanonicalFilename] =
                SmoothTrailPreviewPath();
        var selected = CreatePreviewPack(
            pack,
            selectedFiles,
            SelectedPatch(pack),
            applyElementTints: true);
        var rendered = RenderPackToCanvas(selected, CursorTrailCanvas);
        PreviewModeBar.Visibility = rendererTargetVisible
            ? Visibility.Hidden
            : rendered ? Visibility.Visible : Visibility.Hidden;
        if (currentSkinSource is null)
        {
            ComparePreviewButton.IsChecked = false;
            CurrentSkinPreviewButton.IsChecked = false;
            PackOnlyPreviewButton.IsChecked = true;
            ComparisonPreview.Visibility = Visibility.Collapsed;
            CursorPreview.Visibility = rendered ? Visibility.Visible : Visibility.Collapsed;
        }
        return rendered;
    }

    private string SmoothTrailPreviewPath()
    {
        var directory = Path.Combine(previewTempRoot, "generated");
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, SkinCursorMiddlePolicy.CanonicalFilename);
        if (!File.Exists(path))
            File.WriteAllBytes(path, SkinCursorMiddlePolicy.CreateSmoothTrailPng());
        return path;
    }

    private bool RenderPackToCanvas(SkinExtraPackPreview pack, Canvas canvas)
    {
        ConfigurePreviewCanvas(canvas, pack.Manifest.FamilyId);
        extrasCanvasAnimationStates[canvas] = new ExtrasCanvasAnimationState(
            pack.Manifest.FamilyId,
            UsesSmoothCursorTrail(pack.MiddleImage),
            PackBoolean(pack.Manifest, "General", "CursorExpand", defaultValue: true),
            PackBoolean(pack.Manifest, "General", "CursorRotate", defaultValue: true),
            PackBoolean(pack.Manifest, "General", "CursorTrailRotate", defaultValue: true),
            PackBoolean(pack.Manifest, "General", "SliderBallFlip", defaultValue: true),
            IsLegacyVersionOne(pack.Manifest),
            PackBoolean(pack.Manifest, "General", "SpinnerNoBlink", defaultValue: false))
        {
            CursorCentre = PackBoolean(
                pack.Manifest,
                "General",
                "CursorCentre",
                defaultValue: true),
            HasSpinnerMiddle2 = pack.ImagePaths.Any(path =>
                LogicalImageStem(path).Equals(
                    "spinner-middle2",
                    StringComparison.OrdinalIgnoreCase)),
        };
        var previous = activePreviewCanvas;
        var previousHighlight = activePreviewHighlightElement;
        activePreviewCanvas = canvas;
        activePreviewHighlightElement = null;
        try
        {
            if (pack.Manifest.FamilyId.Equals("osu.cursor", StringComparison.OrdinalIgnoreCase))
            {
                activePreviewCanvas.Children.Clear();
                var hasCursor = pack.CursorImage is not null
                                || pack.TrailImage is not null
                                || pack.MiddleImage is not null;
                if (hasCursor)
                    RenderCursorPreview(pack);
                return hasCursor;
            }
            return RenderFamilyPreview(pack);
        }
        finally
        {
            activePreviewCanvas = previous;
            activePreviewHighlightElement = previousHighlight;
        }
    }

    private static void ConfigurePreviewCanvas(Canvas canvas, string familyId)
    {
        var dimensions = PreviewCanvasDimensions(familyId);
        canvas.Width = dimensions.Width;
        canvas.Height = dimensions.Height;
    }

    internal static (double Width, double Height) PreviewCanvasDimensions(
        string familyId) =>
        SkinCursorMiddlePolicy.IsCursorFamily(familyId)
            ? (SkinCursorPreview.CanvasWidth, SkinCursorPreview.CanvasHeight)
            : (380, 210);

    private SkinExtraPackPreview CreatePreviewPack(
        SkinExtraPackPreview source,
        IReadOnlyDictionary<string, string> targetPaths,
        IReadOnlyList<SkinExtraIniPatchEntry> patch,
        bool applyElementTints = false)
    {
        if (applyElementTints)
            targetPaths = ApplyElementTintsToPreview(source, targetPaths);
        var imageTargets = targetPaths
            .Where(pair => SkinElementCategorizer.IsImage(pair.Key))
            .ToArray();
        var imagePaths = imageTargets
            .Select(pair => pair.Value)
            .ToArray();
        var audioPaths = targetPaths
            .Where(pair => SkinElementCategorizer.IsAudio(pair.Key))
            .Select(pair => pair.Value)
            .ToArray();
        var cursorAssets = SkinCursorPreview.Resolve(imageTargets.Select(pair => pair.Key));
        var cursor = LoadExactTarget(imageTargets, cursorAssets.CursorFilename);
        var trail = LoadExactTarget(imageTargets, cursorAssets.TrailFilename);
        var middle = LoadExactTarget(imageTargets, cursorAssets.MiddleFilename);
        var manifestFiles = source.Manifest.Files
            .Where(file => targetPaths.ContainsKey(file.TargetFilename))
            .ToList();
        var manifest = CopyManifest(
            source.Manifest,
            files: manifestFiles,
            iniPatch: patch.ToList());
        var generic = imagePaths.Take(1)
            .Select(path => LoadBitmap(path, 160))
            .Where(image => image is not null)
            .Cast<BitmapSource>()
            .ToList();
        return source with
        {
            Manifest = manifest,
            FileCount = targetPaths.Count,
            CursorImage = cursor,
            TrailImage = trail,
            MiddleImage = middle,
            PreviewImages = generic,
            ImagePaths = imagePaths,
            AudioPaths = audioPaths,
        };
    }

    private IReadOnlyDictionary<string, string> ApplyElementTintsToPreview(
        SkinExtraPackPreview pack,
        IReadOnlyDictionary<string, string> targetPaths)
    {
        var tints = pack.Elements
            .Where(element => element.CanTint
                              && element.IsTinted
                              && element.IsSelected != false)
            .ToDictionary(
                element => element.Key,
                element => element.TintRgb,
                StringComparer.OrdinalIgnoreCase);
        if (tints.Count == 0)
            return targetPaths;

        var sourceFiles = targetPaths
            .Where(pair => SkinElementCategorizer.IsImage(pair.Key))
            .Select(pair => new SkinExtraPackFile(
                pair.Key,
                File.ReadAllBytes(pair.Value)))
            .ToArray();
        var recoloured = SkinExtraElementTinting.Apply(
            pack.Manifest.FamilyId,
            sourceFiles,
            tints);
        var result = targetPaths.ToDictionary(
            pair => pair.Key,
            pair => pair.Value,
            StringComparer.OrdinalIgnoreCase);
        for (var index = 0; index < recoloured.Count; index++)
        {
            if (ReferenceEquals(recoloured[index], sourceFiles[index]))
                continue;
            var digest = Convert.ToHexString(
                    SHA256.HashData(recoloured[index].Bytes))
                .ToLowerInvariant()[..16];
            var path = RecolouredPreviewPath(
                previewTempRoot,
                digest,
                recoloured[index].Filename);
            var directory = Path.GetDirectoryName(path)!;
            Directory.CreateDirectory(directory);
            if (!File.Exists(path))
                File.WriteAllBytes(path, recoloured[index].Bytes);
            result[recoloured[index].Filename] = path;
        }
        return result;
    }

    internal static string RecolouredPreviewPath(
        string previewRoot,
        string digest,
        string filename) =>
        Path.Combine(
            previewRoot,
            "recoloured",
            digest,
            Path.GetFileName(filename));

    private static BitmapSource? LoadExactTarget(
        IEnumerable<KeyValuePair<string, string>> targets,
        string? filename)
    {
        if (filename is null)
            return null;
        var target = targets.FirstOrDefault(pair => pair.Key.Equals(
            filename,
            StringComparison.OrdinalIgnoreCase));
        return string.IsNullOrWhiteSpace(target.Value)
            ? null
            : LoadBitmap(target.Value);
    }

    private IReadOnlyList<SkinExtraIniPatchEntry> SelectedPatch(SkinExtraPackPreview pack) =>
        pack.Settings
            .Where(setting => setting.IsSelected
                              && (!setting.IsRequired || pack.SelectedFileCount > 0))
            .Select(setting => setting.Patch)
            .ToArray();

    private void RefreshSelectionPreview(SkinExtraPackPreview pack)
    {
        if (!ReferenceEquals(selectedPack, pack))
            return;
        var rendered = rendererTargetVisible || RenderPackOnlyPreview(pack);
        var hasAudioPreview = RenderAudioPreview(pack);
        EmptyPreview.Visibility = rendered || hasAudioPreview
            ? Visibility.Collapsed
            : Visibility.Visible;
        PreviewCaption.Text =
            $"{pack.SelectedElementCount} selected element"
            + (pack.SelectedElementCount == 1 ? "" : "s")
            + $" · {pack.SelectedFileCount} files"
            + (pack.SelectedSettingCount == 0
                ? ""
                : $" · {pack.SelectedSettingCount} settings");
        ResetExtrasPreviewAnimation();
        UpdateExtrasPreviewAnimationSubscription();
        _ = RefreshComparisonPreviewAsync(pack);
    }

    private async Task RefreshComparisonPreviewAsync(SkinExtraPackPreview pack)
    {
        if (rendererTargetVisible)
            return;

        previewCancellation?.Cancel();
        previewCancellation?.Dispose();
        previewCancellation = new CancellationTokenSource();
        var cancellationToken = previewCancellation.Token;
        if (currentSkinSource is null)
            return;

        try
        {
            var currentFiles = await MaterializeCurrentFamilyAsync(pack, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            if (!ReferenceEquals(selectedPack, pack))
                return;

            EnsureCurrentFallbackElements(pack, currentFiles);
            var currentPatch = CurrentPatch(pack);
            var integratedFiles = currentFiles.ToDictionary(
                pair => pair.Key,
                pair => pair.Value,
                StringComparer.OrdinalIgnoreCase);
            OverlaySelectedLogicalElements(pack, currentFiles, integratedFiles);
            if (ChangedOnlyPreviewToggle.IsChecked == true)
            {
                var changed = FindChangedPreviewFiles(currentFiles, integratedFiles);
                currentFiles = currentFiles
                    .Where(pair => changed.Contains(pair.Key))
                    .ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.OrdinalIgnoreCase);
                integratedFiles = integratedFiles
                    .Where(pair => changed.Contains(pair.Key))
                    .ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.OrdinalIgnoreCase);
            }
            var currentPack = CreatePreviewPack(pack, currentFiles, currentPatch);
            var integratedPatch = OverlayPatch(currentPatch, SelectedPatch(pack));
            var integratedPack = CreatePreviewPack(
                pack,
                integratedFiles,
                integratedPatch,
                applyElementTints: true);
            RenderAudioComparison(currentFiles, integratedFiles, pack);

            var currentRendered = RenderPackToCanvas(currentPack, CurrentPreviewCanvas);
            var resultRendered = RenderPackToCanvas(integratedPack, ResultPreviewCanvas);
            var rendered = currentRendered || resultRendered;
            var hasAudioPreview = AudioPlayerPreview.Visibility == Visibility.Visible;
            EmptyPreview.Visibility = rendered || hasAudioPreview
                ? Visibility.Collapsed
                : Visibility.Visible;
            PreviewModeBar.Visibility = rendererTargetVisible
                ? Visibility.Hidden
                : rendered ? Visibility.Visible : Visibility.Hidden;
            if (ComparePreviewButton.IsChecked == true
                || CurrentSkinPreviewButton.IsChecked == true)
            {
                ComparisonPreview.Visibility = rendered
                    ? Visibility.Visible
                    : Visibility.Collapsed;
                CursorPreview.Visibility = Visibility.Collapsed;
            }
            ResetExtrasPreviewAnimation();
        }
        catch (OperationCanceledException)
        {
            // A newer selection owns the preview.
        }
        catch
        {
            CurrentPreviewCanvas.Children.Clear();
            ResultPreviewCanvas.Children.Clear();
            ComparisonPreview.Visibility = Visibility.Collapsed;
            CursorPreview.Visibility = Visibility.Visible;
            ComparePreviewButton.IsChecked = false;
            PackOnlyPreviewButton.IsChecked = true;
        }
    }

    internal static IReadOnlySet<string> FindChangedPreviewFiles(
        IReadOnlyDictionary<string, string> current,
        IReadOnlyDictionary<string, string> integrated)
    {
        var changed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var filename in current.Keys.Concat(integrated.Keys)
                     .Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (!current.TryGetValue(filename, out var before)
                || !integrated.TryGetValue(filename, out var after)
                || !File.ReadAllBytes(before).AsSpan().SequenceEqual(File.ReadAllBytes(after)))
                changed.Add(filename);
        }
        return changed;
    }

    private void OverlaySelectedLogicalElements(
        SkinExtraPackPreview pack,
        IReadOnlyDictionary<string, string> currentFiles,
        IDictionary<string, string> integratedFiles)
    {
        var selectedFiles = pack.Files.Where(file => file.IsSelected).ToArray();
        var replaceableCurrentFiles = currentFiles.Keys.Where(filename =>
            !lazerUsedOnly
            || SkinExtraLazerCompatibility.IsLazerUsed(
                filename,
                pack.Manifest.FamilyId));
        foreach (var filename in
                 SkinExtraLogicalSelectionPlanner.FindReplacedCurrentFiles(
                     pack.Manifest.FamilyId,
                     replaceableCurrentFiles,
                     selectedFiles.Select(file => file.Name)))
            integratedFiles.Remove(filename);
        foreach (var file in selectedFiles)
            integratedFiles[file.Name] = file.Path;

        foreach (var fallback in currentFallbackElements.Where(element =>
                     element.IsSelected == false))
        {
            foreach (var file in fallback.Files)
                integratedFiles.Remove(file.Name);
        }
        if (SkinCursorMiddlePolicy.IsCursorFamily(pack.Manifest.FamilyId))
        {
            foreach (var filename in integratedFiles.Keys
                         .Where(SkinCursorMiddlePolicy.IsCursorMiddle)
                         .ToArray())
                integratedFiles.Remove(filename);
            if (SmoothTrailCheckBox.IsChecked == true)
                integratedFiles[SkinCursorMiddlePolicy.CanonicalFilename] =
                    SmoothTrailPreviewPath();
        }
    }

    private void EnsureCurrentFallbackElements(
        SkinExtraPackPreview pack,
        IReadOnlyDictionary<string, string> currentFiles)
    {
        if (currentFallbackPackKey?.Equals(
                pack.PackKey,
                StringComparison.OrdinalIgnoreCase) == true)
            return;

        var missing = SkinExtraCurrentFallbackPlanner.FindMissingLayers(
            pack.Manifest.FamilyId,
            currentFiles.Keys,
            pack.Files.Select(file => file.Name));
        currentFallbackElements = missing
            .Select(fallback =>
            {
                var files = fallback.Filenames
                    .Where(filename => !lazerUsedOnly
                                       || SkinExtraLazerCompatibility.IsLazerUsed(
                                           filename,
                                           pack.Manifest.FamilyId))
                    .Where(currentFiles.ContainsKey)
                    .Select(filename => new PackFileEntry(
                        filename,
                        "Current skin",
                        currentFiles[filename],
                        FormatFileSize(new FileInfo(currentFiles[filename]).Length),
                        ""))
                    .ToArray();
                return files.Length == 0
                    ? null
                    : new PackElementEntry(
                        fallback.Key,
                        SkinExtraLogicalGrouping.DisplayName(fallback.Key),
                        files,
                        thumbnail: null,
                        fromCurrentSkin: true);
            })
            .OfType<PackElementEntry>()
            .ToArray();
        currentFallbackPackKey = pack.PackKey;
        if (fallbackSelectionsByPack.TryGetValue(pack.PackKey, out var selections))
        {
            foreach (var file in currentFallbackElements.SelectMany(element => element.Files))
            {
                file.IsSelected = SelectionAfterReload(
                    file.Name,
                    file.IsSelected,
                    selections);
            }
        }
        RefreshElementList(pack);
        _ = EnsureFallbackThumbnailsAsync(pack, currentFallbackElements);
    }

    private void RefreshElementList(SkinExtraPackPreview pack)
    {
        if (!ReferenceEquals(pack, selectedPack))
            return;
        PackFilesList.ItemsSource = pack.Elements
            .Concat(currentFallbackElements)
            .ToArray();
    }

    private async Task EnsureFallbackThumbnailsAsync(
        SkinExtraPackPreview pack,
        IReadOnlyList<PackElementEntry> fallbacks)
    {
        if (fallbacks.Count == 0)
            return;
        var version = ++fallbackThumbnailLoadVersion;
        var thumbnails = await Task.Run(() => fallbacks
            .Select(element => LoadElementThumbnail(
                pack.Manifest,
                element.Key,
                element.Files.Select(file => file.Path)
                    .Where(SkinElementCategorizer.IsImage)))
            .ToArray());
        if (disposed
            || version != fallbackThumbnailLoadVersion
            || !ReferenceEquals(pack, selectedPack)
            || !ReferenceEquals(fallbacks, currentFallbackElements))
            return;
        for (var index = 0; index < fallbacks.Count; index++)
            fallbacks[index].SetThumbnail(thumbnails[index]);
    }

    private async Task<IReadOnlyDictionary<string, string>> MaterializeCurrentFamilyAsync(
        SkinExtraPackPreview pack,
        CancellationToken cancellationToken)
    {
        if (currentPreviewFiles.TryGetValue(pack.NavigationFamilyId, out var cached))
            return cached;
        if (currentSkinSource is null)
            return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        var filenames = CurrentFamilyFilenames(pack).ToArray();
        var directory = Path.Combine(
            previewTempRoot,
            SkinExtraNaming.Sanitize(pack.NavigationFamilyId));
        Directory.CreateDirectory(directory);
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        for (var index = 0; index < filenames.Length; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var filename = filenames[index];
            var bytes = await currentSkinSource.ReadFileAsync(filename, cancellationToken);
            if (bytes is null)
                continue;
            var leaf = Path.GetFileName(filename);
            // Preserve every logical source independently. A skin can contain
            // archived/nested files with the same leaf name as its active
            // gameplay file; flattening those names made the final writer win.
            var path = Path.Combine(directory, $"{index:D4}-{leaf}");
            await File.WriteAllBytesAsync(path, bytes, cancellationToken);
            result[filename] = path;
        }
        currentPreviewFiles[pack.NavigationFamilyId] = result;
        return result;
    }

    private IEnumerable<string> CurrentFamilyFilenames(SkinExtraPackPreview pack)
    {
        if (currentSkinSource is null)
            return [];
        if (!pack.Manifest.FamilyId.Equals(
                "osu.number-font",
                StringComparison.OrdinalIgnoreCase))
            return currentSkinSource.Filenames.Where(filename =>
                SkinCursorPreview.IsRootGameplayFile(filename)
                && SkinExtraFamilyRegistry.ForFile(filename)?.Id.Equals(
                    pack.Manifest.FamilyId,
                    StringComparison.OrdinalIgnoreCase) == true);

        var prefixes = pack.Manifest.FontRoles.Select(role => role switch
            {
                "Hitcircle" => (Key: "HitCirclePrefix", Default: "default"),
                "Score" => (Key: "ScorePrefix", Default: "score"),
                "Combo" => (Key: "ComboPrefix", Default: "score"),
                _ => (Key: "", Default: ""),
            })
            .Where(item => item.Key.Length > 0)
            .Select(item => EffectiveCurrentIni?.GetValue("Fonts", item.Key)
                            ?? item.Default)
            .Select(value => Path.GetFileName(value.Replace('\\', '/')))
            .ToArray();
        return currentSkinSource.Filenames.Where(filename =>
            prefixes.Any(prefix => Path.GetFileNameWithoutExtension(filename)
                .StartsWith(prefix + "-", StringComparison.OrdinalIgnoreCase)));
    }

    internal static bool IsRootSkinFile(string filename)
        => SkinCursorPreview.IsRootGameplayFile(filename);

    private IReadOnlyList<SkinExtraIniPatchEntry> CurrentPatch(SkinExtraPackPreview pack)
    {
        var currentIni = EffectiveCurrentIni;
        if (currentIni is null)
            return [];
        var entries = new List<SkinExtraIniPatchEntry>();
        foreach (var setting in pack.Settings)
        {
            var source = setting.Patch;
            string? value;
            if (source.Section.Equals("Mania", StringComparison.OrdinalIgnoreCase)
                && source.ManiaKeys is { } keys)
            {
                value = currentIni.GetSections("Mania")
                    .FirstOrDefault(section => section.ManiaKeys == keys)?
                    .Values.GetValueOrDefault(source.Key);
            }
            else
            {
                value = currentIni.GetValue(source.Section, source.Key);
            }
            if (value is not null)
                entries.Add(source with { Value = value });
        }
        return entries;
    }

    private static IReadOnlyList<SkinExtraIniPatchEntry> OverlayPatch(
        IReadOnlyList<SkinExtraIniPatchEntry> current,
        IReadOnlyList<SkinExtraIniPatchEntry> selected)
    {
        var result = current.ToDictionary(
            entry => $"{entry.Section}\0{entry.Key}\0{entry.ManiaKeys}",
            StringComparer.OrdinalIgnoreCase);
        foreach (var entry in selected)
            result[$"{entry.Section}\0{entry.Key}\0{entry.ManiaKeys}"] = entry;
        return result.Values.ToArray();
    }

    private void PreviewMode_Click(object sender, RoutedEventArgs e)
    {
        var current = currentSkinSource is not null
                      && sender is ToggleButton { Tag: "Current" };
        var compare = currentSkinSource is not null
                      && sender is ToggleButton { Tag: "Compare" };
        SetPreviewMode(current, compare, refresh: true);
    }

    private void ChangedOnlyPreviewToggle_Click(object sender, RoutedEventArgs e)
    {
        if (selectedPack is not null)
            _ = RefreshComparisonPreviewAsync(selectedPack);
    }

    private void BlinkCompareToggle_Click(object sender, RoutedEventArgs e)
    {
        if (BlinkCompareToggle.IsChecked == true)
        {
            SetPreviewMode(current: false, compare: true, refresh: true);
            compareBlinkTimer.Start();
        }
        else
        {
            compareBlinkTimer.Stop();
            ResultPreviewPane.Opacity = 1;
        }
    }

    private void SwipeCompareToggle_Click(object sender, RoutedEventArgs e)
    {
        ComparisonSwipeSlider.Visibility = SwipeCompareToggle.IsChecked == true
            ? Visibility.Visible
            : Visibility.Collapsed;
        if (SwipeCompareToggle.IsChecked == true)
            SetPreviewMode(current: false, compare: true, refresh: true);
        UpdateComparisonSwipe();
    }

    private void ComparisonSwipeSlider_ValueChanged(
        object sender,
        RoutedPropertyChangedEventArgs<double> e) => UpdateComparisonSwipe();

    private void UpdateComparisonSwipe()
    {
        if (CurrentPreviewColumn is null || ResultPreviewColumn is null)
            return;
        var swipe = SwipeCompareToggle?.IsChecked == true
            ? ComparisonSwipeSlider.Value / 100
            : 0.5;
        CurrentPreviewColumn.Width = new GridLength(swipe, GridUnitType.Star);
        ResultPreviewColumn.Width = new GridLength(1 - swipe, GridUnitType.Star);
    }

    private void SetPreviewMode(bool current, bool compare, bool refresh)
    {
        CurrentSkinPreviewButton.IsChecked = current;
        ComparePreviewButton.IsChecked = compare;
        PackOnlyPreviewButton.IsChecked = !current && !compare;
        CurrentPreviewColumn.Width = new GridLength(1, GridUnitType.Star);
        ComparisonDividerColumn.Width = compare ? new GridLength(1) : new GridLength(0);
        ResultPreviewColumn.Width = compare
            ? new GridLength(1, GridUnitType.Star)
            : new GridLength(0);
        ComparisonDivider.Visibility = compare ? Visibility.Visible : Visibility.Collapsed;
        ResultPreviewPane.Visibility = compare ? Visibility.Visible : Visibility.Collapsed;
        if (!compare)
        {
            compareBlinkTimer.Stop();
            BlinkCompareToggle.IsChecked = false;
            ResultPreviewPane.Opacity = 1;
        }
        else if (SwipeCompareToggle.IsChecked == true)
        {
            UpdateComparisonSwipe();
        }
        ComparisonPreview.Visibility = current || compare
            ? Visibility.Visible
            : Visibility.Collapsed;
        CursorPreview.Visibility = current || compare
            ? Visibility.Collapsed
            : Visibility.Visible;
        if (refresh && (current || compare) && selectedPack is not null)
            _ = RefreshComparisonPreviewAsync(selectedPack);
        ResetExtrasPreviewAnimation();
        UpdateExtrasPreviewAnimationSubscription();
    }

    private void PreviewPlaybackToggle_Click(object sender, RoutedEventArgs e)
    {
        previewAnimationsEnabled = PreviewPlaybackToggle.IsChecked == true;
        UpdatePreviewPlaybackPresentation();
        if (previewAnimationsEnabled)
            ResetExtrasPreviewAnimation();
        else
            RenderExtrasPreviewFrame();
        previewAnimationsChanged?.Invoke(previewAnimationsEnabled);
        UpdateExtrasPreviewAnimationSubscription();
    }

    private void ElementTintButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { DataContext: PackElementEntry element } button)
            return;
        if (!element.CanTint)
            return;
        activeTintElement = element;
        ElementColorPicker.Open(
            element.TintHex,
            element.Name,
            "Recolour this element in the preview and stage the recoloured files when it is added to Changes.",
            allowOpacity: false);
        ElementColorPickerPopup.PlacementTarget = button;
        ElementColorPickerPopup.IsOpen = true;
        e.Handled = true;
    }

    private void ElementColorPicker_ColourChanged(string value)
    {
        if (activeTintElement is not { } element)
            return;
        try
        {
            if (System.Windows.Media.ColorConverter.ConvertFromString(value)
                is not Color colour)
            {
                return;
            }
            element.SetTint(colour);
            var pack = allPacks.FirstOrDefault(candidate =>
                candidate.Elements.Contains(element));
            if (pack is not null)
                RefreshSelectionPreview(pack);
            PreviewTintChanged?.Invoke(
                this,
                new SkinExtrasPreviewTintChangedEventArgs(
                    element.Key,
                    colour));
        }
        catch (FormatException)
        {
        }
    }

    private void NativeCursorMotionToggle_Click(object sender, RoutedEventArgs e)
    {
        var active = NativeCursorMotionToggle.IsChecked == true;
        NativeCursorMotionToggle.Content = active ? "Motion on" : "Motion off";
        PreviewMotionChanged?.Invoke(
            this,
            new SkinExtrasPreviewMotionChangedEventArgs(active));
    }

    private void UpdatePreviewPlaybackPresentation()
    {
        if (PreviewPlaybackToggle is null)
            return;
        PreviewPlaybackToggle.IsChecked = previewAnimationsEnabled;
        PreviewPlaybackToggle.Content = previewAnimationsEnabled ? "Pause" : "Play";
        PreviewPlaybackToggle.ToolTip = previewAnimationsEnabled
            ? "Pause animated Extras previews"
            : "Play animated Extras previews";
    }

    private void ResetExtrasPreviewAnimation()
    {
        extrasPreviewElapsed = 0;
        extrasPreviewFrameDelta = 0;
        foreach (var state in extrasCanvasAnimationStates.Values)
            state.Health = 1;
        extrasPreviewLastRenderTime = extrasPreviewAnimationClock.Elapsed.TotalMilliseconds;
        RenderExtrasPreviewFrame();
    }

    private void UpdateExtrasPreviewAnimationSubscription()
    {
        if (rendererTargetVisible)
        {
            StopExtrasPreviewRendering();
            return;
        }
        var previewVisible = PreviewModeBar is { Visibility: Visibility.Visible }
                             && (CursorPreview.Visibility == Visibility.Visible
                                 || ComparisonPreview.Visibility == Visibility.Visible)
                             && extrasCanvasAnimationStates.Count > 0;
        var shouldRender = SkinPreviewAnimation.ShouldRenderExtras(
            IsVisible,
            previewVisible,
            previewAnimationsEnabled);
        if (shouldRender)
        {
            if (extrasPreviewRendering)
                return;
            extrasPreviewLastRenderTime =
                extrasPreviewAnimationClock.Elapsed.TotalMilliseconds;
            CompositionTarget.Rendering += ExtrasPreviewCompositionTarget_Rendering;
            extrasPreviewRendering = true;
            return;
        }

        StopExtrasPreviewRendering();
    }

    private void StopExtrasPreviewRendering()
    {
        if (!extrasPreviewRendering)
            return;
        CompositionTarget.Rendering -= ExtrasPreviewCompositionTarget_Rendering;
        extrasPreviewRendering = false;
    }

    private void ExtrasPreviewCompositionTarget_Rendering(object? sender, EventArgs e)
    {
        var now = extrasPreviewAnimationClock.Elapsed.TotalMilliseconds;
        extrasPreviewFrameDelta = Math.Clamp(
            now - extrasPreviewLastRenderTime,
            0,
            100);
        extrasPreviewLastRenderTime = now;
        extrasPreviewElapsed += extrasPreviewFrameDelta;
        RenderExtrasPreviewFrame();
    }

    private void RenderExtrasPreviewFrame()
    {
        if (CursorPreview is { Visibility: Visibility.Visible })
            AnimateExtrasCanvas(CursorTrailCanvas);
        if (ComparisonPreview is { Visibility: Visibility.Visible })
        {
            AnimateExtrasCanvas(CurrentPreviewCanvas);
            AnimateExtrasCanvas(ResultPreviewCanvas);
        }
    }

    private void AnimateExtrasCanvas(Canvas canvas)
    {
        if (!extrasCanvasAnimationStates.TryGetValue(canvas, out var context))
            return;
        var previewElapsed = SkinPreviewAnimation.ExtrasTime(
            context.FamilyId,
            extrasPreviewElapsed);
        var staticHitCircle = UsesStaticHitCirclePreview(
            context.FamilyId,
            previewAnimationsEnabled);
        var images = canvas.Children
            .OfType<Image>()
            .Where(image => image.Tag is PreviewLayerVisual)
            .ToArray();
        if (images.Length == 0)
            return;

        var roleCounts = images
            .Select(image => (PreviewLayerVisual)image.Tag)
            .GroupBy(layer => layer.AnimationRole)
            .ToDictionary(group => group.Key, group => group.Count());
        if (roleCounts.ContainsKey(SkinPreviewAnimationRole.ScorebarMarker))
        {
            context.Health = SkinPreviewAnimation.SmoothHealth(
                context.Health,
                SkinPreviewAnimation.HealthTarget(previewElapsed),
                extrasPreviewFrameDelta);
        }

        var slider = SkinPreviewAnimation.Slider(
            previewElapsed,
            context.LegacyVersionOne);
        var spinner = SkinPreviewAnimation.Spinner(
            previewElapsed,
            context.SpinnerNoBlink);
        var placements = new Dictionary<PreviewLayerVisual, ExtrasAnimationPlacement>();

        foreach (var image in images)
        {
            var layer = (PreviewLayerVisual)image.Tag;
            var position = layer.Centre;
            var scaleX = 1d;
            var scaleY = 1d;
            var rotation = 0d;
            var opacity = image.Effect is null ? layer.BaseOpacity : 1;

            switch (layer.AnimationRole)
            {
                case SkinPreviewAnimationRole.ApproachCircle:
                    if (staticHitCircle)
                    {
                        opacity = 0;
                    }
                    else
                    {
                        var approach = SkinPreviewAnimation.Approach(previewElapsed);
                        scaleX = scaleY = approach.Scale;
                        opacity *= approach.Opacity;
                    }
                    break;

                case SkinPreviewAnimationRole.HitCircle:
                    if (!staticHitCircle)
                    {
                        var hit = SkinPreviewAnimation.HitObject(previewElapsed);
                        scaleX = scaleY = hit.Scale;
                        opacity *= hit.Opacity;
                    }
                    break;

                case SkinPreviewAnimationRole.Followpoint:
                    var followpointCount = roleCounts[layer.AnimationRole];
                    var fraction = (layer.AnimationIndex + 1d) / (followpointCount + 1d);
                    var followpoint = SkinPreviewAnimation.Followpoint(
                        previewElapsed,
                        fraction);
                    scaleX = scaleY = followpoint.Scale;
                    opacity *= followpoint.Opacity;
                    break;

                case SkinPreviewAnimationRole.SliderBall:
                    position = SkinPreviewAnimation.SamplePolyline(
                        ExtrasSliderPreviewPath,
                        slider.Progress);
                    if (context.SliderBallFlip && slider.Reversed)
                        scaleX = -1;
                    opacity *= slider.BallOpacity;
                    break;

                case SkinPreviewAnimationRole.SliderFollowCircle:
                    position = SkinPreviewAnimation.SamplePolyline(
                        ExtrasSliderPreviewPath,
                        slider.Progress);
                    scaleX = scaleY = slider.FollowScale;
                    opacity *= slider.FollowOpacity;
                    break;

                case SkinPreviewAnimationRole.ReverseArrow:
                    scaleX = scaleY = slider.ReverseScale;
                    rotation = slider.ReverseRotation;
                    opacity *= slider.ReverseOpacity;
                    break;

                case SkinPreviewAnimationRole.Cursor:
                case SkinPreviewAnimationRole.CursorMiddle:
                    var cursor = SkinPreviewAnimation.Cursor(
                        previewElapsed,
                        canvas.Width,
                        canvas.Height,
                        context.CursorExpand,
                        context.CursorRotate);
                    position = cursor.Position;
                    if (!context.CursorCentre)
                    {
                        position.Offset(layer.Width / 2, layer.Height / 2);
                    }
                    if (layer.AnimationRole == SkinPreviewAnimationRole.Cursor)
                    {
                        scaleX = scaleY = cursor.Scale;
                        rotation = cursor.Rotation;
                    }
                    break;

                case SkinPreviewAnimationRole.CursorTrail:
                    var trailCount = roleCounts[layer.AnimationRole];
                    var trailAge = (trailCount - layer.AnimationIndex - 1d)
                                   / Math.Max(1, trailCount - 1)
                                   * (context.SmoothCursorTrail
                                       ? SkinPreviewAnimation.SmoothTrailFadeMilliseconds
                                       : SkinPreviewAnimation.DisjointTrailFadeMilliseconds);
                    var trailCursor = SkinPreviewAnimation.Cursor(
                        previewElapsed - trailAge,
                        canvas.Width,
                        canvas.Height,
                        expand: false,
                        rotate: context.CursorTrailRotate);
                    position = trailCursor.Position;
                    rotation = trailCursor.Rotation;
                    scaleX = scaleY = context.SmoothCursorTrail
                        ? 1
                        : SkinPreviewAnimation.LegacyTrailTextureScale;
                    opacity *= SkinPreviewAnimation.TrailOpacity(
                        trailAge,
                        context.SmoothCursorTrail);
                    break;

                case SkinPreviewAnimationRole.SpinnerCircle:
                    rotation = spinner.Rotation;
                    opacity *= spinner.BodyOpacity;
                    break;

                case SkinPreviewAnimationRole.SpinnerTop:
                    scaleX = scaleY = spinner.BodyScale;
                    rotation = spinner.Rotation
                               * (context.HasSpinnerMiddle2 ? 0.5 : 1);
                    opacity *= spinner.BodyOpacity;
                    break;

                case SkinPreviewAnimationRole.SpinnerBottom:
                    scaleX = scaleY = spinner.BodyScale;
                    rotation = spinner.Rotation
                               * (context.HasSpinnerMiddle2 ? 0.5 : 1)
                               / 3;
                    opacity *= spinner.BodyOpacity;
                    break;

                case SkinPreviewAnimationRole.SpinnerMiddle2:
                    scaleX = scaleY = spinner.BodyScale;
                    rotation = spinner.Rotation;
                    opacity *= spinner.BodyOpacity;
                    break;

                case SkinPreviewAnimationRole.SpinnerMiddle:
                    scaleX = scaleY = spinner.BodyScale;
                    opacity *= spinner.BodyOpacity;
                    break;

                case SkinPreviewAnimationRole.SpinnerGlow:
                    scaleX = scaleY = spinner.BodyScale;
                    opacity *= spinner.GlowOpacity * spinner.BodyOpacity;
                    break;

                case SkinPreviewAnimationRole.SpinnerApproach:
                    scaleX = scaleY = spinner.ApproachScale;
                    opacity *= spinner.BodyOpacity;
                    break;

                case SkinPreviewAnimationRole.SpinnerMetre:
                    scaleY = spinner.MetreFill;
                    opacity *= spinner.BodyOpacity;
                    break;

                case SkinPreviewAnimationRole.SpinnerSpin:
                    opacity *= spinner.SpinOpacity;
                    break;

                case SkinPreviewAnimationRole.SpinnerClear:
                    scaleX = scaleY = spinner.ClearScale;
                    opacity *= spinner.ClearOpacity;
                    break;

                case SkinPreviewAnimationRole.ScorebarMarker:
                    position.Offset(
                        SkinPreviewAnimation.ScorebarOffsetFromHealth(context.Health),
                        0);
                    break;
            }

            var placement = new ExtrasAnimationPlacement(
                position,
                scaleX,
                scaleY,
                rotation,
                Math.Clamp(opacity, 0, 1));
            placements[layer] = placement;
            ApplyExtrasAnimationPlacement(image, layer, placement, outline: false);
        }

        foreach (var outline in canvas.Children.OfType<Border>())
        {
            if (outline.DataContext is PreviewLayerVisual layer
                && placements.TryGetValue(layer, out var placement))
            {
                ApplyExtrasAnimationPlacement(
                    outline,
                    layer,
                    placement with { Opacity = placement.Opacity > 0.01 ? 1 : 0 },
                    outline: true);
            }
        }
    }

    internal static bool UsesStaticHitCirclePreview(
        string familyId,
        bool animationsEnabled) =>
        !animationsEnabled
        && familyId.Equals(
            "osu.hitcircles",
            StringComparison.OrdinalIgnoreCase);

    private static void ApplyExtrasAnimationPlacement(
        FrameworkElement element,
        PreviewLayerVisual layer,
        ExtrasAnimationPlacement placement,
        bool outline)
    {
        var width = outline ? layer.Width + 10 : layer.Width;
        var height = outline ? layer.Height + 10 : layer.Height;
        Canvas.SetLeft(element, placement.Position.X - width / 2);
        Canvas.SetTop(element, placement.Position.Y - height / 2);
        element.Opacity = placement.Opacity;
        element.RenderTransformOrigin =
            layer.AnimationRole == SkinPreviewAnimationRole.SpinnerMetre
                ? new Point(0.5, 1)
                : new Point(0.5, 0.5);
        var transforms = new TransformGroup();
        if (Math.Abs(placement.ScaleX - 1) > double.Epsilon
            || Math.Abs(placement.ScaleY - 1) > double.Epsilon)
        {
            transforms.Children.Add(new ScaleTransform(
                placement.ScaleX,
                placement.ScaleY));
        }
        var rotation = layer.RotationDegrees + placement.Rotation;
        if (Math.Abs(rotation) > double.Epsilon)
            transforms.Children.Add(new RotateTransform(rotation));
        element.RenderTransform = transforms.Children.Count == 0
            ? Transform.Identity
            : transforms;
    }

    internal static SkinPreviewAnimationRole ExtrasAnimationRole(string logicalKey)
    {
        var stem = LogicalImageStem(logicalKey).ToLowerInvariant();
        if (stem.StartsWith("spinner-", StringComparison.Ordinal))
        {
            return stem switch
            {
                "spinner-circle" => SkinPreviewAnimationRole.SpinnerCircle,
                "spinner-glow" => SkinPreviewAnimationRole.SpinnerGlow,
                "spinner-bottom" => SkinPreviewAnimationRole.SpinnerBottom,
                "spinner-top" => SkinPreviewAnimationRole.SpinnerTop,
                "spinner-middle2" => SkinPreviewAnimationRole.SpinnerMiddle2,
                "spinner-middle" => SkinPreviewAnimationRole.SpinnerMiddle,
                "spinner-approachcircle" => SkinPreviewAnimationRole.SpinnerApproach,
                "spinner-metre" => SkinPreviewAnimationRole.SpinnerMetre,
                "spinner-spin" => SkinPreviewAnimationRole.SpinnerSpin,
                "spinner-clear" => SkinPreviewAnimationRole.SpinnerClear,
                _ => SkinPreviewAnimationRole.None,
            };
        }
        if (stem.StartsWith("followpoint", StringComparison.Ordinal))
            return SkinPreviewAnimationRole.Followpoint;
        if (stem.StartsWith("sliderfollowcircle", StringComparison.Ordinal))
            return SkinPreviewAnimationRole.SliderFollowCircle;
        if (stem.StartsWith("sliderb", StringComparison.Ordinal))
            return SkinPreviewAnimationRole.SliderBall;
        if (stem.Equals("reversearrow", StringComparison.Ordinal))
            return SkinPreviewAnimationRole.ReverseArrow;
        if (stem.Equals("cursortrail", StringComparison.Ordinal))
            return SkinPreviewAnimationRole.CursorTrail;
        if (stem.Equals("cursormiddle", StringComparison.Ordinal))
            return SkinPreviewAnimationRole.CursorMiddle;
        if (stem.Equals("cursor", StringComparison.Ordinal))
            return SkinPreviewAnimationRole.Cursor;
        if (stem.Equals("approachcircle", StringComparison.Ordinal))
            return SkinPreviewAnimationRole.ApproachCircle;
        if (stem is "hitcircle" or "hitcircleoverlay" or "hitcircle-number"
            || stem.StartsWith("hit0", StringComparison.Ordinal)
            || stem.StartsWith("hit50", StringComparison.Ordinal)
            || stem.StartsWith("hit100", StringComparison.Ordinal)
            || stem.StartsWith("hit300", StringComparison.Ordinal)
            || stem.Equals("lighting", StringComparison.Ordinal))
        {
            return SkinPreviewAnimationRole.HitCircle;
        }
        if (stem is "scorebar-ki" or "scorebar-kidanger"
            or "scorebar-kidanger2" or "scorebar-marker")
        {
            return SkinPreviewAnimationRole.ScorebarMarker;
        }
        return SkinPreviewAnimationRole.None;
    }

    private bool RenderFamilyPreview(SkinExtraPackPreview pack)
    {
        activePreviewCanvas.Children.Clear();
        switch (pack.Manifest.FamilyId.ToLowerInvariant())
        {
            case "osu.hitcircles":
                RenderHitCirclePreview(pack);
                return true;
            case "osu.slider":
                return RenderSliderPreview(pack);
            case "osu.slider-colours":
                return RenderSliderColourPreview(pack);
            case "osu.combo-colours":
                return RenderComboColourPreview(pack);
            case "osu.hitbursts":
            case "osu.result-judgements":
                return RenderHitBurstPreview(pack);
            case "osu.spinner":
                return RenderSpinnerPreview(pack);
            case "osu.followpoints":
                return RenderFollowpointPreview(pack);
            case "osu.number-font":
                return RenderNumberFontPreview(pack);
            case "interface.background":
                return RenderBackgroundPreview(pack);
            case "interface.input-overlay":
                return RenderInputOverlayPreview(pack);
            case "interface.scorebar":
                return RenderScorebarPreview(pack);
        }

        var paths = PreferredPreviewPaths(pack.ImagePaths)
            .OrderBy(path => Path.GetFileName(path), StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var images = paths
            .Select(path => (Path: path, Image: LoadBitmap(path, 512)))
            .Where(item => item.Image is not null)
            .ToArray();
        if (images.Length == 0)
            return false;

        var columns = Math.Max(1, (int)Math.Ceiling(Math.Sqrt(images.Length * 2.05)));
        var rows = (int)Math.Ceiling(images.Length / (double)columns);
        var cellWidth = 372d / columns;
        var cellHeight = 198d / rows;
        for (var index = 0; index < images.Length; index++)
        {
            var column = index % columns;
            var row = index / columns;
            AddPreviewImage(
                images[index].Image!,
                new Point(
                    4 + cellWidth * (column + 0.5),
                    6 + cellHeight * (row + 0.5)),
                1,
                Math.Max(12, cellWidth - 10),
                Math.Max(12, cellHeight - 10),
                logicalKey: Path.GetFileName(images[index].Path));
        }
        return true;
    }

    private bool RenderFollowpointPreview(SkinExtraPackPreview pack)
    {
        var frames = PreferredPreviewPaths(pack.ImagePaths)
            .OrderBy(path => Path.GetFileName(path), StringComparer.OrdinalIgnoreCase)
            .Select(path => (Name: Path.GetFileNameWithoutExtension(path), Image: LoadBitmap(path, 256)))
            .Where(frame => frame.Image is not null)
            .Select(frame => (frame.Name, Image: VisibleCrop(frame.Image!)))
            .Where(frame => frame.Image is not null)
            .Select(frame => (frame.Name, Image: frame.Image!))
            .Take(5)
            .ToArray();
        if (frames.Length == 0)
            return false;

        var cellWidth = 360d / frames.Length;
        for (var index = 0; index < frames.Length; index++)
        {
            var centre = new Point(10 + cellWidth * (index + 0.5), 93);
            AddPreviewImage(
                frames[index].Image,
                centre,
                1,
                Math.Max(48, cellWidth - 12),
                136,
                allowUpscale: true,
                logicalKey: frames[index].Name);
            AddPreviewLabel(frames[index].Name, 10 + cellWidth * index, 180, cellWidth);
        }
        AddPreviewLabel(
            $"{frames.Length} visible frame{(frames.Length == 1 ? "" : "s")} · transparent placeholders excluded",
            4,
            4,
            372);
        return true;
    }

    private bool RenderNumberFontPreview(SkinExtraPackPreview pack)
    {
        var includeScoreSymbols = pack.Manifest.FontRoles.Any(role =>
            role.Equals("Score", StringComparison.OrdinalIgnoreCase)
            || role.Equals("Combo", StringComparison.OrdinalIgnoreCase));
        var suffixes = includeScoreSymbols
            ? new[] { "0", "1", "2", "3", "4", "5", "6", "7", "8", "9", "x", "comma", "dot", "percent" }
            : new[] { "0", "1", "2", "3", "4", "5", "6", "7", "8", "9" };
        var glyphs = suffixes
            .Select(suffix => (Suffix: suffix, Image: LoadNumberGlyph(pack, suffix)))
            .ToArray();

        // A few older skins (including Shiro) use one visible `-dot` asset for
        // every hit-circle number and leave the numbered files transparent.
        // Keep the numbered labels, but show the dot as the glyph instead of
        // rendering an apparently empty grid.
        var dotImage = LoadNumberGlyph(pack, "dot");
        var dotGlyph = dotImage is null
            ? null
            : VisibleCrop(dotImage);
        var hasVisibleNumberGlyph = glyphs.Any(item =>
            item.Suffix.Length == 1
            && char.IsDigit(item.Suffix[0])
            && item.Image is not null
            && VisibleCrop(item.Image) is not null);
        if (!hasVisibleNumberGlyph && dotGlyph is not null)
        {
            glyphs = glyphs.Select(item =>
                    item.Suffix.Length == 1
                    && char.IsDigit(item.Suffix[0])
                    ? (Suffix: item.Suffix, Image: (BitmapSource?)dotGlyph)
                    : item)
                .ToArray();
        }

        glyphs = glyphs
            .Where(item => item.Image is not null)
            .ToArray();
        if (glyphs.Length == 0)
            return false;

        const int columns = 5;
        var rows = (int)Math.Ceiling(glyphs.Length / (double)columns);
        var cellWidth = 68d;
        var cellHeight = Math.Min(92d, 198d / rows);
        for (var index = 0; index < glyphs.Length; index++)
        {
            var row = index / columns;
            var itemsInRow = Math.Min(columns, glyphs.Length - row * columns);
            var rowWidth = itemsInRow * cellWidth;
            var left = (380 - rowWidth) / 2;
            var column = index % columns;
            var centre = new Point(
                left + cellWidth * (column + 0.5),
                5 + cellHeight * row + cellHeight * 0.43);
            var visible = VisibleCrop(glyphs[index].Image!);
            if (visible is not null)
                AddPreviewImage(
                    visible,
                    centre,
                    1,
                    48,
                    Math.Max(32, cellHeight - 24),
                    allowUpscale: true,
                    logicalKey: glyphs[index].Suffix);
            AddPreviewLabel(
                glyphs[index].Suffix,
                left + cellWidth * column,
                5 + cellHeight * (row + 1) - 15,
                cellWidth);
        }
        return true;
    }

    private static BitmapSource? LoadNumberGlyph(
        SkinExtraPackPreview pack,
        string suffix)
    {
        var prefix = pack.Manifest.Variant ?? "default";
        var path = pack.ImagePaths
            .Where(candidate => LogicalImageStem(candidate).Equals(
                $"{prefix}-{suffix}",
                StringComparison.OrdinalIgnoreCase))
            .OrderBy(candidate => Path.GetFileNameWithoutExtension(candidate)
                .EndsWith("@2x", StringComparison.OrdinalIgnoreCase))
            .FirstOrDefault()
            ?? pack.ImagePaths
                .Where(candidate => LogicalImageStem(candidate).EndsWith(
                    $"-{suffix}",
                    StringComparison.OrdinalIgnoreCase))
                .OrderBy(candidate => Path.GetFileNameWithoutExtension(candidate)
                    .EndsWith("@2x", StringComparison.OrdinalIgnoreCase))
                .FirstOrDefault();
        return path is null ? null : LoadBitmap(path, 192);
    }

    private bool RenderBackgroundPreview(SkinExtraPackPreview pack)
    {
        var background = LoadLogicalImage(pack.ImagePaths, "menu-background")
                         ?? pack.ImagePaths
                             .Select(path => LoadBitmap(path, 512))
                             .FirstOrDefault(image => image is not null);
        if (background is null)
            return false;
        AddPreviewImageCover(
            background,
            new Point(190, 105),
            372,
            202,
            "menu-background");
        return true;
    }

    private bool RenderInputOverlayPreview(SkinExtraPackPreview pack)
    {
        var background = LoadLogicalImage(pack.ImagePaths, "inputoverlay-background");
        var key = LoadLogicalImage(pack.ImagePaths, "inputoverlay-key");
        if (background is null && key is null)
            return false;

        if (background is not null)
            AddPreviewImageSized(
                background,
                new Point(190, 105),
                330,
                154,
                "inputoverlay-background");
        if (key is not null)
        {
            var labels = new[] { "K1", "K2", "M1", "M2" };
            for (var index = 0; index < labels.Length; index++)
            {
                var centre = new Point(91 + index * 66, 105);
                AddPreviewImage(
                    key,
                    centre,
                    1,
                    58,
                    58,
                    allowUpscale: true,
                    logicalKey: "inputoverlay-key");
                AddPreviewKeyLabel(labels[index], centre);
            }
        }
        return true;
    }

    private bool RenderScorebarPreview(SkinExtraPackPreview pack)
    {
        var background = LoadLogicalImage(pack.ImagePaths, "scorebar-bg");
        var colour = LoadLogicalImage(pack.ImagePaths, "scorebar-colour");
        var ki = LoadLogicalImage(pack.ImagePaths, "scorebar-ki")
                 ?? LoadLogicalImage(pack.ImagePaths, "scorebar-kidanger")
                 ?? LoadLogicalImage(pack.ImagePaths, "scorebar-kidanger2");
        if (background is null && colour is null && ki is null)
            return false;

        // Some skins use scorebar-bg as a full-screen playfield frame rather
        // than the conventional thin HUD strip. Keep its real geometry: the
        // asset itself decides whether there is a border at all.
        var isPlayfieldFrame = background is not null
                              && background.PixelWidth / (double)background.PixelHeight < 3.5;
        if (background is not null)
        {
            AddPreviewImage(
                background,
                isPlayfieldFrame ? new Point(190, 105) : new Point(190, 18),
                1,
                372,
                isPlayfieldFrame ? 202 : 42,
                allowUpscale: true,
                logicalKey: "scorebar-bg");
        }
        if (colour is not null)
        {
            // scorebar-colour is the skin-provided health fill. In a frame
            // layout it belongs at the top-left; ordinary scorebars overlay
            // it on their own thin background strip.
            AddPreviewImageSized(
                colour,
                isPlayfieldFrame ? new Point(66, 24) : new Point(190, 18),
                isPlayfieldFrame ? 104 : 372,
                isPlayfieldFrame ? 7 : 14,
                "scorebar-colour");
        }
        if (ki is not null)
        {
            var visibleKi = VisibleCrop(ki);
            if (visibleKi is not null)
                AddPreviewImage(
                    visibleKi,
                    isPlayfieldFrame ? new Point(360, 114) : new Point(350, 92),
                    1,
                    isPlayfieldFrame ? 20 : 26,
                    isPlayfieldFrame ? 142 : 126,
                    allowUpscale: true,
                    logicalKey: "scorebar-ki");
        }
        return true;
    }

    private void RenderHitCirclePreview(SkinExtraPackPreview pack)
    {
        var centre = new Point(190, 105);
        const double circleSize = 132;
        var approach = LoadLogicalImage(pack.ImagePaths, "approachcircle");
        var circle = LoadLogicalImage(pack.ImagePaths, "hitcircle");
        var overlay = LoadLogicalImage(pack.ImagePaths, "hitcircleoverlay");
        var combo = previewContext.ComboColours.FirstOrDefault();
        if (combo == default)
            combo = Color.FromRgb(80, 220, 255);
        var overlaySetting = pack.Manifest.IniPatch.FirstOrDefault(entry =>
            entry.Section.Equals("General", StringComparison.OrdinalIgnoreCase)
            && entry.Key.Equals(
                "HitCircleOverlayAboveNumber",
                StringComparison.OrdinalIgnoreCase));
        var overlayAboveNumber = overlaySetting is null
            ? previewContext.HitCircleOverlayAboveNumber
            : overlaySetting.Value == "1";

        if (approach is not null)
            AddPreviewImage(
                TintBitmap(approach, combo),
                centre,
                0.78,
                190,
                190,
                allowUpscale: true,
                logicalKey: "approachcircle");
        if (circle is not null)
            AddPreviewImage(
                TintBitmap(circle, combo),
                centre,
                1,
                circleSize,
                circleSize,
                allowUpscale: true,
                logicalKey: "hitcircle");

        if (!overlayAboveNumber && overlay is not null)
            AddPreviewImage(
                overlay,
                centre,
                1,
                circleSize,
                circleSize,
                allowUpscale: true,
                logicalKey: "hitcircleoverlay");
        if (previewContext.HitCircleNumber is not null)
            AddPreviewImage(
                previewContext.HitCircleNumber,
                centre,
                1,
                circleSize * 0.3,
                circleSize * 0.43,
                allowUpscale: true,
                logicalKey: "hitcircle-number");
        if (overlayAboveNumber && overlay is not null)
            AddPreviewImage(
                overlay,
                centre,
                1,
                circleSize,
                circleSize,
                allowUpscale: true,
                logicalKey: "hitcircleoverlay");
    }

    internal static BitmapSource? ComposeHitCircleThumbnail(
        IEnumerable<string> imagePaths,
        SkinExtrasPreviewContext previewContext)
    {
        var paths = imagePaths.ToArray();
        var approach = LoadLogicalImage(paths, "approachcircle", 160);
        var circle = LoadLogicalImage(paths, "hitcircle", 160);
        var overlay = LoadLogicalImage(paths, "hitcircleoverlay", 160);
        if (approach is null && circle is null && overlay is null)
            return null;

        var combo = previewContext.ComboColours.FirstOrDefault();
        if (combo == default)
            combo = Color.FromRgb(80, 220, 255);

        const double canvasSize = 128;
        const double circleSize = 88;
        var visual = new DrawingVisual();
        RenderOptions.SetBitmapScalingMode(visual, BitmapScalingMode.HighQuality);
        using (var drawing = visual.RenderOpen())
        {
            void Draw(
                BitmapSource? image,
                double width,
                double height,
                double opacity = 1)
            {
                if (image is null)
                    return;
                if (opacity < 1)
                    drawing.PushOpacity(opacity);
                drawing.DrawImage(
                    image,
                    new Rect(
                        (canvasSize - width) / 2,
                        (canvasSize - height) / 2,
                        width,
                        height));
                if (opacity < 1)
                    drawing.Pop();
            }

            Draw(approach is null ? null : TintBitmap(approach, combo), 122, 122, 0.78);
            Draw(circle is null ? null : TintBitmap(circle, combo), circleSize, circleSize);
            if (!previewContext.HitCircleOverlayAboveNumber)
                Draw(overlay, circleSize, circleSize);
            Draw(
                previewContext.HitCircleNumber,
                circleSize * 0.3,
                circleSize * 0.43);
            if (previewContext.HitCircleOverlayAboveNumber)
                Draw(overlay, circleSize, circleSize);
        }

        var bitmap = new RenderTargetBitmap(
            (int)canvasSize,
            (int)canvasSize,
            96,
            96,
            PixelFormats.Pbgra32);
        bitmap.Render(visual);
        bitmap.Freeze();
        return bitmap;
    }

    private bool RenderSliderPreview(SkinExtraPackPreview pack)
    {
        var combo = previewContext.ComboColours.FirstOrDefault();
        if (combo == default)
            combo = Color.FromRgb(80, 220, 255);
        var isAssetPack = pack.Manifest.FamilyId.Equals(
            "osu.slider",
            StringComparison.OrdinalIgnoreCase);
        var border = isAssetPack
            ? Colors.White
            : PackColour(pack.Manifest, "SliderBorder") ?? Colors.White;
        var track = isAssetPack
            ? Colors.Black
            : PackColour(pack.Manifest, "SliderTrackOverride");
        const double renderScale = 2;
        const double circleDiameter = 76;
        var path = ExtrasSliderPreviewPath;
        var renderPath = path
            .Select(point => new Point(point.X * renderScale, point.Y * renderScale))
            .ToArray();
        var body = LegacySliderRenderer.Render(
            (int)(380 * renderScale),
            (int)(210 * renderScale),
            renderPath,
            circleDiameter / 2 * renderScale,
            combo,
            border,
            track);
        var bodyImage = new Image
        {
            Source = body,
            Width = 380,
            Height = 210,
            Stretch = Stretch.Uniform,
            IsHitTestVisible = false,
        };
        RenderOptions.SetBitmapScalingMode(bodyImage, BitmapScalingMode.HighQuality);
        activePreviewCanvas.Children.Add(bodyImage);

        var tick = LoadLogicalImage(pack.ImagePaths, "sliderscorepoint");
        if (tick is not null)
        {
            foreach (var progress in new[] { 0.22, 0.42, 0.63, 0.82 })
                AddPreviewImage(
                    tick,
                    PathPoint(path, progress),
                    0.95,
                    18,
                    18,
                    allowUpscale: true,
                    logicalKey: "sliderscorepoint");
        }

        AddCurrentHitCircle(path[0], combo, circleDiameter, showNumber: true);
        AddCurrentHitCircle(path[^1], combo, circleDiameter, showNumber: false);

        var reverse = LoadLogicalImage(pack.ImagePaths, "reversearrow");
        if (reverse is not null)
        {
            var direction = path[^2] - path[^1];
            var angle = Math.Atan2(direction.Y, direction.X) * 180 / Math.PI;
            AddPreviewImage(
                reverse,
                path[^1],
                1,
                50,
                50,
                angle,
                allowUpscale: true,
                logicalKey: "reversearrow");
        }
        var follow = LoadLogicalImage(pack.ImagePaths, "sliderfollowcircle");
        var ball = LoadLogicalImage(pack.ImagePaths, "sliderb0")
                   ?? LoadLogicalImage(pack.ImagePaths, "sliderb");
        var ballPoint = PathPoint(path, 0.58);
        if (follow is not null)
            AddPreviewImage(
                follow,
                ballPoint,
                0.92,
                104,
                104,
                allowUpscale: true,
                logicalKey: "sliderfollowcircle");
        if (ball is not null)
        {
            var renderedBall = PackBoolean(pack.Manifest, "General", "AllowSliderBallTint")
                ? TintBitmap(ball, combo)
                : ball;
            AddPreviewImage(
                renderedBall,
                ballPoint,
                1,
                58,
                58,
                allowUpscale: true,
                logicalKey: "sliderb0");
        }
        return true;
    }

    private static Point PathPoint(IReadOnlyList<Point> path, double progress)
    {
        if (path.Count == 0)
            return default;
        if (path.Count == 1)
            return path[0];

        progress = Math.Clamp(progress, 0, 1);
        var lengths = new double[path.Count - 1];
        var total = 0d;
        for (var index = 0; index < lengths.Length; index++)
        {
            lengths[index] = (path[index + 1] - path[index]).Length;
            total += lengths[index];
        }
        if (total <= double.Epsilon)
            return path[0];

        var target = total * progress;
        var travelled = 0d;
        for (var index = 0; index < lengths.Length; index++)
        {
            if (travelled + lengths[index] < target)
            {
                travelled += lengths[index];
                continue;
            }
            var amount = lengths[index] <= double.Epsilon
                ? 0
                : (target - travelled) / lengths[index];
            return path[index] + (path[index + 1] - path[index]) * amount;
        }
        return path[^1];
    }

    private bool RenderHitBurstPreview(SkinExtraPackPreview pack)
    {
        var slots = new[]
        {
            ("hit0", "Miss"),
            ("hit50", "50"),
            ("hit50k", "50k"),
            ("hit100", "100"),
            ("hit100k", "100k"),
            ("hit300", "300"),
            ("hit300k", "300k"),
            ("hit300g", "300g"),
            ("lighting", "Lighting"),
        };
        var items = slots
            .Select(slot => (
                Stem: slot.Item1,
                Label: slot.Item2,
                Image: LoadAnimationRepresentative(pack.ImagePaths, slot.Item1)))
            .Where(item => item.Image is not null)
            .ToArray();
        if (items.Length == 0)
            return false;

        const int columns = 5;
        const double cellWidth = 74;
        const double cellHeight = 94;
        for (var index = 0; index < items.Length; index++)
        {
            var row = index / columns;
            var itemsInRow = Math.Min(columns, items.Length - row * columns);
            var rowOffset = (380 - itemsInRow * cellWidth) / 2;
            var column = index % columns;
            var centre = new Point(
                rowOffset + cellWidth * (column + 0.5),
                12 + cellHeight * row + 33);
            AddPreviewImage(
                items[index].Image!,
                centre,
                1,
                64,
                58,
                logicalKey: items[index].Stem);
            AddPreviewLabel(items[index].Label, rowOffset + cellWidth * column, centre.Y + 35, cellWidth);
        }
        return true;
    }

    private bool RenderSpinnerPreview(SkinExtraPackPreview pack)
    {
        var centre = new Point(105, 101);
        var rendered = false;

        BitmapSource? Visible(string stem)
        {
            var image = LoadLogicalImage(pack.ImagePaths, stem);
            return image is null ? null : VisibleCrop(image);
        }

        var background = Visible("spinner-background");
        var top = Visible("spinner-top");
        var style = LegacySpinnerPreview.Resolve(background is not null, top is not null);
        var layers = style switch
        {
            LegacySpinnerPreviewStyle.Old =>
                new[]
                {
                    (Stem: "spinner-background", Image: background),
                    (Stem: "spinner-circle", Image: Visible("spinner-circle")),
                    (Stem: "spinner-approachcircle", Image: Visible("spinner-approachcircle")),
                },
            LegacySpinnerPreviewStyle.New =>
                new[]
                {
                    (Stem: "spinner-bottom", Image: Visible("spinner-bottom")),
                    (Stem: "spinner-glow", Image: Visible("spinner-glow")),
                    (Stem: "spinner-top", Image: top),
                    (Stem: "spinner-middle2", Image: Visible("spinner-middle2")),
                    (Stem: "spinner-middle", Image: Visible("spinner-middle")),
                    (Stem: "spinner-approachcircle", Image: Visible("spinner-approachcircle")),
                },
            _ => new[]
            {
                (Stem: "spinner-circle", Image: Visible("spinner-circle")),
                (Stem: "spinner-approachcircle", Image: Visible("spinner-approachcircle")),
            },
        };
        foreach (var layer in layers.Where(layer => layer.Image is not null))
        {
            AddPreviewImage(
                layer.Image!,
                centre,
                1,
                184,
                184,
                allowUpscale: true,
                logicalKey: layer.Stem);
            rendered = true;
        }

        var accessories = new[]
        {
            (Stem: "spinner-clear", Label: "Clear", Image: Visible("spinner-clear")),
            (Stem: "spinner-spin", Label: "Spin", Image: Visible("spinner-spin")),
            (Stem: "spinner-rpm", Label: "RPM", Image: Visible("spinner-rpm")),
            (Stem: "spinner-metre", Label: "Metre", Image: Visible("spinner-metre")),
        }.Where(item => item.Image is not null).ToArray();
        for (var index = 0; index < accessories.Length; index++)
        {
            var column = index % 2;
            var row = index / 2;
            var left = 215 + column * 80;
            var cellTop = 13 + row * 94;
            AddPreviewImage(
                accessories[index].Image!,
                new Point(left + 38, cellTop + 36),
                1,
                66,
                58,
                allowUpscale: true,
                logicalKey: accessories[index].Stem);
            AddPreviewLabel(accessories[index].Label, left, cellTop + 68, 76);
        }
        return rendered || accessories.Length > 0;
    }

    private bool RenderSliderColourPreview(SkinExtraPackPreview pack)
    {
        var rendered = RenderSliderPreview(pack);
        AddColourLegend(GetPaletteColours(pack), top: 6);
        return rendered;
    }

    private bool RenderComboColourPreview(SkinExtraPackPreview pack)
    {
        var colours = GetPaletteColours(pack);
        if (colours.Length == 0)
            return false;

        var columns = Math.Min(4, colours.Length);
        var rows = (int)Math.Ceiling(colours.Length / (double)columns);
        var cellWidth = 350d / columns;
        var cellHeight = 184d / rows;
        for (var index = 0; index < colours.Length; index++)
        {
            var row = index / columns;
            var itemsInRow = Math.Min(columns, colours.Length - row * columns);
            var rowWidth = itemsInRow * cellWidth;
            var left = (380 - rowWidth) / 2;
            var column = index % columns;
            var centre = new Point(
                left + cellWidth * (column + 0.5),
                5 + cellHeight * row + Math.Min(41, cellHeight * 0.4));
            AddCurrentHitCircle(centre, colours[index].Colour, Math.Min(66, cellHeight * 0.58), showNumber: true);
            AddColourSwatch(colours[index].Colour, left + cellWidth * column + 5, 7 + cellHeight * row, 17);
            AddPreviewLabel(colours[index].Key, left + cellWidth * column, centre.Y + 38, cellWidth);
            AddPreviewLabel(
                $"RGB({colours[index].Colour.R}, {colours[index].Colour.G}, {colours[index].Colour.B})",
                left + cellWidth * column,
                centre.Y + 52,
                cellWidth);
        }
        return true;
    }

    private static (string Key, Color Colour)[] GetPaletteColours(SkinExtraPackPreview pack)
    {
        var isComboPalette = pack.Manifest.FamilyId.Equals(
            "osu.combo-colours",
            StringComparison.OrdinalIgnoreCase);
        return pack.Manifest.IniPatch
            .Where(entry => entry.Section.Equals("Colours", StringComparison.OrdinalIgnoreCase)
                            && (isComboPalette
                                ? entry.Key.StartsWith("Combo", StringComparison.OrdinalIgnoreCase)
                                : entry.Key is "SliderBall" or "SliderBorder" or "SliderTrackOverride"))
            .OrderBy(entry => entry.Key, StringComparer.OrdinalIgnoreCase)
            .Select(entry => TryParsePreviewColour(entry.Value, out var colour)
                ? (Key: entry.Key, Colour: (Color?)colour)
                : (Key: entry.Key, Colour: (Color?)null))
            .Where(item => item.Colour.HasValue)
            .Select(item => (item.Key, Colour: item.Colour!.Value))
            .ToArray();
    }

    private void AddColourLegend(
        IReadOnlyList<(string Key, Color Colour)> colours,
        double top)
    {
        if (colours.Count == 0)
            return;

        var visibleColours = colours.Take(3).ToArray();
        var cellWidth = 366d / visibleColours.Length;
        for (var index = 0; index < visibleColours.Length; index++)
        {
            var left = 7 + cellWidth * index;
            AddColourSwatch(visibleColours[index].Colour, left, top, 17);
            AddPreviewLabel(
                $"{visibleColours[index].Key}: RGB({visibleColours[index].Colour.R}, {visibleColours[index].Colour.G}, {visibleColours[index].Colour.B})",
                left + 20,
                top + 3,
                cellWidth - 20);
        }
    }

    private void AddColourSwatch(Color colour, double left, double top, double size)
    {
        var swatch = new Border
        {
            Width = size,
            Height = size,
            Background = new SolidColorBrush(colour),
            BorderBrush = Brushes.White,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(3),
            IsHitTestVisible = false,
        };
        Canvas.SetLeft(swatch, left);
        Canvas.SetTop(swatch, top);
        activePreviewCanvas.Children.Add(swatch);
    }

    private void AddCurrentHitCircle(
        Point centre,
        Color colour,
        double size,
        bool showNumber)
    {
        if (previewContext.HitCircle is not null)
        {
            AddPreviewImage(
                TintBitmap(previewContext.HitCircle, colour),
                centre,
                1,
                size,
                size);
        }
        else
        {
            var fallback = new System.Windows.Shapes.Ellipse
            {
                Width = size,
                Height = size,
                Fill = new SolidColorBrush(colour),
                Stroke = Brushes.White,
                StrokeThickness = 3,
                IsHitTestVisible = false,
            };
            Canvas.SetLeft(fallback, centre.X - size / 2);
            Canvas.SetTop(fallback, centre.Y - size / 2);
            activePreviewCanvas.Children.Add(fallback);
        }
        if (previewContext.HitCircleOverlay is not null)
            AddPreviewImage(previewContext.HitCircleOverlay, centre, 1, size, size);
        if (showNumber && previewContext.HitCircleNumber is not null)
            AddPreviewImage(previewContext.HitCircleNumber, centre, 1, size * 0.3, size * 0.43);
    }

    private static BitmapSource TintBitmap(BitmapSource source, Color colour)
    {
        var cache = TintedBitmaps.GetValue(source, _ => new TintBitmapCache());
        lock (cache.Images)
        {
            if (cache.Images.TryGetValue(colour, out var cached))
                return cached;

            var pixels = SkinImageTools.Pixels(source, out var stride);
            SkinImageTools.ApplyMultiplicativeTint(pixels, colour);
            var bitmap = SkinImageTools.ToBitmap(
                pixels,
                source.PixelWidth,
                source.PixelHeight,
                stride);
            bitmap.Freeze();
            cache.Images[colour] = bitmap;
            return bitmap;
        }
    }

    private static BitmapSource? VisibleCrop(BitmapSource source)
    {
        var cache = VisibleCrops.GetValue(source, _ => new VisibleCropCache());
        lock (cache)
        {
            if (!cache.Initialized)
            {
                cache.Image = SkinImageTools.CropToVisiblePixels(source);
                cache.Initialized = true;
            }
            return cache.Image;
        }
    }

    private void AddPreviewLabel(string text, double left, double top, double width)
    {
        var label = new TextBlock
        {
            Text = text,
            Width = width,
            FontSize = 9,
            TextAlignment = TextAlignment.Center,
            Foreground = (Brush)FindResource("Brush.TextMuted"),
            IsHitTestVisible = false,
        };
        Canvas.SetLeft(label, left);
        Canvas.SetTop(label, top);
        activePreviewCanvas.Children.Add(label);
    }

    private static BitmapSource? LoadAnimationRepresentative(
        IEnumerable<string> paths,
        string logicalStem)
    {
        var path = paths
            .Select(path => (Path: path, Stem: LogicalImageStem(path)))
            .Where(item => item.Stem.Equals(logicalStem, StringComparison.OrdinalIgnoreCase)
                           || item.Stem.StartsWith(logicalStem + "-", StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(item =>
                item.Stem.Equals(logicalStem + "-0", StringComparison.OrdinalIgnoreCase))
            .ThenByDescending(item =>
                item.Stem.Equals(logicalStem, StringComparison.OrdinalIgnoreCase))
            .ThenByDescending(item => Path.GetFileNameWithoutExtension(item.Path)
                .EndsWith("@2x", StringComparison.OrdinalIgnoreCase))
            .Select(item => item.Path)
            .FirstOrDefault();
        return path is null ? null : LoadBitmap(path);
    }

    private static Color? PackColour(SkinExtraPackManifest manifest, string key)
    {
        var value = manifest.IniPatch.FirstOrDefault(entry =>
            entry.Section.Equals("Colours", StringComparison.OrdinalIgnoreCase)
            && entry.Key.Equals(key, StringComparison.OrdinalIgnoreCase))?.Value;
        return TryParsePreviewColour(value, out var colour) ? colour : null;
    }

    private static bool PackBoolean(
        SkinExtraPackManifest manifest,
        string section,
        string key,
        bool defaultValue = false)
    {
        var value = manifest.IniPatch.FirstOrDefault(entry =>
            entry.Section.Equals(section, StringComparison.OrdinalIgnoreCase)
            && entry.Key.Equals(key, StringComparison.OrdinalIgnoreCase))?.Value;
        return value is null ? defaultValue : value == "1";
    }

    private static bool IsLegacyVersionOne(SkinExtraPackManifest manifest)
    {
        var value = manifest.IniPatch.FirstOrDefault(entry =>
            entry.Section.Equals("General", StringComparison.OrdinalIgnoreCase)
            && entry.Key.Equals("Version", StringComparison.OrdinalIgnoreCase))?.Value;
        return decimal.TryParse(
                   value,
                   System.Globalization.NumberStyles.Number,
                   System.Globalization.CultureInfo.InvariantCulture,
                   out var version)
               && version <= 1;
    }

    private static bool TryParsePreviewColour(string? raw, out Color colour)
    {
        colour = Colors.White;
        if (string.IsNullOrWhiteSpace(raw))
            return false;
        var parts = raw.Split(',', StringSplitOptions.TrimEntries);
        if (parts.Length < 3
            || !byte.TryParse(parts[0], out var red)
            || !byte.TryParse(parts[1], out var green)
            || !byte.TryParse(parts[2], out var blue))
            return false;
        colour = Color.FromRgb(red, green, blue);
        return true;
    }

    private static BitmapSource? LoadLogicalImage(
        IEnumerable<string> paths,
        string logicalStem,
        int maxLogicalPixelWidth = 512)
    {
        var candidates = paths
            .Where(path => LogicalImageStem(path).Equals(
                logicalStem,
                StringComparison.OrdinalIgnoreCase))
            .Select(path => new LogicalImageCandidate(path, path));
        return LoadBestLogicalImage(candidates, maxLogicalPixelWidth);
    }

    private static BitmapSource? LoadBestLogicalImage(
        IEnumerable<LogicalImageCandidate> candidates,
        int maxLogicalPixelWidth = 0)
    {
        var loaded = candidates
            .Select(candidate =>
            {
                var image = LoadBitmap(candidate.Path, maxLogicalPixelWidth);
                var visible = image is null
                    ? null
                    : VisibleCrop(image);
                return new
                {
                    Image = image,
                    VisibleArea = visible is null
                        ? 0L
                        : (long)visible.PixelWidth * visible.PixelHeight,
                    LogicalArea = image is null
                        ? 0L
                        : (long)image.PixelWidth * image.PixelHeight,
                    IsHighDefinition = Path.GetFileNameWithoutExtension(candidate.Filename)
                        .EndsWith("@2x", StringComparison.OrdinalIgnoreCase),
                };
            })
            .Where(candidate => candidate.Image is not null)
            .OrderByDescending(candidate => candidate.VisibleArea > 0)
            .ThenByDescending(candidate => candidate.IsHighDefinition)
            .ThenByDescending(candidate => candidate.VisibleArea)
            .ThenByDescending(candidate => candidate.LogicalArea)
            .FirstOrDefault();
        return loaded?.Image;
    }

    private static IEnumerable<string> PreferredPreviewPaths(IEnumerable<string> paths) =>
        paths.GroupBy(LogicalImageStem, StringComparer.OrdinalIgnoreCase)
            .Select(group => group
                .OrderByDescending(path => Path.GetFileNameWithoutExtension(path)
                    .EndsWith("@2x", StringComparison.OrdinalIgnoreCase))
                .First());

    private static string LogicalImageStem(string path)
    {
        var stem = Path.GetFileNameWithoutExtension(path);
        return stem.EndsWith("@2x", StringComparison.OrdinalIgnoreCase)
            ? stem[..^3]
            : stem;
    }

    private sealed record LogicalImageCandidate(string Filename, string Path);

    private void RenderCursorPreview(SkinExtraPackPreview pack)
    {
        activePreviewCanvas.Children.Clear();
        var layers = SkinCursorPreview.Compose(
            pack.CursorImage is not null,
            pack.TrailImage is not null,
            pack.MiddleImage is not null,
            HasVisibleCursorMiddle(pack.MiddleImage));
        foreach (var layer in layers)
        {
            var bitmap = layer.Kind switch
            {
                SkinCursorPreviewLayerKind.Trail => pack.TrailImage,
                SkinCursorPreviewLayerKind.Middle => pack.MiddleImage,
                SkinCursorPreviewLayerKind.Cursor => pack.CursorImage,
                _ => null,
            };
            if (bitmap is null)
                continue;
            AddPreviewImage(
                bitmap,
                new Point(layer.CentreX, layer.CentreY),
                layer.Opacity,
                layer.MaxWidth,
                layer.MaxHeight,
                allowUpscale: true,
                logicalKey: layer.Kind switch
                {
                    SkinCursorPreviewLayerKind.Trail => "cursortrail",
                    SkinCursorPreviewLayerKind.Middle => "cursormiddle",
                    SkinCursorPreviewLayerKind.Cursor => "cursor",
                    _ => null,
                });
        }
    }

    // Visible cursor-middle artwork remains a supported comparison input, but
    // pack application never imports it. The fully transparent 1x1 Smooth Trail
    // placeholder is recognized separately below; alpha noise is not.
    internal static bool HasVisibleCursorMiddle(BitmapSource? middle)
    {
        if (middle is null)
            return false;
        var pixels = SkinImageTools.Pixels(middle, out _);
        return SkinCursorMiddlePolicy.HasRenderablePixels(
            SkinCursorMiddlePolicy.CanonicalFilename,
            middle.PixelWidth,
            middle.PixelHeight,
            pixels);
    }

    internal static bool UsesSmoothCursorTrail(BitmapSource? middle) =>
        middle is not null;

    internal static bool IsSmoothTrailPlaceholder(BitmapSource? middle)
    {
        if (middle is not { PixelWidth: 1, PixelHeight: 1 })
            return false;
        var pixels = SkinImageTools.Pixels(middle, out _);
        return pixels.Length >= 4 && pixels[3] == 0;
    }

    internal static IReadOnlyList<Point> BuildTrailPoints(bool continuous)
        => SkinCursorPreview.TrailPoints(continuous)
            .Select(point => new Point(point.X, point.Y))
            .ToArray();

    private void AddPreviewImage(
        BitmapSource bitmap,
        Point centre,
        double opacity,
        double maxWidth = double.PositiveInfinity,
        double maxHeight = double.PositiveInfinity,
        double rotationDegrees = 0,
        bool allowUpscale = false,
        string? logicalKey = null)
    {
        // osu! skin coordinates are pixel-based. PNG DPI metadata is frequently
        // arbitrary and must not change the apparent gameplay size.
        var width = (double)bitmap.PixelWidth;
        var height = (double)bitmap.PixelHeight;
        var scale = Math.Min(maxWidth / width, maxHeight / height);
        if (!allowUpscale)
            scale = Math.Min(1, scale);
        width *= scale;
        height *= scale;
        var image = new Image
        {
            Source = bitmap,
            Width = width,
            Height = height,
            Stretch = Stretch.Fill,
            Opacity = opacity,
            IsHitTestVisible = false,
        };
        if (Math.Abs(rotationDegrees) > double.Epsilon)
        {
            image.RenderTransformOrigin = new Point(0.5, 0.5);
            image.RenderTransform = new RotateTransform(rotationDegrees);
        }
        RenderOptions.SetBitmapScalingMode(image, BitmapScalingMode.HighQuality);
        Canvas.SetLeft(image, centre.X - width / 2);
        Canvas.SetTop(image, centre.Y - height / 2);
        activePreviewCanvas.Children.Add(image);
        HighlightPreviewLayer(image, centre, width, height, rotationDegrees, logicalKey);
    }

    private void AddPreviewImageSized(
        BitmapSource bitmap,
        Point centre,
        double width,
        double height,
        string? logicalKey = null)
    {
        var image = new Image
        {
            Source = bitmap,
            Width = width,
            Height = height,
            Stretch = Stretch.Fill,
            IsHitTestVisible = false,
        };
        RenderOptions.SetBitmapScalingMode(image, BitmapScalingMode.HighQuality);
        Canvas.SetLeft(image, centre.X - width / 2);
        Canvas.SetTop(image, centre.Y - height / 2);
        activePreviewCanvas.Children.Add(image);
        HighlightPreviewLayer(image, centre, width, height, 0, logicalKey);
    }

    private void AddPreviewImageCover(
        BitmapSource bitmap,
        Point centre,
        double width,
        double height,
        string? logicalKey = null)
    {
        var image = new Image
        {
            Source = bitmap,
            Width = width,
            Height = height,
            Stretch = Stretch.UniformToFill,
            ClipToBounds = true,
            IsHitTestVisible = false,
        };
        RenderOptions.SetBitmapScalingMode(image, BitmapScalingMode.HighQuality);
        Canvas.SetLeft(image, centre.X - width / 2);
        Canvas.SetTop(image, centre.Y - height / 2);
        activePreviewCanvas.Children.Add(image);
        HighlightPreviewLayer(image, centre, width, height, 0, logicalKey);
    }

    private void HighlightPreviewLayer(
        Image image,
        Point centre,
        double width,
        double height,
        double rotationDegrees,
        string? logicalKey)
    {
        if (string.IsNullOrWhiteSpace(logicalKey))
            return;

        var animationRole = ExtrasAnimationRole(logicalKey);
        var animationIndex = activePreviewCanvas.Children
            .OfType<Image>()
            .Count(candidate =>
                candidate.Tag is PreviewLayerVisual existing
                && existing.AnimationRole == animationRole);
        var layer = new PreviewLayerVisual(
            logicalKey,
            image.Opacity,
            Panel.GetZIndex(image),
            centre,
            width,
            height,
            rotationDegrees,
            animationRole,
            animationIndex,
            new DropShadowEffect
            {
                Color = Color.FromRgb(236, 73, 142),
                BlurRadius = 22,
                ShadowDepth = 0,
                Opacity = 0.95,
            });
        image.Tag = layer;
        if (!IsPreviewLayerHighlighted(logicalKey))
            return;

        ApplyPreviewLayerHighlight(image, layer);
        activePreviewCanvas.Children.Add(CreatePreviewLayerOutline(layer));
    }

    private void ApplyPreviewLayerHighlight(
        Canvas canvas,
        PackElementEntry? element)
    {
        foreach (var outline in canvas.Children
                     .OfType<Border>()
                     .Where(candidate => ReferenceEquals(
                         candidate.Tag,
                         PreviewLayerOutlineTag))
                     .ToArray())
        {
            canvas.Children.Remove(outline);
        }

        var outlines = new List<Border>();
        foreach (var image in canvas.Children.OfType<Image>())
        {
            if (image.Tag is not PreviewLayerVisual layer)
                continue;

            image.Opacity = layer.BaseOpacity;
            image.Effect = null;
            Panel.SetZIndex(image, layer.BaseZIndex);
            if (!IsPreviewLayerHighlighted(layer.LogicalKey, element))
                continue;

            ApplyPreviewLayerHighlight(image, layer);
            outlines.Add(CreatePreviewLayerOutline(layer));
        }

        foreach (var outline in outlines)
            canvas.Children.Add(outline);
        RenderExtrasPreviewFrame();
    }

    private static void ApplyPreviewLayerHighlight(
        Image image,
        PreviewLayerVisual layer)
    {
        image.Opacity = 1;
        image.Effect = layer.Glow;
        Panel.SetZIndex(image, 100);
    }

    private static Border CreatePreviewLayerOutline(PreviewLayerVisual layer)
    {
        var outline = new Border
        {
            Tag = PreviewLayerOutlineTag,
            DataContext = layer,
            Width = layer.Width + 10,
            Height = layer.Height + 10,
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(2),
            CornerRadius = new CornerRadius(7),
            IsHitTestVisible = false,
            RenderTransformOrigin = new Point(0.5, 0.5),
            RenderTransform = Math.Abs(layer.RotationDegrees) > double.Epsilon
                ? new RotateTransform(layer.RotationDegrees)
                : Transform.Identity,
        };
        outline.SetResourceReference(Border.BorderBrushProperty, "Brush.AccentPink");
        Canvas.SetLeft(outline, layer.Centre.X - layer.Width / 2 - 5);
        Canvas.SetTop(outline, layer.Centre.Y - layer.Height / 2 - 5);
        Panel.SetZIndex(outline, 110);
        return outline;
    }

    private bool IsPreviewLayerHighlighted(string? logicalKey)
        => IsPreviewLayerHighlighted(logicalKey, activePreviewHighlightElement);

    private bool IsPreviewLayerHighlighted(
        string? logicalKey,
        PackElementEntry? element)
    {
        if (element is null
            || selectedPack is not { } pack
            || string.IsNullOrWhiteSpace(logicalKey))
            return false;

        var filenames = pack.Files.Select(file => file.Name).ToArray();
        var groupedKey = SkinExtraLogicalGrouping.Key(
            pack.Manifest.FamilyId,
            logicalKey,
            filenames);
        if (groupedKey.Equals(element.Key, StringComparison.OrdinalIgnoreCase))
            return true;

        var previewStem = LogicalImageStem(logicalKey);
        return element.Files.Any(file =>
        {
            var elementStem = LogicalImageStem(file.Name);
            return elementStem.Equals(previewStem, StringComparison.OrdinalIgnoreCase)
                   || elementStem.StartsWith(
                       previewStem + "-",
                       StringComparison.OrdinalIgnoreCase)
                   || elementStem.EndsWith(
                       "-" + previewStem,
                       StringComparison.OrdinalIgnoreCase)
                   || previewStem.StartsWith(
                       elementStem + "-",
                       StringComparison.OrdinalIgnoreCase)
                   || previewStem.EndsWith(
                       "-" + elementStem,
                       StringComparison.OrdinalIgnoreCase);
        });
    }

    private void AddPreviewKeyLabel(string text, Point centre)
    {
        var label = new TextBlock
        {
            Text = text,
            Width = 54,
            FontSize = 12,
            FontWeight = FontWeights.SemiBold,
            TextAlignment = TextAlignment.Center,
            Foreground = Brushes.White,
            IsHitTestVisible = false,
        };
        Canvas.SetLeft(label, centre.X - 27);
        Canvas.SetTop(label, centre.Y - 8);
        activePreviewCanvas.Children.Add(label);
    }

    private async void UsePack_Click(object sender, RoutedEventArgs e) => await UseSelectedPackAsync();

    private async void RandomHitsounds_Click(object sender, RoutedEventArgs e) =>
        await StageRandomMixAsync(hitsoundsOnly: true);

    private async void FullRandom_Click(object sender, RoutedEventArgs e) =>
        await StageRandomMixAsync(hitsoundsOnly: false);

    private async Task StageRandomMixAsync(bool hitsoundsOnly)
    {
        if (staging || allPacks.Count == 0)
            return;
        var candidates = allPacks
            .Where(pack => SkinExtraPackValidator.Validate(
                pack.Descriptor,
                verifyContent: false).IsHealthy)
            .Select(pack => new SkinStudioRandomPackCandidate(
                pack.PackKey,
                pack.NavigationFamilyId,
                pack.IsCurrentlyInUse))
            .ToArray();
        var keys = hitsoundsOnly
            ? SkinStudioRandomMix.ChooseHitsounds(candidates, Random.Shared)
            : SkinStudioRandomMix.ChooseFull(candidates, Random.Shared);
        var packs = keys.Select(key => allPacks.First(pack => pack.PackKey == key)).ToArray();
        if (packs.Length == 0)
        {
            KumoriDialog.Show(
                dialogOwner,
                hitsoundsOnly
                    ? "No complete Normal, Soft, or Drum hitsound packs are available."
                    : "No complete compatible packs are available.",
                "Random mix",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
            return;
        }
        var selections = packs.Select(CreateWholePackSelection).ToArray();
        staging = true;
        RandomHitsoundsButton.IsEnabled = false;
        FullRandomButton.IsEnabled = false;
        RandomMixProgressBar.Minimum = 0;
        RandomMixProgressBar.Maximum = packs.Length;
        RandomMixProgressBar.Value = 0;
        RandomMixOverlayText.Text = hitsoundsOnly
            ? "Adding a fresh hitsound trio to Changes…"
            : $"Building a random skin from {packs.Length} families…";
        RandomMixProgressDetailText.Text = $"0 of {packs.Length} packs staged";
        RandomMixOverlay.Visibility = Visibility.Visible;
        IProgress<SkinExtrasBatchProgress> progress =
            new Progress<SkinExtrasBatchProgress>(UpdateRandomMixProgress);
        try
        {
            bool staged;
            if (stageSelections is not null)
            {
                staged = await stageSelections(selections, progress);
            }
            else if (stageSelection is not null)
            {
                staged = true;
                for (var index = 0; index < selections.Length; index++)
                {
                    progress.Report(new SkinExtrasBatchProgress(
                        index,
                        selections.Length,
                        packs[index].NavigationFamilyName,
                        packs[index].Name));
                    if (!await stageSelection(selections[index]))
                    {
                        staged = false;
                        break;
                    }
                    progress.Report(new SkinExtrasBatchProgress(
                        index + 1,
                        selections.Length,
                        packs[index].NavigationFamilyName,
                        packs[index].Name));
                }
            }
            else
            {
                staged = false;
            }
            if (!staged)
                return;
            InvalidateCurrentPreview();
            SubtitleText.Text = hitsoundsOnly
                ? "A fresh Normal + Soft + Drum hitsound trio is staged in Changes."
                : $"A full random mix from {packs.Length} families is staged in Changes.";
        }
        finally
        {
            staging = false;
            RandomMixOverlay.Visibility = Visibility.Collapsed;
            RandomHitsoundsButton.IsEnabled = true;
            FullRandomButton.IsEnabled = true;
            if (selectedPack is { } selected)
                UpdateSelectionUi(selected);
        }
    }

    private void UpdateRandomMixProgress(SkinExtrasBatchProgress progress)
    {
        var total = Math.Max(1, progress.TotalPacks);
        var completed = Math.Clamp(progress.CompletedPacks, 0, total);
        RandomMixProgressBar.Maximum = total;
        RandomMixProgressBar.Value = completed;
        RandomMixOverlayText.Text = completed >= total
            ? "Finishing the random mix…"
            : $"Adding {progress.FamilyName}: {progress.PackName}";
        RandomMixProgressDetailText.Text =
            $"{completed} of {progress.TotalPacks} packs staged";
    }

    private SkinExtrasSelectionResult CreateWholePackSelection(SkinExtraPackPreview pack)
    {
        var manifest = CopyManifest(
            pack.Manifest,
            files: pack.Manifest.Files.ToList(),
            iniPatch: pack.Manifest.IniPatch.ToList());
        return new SkinExtrasSelectionResult(
            pack.DirectoryPath,
            manifest,
            pack.Elements.Count,
            manifest.Files.Count,
            manifest.IniPatch.Count,
            ReplaceEntireFamily: false,
            ResolutionPolicy: UseOneXResolutionOption.IsChecked == true
                ? SkinExtraResolutionPolicy.UseOneX
                : SkinExtraResolutionPolicy.UpscaleToTwoX,
            DeleteCurrentFiles: [],
            SmoothTrail: false,
            ElementTints: null);
    }

    private async Task UseSelectedPackAsync()
    {
        if (selectedPack is not { } pack)
            return;
        var health = SkinExtraPackValidator.Validate(pack.Descriptor, verifyContent: false);
        if (!health.IsHealthy)
        {
            var details = string.Join(
                "\n",
                health.Issues
                    .Where(issue => issue.Severity == SkinExtraHealthSeverity.Error)
                    .Take(5)
                    .Select(issue => $"• {issue.Message}"));
            KumoriDialog.Show(
                dialogOwner,
                "This Extras pack cannot be staged because it is incomplete or damaged."
                + (details.Length == 0 ? "" : $"\n\n{details}"),
                "Extras pack needs repair",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
            return;
        }
        var completingImport = incompleteImportGuide is not null
                               && pack.NavigationFamilyId.Equals(
                                   incompleteImportGuide.NavigationFamilyId,
                                   StringComparison.OrdinalIgnoreCase);
        var selectedFilenames = pack.Files
            .Where(file => file.IsSelected)
            .Select(file => file.Name)
            .ToArray();
        if (completingImport
            && !incompleteImportGuide!.RemainingAssets.Any(asset =>
                selectedFilenames.Any(filename =>
                    SkinExtraCompleteness.Supplies(
                        pack.Manifest.FamilyId,
                        filename,
                        asset.Key))))
        {
            KumoriDialog.Show(
                dialogOwner,
                $"This selection does not contain any of the files still needed: "
                + $"{incompleteImportGuide.MissingSummary}.\n\n"
                + "Choose a highlighted missing element from another pack, then try again.",
                "Choose missing skin files",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
            return;
        }

        var supplementIncompletePack = false;
        IncompleteImportGuide? requestedGuide = null;
        if (!completingImport && pack.Files.Count > 0)
        {
            var completeness = SkinExtraCompleteness.Analyze(
                pack.Manifest.FamilyId,
                pack.Files.Select(file => file.Name));
            if (!completeness.IsComplete)
            {
                var decision = KumoriDialog.Show(
                    dialogOwner,
                    $"“{pack.Name}” is incomplete. It is missing "
                    + $"{completeness.MissingSummary}.\n\n"
                    + "Yes — import it as-is.\n"
                    + "No — import these files, then choose the missing files "
                    + $"from another {pack.Manifest.FamilyName} pack.\n"
                    + "Cancel — return to the library without importing.",
                    "Incomplete skin element",
                    MessageBoxButton.YesNoCancel,
                    MessageBoxImage.Warning,
                    MessageBoxResult.No);
                if (decision == MessageBoxResult.Cancel)
                    return;
                supplementIncompletePack = decision == MessageBoxResult.No;
                if (supplementIncompletePack)
                {
                    requestedGuide = new IncompleteImportGuide(
                        pack.Name,
                        pack.NavigationFamilyId,
                        pack.Manifest.FamilyId,
                        pack.Manifest.Fingerprint,
                        completeness.MissingAssets,
                        new HashSet<string>(StringComparer.OrdinalIgnoreCase)
                        {
                            pack.PackKey,
                        });
                }
            }
        }
        var selectedTargets = pack.Files
            .Where(file => file.IsSelected)
            .Select(file => file.Name)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        SkinFollowpointSequence.IncludeTransparentManifestFrames(
            pack.Manifest,
            selectedTargets);
        var selectedSettings = pack.Settings
            .Where(setting => setting.IsSelected
                              && (!setting.IsRequired || selectedTargets.Count > 0))
            .Select(setting => setting.Patch)
            .ToList();
        var deleteCurrentFiles = currentFallbackElements
            .Where(element => element.IsSelected == false)
            .SelectMany(element => element.Files)
            .Select(file => file.Name)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var smoothTrail = SkinCursorMiddlePolicy.IsCursorFamily(pack.Manifest.FamilyId)
                          && SmoothTrailCheckBox.IsChecked == true;
        var removeExistingCursorMiddle =
            SkinCursorMiddlePolicy.IsCursorFamily(pack.Manifest.FamilyId)
            && currentSkinSource?.Filenames.Any(
                SkinCursorMiddlePolicy.IsCursorMiddle) == true;
        if (selectedTargets.Count == 0
            && selectedSettings.Count == 0
            && deleteCurrentFiles.Length == 0
            && !smoothTrail
            && !removeExistingCursorMiddle)
            return;
        var manifest = CopyManifest(
            pack.Manifest,
            files: pack.Manifest.Files
                .Where(file => selectedTargets.Contains(file.TargetFilename))
                .ToList(),
            iniPatch: selectedSettings,
            fingerprint: completingImport
                         && pack.Manifest.FamilyId.Equals(
                             "osu.number-font",
                             StringComparison.OrdinalIgnoreCase)
                ? incompleteImportGuide!.SourceFingerprint
                : null);
        var elementTints = pack.Elements
            .Where(element => element.IsTinted && element.IsSelected != false)
            .ToDictionary(
                element => element.Key,
                element => element.TintRgb,
                StringComparer.OrdinalIgnoreCase);
        var selection = new SkinExtrasSelectionResult(
            pack.DirectoryPath,
            manifest,
            pack.SelectedElementCount,
            selectedTargets.Count + (smoothTrail ? 1 : 0),
            selectedSettings.Count,
            // Selecting every file in a partial Extras pack must not mean
            // "delete the rest of this family". Extras are additive by
            // default; only the checked targets can be staged.
            ReplaceEntireFamily: false,
            ResolutionPolicy: UseOneXResolutionOption.IsChecked == true
                ? SkinExtraResolutionPolicy.UseOneX
                : SkinExtraResolutionPolicy.UpscaleToTwoX,
            DeleteCurrentFiles: deleteCurrentFiles,
            SmoothTrail: smoothTrail,
            ElementTints: elementTints);

        if (stageSelection is not null)
        {
            var staged = false;
            staging = true;
            UsePackButton.IsEnabled = false;
            UsePackButton.Content = "Staging…";
            try
            {
                staged = await stageSelection(selection);
                if (staged)
                {
                    InvalidateCurrentPreview();
                    SkinExtrasLibraryStateStore.Update(
                        AppPaths.SkinExtrasDir,
                        pack.StateKey,
                        state => state.LastUsedUtc = DateTimeOffset.UtcNow);
                    if (completingImport)
                    {
                        if (CompleteGuidedAssets(pack, selectedTargets))
                        {
                            SubtitleText.Text =
                                $"Complete: “{pack.Name}” supplied the remaining files. "
                                + "Both selections are staged in Changes.";
                            UsePackButton.Content = "Add another selection";
                        }
                    }
                    else if (supplementIncompletePack)
                    {
                        incompleteImportGuide = requestedGuide;
                        if (!GuideToNextMissingPack())
                        {
                            var missing = incompleteImportGuide?.MissingSummary
                                          ?? "the missing files";
                            incompleteImportGuide = null;
                            SubtitleText.Text =
                                "The available files were staged, but no other library pack "
                                + $"contains {missing}. Import another skin or pack to finish it.";
                            UsePackButton.Content = "Add another selection";
                            KumoriDialog.Show(
                                dialogOwner,
                                $"The available files from “{pack.Name}” were staged.\n\n"
                                + $"No other {pack.Manifest.FamilyName} pack in Extras contains "
                                + $"{missing}. Import another skin or pack that contains those "
                                + "files, then return here to add them.",
                                "Missing files not found",
                                MessageBoxButton.OK,
                                MessageBoxImage.Information);
                        }
                    }
                    else
                    {
                        SubtitleText.Text =
                            "Staged for the current skin. Choose another pack or close this window when you are done.";
                        UsePackButton.Content = "Add another selection";
                    }
                }
            }
            finally
            {
                staging = false;
                if (!staged)
                    UsePackButton.Content = "Add to Changes";
                UpdateSelectionUi(pack);
            }
            return;
        }

        SkinExtrasLibraryStateStore.Update(
            AppPaths.SkinExtrasDir,
            pack.StateKey,
            state => state.LastUsedUtc = DateTimeOffset.UtcNow);
        SelectedPackDirectory = pack.DirectoryPath;
        SelectedManifest = manifest;
        SelectionResult = selection;
        CloseRequested?.Invoke(this, EventArgs.Empty);
    }

    private void AudioCurrentTrackPicker_SelectionChanged(
        object sender,
        SelectionChangedEventArgs e)
    {
        if (!updatingAudioTrackSelection
            && AudioCurrentTrackPicker.SelectedItem is AudioTrackOption track)
            SelectAudioTrack(track, autoplay: false);
    }

    private void AudioPackTrackPicker_SelectionChanged(
        object sender,
        SelectionChangedEventArgs e)
    {
        if (!updatingAudioTrackSelection
            && AudioPackTrackPicker.SelectedItem is AudioTrackOption track)
            SelectAudioTrack(track, autoplay: false);
    }

    private void AudioTrackItem_Click(object sender, MouseButtonEventArgs e)
    {
        if (sender is ListBoxItem { DataContext: AudioTrackOption track })
            SelectAudioTrack(track, autoplay: true);
    }

    private void AudioPlayPause_Click(object sender, RoutedEventArgs e)
    {
        if (audioStream == 0)
        {
            if (SelectedAudioRequest() is { } request)
                PlayAudio(request.Path, request.Label);
            return;
        }

        var state = Bass.ChannelIsActive(audioStream);
        if (state == PlaybackState.Playing)
        {
            if (Bass.ChannelPause(audioStream))
            {
                audioProgressTimer.Stop();
                AudioPlayPauseButton.Content = "Play";
                AudioPlaybackStateText.Text = "Paused";
            }
            return;
        }

        if (Bass.ChannelPlay(audioStream, false))
        {
            audioProgressTimer.Start();
            AudioPlayPauseButton.Content = "Pause";
            AudioPlaybackStateText.Text = playingAudioLoops ? "Looping" : "Playing";
        }
    }

    private void AudioProgress_PreviewMouseLeftButtonDown(
        object sender,
        MouseButtonEventArgs e)
    {
        audioSeeking = true;
    }

    private void AudioProgress_PreviewMouseLeftButtonUp(
        object sender,
        MouseButtonEventArgs e)
    {
        if (audioStream != 0)
        {
            var position = Bass.ChannelSeconds2Bytes(
                audioStream,
                AudioProgressSlider.Value);
            Bass.ChannelSetPosition(audioStream, position);
            UpdateAudioProgress();
        }
        audioSeeking = false;
    }

    private void StopAudioPreview_Click(object sender, RoutedEventArgs e) => StopAudio();

    private void AudioPlayPack_Click(object sender, RoutedEventArgs e)
    {
        if (selectedPack is not { } pack
            || activeAudioFamilyId is null
            || SkinAudioScenarioAudition.Build(activeAudioFamilyId, true) is null)
            return;

        var useCurrent = selectedAudioTrack?.SourceLabel == "Current skin";
        var sourceLabel = useCurrent ? "Current skin" : "With selection";
        var picker = useCurrent ? AudioCurrentTrackPicker : AudioPackTrackPicker;
        var tracks = picker.Items.Cast<AudioTrackOption>().ToArray();
        if (tracks.Length == 0)
            return;
        var plan = SkinAudioScenarioAudition.Build(
            activeAudioFamilyId,
            EffectiveLayeredHitSounds(pack, withSelection: !useCurrent));
        if (plan is null)
            return;
        var sequence = ResolveAudioSequence(plan, tracks);
        if (sequence.Count == 0)
            return;

        try
        {
            StopAudio();
            SelectAudioDevice();
            audioSequenceSteps = sequence;
            audioSequenceIndex = 0;
            audioSequenceSourceLabel = sourceLabel;
            audioSequenceTimer.Interval = TimeSpan.FromMilliseconds(
                plan.IntervalMilliseconds);
            AudioProgressSlider.Minimum = 0;
            AudioProgressSlider.Maximum = sequence.Count;
            AudioProgressSlider.Value = 0;
            AudioDurationText.Text = $"{sequence.Count} hits";
            AudioPlayPauseButton.IsEnabled = false;
            AudioPlayPackButton.IsEnabled = false;
            StopAudioPreviewButton.IsEnabled = true;
            PlayNextAudioSequenceStep();
            audioSequenceTimer.Start();
        }
        catch (Exception ex)
        {
            StopAudio();
            PackDetails.Text = $"Could not preview this hitsound set: {ex.Message}";
            PackNoticePanel.Visibility = Visibility.Visible;
        }
    }

    private void PlayNextAudioSequenceStep()
    {
        ReleaseFinishedAudioSequenceStreams();
        if (audioSequenceIndex >= audioSequenceSteps.Count)
        {
            if (audioSequenceStreams.Count > 0)
            {
                AudioPlaybackStateText.Text = "Finishing last hit";
                return;
            }
            audioSequenceTimer.Stop();
            AudioPlaybackStateText.Text = "Sequence complete";
            AudioPlayPackButton.IsEnabled = true;
            AudioPlayPauseButton.IsEnabled = selectedAudioTrack is not null;
            StopAudioPreviewButton.IsEnabled = false;
            return;
        }

        var step = audioSequenceSteps[audioSequenceIndex++];
        foreach (var track in step.Tracks)
        {
            var stream = Bass.CreateStream(track.Path, 0, 0, BassFlags.Default);
            if (stream == 0)
                throw new InvalidOperationException(
                    $"BASS could not open {track.Filename} ({Bass.LastError}).");
            if (!Bass.ChannelPlay(stream, true))
            {
                var error = Bass.LastError;
                Bass.StreamFree(stream);
                throw new InvalidOperationException(
                    $"BASS could not play {track.Filename} ({error}).");
            }
            audioSequenceStreams.Add(stream);
        }

        AudioNowPlayingText.Text = step.Label;
        AudioSourceStatusText.Text = $"{audioSequenceSourceLabel} · osu! hitsound sequence";
        AudioPlaybackStateText.Text = $"Step {audioSequenceIndex} of {audioSequenceSteps.Count}";
        AudioElapsedText.Text = $"{audioSequenceIndex}/{audioSequenceSteps.Count}";
        AudioProgressSlider.Value = audioSequenceIndex;
    }

    private void ReleaseFinishedAudioSequenceStreams()
    {
        foreach (var stream in audioSequenceStreams
                     .Where(stream => Bass.ChannelIsActive(stream) == PlaybackState.Stopped)
                     .ToArray())
        {
            Bass.StreamFree(stream);
            audioSequenceStreams.Remove(stream);
        }
    }

    private void PackFileChoice_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: PackFileEntry file })
            return;
        var pack = allPacks.FirstOrDefault(candidate => candidate.Files.Contains(file));
        if (pack is not null)
        {
            pack.NotifySelectionChanged();
            SelectPack(pack);
            UpdateSelectionUi(pack);
            RefreshSelectionPreview(pack);
        }
    }

    private void PackExpander_Expanded(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: SkinExtraPackPreview pack })
            return;
        pack.IsExpanded = true;
        expandedPackKeys.Add(pack.PackKey);
    }

    private void PackRow_PreviewMouseLeftButtonDown(
        object sender,
        MouseButtonEventArgs e)
    {
        if (sender is not FrameworkElement
            {
                DataContext: SkinExtraPackPreview pack
            } row)
            return;

        if (IsPackExpandToggleClick(e.OriginalSource as DependencyObject, row))
        {
            SelectPack(pack);
            pack.IsExpanded = !pack.IsExpanded;
            e.Handled = true;
            return;
        }

        SelectPack(pack);
        e.Handled = true;
    }

    private static bool IsPackExpandToggleClick(
        DependencyObject? source,
        DependencyObject row)
    {
        for (var current = source;
             current is not null && !ReferenceEquals(current, row);
             current = VisualTreeHelper.GetParent(current))
        {
            if (current is ToggleButton { Name: "PackExpandToggle" })
                return true;
        }
        return false;
    }

    private void PackExpander_Collapsed(object sender, RoutedEventArgs e)
    {
        if (loadingPacks
            || sender is not FrameworkElement { DataContext: SkinExtraPackPreview pack })
            return;
        pack.IsExpanded = false;
        expandedPackKeys.Remove(pack.PackKey);
    }

    private void SelectAllPackFiles_Click(object sender, RoutedEventArgs e) =>
        SetPackFileSelection(sender, true);

    private void SelectNoPackFiles_Click(object sender, RoutedEventArgs e) =>
        SetPackFileSelection(sender, false);

    private void SetPackFileSelection(object sender, bool selected)
    {
        var pack = (sender as FrameworkElement)?.DataContext as SkinExtraPackPreview
                   ?? selectedPack;
        if (pack is null)
            return;
        foreach (var file in pack.Files)
            file.IsSelected = selected;
        foreach (var setting in pack.Settings.Where(setting => !setting.IsRequired))
            setting.IsSelected = selected;
        foreach (var element in pack.Elements)
            element.NotifySelectionChanged();
        pack.NotifySelectionChanged();
        SelectPack(pack);
        UpdateSelectionUi(pack);
        RefreshSelectionPreview(pack);
    }

    private void InvertPackFiles_Click(object sender, RoutedEventArgs e)
    {
        var pack = (sender as FrameworkElement)?.DataContext as SkinExtraPackPreview
                   ?? selectedPack;
        if (pack is null)
            return;
        foreach (var file in pack.Files)
            file.IsSelected = !file.IsSelected;
        foreach (var setting in pack.Settings.Where(setting => !setting.IsRequired))
            setting.IsSelected = !setting.IsSelected;
        foreach (var element in pack.Elements)
            element.NotifySelectionChanged();
        pack.NotifySelectionChanged();
        SelectPack(pack);
        UpdateSelectionUi(pack);
        RefreshSelectionPreview(pack);
    }

    private void PackElementChoice_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: PackElementEntry element })
            return;
        var pack = element.FromCurrentSkin
            && currentFallbackElements.Contains(element)
                ? selectedPack
                : allPacks.FirstOrDefault(candidate => candidate.Elements.Contains(element));
        if (pack is null)
            return;
        element.NotifySelectionChanged();
        if (element.FromCurrentSkin)
            RememberCurrentFallbackSelections();
        pack.NotifySelectionChanged();
        SelectPack(pack);
        UpdateSelectionUi(pack);
        RefreshSelectionPreview(pack);
    }

    private void SelectOnlyElement_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: PackElementEntry element })
            return;
        var pack = allPacks.FirstOrDefault(candidate => candidate.Elements.Contains(element));
        if (pack is null)
            return;
        foreach (var candidate in pack.Elements)
            candidate.IsSelected = ReferenceEquals(candidate, element);
        foreach (var setting in pack.Settings.Where(setting => !setting.IsRequired))
            setting.IsSelected = false;
        pack.NotifySelectionChanged();
        SelectPack(pack);
        UpdateSelectionUi(pack);
        RefreshSelectionPreview(pack);
        e.Handled = true;
    }

    private void PackSettingChoice_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: PackSettingEntry setting })
            return;
        var pack = allPacks.FirstOrDefault(candidate => candidate.Settings.Contains(setting));
        if (pack is null)
            return;
        pack.NotifySelectionChanged();
        SelectPack(pack);
        UpdateSelectionUi(pack);
        RefreshSelectionPreview(pack);
    }

    private void UpdateSelectionUi(SkinExtraPackPreview pack)
    {
        if (!ReferenceEquals(selectedPack, pack))
            return;
        PackFilesExpander.Header =
            $"Elements ({pack.SelectedElementCount}) · Files "
            + $"({pack.SelectedFileCount}/{pack.FileCount})";
        PackSettingsExpander.Header =
            $"Settings ({pack.SelectedSettingCount}/{pack.Settings.Count})";
        var resolutionMismatches = currentSkinSource is null
            ? []
            : SkinExtraResolutionPlanner.FindMismatches(
                currentSkinSource.Filenames,
                pack.Files
                    .Where(file => file.IsSelected)
                    .Select(file => file.Name));
        ResolutionMismatchPanel.Visibility = resolutionMismatches.Count > 0
            ? Visibility.Visible
            : Visibility.Collapsed;
        if (resolutionMismatches.Count > 0)
        {
            ResolutionMismatchText.Text =
                $"Your skin has @2x versions for {resolutionMismatches.Count} selected "
                + (resolutionMismatches.Count == 1 ? "file" : "files")
                + ", but this pack supplies only 1×. Choose how the conflict should be resolved.";
        }
        var hasFallbackDeletion = currentFallbackElements.Any(element =>
            element.IsSelected == false);
        var hasCursorPolicyChange =
            SkinCursorMiddlePolicy.IsCursorFamily(pack.Manifest.FamilyId)
            && (SmoothTrailCheckBox.IsChecked == true
                || currentSkinSource?.Filenames.Any(
                    SkinCursorMiddlePolicy.IsCursorMiddle) == true);
        UsePackButton.IsEnabled = !staging && (pack.SelectedFileCount > 0
                                  || pack.SelectedSettingCount > 0
                                  || hasFallbackDeletion
                                  || hasCursorPolicyChange);
        if (!staging && incompleteImportGuide is not null
                     && pack.NavigationFamilyId.Equals(
                         incompleteImportGuide.NavigationFamilyId,
                         StringComparison.OrdinalIgnoreCase))
            UsePackButton.Content = "Add missing files to Changes";
    }

    private void SmoothTrail_Changed(object sender, RoutedEventArgs e)
    {
        if (selectedPack is not { } pack)
            return;
        UpdateSelectionUi(pack);
        if (rendererTargetVisible)
        {
            PreviewSmoothTrailChanged?.Invoke(
                this,
                new SkinExtrasPreviewSmoothTrailChangedEventArgs(
                    SmoothTrailCheckBox.IsChecked == true));
            return;
        }
        RefreshSelectionPreview(pack);
    }

    private void InvalidateCurrentPreview()
    {
        currentPreviewFiles.Clear();
        RememberCurrentFallbackSelections();
        currentFallbackElements = [];
        currentFallbackPackKey = null;
        CurrentPreviewCanvas.Children.Clear();
        ResultPreviewCanvas.Children.Clear();
        CurrentSkinPreviewLabel.Text = currentSkinSource?.HasStagedChanges == true
            ? "CURRENT + CHANGES"
            : "CURRENT SKIN";
        _ = RefreshUsageBadgesAsync(packLoadVersion);
        if (selectedPack is { } pack)
        {
            // The same pack instance is still selected after staging, but its
            // current-skin projection has changed. Force the detail surface to
            // publish and compose that new projection instead of accepting the
            // normal same-pack short circuit.
            displayedPack = null;
            SetPreviewMode(
                current: currentSkinSource is not null,
                compare: false,
                refresh: false);
            DisplayPack(pack);
        }
    }

    private void RememberCurrentFallbackSelections()
    {
        if (currentFallbackPackKey is null || currentFallbackElements.Count == 0)
            return;
        fallbackSelectionsByPack[currentFallbackPackKey] = currentFallbackElements
            .SelectMany(element => element.Files)
            .GroupBy(file => file.Name, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(
                group => group.Key,
                group => group.Last().IsSelected,
                StringComparer.OrdinalIgnoreCase);
    }

    private bool CompleteGuidedAssets(
        SkinExtraPackPreview pack,
        IReadOnlySet<string> selectedTargets)
    {
        if (incompleteImportGuide is not { } guide)
            return false;
        var supplied = guide.RemainingAssets
            .Where(asset => selectedTargets.Any(filename =>
                SkinExtraCompleteness.Supplies(
                    pack.Manifest.FamilyId,
                    filename,
                    asset.Key)))
            .ToHashSet();
        guide.RemainingAssets.RemoveAll(supplied.Contains);
        guide.VisitedPackKeys.Add(pack.PackKey);
        if (guide.RemainingAssets.Count == 0)
        {
            incompleteImportGuide = null;
            return true;
        }
        if (GuideToNextMissingPack())
            return false;

        var missing = guide.MissingSummary;
        incompleteImportGuide = null;
        SubtitleText.Text =
            "The selected files were staged, but no other library pack contains "
            + $"{missing}. Import another skin or pack to finish it.";
        UsePackButton.Content = "Add another selection";
        KumoriDialog.Show(
            dialogOwner,
            $"The selected files from “{pack.Name}” were staged.\n\n"
            + $"No other {pack.Manifest.FamilyName} pack in Extras contains {missing}. "
            + "Import another skin or pack that contains those files, then return "
            + "here to add them.",
            "Missing files not found",
            MessageBoxButton.OK,
            MessageBoxImage.Information);
        return false;
    }

    private bool GuideToNextMissingPack()
    {
        if (incompleteImportGuide is not { } guide)
            return false;
        var donor = allPacks
            .Where(pack => pack.NavigationFamilyId.Equals(
                guide.NavigationFamilyId,
                StringComparison.OrdinalIgnoreCase))
            .Where(pack => !guide.VisitedPackKeys.Contains(pack.PackKey))
            .Select(pack => new
            {
                Pack = pack,
                Coverage = guide.RemainingAssets.Count(asset =>
                    pack.Files.Any(file => SkinExtraCompleteness.Supplies(
                        pack.Manifest.FamilyId,
                        file.Name,
                        asset.Key))),
            })
            .Where(candidate => candidate.Coverage > 0)
            .OrderByDescending(candidate => candidate.Coverage)
            .ThenBy(candidate => candidate.Pack.Name, StringComparer.OrdinalIgnoreCase)
            .Select(candidate => candidate.Pack)
            .FirstOrDefault();
        if (donor is null)
            return false;

        guide.VisitedPackKeys.Add(donor.PackKey);
        foreach (var file in donor.Files)
            file.IsSelected = guide.RemainingAssets.Any(asset =>
                SkinExtraCompleteness.Supplies(
                    donor.Manifest.FamilyId,
                    file.Name,
                    asset.Key));
        foreach (var setting in donor.Settings.Where(setting => !setting.IsRequired))
            setting.IsSelected = false;
        foreach (var element in donor.Elements)
            element.NotifySelectionChanged();
        donor.NotifySelectionChanged();

        var family = FamilyList.Items
            .OfType<FamilyNavigationItem>()
            .FirstOrDefault(item => item.FamilyId.Equals(
                donor.NavigationFamilyId,
                StringComparison.OrdinalIgnoreCase));
        if (family is not null)
        {
            updatingFamilies = true;
            FamilyList.SelectedItem = family;
            updatingFamilies = false;
            ShowFamily(family, donor.DirectoryPath);
        }
        else
        {
            SelectPack(donor);
        }

        PackFilesExpander.IsExpanded = true;
        SubtitleText.Text =
            $"Step 2: “{guide.SourcePackName}” was staged. Review "
            + $"{guide.MissingSummary} selected from “{donor.Name}”, then click "
            + "Add missing files to Changes.";
        PackDetails.Text +=
            $"\nGuided completion: only files matching {guide.MissingSummary} are selected.";
        PackNoticePanel.Visibility = Visibility.Visible;
        UsePackButton.Content = "Add missing files to Changes";
        UpdateSelectionUi(donor);
        RefreshSelectionPreview(donor);
        return true;
    }

    private void PlayAudio(string path, string? label = null)
    {
        try
        {
            if (audioStream != 0
                && string.Equals(playingAudioPath, path, StringComparison.OrdinalIgnoreCase))
            {
                Bass.ChannelSetPosition(audioStream, 0);
                if (!Bass.ChannelPlay(audioStream, false))
                    throw new InvalidOperationException(
                        $"BASS could not restart playback ({Bass.LastError}).");
                playingAudioLabel = label ?? AudioPadLabel(path);
                audioProgressTimer.Start();
                SetAudioPlaybackUi(playingAudioLabel);
                UpdateAudioProgress();
                return;
            }

            StopAudio();
            SelectAudioDevice();

            playingAudioLoops = ShouldLoopAudio(path);
            var flags = playingAudioLoops ? BassFlags.Loop : BassFlags.Default;
            audioStream = Bass.CreateStream(path, 0, 0, flags);
            if (audioStream == 0)
                throw new InvalidOperationException(
                    $"BASS could not open the file ({Bass.LastError}).");

            if (!Bass.ChannelPlay(audioStream, true))
            {
                var error = Bass.LastError;
                StopAudio();
                throw new InvalidOperationException(
                    $"BASS could not start playback ({error}).");
            }
            playingAudioPath = path;
            playingAudioLabel = label ?? AudioPadLabel(path);
            var length = Bass.ChannelGetLength(audioStream);
            audioDurationSeconds = length > 0
                ? Bass.ChannelBytes2Seconds(audioStream, length)
                : 0;
            AudioProgressSlider.Maximum = Math.Max(1, audioDurationSeconds);
            AudioDurationText.Text = FormatPlaybackTime(audioDurationSeconds);
            audioProgressTimer.Start();
            SetAudioPlaybackUi(playingAudioLabel);
            UpdateAudioProgress();
        }
        catch (Exception ex)
        {
            StopAudio();
            PackDetails.Text = $"Could not preview this audio file: {ex.Message}";
            PackNoticePanel.Visibility = Visibility.Visible;
        }
    }

    private void SelectAudioDevice()
    {
        if (audioDevice > 0)
        {
            Bass.CurrentDevice = audioDevice;
            return;
        }

        if (Bass.Init(
                Bass.DefaultDevice,
                44_100,
                (DeviceInitFlags)0,
                IntPtr.Zero,
                IntPtr.Zero))
        {
            audioDevice = Bass.CurrentDevice;
            return;
        }

        var initializationError = Bass.LastError;
        if (initializationError == Errors.Already)
        {
            for (var device = 1; device < Bass.DeviceCount; device++)
            {
                var info = Bass.GetDeviceInfo(device);
                if (!info.IsInitialized) continue;
                audioDevice = device;
                Bass.CurrentDevice = audioDevice;
                return;
            }
        }

        throw new InvalidOperationException(
            $"No audio output device could be initialized ({initializationError}).");
    }

    private void StopAudio()
    {
        audioProgressTimer.Stop();
        audioSequenceTimer.Stop();
        foreach (var stream in audioSequenceStreams)
        {
            try
            {
                Bass.ChannelStop(stream);
                Bass.StreamFree(stream);
            }
            catch
            {
                // The audio device may already have released the stream.
            }
        }
        audioSequenceStreams.Clear();
        audioSequenceSteps = [];
        audioSequenceIndex = 0;
        audioSequenceSourceLabel = null;
        if (audioStream == 0)
        {
            playingAudioPath = null;
            playingAudioLabel = null;
            playingAudioLoops = false;
            SetAudioPlaybackUi(null);
            if (AudioPlayPackButton?.Visibility == Visibility.Visible)
                AudioPlayPackButton.IsEnabled = true;
            return;
        }

        try
        {
            Bass.ChannelStop(audioStream);
            Bass.StreamFree(audioStream);
        }
        catch
        {
            // The stream can already have been released after a device change.
        }
        finally
        {
            audioStream = 0;
            playingAudioPath = null;
            playingAudioLabel = null;
            playingAudioLoops = false;
            SetAudioPlaybackUi(null);
            if (AudioPlayPackButton?.Visibility == Visibility.Visible)
                AudioPlayPackButton.IsEnabled = true;
        }
    }

    private void SetAudioPlaybackUi(string? nowPlaying)
    {
        if (AudioNowPlayingText is null) return;
        AudioNowPlayingText.Text = selectedAudioTrack?.Label
                                   ?? (string.IsNullOrWhiteSpace(nowPlaying)
                                       ? "Choose a track"
                                       : AudioPadLabel(playingAudioPath ?? nowPlaying));
        AudioNowPlayingText.ToolTip = playingAudioPath ?? selectedAudioTrack?.Path;
        AudioSourceStatusText.Text = selectedAudioTrack?.SourceLabel
                                     ?? "Choose a sound above";
        StopAudioPreviewButton.IsEnabled = audioStream != 0;
        AudioPlayPauseButton.IsEnabled = selectedAudioTrack is not null;
        AudioPlayPauseButton.Content = audioStream == 0 ? "Play" : "Pause";
        AudioPlaybackStateText.Text = audioStream == 0
            ? "Ready"
            : playingAudioLoops ? "Looping" : "Playing";
        if (audioStream == 0)
            ResetAudioProgress();
    }

    private void UpdateAudioProgress()
    {
        if (audioStream == 0)
            return;
        var state = Bass.ChannelIsActive(audioStream);
        if (state == PlaybackState.Stopped)
        {
            if (playingAudioLoops && Bass.ChannelPlay(audioStream, true))
            {
                AudioProgressSlider.Value = 0;
                SetTextIfChanged(AudioElapsedText, "0:00");
                SetTextIfChanged(AudioPlaybackStateText, "Looping");
                return;
            }
            StopAudio();
            return;
        }

        var position = Bass.ChannelGetPosition(audioStream);
        var seconds = position >= 0
            ? Bass.ChannelBytes2Seconds(audioStream, position)
            : 0;
        if (!audioSeeking)
            AudioProgressSlider.Value = Math.Clamp(seconds, 0, AudioProgressSlider.Maximum);
        SetTextIfChanged(AudioElapsedText, FormatPlaybackTime(seconds));
        SetTextIfChanged(AudioPlaybackStateText, state == PlaybackState.Paused
            ? "Paused"
            : playingAudioLoops ? "Looping" : "Playing");
        var buttonText = state == PlaybackState.Paused ? "Play" : "Pause";
        if (!Equals(AudioPlayPauseButton.Content, buttonText))
            AudioPlayPauseButton.Content = buttonText;
    }

    internal static bool ShouldLoopAudio(string path)
    {
        if (!SkinElementCategorizer.IsAudio(path))
            return false;
        var stem = Path.GetFileNameWithoutExtension(path).ToLowerInvariant();
        return stem is "spinnerspin" or "pause-loop"
               || stem.EndsWith("-sliderslide", StringComparison.Ordinal)
               || stem.EndsWith("-sliderwhistle", StringComparison.Ordinal);
    }

    private static void SetTextIfChanged(TextBlock textBlock, string value)
    {
        if (!textBlock.Text.Equals(value, StringComparison.Ordinal))
            textBlock.Text = value;
    }

    private void ResetAudioProgress()
    {
        audioDurationSeconds = 0;
        AudioProgressSlider.Maximum = 1;
        AudioProgressSlider.Value = 0;
        AudioElapsedText.Text = "0:00";
        AudioDurationText.Text = "0:00";
        AudioPlaybackStateText.Text = "Ready";
    }

    private static string FormatPlaybackTime(double seconds)
    {
        if (!double.IsFinite(seconds) || seconds < 0)
            seconds = 0;
        var value = TimeSpan.FromSeconds(seconds);
        return value.TotalHours >= 1
            ? $"{(int)value.TotalHours}:{value.Minutes:00}:{value.Seconds:00}"
            : $"{(int)value.TotalMinutes}:{value.Seconds:00}";
    }

    private void Favorite_Click(object sender, RoutedEventArgs e)
    {
        if (selectedPack is not { } pack) return;
        SkinExtrasLibraryStateStore.Update(
            AppPaths.SkinExtrasDir,
            pack.StateKey,
            state => state.Favorite = !state.Favorite);
        LoadPacks();
    }

    private void RenamePack_Click(object sender, RoutedEventArgs e)
    {
        if (selectedPack is not { } pack) return;
        var requested = KumoriDialog.Input(
            dialogOwner,
            "Choose a new name for this Extras pack.",
            "Rename Extras pack",
            pack.Name);
        if (string.IsNullOrWhiteSpace(requested)) return;
        try
        {
            StopAudio();
            if (pack.IsDerivedView)
            {
                SkinExtrasLibraryStateStore.Update(
                    AppPaths.SkinExtrasDir,
                    pack.StateKey,
                    state => state.DisplayNameOverride = SkinExtraNaming.Sanitize(requested));
                var selectedDirectory = pack.DirectoryPath;
                LoadPacks(selectedDirectory);
                return;
            }

            var renamed = SkinExtraPackRenamer.Rename(
                AppPaths.SkinExtrasDir,
                pack.SourceDescriptor,
                requested);
            LoadPacks(renamed.DirectoryPath);
        }
        catch (Exception ex)
        {
            KumoriDialog.Show(
                dialogOwner,
                ex.Message,
                "Rename Extras pack",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
        }
    }

    private void DeletePack_Click(object sender, RoutedEventArgs e)
    {
        if (selectedPack is not { } pack) return;
        var duplicateNote = pack.DuplicateCount > 1
            ? $"\n\nThis is one of {pack.DuplicateCount} overlapping copies; another may remain."
            : "";
        if (!KumoriDialog.Confirm(
                dialogOwner,
                $"Move “{pack.Name}” to the Recycle Bin?\n\n"
                + $"The {pack.Manifest.FamilyName} pack will be removed from the Extras library."
                + duplicateNote,
                "Delete Extras pack",
                MessageBoxImage.Warning))
            return;

        try
        {
            var target = SkinExtraPackDeletion.ResolvePackDirectory(
                AppPaths.SkinExtrasDir,
                pack.SourceDescriptor.DirectoryPath);
            if (!Directory.Exists(target))
                throw new DirectoryNotFoundException("This Extras pack no longer exists.");
            StopAudio();
            FileSystem.DeleteDirectory(
                target,
                UIOption.OnlyErrorDialogs,
                RecycleOption.SendToRecycleBin);
            SkinExtrasPersistentIndex.Invalidate(AppPaths.SkinExtrasDir);
            LoadPacks();
        }
        catch (Exception ex)
        {
            KumoriDialog.Show(
                dialogOwner,
                $"Could not delete this Extras pack:\n\n{ex.Message}",
                "Delete Extras pack",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
        }
    }

    private void ExportPackage_Click(object sender, RoutedEventArgs e)
    {
        if (selectedPack is not { } pack) return;
        var dialog = new SaveFileDialog
        {
            Title = "Export portable Extras package",
            Filter = "Kumori Extras package|*.kextra",
            FileName = SkinExtraNaming.Sanitize(pack.Name) + ".kextra",
            AddExtension = true,
        };
        if (dialogOwner is { } exportOwner
                ? dialog.ShowDialog(exportOwner) != true
                : dialog.ShowDialog() != true)
            return;
        try
        {
            StopAudio();
            SkinExtraPortablePackage.Export(pack.SourceDescriptor, dialog.FileName);
            PackDetails.Text = $"Exported {Path.GetFileName(dialog.FileName)}.";
            PackNoticePanel.Visibility = Visibility.Visible;
        }
        catch (Exception ex)
        {
            KumoriDialog.Show(
                dialogOwner,
                ex.Message,
                "Export Extras package",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
        }
    }

    private void ImportPackage_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Title = "Import an Extras package or osu! skin",
            Filter = "Supported Extras and skins|*.kextra;*.osk;*.zip"
                     + "|Kumori Extras package|*.kextra"
                     + "|osu! skin archives|*.osk;*.zip"
                     + "|All files|*.*",
            Multiselect = false,
        };
        if (dialogOwner is { } importOwner
                ? dialog.ShowDialog(importOwner) != true
                : dialog.ShowDialog() != true)
            return;
        try
        {
            if (Path.GetExtension(dialog.FileName).Equals(
                    ".kextra",
                    StringComparison.OrdinalIgnoreCase))
            {
                var result = SkinExtraPortablePackage.Import(
                    dialog.FileName,
                    AppPaths.SkinExtrasDir);
                var importedPack = result.Pack;
                if (!result.WasDuplicate)
                {
                    var requested = KumoriDialog.Input(
                        dialogOwner,
                        "Choose the name this package should use in your Extras library.",
                        "Name imported Extras pack",
                        importedPack.Manifest.DisplayName);
                    if (!string.IsNullOrWhiteSpace(requested)
                        && !SkinExtraNaming.Sanitize(requested).Equals(
                            importedPack.Manifest.DisplayName,
                            StringComparison.Ordinal))
                    {
                        importedPack = SkinExtraPackRenamer.Rename(
                            AppPaths.SkinExtrasDir,
                            importedPack,
                            requested);
                    }
                }
                LoadPacks(importedPack.DirectoryPath);
                PackDetails.Text = result.Message;
                PackNoticePanel.Visibility = Visibility.Visible;
                return;
            }

            var source = new SkinExtrasExtractionService().ReadOsk(dialog.FileName);
            ShowSkinExtractionReview(source);
        }
        catch (Exception ex)
        {
            KumoriDialog.Show(
                dialogOwner,
                ex.Message,
                "Import Extras or skin",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
        }
    }

    private void ImportFolder_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog
        {
            Title = "Choose a folder of loose osu! skin files",
            Multiselect = false,
        };
        if (dialogOwner is { } folderOwner
                ? dialog.ShowDialog(folderOwner) != true
                : dialog.ShowDialog() != true)
            return;
        try
        {
            var source = new SkinExtrasExtractionService().ReadFolder(dialog.FolderName);
            ShowSkinExtractionReview(source);
        }
        catch (Exception ex)
        {
            KumoriDialog.Show(
                dialogOwner,
                ex.Message,
                "Import skin files",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
        }
    }

    private void ShowSkinExtractionReview(SkinExtractionSource source)
    {
        var visibility = modeVisibility with { LazerUsedOnly = lazerUsedOnly };
        var extractor = new SkinExtrasExtractorWindow(
            dialogOwner,
            source,
            visibility,
            value =>
            {
                lazerUsedOnly = value;
                initializingLazerFilter = true;
                LazerUsedOnlyCheckBox.IsChecked = value;
                initializingLazerFilter = false;
                lazerFilterChanged?.Invoke(value);
            });
        var accepted = extractor.ShowDialog() == true;
        LoadPacks();
        if (!accepted) return;
        var extracted = extractor.Results.Count(result =>
            result.Status == SkinExtraExtractionStatus.Extracted);
        var duplicates = extractor.Results.Count - extracted;
        PackDetails.Text =
            $"{extracted} pack(s) imported from {source.DisplayName}; "
            + $"{duplicates} exact duplicate(s) skipped.";
        PackNoticePanel.Visibility = Visibility.Visible;
    }

    private void ValidateRepair_Click(object sender, RoutedEventArgs e)
    {
        if (selectedPack is not { } pack) return;
        var report = SkinExtraPackValidator.Validate(pack.SourceDescriptor);
        if (report.Issues.Count == 0)
        {
            KumoriDialog.Show(
                dialogOwner,
                "This pack is healthy.",
                "Extras health",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
            return;
        }
        var details = string.Join(
            "\n",
            report.Issues.Take(12).Select(issue =>
                $"• {issue.Severity}: {issue.Message}"
                + (issue.Filename is null ? "" : $" ({issue.Filename})")));
        var repairable = report.Issues.Any(issue => issue.Code is
            "old-schema" or "byte-hash" or "pack-fingerprint" or "duplicate-target");
        if (!repairable)
        {
            KumoriDialog.Show(
                dialogOwner,
                details,
                "Extras health",
                MessageBoxButton.OK,
                report.IsHealthy ? MessageBoxImage.Warning : MessageBoxImage.Error);
            return;
        }
        if (KumoriDialog.Show(
                dialogOwner,
                details + "\n\nRebuild this pack's manifest now?",
                "Extras health",
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning) != MessageBoxResult.Yes)
            return;
        SkinExtraPackValidator.Repair(pack.SourceDescriptor);
        SkinExtrasPersistentIndex.Invalidate(AppPaths.SkinExtrasDir);
        LoadPacks();
    }

    private void OpenFolder_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            Directory.CreateDirectory(AppPaths.SkinExtrasDir);
            Process.Start(new ProcessStartInfo(AppPaths.SkinExtrasDir) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            KumoriDialog.Show(
                dialogOwner,
                $"Could not open the Extras folder:\n\n{ex.Message}",
                "Open Extras folder",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
        }
    }

    private sealed record AudioAuditionCue(
        string Label,
        string Filename,
        string? BeforePath,
        string? AfterPath);

    private sealed record AudioTrackOption(
        string Label,
        string Filename,
        string Path,
        string SourceLabel);

    private sealed record AudioSequenceStep(
        string Label,
        IReadOnlyList<AudioTrackOption> Tracks);

    private sealed record AudioPlaybackRequest(string Path, string Label);

    private sealed record IncompleteImportGuide(
        string SourcePackName,
        string NavigationFamilyId,
        string FamilyId,
        string SourceFingerprint,
        List<SkinExtraMissingAsset> RemainingAssets,
        HashSet<string> VisitedPackKeys)
    {
        public IncompleteImportGuide(
            string sourcePackName,
            string navigationFamilyId,
            string familyId,
            string sourceFingerprint,
            IReadOnlyList<SkinExtraMissingAsset> remainingAssets,
            HashSet<string> visitedPackKeys)
            : this(
                sourcePackName,
                navigationFamilyId,
                familyId,
                sourceFingerprint,
                remainingAssets.ToList(),
                visitedPackKeys)
        {
        }

        public string MissingSummary => string.Join(
            ", ",
            RemainingAssets.Select(asset => asset.DisplayName));
    }

    private sealed record PackEditState(
        IReadOnlyDictionary<string, bool> FileSelections,
        IReadOnlyDictionary<string, bool> SettingSelections,
        IReadOnlyDictionary<string, SkinRgb> ElementTints);

    private sealed record SkinExtraPackPreview(
        string Name,
        string Collection,
        string DirectoryPath,
        int FileCount,
        SkinExtraPackManifest Manifest,
        SkinExtraPackDescriptor Descriptor,
        SkinExtraPackDescriptor SourceDescriptor,
        string StateKey,
        bool IsDerivedView,
        SkinExtrasLibraryItemState State,
        int DuplicateCount,
        int IgnoredManifestEntries,
        string CompatibilityBadge,
        BitmapSource? CursorImage,
        BitmapSource? TrailImage,
        BitmapSource? MiddleImage,
        List<BitmapSource> PreviewImages,
        IReadOnlyList<string> ImagePaths,
        IReadOnlyList<string> AudioPaths,
        IReadOnlyList<PackElementEntry> Elements,
        IReadOnlyList<PackSettingEntry> Settings,
        IReadOnlyList<PackFileEntry> Files) : INotifyPropertyChanged
    {
        private bool isExpanded;
        private bool isSelected;
        private bool previewLoaded;
        private string usageBadge = "";
        private string usageDetail = "";
        private bool isCurrentlyInUse;
        private BitmapSource? deferredThumbnail;
        public event PropertyChangedEventHandler? PropertyChanged;
        public bool IsSelected
        {
            get => isSelected;
            set
            {
                if (isSelected == value) return;
                isSelected = value;
                PropertyChanged?.Invoke(
                    this,
                    new PropertyChangedEventArgs(nameof(IsSelected)));
            }
        }
        public bool IsExpanded
        {
            get => isExpanded;
            set
            {
                if (isExpanded == value) return;
                isExpanded = value;
                PropertyChanged?.Invoke(
                    this,
                    new PropertyChangedEventArgs(nameof(IsExpanded)));
            }
        }
        public string PackKey =>
            $"{DirectoryPath}\0{Manifest.FamilyId}\0{Manifest.Variant}";
        public string NavigationFamilyId
        {
            get
            {
                if (!Manifest.FamilyId.Equals(
                        "osu.number-font",
                        StringComparison.OrdinalIgnoreCase))
                    return Manifest.FamilyId;
                var roles = new[] { "Hitcircle", "Score", "Combo" }
                    .Where(role => NumberFontHasRole(Manifest, role))
                    .ToArray();
                if (roles.Length == 1)
                    return $"osu.number-font.{roles[0].ToLowerInvariant()}";
                if (roles.Contains("Score", StringComparer.OrdinalIgnoreCase)
                    && roles.Contains("Combo", StringComparer.OrdinalIgnoreCase))
                    return "osu.number-font.score-combo";
                return "osu.number-font.other";
            }
        }
        public string NavigationFamilyName => NavigationFamilyId switch
        {
            "osu.number-font.hitcircle" => "Hitcircle numbers",
            "osu.number-font.score" => "Score numbers",
            "osu.number-font.combo" => "Combo numbers",
            "osu.number-font.score-combo" => "Score & combo numbers",
            _ => Manifest.FamilyName,
        };
        public int SelectedFileCount => Files.Count(file => file.IsSelected);
        public int SelectedElementCount => Elements.Count(element => element.IsSelected != false);
        public int SelectedSettingCount => Settings.Count(setting =>
            setting.IsSelected
            && (!setting.IsRequired || SelectedFileCount > 0));
        public string FileCountText => $"{FileCount} file{(FileCount == 1 ? "" : "s")}";
        public string SelectionText =>
            $"{SelectedElementCount} element{(SelectedElementCount == 1 ? "" : "s")} · "
            + $"{SelectedFileCount}/{FileCount} files";
        public bool PreviewLoaded => previewLoaded;
        public BitmapSource? Thumbnail =>
            deferredThumbnail ?? CursorImage ?? PreviewImages.FirstOrDefault();
        public string? AudioPath => AudioPaths.FirstOrDefault();
        public string FavoriteGlyph => State.Favorite ? "★" : "";
        public string CatalogBadge { get; set; } = "";
        public string UsageBadge => usageBadge;
        public string UsageDetail => usageDetail;
        public bool IsCurrentlyInUse => isCurrentlyInUse;

        public void SetUsage(
            int matchingFiles,
            int totalFiles,
            bool settingsMatch = true)
        {
            var presentation = DescribePackUsage(
                matchingFiles,
                totalFiles,
                settingsMatch);
            isCurrentlyInUse = presentation.IsInUse;
            usageBadge = presentation.Badge;
            usageDetail = presentation.Detail;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(UsageBadge)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(UsageDetail)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsCurrentlyInUse)));
        }

        public void SetDeferredThumbnail(BitmapSource? thumbnail)
        {
            deferredThumbnail = thumbnail;
            previewLoaded = true;
            PropertyChanged?.Invoke(
                this,
                new PropertyChangedEventArgs(nameof(PreviewLoaded)));
            PropertyChanged?.Invoke(
                this,
                new PropertyChangedEventArgs(nameof(Thumbnail)));
        }

        public void NotifySelectionChanged()
        {
            PropertyChanged?.Invoke(
                this,
                new PropertyChangedEventArgs(nameof(SelectedFileCount)));
            PropertyChanged?.Invoke(
                this,
                new PropertyChangedEventArgs(nameof(SelectedElementCount)));
            PropertyChanged?.Invoke(
                this,
                new PropertyChangedEventArgs(nameof(SelectedSettingCount)));
            PropertyChanged?.Invoke(
                this,
                new PropertyChangedEventArgs(nameof(SelectionText)));
        }
    }

    private sealed class PackElementEntry : INotifyPropertyChanged
    {
        private bool suppressChildEvents;
        private BitmapSource? thumbnail;
        private Color tint = Colors.White;
        private string usageBadge = "";

        public PackElementEntry(
            string key,
            string name,
            IReadOnlyList<PackFileEntry> files,
            BitmapSource? thumbnail,
            bool fromCurrentSkin = false)
        {
            Key = key;
            Name = name;
            Files = files;
            FromCurrentSkin = fromCurrentSkin;
            this.thumbnail = thumbnail;
            foreach (var file in Files)
                file.PropertyChanged += Child_PropertyChanged;
        }

        public event PropertyChangedEventHandler? PropertyChanged;
        public string Key { get; }
        public string Name { get; }
        public IReadOnlyList<PackFileEntry> Files { get; }
        public bool FromCurrentSkin { get; }
        public bool CanTint => !FromCurrentSkin
                               && Files.Any(file =>
                                   SkinElementCategorizer.IsImage(file.Name));
        public BitmapSource? Thumbnail => thumbnail;
        public string TintHex => $"#{tint.R:X2}{tint.G:X2}{tint.B:X2}";
        public string TintLabel => $"RGB {tint.R}, {tint.G}, {tint.B}";
        public Brush TintBrush => new SolidColorBrush(tint);
        public bool IsTinted => tint != Colors.White;
        public SkinRgb TintRgb => new(tint.R, tint.G, tint.B);
        public string FileCountText =>
            $"{Files.Count} file{(Files.Count == 1 ? "" : "s")}";
        public string UsageBadge => usageBadge;
        public string LayerDetailText => FromCurrentSkin
            ? $"Current skin · {FileCountText}"
            : FileCountText;
        public string SelectionToolTip => FromCurrentSkin
            ? "Keep this current-skin layer in the staged skin. Uncheck it to remove its file."
            : "Include this Extras layer when staging.";
        public bool? IsSelected
        {
            get
            {
                var selected = Files.Count(file => file.IsSelected);
                return selected == 0 ? false : selected == Files.Count ? true : null;
            }
            set
            {
                if (value is null)
                    return;
                suppressChildEvents = true;
                foreach (var file in Files)
                    file.IsSelected = value.Value;
                suppressChildEvents = false;
                NotifySelectionChanged();
            }
        }

        private void Child_PropertyChanged(object? sender, PropertyChangedEventArgs e)
        {
            if (!suppressChildEvents && e.PropertyName == nameof(PackFileEntry.IsSelected))
                NotifySelectionChanged();
        }

        public void NotifySelectionChanged()
        {
            PropertyChanged?.Invoke(
                this,
                new PropertyChangedEventArgs(nameof(IsSelected)));
        }

        public void SetThumbnail(BitmapSource? value)
        {
            if (ReferenceEquals(thumbnail, value))
                return;
            thumbnail = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Thumbnail)));
        }

        public void SetTint(Color value)
        {
            if (tint == value)
                return;
            tint = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(TintHex)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(TintLabel)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(TintBrush)));
        }

        public void SetUsage(int matchingFiles, int totalFiles)
        {
            usageBadge = matchingFiles == 0
                ? ""
                : matchingFiles == totalFiles
                    ? "IN USE"
                    : $"{matchingFiles}/{totalFiles} MATCH";
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(UsageBadge)));
        }
    }

    private sealed class PackSettingEntry : INotifyPropertyChanged
    {
        private bool isSelected = true;

        public PackSettingEntry(
            SkinExtraIniPatchEntry patch,
            bool isRequired,
            string changeText)
        {
            Patch = patch;
            IsRequired = isRequired;
            ChangeText = changeText;
        }

        public event PropertyChangedEventHandler? PropertyChanged;
        public SkinExtraIniPatchEntry Patch { get; }
        public string Section => Patch.ManiaKeys is { } keys
            ? $"Mania {keys}K"
            : Patch.Section;
        public string Key => Patch.Key;
        public string Value => Patch.Value ?? "(remove)";
        public string ChangeText { get; }
        public bool IsRequired { get; }
        public bool IsEditable => !IsRequired;
        public string RequirementText => IsRequired ? "Required" : "";
        public bool IsSelected
        {
            get => isSelected;
            set
            {
                if (IsRequired || isSelected == value)
                    return;
                isSelected = value;
                PropertyChanged?.Invoke(
                    this,
                    new PropertyChangedEventArgs(nameof(IsSelected)));
            }
        }
    }

    private sealed class PackFileEntry : INotifyPropertyChanged
    {
        private bool isSelected;

        public PackFileEntry(
            string name,
            string kind,
            string path,
            string size,
            string compatibilityBadge,
            bool isSelectable = true)
        {
            Name = name;
            Kind = kind;
            Path = path;
            Size = size;
            CompatibilityBadge = compatibilityBadge;
            IsSelectable = isSelectable;
            isSelected = isSelectable;
        }

        public event PropertyChangedEventHandler? PropertyChanged;
        public string Name { get; }
        public string Kind { get; }
        public string Path { get; }
        public string Size { get; }
        public string CompatibilityBadge { get; }
        public bool IsSelectable { get; }
        public bool IsSelected
        {
            get => isSelected;
            set
            {
                if (!IsSelectable || isSelected == value) return;
                isSelected = value;
                PropertyChanged?.Invoke(
                    this,
                    new PropertyChangedEventArgs(nameof(IsSelected)));
            }
        }
        public bool IsAudio => Kind.Equals("Audio", StringComparison.OrdinalIgnoreCase);
        public string DisplayKind => IsAudio ? "▶ Audio" : Kind;
        public string PreviewToolTip => IsAudio
            ? "Select this file, then use Before / After to audition it"
            : IsSelectable
                ? "Include this file when applying the pack"
                : "Transparent Followpoint placeholder; excluded from this pack.";
    }

    private sealed record FamilyNavigationItem(
        string FamilyId,
        string Area,
        string Name,
        string LegacyCategory,
        int PackCount);

    private sealed record CachedBitmap(
        BitmapSource Image,
        long Length,
        long LastWriteUtcTicks,
        long Bytes,
        long LastAccess);

    private sealed record PreviewLayerVisual(
        string LogicalKey,
        double BaseOpacity,
        int BaseZIndex,
        Point Centre,
        double Width,
        double Height,
        double RotationDegrees,
        SkinPreviewAnimationRole AnimationRole,
        int AnimationIndex,
        DropShadowEffect Glow);

    private sealed record ExtrasAnimationPlacement(
        Point Position,
        double ScaleX,
        double ScaleY,
        double Rotation,
        double Opacity);

    private sealed class ExtrasCanvasAnimationState(
        string familyId,
        bool smoothCursorTrail,
        bool cursorExpand,
        bool cursorRotate,
        bool cursorTrailRotate,
        bool sliderBallFlip,
        bool legacyVersionOne,
        bool spinnerNoBlink)
    {
        public string FamilyId { get; } = familyId;
        public bool SmoothCursorTrail { get; } = smoothCursorTrail;
        public bool CursorExpand { get; } = cursorExpand;
        public bool CursorRotate { get; } = cursorRotate;
        public bool CursorTrailRotate { get; } = cursorTrailRotate;
        public bool SliderBallFlip { get; } = sliderBallFlip;
        public bool LegacyVersionOne { get; } = legacyVersionOne;
        public bool SpinnerNoBlink { get; } = spinnerNoBlink;
        public bool CursorCentre { get; init; } = true;
        public bool HasSpinnerMiddle2 { get; init; }
        public double Health { get; set; } = 1;
    }

    private sealed class VisibleCropCache
    {
        public bool Initialized { get; set; }
        public BitmapSource? Image { get; set; }
    }

    private sealed class TintBitmapCache
    {
        public Dictionary<Color, BitmapSource> Images { get; } = [];
    }
}
