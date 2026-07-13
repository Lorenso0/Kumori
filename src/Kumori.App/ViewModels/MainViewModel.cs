using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Kumori.Core;
using Kumori.Core.Models;
using Kumori.Core.Settings;
using Kumori.Core.State;
using Kumori.Native;
using Kumori.Storage;
using Kumori.Tracking;
using Serilog;
using static System.FormattableString;

namespace Kumori.App.ViewModels;

public partial class MainViewModel : ObservableObject
{
    private const int PageSize = 1000;

    private readonly AppStateStore _store;
    private readonly AttemptRepository _attempts;
    private readonly AnalyticsRepository _analytics;
    private readonly SettingsService _settings;
    private readonly TrackingMaintenanceRepository _maintenance;
    private readonly SessionRepository _sessionsRepo;
    private readonly AppStateStore _appState;
    private readonly AttemptDetailsRepository _detailsRepository;
    private readonly ReplayViewerContractService? _replayViewer;

    /// <summary>Attempt rows only — used for compare selection and the inspector.</summary>
    public ObservableCollection<AttemptRowViewModel> Attempts { get; } = new();

    /// <summary>Interleaved session separators + attempt rows bound by the list.</summary>
    public ObservableCollection<object> Rows { get; } = new();
    public ObservableCollection<PerformanceDayViewModel> PerformanceDays { get; } = new();
    public ObservableCollection<MapCardViewModel> MapCards { get; } = new();
    public AttemptDetailsViewModel Inspector { get; }
    public IReadOnlyList<string> FilterModeOptions { get; } = new[]
    {
        "All", "Completed", "Failed", "Retried", "Quit"
    };
    public IReadOnlyList<string> ArtworkModeOptions { get; } = new[] { "Thumbnail cards", "No artwork" };

    [ObservableProperty] private AttemptRowViewModel? _selectedAttempt;
    [ObservableProperty] private string _historyStatus = "Loading history...";
    [ObservableProperty] private string _captureChipText = "Capture idle";
    [ObservableProperty] private string _captureChipColor = "#A86C9E";
    [ObservableProperty] private string _companionLine = "Waiting for osu!";
    [ObservableProperty] private string _sessionIndicator = "No active session";
    [ObservableProperty] private string _tosuChipText = "Tracker offline";
    [ObservableProperty] private string _tosuChipColor = "#FF4F7B";
    [ObservableProperty] private bool _isUpdateAvailable;
    [ObservableProperty] private string _updateAvailableText = "Update available";
    [ObservableProperty] private bool _isTosuLaunchVisible = true;
    [ObservableProperty] private bool _isLaunchingTosu;
    [ObservableProperty] private bool _isReplayAnalyzerLoading;
    [ObservableProperty] private string _replayAnalyzerLoadingText = "";
    [ObservableProperty] private bool _isLoadingMore;
    [ObservableProperty] private bool _isScrolledToBottom;
    [ObservableProperty] private string _searchText = "";

    [ObservableProperty] private string _playsMetric = "-";
    [ObservableProperty] private string _completedMetric = "-";
    [ObservableProperty] private string _activeMetric = "-";
    [ObservableProperty] private string _completionMetric = "-";
    [ObservableProperty] private string _playtimeMetric = "-";
    [ObservableProperty] private string _keysMetric = "-";
    [ObservableProperty] private string _keysBreakdown = "";
    [ObservableProperty] private string _key1Metric = "K1 0";
    [ObservableProperty] private string _key2Metric = "K2 0";
    [ObservableProperty] private string _syncLine = "";
    [ObservableProperty] private string _bestMetric = "-";
    [ObservableProperty] private string _ppGainedMetric = "-";
    [ObservableProperty] private string _ranksGainedMetric = "-";
    [ObservableProperty] private string _globalAttemptsMetric = "-";
    [ObservableProperty] private string _globalAccuracyMetric = "-";
    [ObservableProperty] private string _globalBestPpMetric = "-";
    [ObservableProperty] private string _globalPlaytimeMetric = "-";
    [ObservableProperty] private string _globalCompletionMetric = "-";
    [ObservableProperty] private string _globalCompletedMetric = "-";
    [ObservableProperty] private string _globalFailedMetric = "-";
    [ObservableProperty] private string _globalScoreMetric = "-";
    [ObservableProperty] private string _accountChangeLine = "ACCOUNT CHANGE  ·  PP +0.0  ·  Rank +0  ·  Accuracy +0.000%  ·  Plays +0";
    [ObservableProperty] private string _groupHeader = "Recent plays";
    [ObservableProperty] private string _groupStats = "";
    [ObservableProperty] private bool _isGroupRepeats;
    [ObservableProperty] private bool _isGroupSessions;
    [ObservableProperty] private bool _isWideHistoryLayout;
    [ObservableProperty] private string _selectedFilterMode = "All";
    [ObservableProperty] private string _selectedArtworkMode = "Thumbnail cards";

    private readonly List<AttemptSummary> _loadedAttempts = new();
    private readonly List<AttemptSummary> _mapAttempts = new();
    private readonly Dictionary<long, SessionSummary> _sessions = new();
    private readonly HashSet<long> _collapsedSessions = new();
    private long _dbBytes;
    private long _cacheBytes;
    private AnalyticsSummary _currentAnalytics = new();
    private bool _usingLazerRealm;
    private long? _activeSessionId;
    private long? _latestAttemptId;
    private Func<Task<bool>>? _endLiveSession;
    public event Action<Window, string>? WorkspaceWindowRequested;
    private long? _oldestLoadedId;
    private bool _reachedEnd;
    private string? _mapFilterKey;

    /// <summary>The Load older button is only shown once the list is scrolled to the bottom and more rows remain.</summary>
    public bool LoadOlderVisible => IsScrolledToBottom && !_reachedEnd;

    partial void OnIsScrolledToBottomChanged(bool value) => OnPropertyChanged(nameof(LoadOlderVisible));
    private string? _activeSearch;
    private CancellationTokenSource? _searchDebounce;
    private readonly SemaphoreSlim _reloadGate = new(1, 1);
    private long _reloadGeneration;

    public MainViewModel(
        AppStateStore store,
        AttemptRepository attempts,
        AttemptDetailsRepository details,
        AnalyticsRepository analytics,
        SettingsService settings,
        ReplayViewerContractService? replayViewer = null,
        TrackingMaintenanceRepository? maintenance = null,
        SessionRepository? sessions = null)
    {
        _store = store;
        _appState = store;
        _attempts = attempts;
        _analytics = analytics;
        _settings = settings;
        _detailsRepository = details;
        _replayViewer = replayViewer;
        _maintenance = maintenance ?? new TrackingMaintenanceRepository(new SqliteConnectionFactory(AppPaths.TrackingDatabase, readOnly: false));
        _sessionsRepo = sessions ?? new SessionRepository(new SqliteConnectionFactory(AppPaths.TrackingDatabase, readOnly: true));
        Inspector = new AttemptDetailsViewModel(details, replayViewer);
        _activeSessionId = store.Current.ActiveSession?.SessionId;
        IsGroupSessions = settings.Current.Appearance.GroupSessions;
        _store.StateChanged += OnStateChanged;
    }

    public void SetEndLiveSessionHandler(Func<Task<bool>> handler) => _endLiveSession = handler;

    public bool IsThumbnailArtwork => SelectedArtworkMode == "Thumbnail cards";
    public string ResultsText => $"{Attempts.Count:N0} results";
    public string ResultsShortText => $"{Attempts.Count:N0}";
    public bool CanLaunchTosu => IsTosuLaunchVisible && !IsLaunchingTosu;
    public bool HasActiveSession => _activeSessionId is not null;
    public string AppVersionText => $"v{typeof(MainViewModel).Assembly.GetName().Version?.ToString(3) ?? "0.3.0"}";

    partial void OnIsTosuLaunchVisibleChanged(bool value) => LaunchTosuCommand.NotifyCanExecuteChanged();
    partial void OnIsLaunchingTosuChanged(bool value) => LaunchTosuCommand.NotifyCanExecuteChanged();

    partial void OnSelectedAttemptChanged(AttemptRowViewModel? value)
    {
        _ = Inspector.LoadAsync(value?.Id);
    }

    partial void OnSearchTextChanged(string value)
    {
        _searchDebounce?.Cancel();
        var cts = new CancellationTokenSource();
        _searchDebounce = cts;
        _ = DebouncedSearchAsync(value, cts.Token);
    }

    partial void OnSelectedFilterModeChanged(string value) => ApplyVisibleAttempts();

    partial void OnIsGroupRepeatsChanged(bool value) => ApplyVisibleAttempts();
    partial void OnIsGroupSessionsChanged(bool value)
    {
        if (_settings.Current.Appearance.GroupSessions != value)
        {
            _settings.Update(settings => settings.Appearance.GroupSessions = value);
        }
        ApplyVisibleAttempts();
    }

    partial void OnSelectedArtworkModeChanged(string value)
    {
        OnPropertyChanged(nameof(IsThumbnailArtwork));
    }

    public Task HydrateAsync() => ReloadFirstPageAsync();

    public Task RefreshDashboardAsync() => ReloadFirstPageAsync();

    private async Task DebouncedSearchAsync(string search, CancellationToken token)
    {
        try
        {
            await Task.Delay(250, token);
        }
        catch (OperationCanceledException)
        {
            return;
        }
        _activeSearch = string.IsNullOrWhiteSpace(search) ? null : search;
        await ReloadFirstPageAsync();
    }

    private async Task ReloadFirstPageAsync()
    {
        var generation = Interlocked.Increment(ref _reloadGeneration);
        await _reloadGate.WaitAsync();
        try
        {
            if (generation != Volatile.Read(ref _reloadGeneration)) return;
            var search = _activeSearch;
            var load = await Task.Run(() => new
            {
                Page = _attempts.GetRecentAttempts(null, PageSize, search, mapKey: _mapFilterKey),
                Maps = _attempts.GetMapSummaries(),
                Analytics = _analytics.GetSummary(),
                Sessions = _sessionsRepo.GetRecentSessions(10_000),
                DbBytes = SafeFileSize(AppPaths.TrackingDatabase),
                CacheBytes = CacheStorageUsage.GetAdditionalBytes(AppPaths.BeatmapMediaDir),
                UsingLazerRealm = LazerStorage.GetDiagnostics().RealmOpened,
            });
            if (search != _activeSearch || generation != Volatile.Read(ref _reloadGeneration))
            {
                return;
            }

            _sessions.Clear();
            foreach (var session in load.Sessions)
            {
                _sessions[session.Id] = session;
            }
            _reachedEnd = load.Page.Count < PageSize;
            OnPropertyChanged(nameof(LoadOlderVisible));
            var destination = _mapFilterKey is null ? _loadedAttempts : _mapAttempts;
            destination.Clear();
            destination.AddRange(load.Page);
            MapCards.Clear();
            foreach (var map in load.Maps)
            {
                MapCards.Add(new MapCardViewModel(map));
            }
            ApplyVisibleAttempts(selectFirst: false);
            if (Attempts.Count > 0 && (SelectedAttempt is null || !Attempts.Any(a => a.Id == SelectedAttempt.Id)))
            {
                SelectedAttempt = Attempts[0];
            }
            else if (Attempts.Count == 0)
            {
                SelectedAttempt = null;
            }
            _dbBytes = load.DbBytes;
            _cacheBytes = load.CacheBytes;
            _currentAnalytics = load.Analytics;
            _usingLazerRealm = load.UsingLazerRealm;
            ApplyDashboard(load.Analytics, load.Page, load.DbBytes, load.CacheBytes);
            _oldestLoadedId = load.Page.Count > 0 ? load.Page[^1].Id : null;
            HistoryStatus = load.Page.Count == 0
                ? (search is not null
                    ? "No attempts match the search"
                    : File.Exists(AppPaths.TrackingDatabase)
                        ? "No attempts recorded yet"
                        : "No tracking database found - play a map with the tracker running")
                : search is not null
                    ? $"{load.Page.Count} matching attempts"
                    : $"{load.Page.Count} recent attempts";
        }
        catch (Exception ex)
        {
            Log.Error(ex, "History load failed");
            HistoryStatus = "Could not read history (see logs)";
        }
        finally
        {
            _reloadGate.Release();
        }
    }

    private void ApplyDashboard(AnalyticsSummary analytics, IReadOnlyList<AttemptSummary> page, long dbBytes, long cacheBytes)
    {
        PlaysMetric = analytics.Attempts.ToString("N0", System.Globalization.CultureInfo.InvariantCulture);
        PlaytimeMetric = FormatPlaytime(analytics.TotalDurationSeconds);
        KeysMetric = (analytics.ZTotal + analytics.XTotal).ToString("N0", System.Globalization.CultureInfo.InvariantCulture);
        Key1Metric = Invariant($"K1 {analytics.ZTotal:N0}");
        Key2Metric = Invariant($"K2 {analytics.XTotal:N0}");
        KeysBreakdown = Invariant($"K1 {analytics.ZTotal:N0}  /  K2 {analytics.XTotal:N0}");
        BestMetric = Invariant($"{analytics.BestPp:0.0}pp");
        PpGainedMetric = FormatPpGained(analytics.LatestAccountChange);
        RanksGainedMetric = FormatRanksGained(analytics.LatestAccountChange);
        GlobalAttemptsMetric = analytics.Attempts.ToString("N0", System.Globalization.CultureInfo.InvariantCulture);
        GlobalAccuracyMetric = Invariant($"{analytics.AverageAccuracy:0.00}%");
        GlobalBestPpMetric = Invariant($"{analytics.BestPp:0.0}pp");
        GlobalPlaytimeMetric = FormatPlaytime(analytics.TotalDurationSeconds);
        GlobalCompletionMetric = analytics.Attempts == 0
            ? "0%"
            : Invariant($"{analytics.Completed * 100.0 / analytics.Attempts:0.0}%");
        GlobalCompletedMetric = analytics.Completed.ToString("N0", System.Globalization.CultureInfo.InvariantCulture);
        GlobalFailedMetric = analytics.Failed.ToString("N0", System.Globalization.CultureInfo.InvariantCulture);
        GlobalScoreMetric = analytics.TotalScore.ToString("N0", System.Globalization.CultureInfo.InvariantCulture);
        var synced = FormatLocalProfileSyncTime(analytics.LastSyncedAt);
        var mediaStatus = _usingLazerRealm
            ? $"Using Realm ({cacheBytes / 1_048_576.0:0.00} MB Kumori cache)"
            : $"Cache {cacheBytes / 1_048_576.0:0.00} MB";
        SyncLine = Invariant($"Profile synced {synced}  ·  DB {dbBytes / 1_048_576.0:0.0} MB  ·  {mediaStatus}");

        var first = page.FirstOrDefault();
        GroupHeader = first?.StartedAt.Length >= 10 && DateTime.TryParse(first.StartedAt[..10], out var day)
            ? day.ToString("dd/MM/yyyy", System.Globalization.CultureInfo.InvariantCulture)
            : "Recent plays";
        var completed = page.Count(a => a.Outcome == "completed");
        var averageAccuracy = page.Count == 0 ? 0 : page.Average(a => a.Accuracy);
        var bestPp = page.Count == 0 ? 0 : page.Max(a => a.Pp);
        GroupStats = Invariant($"{page.Count} plays  -  {completed} completed  -  {averageAccuracy:0.00}%  -  {bestPp:0.0}pp");

        PerformanceDays.Clear();
        foreach (var trend in analytics.Daily)
        {
            PerformanceDays.Add(new PerformanceDayViewModel(trend));
        }
    }

    private static string FormatPlaytime(double seconds)
    {
        var totalMinutes = Math.Max(0, (long)Math.Round(seconds / 60d));
        var hours = totalMinutes / 60;
        var minutes = totalMinutes % 60;
        return hours > 0
            ? Invariant($"{hours}h {minutes:00}m")
            : Invariant($"{minutes}m");
    }

    private static string FormatLocalProfileSyncTime(string? timestamp)
    {
        return DateTimeOffset.TryParse(timestamp, System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.AllowWhiteSpaces, out var capturedAt)
            ? capturedAt.ToLocalTime().ToString("HH:mm", System.Globalization.CultureInfo.InvariantCulture)
            : "—";
    }

    private static string FormatPpGained(AccountChangeSummary? change)
    {
        return change is { OldTotalPp: { } oldPp, NewTotalPp: { } newPp }
            ? Invariant($"{newPp - oldPp:+0.0;-0.0;0.0}")
            : "-";
    }

    private static string FormatRanksGained(AccountChangeSummary? change)
    {
        return change is { OldGlobalRank: { } oldRank, NewGlobalRank: { } newRank }
            ? Invariant($"{oldRank - newRank:+0;-0;0}")
            : "-";
    }

    private static long SafeFileSize(string path)
    {
        try
        {
            return File.Exists(path) ? new FileInfo(path).Length : 0;
        }
        catch (IOException ex)
        {
            Log.Debug(ex, "Could not read file size for {Path}", path);
            return 0;
        }
        catch (UnauthorizedAccessException ex)
        {
            Log.Debug(ex, "Could not read file size for {Path}", path);
            return 0;
        }
    }

    private static string FormatAccountChange(AccountChangeSummary? change)
    {
        if (change is null)
        {
            return "ACCOUNT CHANGE  ·  pending profile data";
        }

        var pp = change is { OldTotalPp: { } oldPp, NewTotalPp: { } newPp }
            ? Invariant($"PP {newPp - oldPp:+0.0;-0.0;0.0}")
            : "PP n/a";
        var rank = change is { OldGlobalRank: { } oldRank, NewGlobalRank: { } newRank }
            ? Invariant($"Rank {oldRank - newRank:+0;-0;0}")
            : "Rank n/a";
        var acc = change is { OldAccuracy: { } oldAcc, NewAccuracy: { } newAcc }
            ? Invariant($"Accuracy {newAcc - oldAcc:+0.000;-0.000;0.000}%")
            : "Accuracy n/a";
        var plays = change is { OldPlayCount: { } oldPlays, NewPlayCount: { } newPlays }
            ? Invariant($"Plays {newPlays - oldPlays:+0;-0;0}")
            : "Plays n/a";
        return $"ACCOUNT CHANGE  ·  {pp}  ·  {rank}  ·  {acc}  ·  {plays}";
    }

    [RelayCommand]
    private Task RefreshAsync() => ReloadFirstPageAsync();

    [RelayCommand]
    private void ClearFilters()
    {
        SearchText = "";
        SelectedFilterMode = "All";
        IsGroupRepeats = false;
        _mapFilterKey = null;
        ApplyVisibleAttempts();
    }

    [RelayCommand]
    private void OpenHealthDashboard()
    {
        OpenInWorkspace(new HealthDashboardWindow(_appState, _settings), "Health dashboard");
    }

    [RelayCommand]
    private void OpenBackups() => OpenInWorkspace(new BackupWindow(_settings), "Backup & restore");

    [RelayCommand]
    private void OpenSetupWizard()
    {
        MainWindow.TryOpenOnboarding(new WelcomeWindow(_settings, _appState));
    }

    [RelayCommand]
    private void OpenSkinLibrary()
    {
        OpenInWorkspace(new SkinLibraryWindow(_settings), "Skin library");
    }

    [RelayCommand]
    private void OpenTosuSetup()
    {
        OpenInWorkspace(new TosuSetupWindow(), "tosu setup");
    }

    [RelayCommand]
    private void OpenTosuDiagnostics()
    {
        OpenInWorkspace(new TosuDiagnosticsWindow(_appState), "tosu diagnostics");
    }

    [RelayCommand]
    private void OpenSettings()
    {
        OpenInWorkspace(new SettingsWindow(_settings), "Settings");
    }

    [RelayCommand]
    private void OpenLazerFrameDebug()
    {
        OpenInWorkspace(new LazerFrameDebugWindow(_settings), "Lazer frame debug");
    }

    [RelayCommand]
    private void OpenStableFrameDebug()
    {
        OpenInWorkspace(new StableFrameDebugWindow(), "Stable frame debug");
    }

    [RelayCommand]
    private void CheckForUpdates()
    {
        OpenInWorkspace(new UpdateCheckWindow(), "Updates");
    }

    [RelayCommand]
    private void OpenAvailableUpdate()
    {
        var releaseUrl = _appState.Current.ApplicationUpdate.ReleaseUrl;
        if (string.IsNullOrWhiteSpace(releaseUrl)) releaseUrl = KumoriUpdateService.ReleasesUrl;
        try { Process.Start(new ProcessStartInfo { FileName = releaseUrl, UseShellExecute = true }); }
        catch (Exception ex) { Log.Warning(ex, "Could not open Kumori release page {Url}", releaseUrl); }
    }

    private void OpenInWorkspace(Window window, string title)
        => WorkspaceWindowRequested?.Invoke(window, title);

    [RelayCommand]
    private void OpenLogs()
    {
        Directory.CreateDirectory(AppPaths.LogDir);
        Process.Start(new ProcessStartInfo { FileName = AppPaths.LogDir, UseShellExecute = true });
    }

    [RelayCommand]
    private void OpenAppData()
    {
        Directory.CreateDirectory(AppPaths.ReportsDir);
        Process.Start(new ProcessStartInfo { FileName = AppPaths.AppDataDir, UseShellExecute = true });
    }

    [RelayCommand(CanExecute = nameof(CanLaunchTosu))]
    private async Task LaunchTosuAsync()
    {
        IsLaunchingTosu = true;
        try
        {
            HistoryStatus = "Checking tosu release...";
            await Task.Run(() =>
            {
                TosuManager.EnsureInstalledAndLaunchAsync().GetAwaiter().GetResult();
            });

            HistoryStatus = "Launched tosu; waiting for tracker connection";
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Failed to launch tosu");
            HistoryStatus = "Could not launch tosu (see logs)";
            KumoriDialog.Show(ActiveOwner(), ex.Message, "Kumori", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            IsLaunchingTosu = false;
        }
    }

    [RelayCommand]
    private async Task ExportProblemReportAsync()
    {
        Directory.CreateDirectory(AppPaths.AppDataDir);
        var includeDatabase = KumoriDialog.Show(
            ActiveOwner(),
            "Include the tracking database in the diagnostics zip?",
            "Kumori",
            MessageBoxButton.YesNo,
            MessageBoxImage.Question) == MessageBoxResult.Yes;
        var report = Path.Combine(AppPaths.ReportsDir, $"problem-report-{DateTimeOffset.Now:yyyyMMdd-HHmmss}.zip");
        var tempDir = Path.Combine(Path.GetTempPath(), $"kumori-report-{Guid.NewGuid():N}");
        var state = _store.Current;
        try
        {
            HistoryStatus = "Creating diagnostics...";
            await Task.Run(() =>
            {
                Directory.CreateDirectory(tempDir);
                File.WriteAllText(Path.Combine(tempDir, "summary.txt"),
                    $"""
                    Kumori problem report
                    Generated: {DateTimeOffset.Now:O}

                    Tracking: {state.Tracking.Health} / {state.Tracking.Detail}
                    Capture: {state.Capture.Health} / {state.Capture.Error}
                    Media: {state.Media.LastError}

                    AppData: {AppPaths.AppDataDir}
                    Tracking DB: {AppPaths.TrackingDatabase}
                    Writer: .NET real DB
                    Logs: {AppPaths.LogDir}

                    Contact: {SupportLinks.DiscordInviteUrl}
                    """);
                CopyIfExists(AppPaths.SettingsFile, Path.Combine(tempDir, "settings.v2.json"));
                CopyIfExists(AppPaths.LegacySettingsFile, Path.Combine(tempDir, "settings.legacy.json"));
                CopyIfExists(LazerReplayFrameDiagnostics.StatusPath, Path.Combine(tempDir, "lazer_replay_frame_status.json"));
                CopyIfExists(StableReplayFrameDiagnostics.StatusPath, Path.Combine(tempDir, "stable_replay_frame_status.json"));
                CopyDirectoryIfExists(AppPaths.LogDir, Path.Combine(tempDir, "logs"));
                if (includeDatabase)
                {
                    CopyIfExists(AppPaths.TrackingDatabase, Path.Combine(tempDir, "osu_tracking.sqlite3"));
                    CopyIfExists(AppPaths.TrackingDatabase + "-wal", Path.Combine(tempDir, "osu_tracking.sqlite3-wal"));
                    CopyIfExists(AppPaths.TrackingDatabase + "-shm", Path.Combine(tempDir, "osu_tracking.sqlite3-shm"));
                }
                if (File.Exists(report))
                {
                    File.Delete(report);
                }
                ZipFile.CreateFromDirectory(tempDir, report, CompressionLevel.Optimal, includeBaseDirectory: false);
            });
            Process.Start(new ProcessStartInfo
            {
                FileName = "explorer.exe",
                ArgumentList = { "/select,", report },
                UseShellExecute = false,
            });
            HistoryStatus = $"Diagnostics written to {report}";
            if (KumoriDialog.Confirm(ActiveOwner(), "Copy the support Discord invite to clipboard?", "Kumori", MessageBoxImage.Question))
            {
                Clipboard.SetText(SupportLinks.DiscordInviteUrl);
            }
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Problem report export failed");
            KumoriDialog.Show(ActiveOwner(), ex.Message, "Kumori", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            try { Directory.Delete(tempDir, recursive: true); } catch { }
        }
    }

    [RelayCommand]
    private async Task EndSessionAsync()
    {
        var changed = _endLiveSession is null
            ? await Task.Run(() => _maintenance.EndOpenSessions()) > 0
            : await _endLiveSession();
        _store.Update(s => s.ActiveSession is null ? s : s with { ActiveSession = null });
        HistoryStatus = changed ? "Ended current session" : "No open sessions to end";
        await ReloadFirstPageAsync();
    }

    [RelayCommand]
    private async Task ClearBeatmapCacheAsync()
    {
        if (!KumoriDialog.Confirm(ActiveOwner(), "Clear cached beatmap artwork and media?", "Kumori", MessageBoxImage.Question))
        {
            return;
        }

        await Task.Run(() => _maintenance.ClearBeatmapCache());
        HistoryStatus = "Beatmap cache cleared";
    }

    [RelayCommand]
    private async Task DeleteEntriesBeforeAsync()
    {
        var value = KumoriDialog.Input(
            ActiveOwner(),
            "Delete sessions before this date (dd/MM/yyyy):",
            "Delete Entries Before",
            DateTimeOffset.Now.Date.ToString("dd/MM/yyyy", System.Globalization.CultureInfo.InvariantCulture));
        if (string.IsNullOrWhiteSpace(value))
        {
            return;
        }

        if (!DateTime.TryParseExact(value.Trim(), "dd/MM/yyyy", System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.None, out var before))
        {
            KumoriDialog.Show(ActiveOwner(), "Enter the date as dd/MM/yyyy.", "Invalid date", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        if (!KumoriDialog.Confirm(ActiveOwner(), $"Delete sessions before {before:dd/MM/yyyy}?", "Kumori", MessageBoxImage.Warning))
        {
            return;
        }

        var cutoff = before.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);
        var deleted = await Task.Run(() => _maintenance.DeleteBefore(cutoff));
        HistoryStatus = $"Deleted {deleted} old session(s)";
        await ReloadFirstPageAsync();
    }

    [RelayCommand]
    private async Task DeleteAllTrackingDataAsync()
    {
        if (!KumoriDialog.Confirm(ActiveOwner(), "Delete all tracking data? This cannot be undone.", "Kumori", MessageBoxImage.Warning))
        {
            return;
        }

        var deleted = await Task.Run(() => _maintenance.DeleteAll());
        HistoryStatus = $"Deleted {deleted} session(s)";
        await ReloadFirstPageAsync();
    }

    [RelayCommand]
    private async Task CleanupInvalidAttemptsAsync()
    {
        if (!KumoriDialog.Confirm(ActiveOwner(), "Cleanup invalid/empty finalized attempts and rebuild personal bests?", "Kumori", MessageBoxImage.Warning))
        {
            return;
        }
        var result = await Task.Run(() => _maintenance.CleanupInvalidAttempts());
        HistoryStatus = $"Cleanup removed {result.InvalidAttempts} attempt(s), {result.EmptySessions} empty session(s), reclassified {result.ReclassifiedCompleted}";
        await ReloadFirstPageAsync();
    }

    [RelayCommand]
    private async Task LoadOlderAsync()
    {
        if (IsLoadingMore || _oldestLoadedId is null)
        {
            return;
        }
        IsLoadingMore = true;
        try
        {
            var before = _oldestLoadedId;
            var search = _activeSearch;
            var page = await Task.Run(() => _attempts.GetRecentAttempts(before, PageSize, search, mapKey: _mapFilterKey));
            if (search != _activeSearch)
            {
                return;
            }
            (_mapFilterKey is null ? _loadedAttempts : _mapAttempts).AddRange(page);
            _reachedEnd = page.Count < PageSize;
            OnPropertyChanged(nameof(LoadOlderVisible));
            ApplyVisibleAttempts(selectFirst: false);
            if (page.Count > 0)
            {
                _oldestLoadedId = page[^1].Id;
            }
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Load older attempts failed");
        }
        finally
        {
            IsLoadingMore = false;
        }
    }

    private static string Truncate(string value, int max) =>
        value.Length <= max ? value : value[..(max - 1)] + "...";

    private void ApplyVisibleAttempts(bool selectFirst = true)
    {
        var previousSelectedId = SelectedAttempt?.Id;
        var source = _mapFilterKey is null ? _loadedAttempts : _mapAttempts;
        var filtered = FilterAttempts(source).ToArray();

        Attempts.Clear();
        Rows.Clear();
        var ordered = filtered.OrderByDescending(attempt => attempt.Id).ToArray();
        var attemptRows = new Dictionary<long, AttemptRowViewModel>();
        foreach (var model in ordered)
        {
            var row = new AttemptRowViewModel(model);
            Attempts.Add(row);
            attemptRows[model.Id] = row;
        }

        if (IsGroupSessions)
        {
            foreach (var group in ordered.GroupBy(attempt => attempt.SessionId))
            {
                var collapsed = _collapsedSessions.Contains(group.Key);
                if (_sessions.TryGetValue(group.Key, out var session))
                {
                    Rows.Add(new SessionRowViewModel(session, collapsed, _activeSessionId));
                }
                if (!collapsed)
                {
                    foreach (var model in group)
                    {
                        Rows.Add(attemptRows[model.Id]);
                    }
                }
            }
        }
        else
        {
            foreach (var model in ordered)
            {
                Rows.Add(attemptRows[model.Id]);
            }
        }

        SelectedAttempt = Attempts.FirstOrDefault(a => a.Id == previousSelectedId)
            ?? (selectFirst ? Attempts.FirstOrDefault() : SelectedAttempt);
        OnPropertyChanged(nameof(ResultsText));
        OnPropertyChanged(nameof(ResultsShortText));
        ApplyDashboard(_currentAnalytics, filtered, _dbBytes, _cacheBytes);
        HistoryStatus = filtered.Length == 0
            ? "No results match the current filters"
            : $"{filtered.Length} visible attempt(s)";
    }

    public void ToggleSession(SessionRowViewModel row)
    {
        if (!_collapsedSessions.Add(row.Model.Id))
        {
            _collapsedSessions.Remove(row.Model.Id);
        }
        ApplyVisibleAttempts(selectFirst: false);
    }

    private static string MapKey(AttemptSummary a) => a.OsuBeatmapId is > 0
        ? $"id:{a.OsuBeatmapId}"
        : !string.IsNullOrWhiteSpace(a.Checksum)
            ? $"hash:{a.Checksum.ToLowerInvariant()}"
            : $"meta:{a.Artist.ToLowerInvariant()}\u001f{a.Title.ToLowerInvariant()}\u001f{a.Difficulty.ToLowerInvariant()}\u001f{a.Mapper.ToLowerInvariant()}";

    /// <summary>Confirm + delete a single attempt, then reload.</summary>
    public async Task DeleteAttemptAsync(AttemptRowViewModel row)
    {
        if (!KumoriDialog.Confirm(ActiveOwner(), "Permanently delete this attempt?", "Delete attempt", MessageBoxImage.Warning))
        {
            return;
        }
        await Task.Run(() => _maintenance.DeleteAttempt(row.Id));
        Inspector.ForgetAttempt(row.Id);
        if (SelectedAttempt?.Id == row.Id)
        {
            SelectedAttempt = null;
        }
        await ReloadFirstPageAsync();
    }

    /// <summary>Confirm + delete a whole session and its attempts, then reload.</summary>
    public async Task DeleteSessionAsync(long sessionId)
    {
        if (!KumoriDialog.Confirm(ActiveOwner(), "Permanently delete this session and all its attempts?", "Delete session", MessageBoxImage.Warning))
        {
            return;
        }
        await Task.Run(() => _maintenance.DeleteSession(sessionId));
        if (SelectedAttempt?.Model.SessionId == sessionId)
        {
            Inspector.ForgetAttempt(SelectedAttempt.Id);
            SelectedAttempt = null;
        }
        await ReloadFirstPageAsync();
    }

    /// <summary>Filter the list to every loaded attempt on the same beatmap.</summary>
    public async Task ShowAllPlaysForMapAsync(AttemptRowViewModel row)
    {
        _mapFilterKey = MapKey(row.Model);
        SelectedFilterMode = "All";
        _activeSearch = null;
        SearchText = "";
        await ReloadFirstPageAsync();
    }

    public async Task ShowAllPlaysForMapAsync(MapCardViewModel map)
    {
        _mapFilterKey = map.MapKey;
        SelectedFilterMode = "All";
        _activeSearch = null;
        SearchText = "";
        await ReloadFirstPageAsync();
    }

    public void ShowProblemPlaysForMap(AttemptRowViewModel row)
    {
        _mapFilterKey = MapKey(row.Model);
        SelectedFilterMode = "All";
        IsGroupRepeats = false;
        ApplyVisibleAttempts();
        foreach (var attempt in Attempts.Where(a => a.Model.Misses == 0 && a.Model.Progress >= 0.98 && !string.Equals(a.Model.Outcome, "failed", StringComparison.OrdinalIgnoreCase)).ToArray())
        {
            Attempts.Remove(attempt);
            Rows.Remove(attempt);
        }
        HistoryStatus = $"{Attempts.Count} problem play(s) for this map";
    }

    public async Task OpenReplayInspectorAsync(AttemptRowViewModel row)
    {
        SelectedAttempt = row;
        var owner = ActiveOwner();
        IsReplayAnalyzerLoading = true;
        ReplayAnalyzerLoadingText = "Loading replay details...";
        try
        {
            await Inspector.LoadAsync(row.Id);
            if (Inspector.OpenReplayInspectorCommand.CanExecute(null))
            {
                ReplayAnalyzerLoadingText = "Simulating replay judgements...";
                await Inspector.OpenReplayInspectorCommand.ExecuteAsync(null);
                if (!string.IsNullOrWhiteSpace(Inspector.LoadError))
                {
                    HistoryStatus = "Could not open Replay Analyzer";
                    return;
                }

                if (Inspector.LastReplayInspectorProcess is { } process && owner is not null)
                {
                    ReplayAnalyzerLoadingText = "Opening replay analyzer...";
                    _ = await ReplayAnalyzerWindowPlacement.CenterNearOwnerAsync(process, owner);
                }

                HistoryStatus = "Replay Analyzer opened";
            }
            else
            {
                KumoriDialog.Show(owner, "Replay Analyzer needs movement capture and cached beatmap media for this play.",
                    "Kumori", MessageBoxButton.OK, MessageBoxImage.Information);
            }
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Replay Analyzer launch failed for attempt {AttemptId}", row.Id);
            HistoryStatus = "Could not open Replay Analyzer";
            KumoriDialog.Show(owner, ex.Message, "Kumori", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            IsReplayAnalyzerLoading = false;
            ReplayAnalyzerLoadingText = "";
        }
    }

    [RelayCommand]
    private Task OpenSelectedReplayInspector()
    {
        if (SelectedAttempt is { } row)
        {
            return OpenReplayInspectorAsync(row);
        }
        return Task.CompletedTask;
    }

    private IEnumerable<AttemptSummary> FilterAttempts(IEnumerable<AttemptSummary> attempts)
    {
        if (_mapFilterKey is { } mapKey)
        {
            attempts = attempts.Where(a => MapKey(a) == mapKey);
        }
        var mode = SelectedFilterMode;
        var filtered = attempts.Where(a => mode switch
        {
            "All" => true,
            _ => string.Equals(a.Outcome, mode, StringComparison.OrdinalIgnoreCase),
        });

        if (IsGroupRepeats)
        {
            filtered = filtered
                .GroupBy(a => $"{a.Checksum ?? a.OsuBeatmapId?.ToString() ?? a.Title}|{a.ModsKey}")
                .Select(g => g.OrderByDescending(a => a.Id).First())
                .OrderByDescending(a => a.Id);
        }

        return filtered;
    }

    private static Window? ActiveOwner() =>
        Application.Current?.Windows.OfType<Window>().FirstOrDefault(w => w.IsActive)
        ?? Application.Current?.MainWindow;

    private static void CopyIfExists(string source, string destination)
    {
        try
        {
            if (File.Exists(source))
            {
                Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                File.Copy(source, destination, overwrite: true);
            }
        }
        catch (Exception ex)
        {
            Log.Debug(ex, "Could not copy diagnostics file {Path}", source);
        }
    }

    private static void CopyDirectoryIfExists(string source, string destination)
    {
        if (!Directory.Exists(source))
        {
            return;
        }
        foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(source, file);
            CopyIfExists(file, Path.Combine(destination, relative));
        }
    }

    private void OnStateChanged(AppState state)
    {
        Application.Current?.Dispatcher.InvokeAsync(() =>
        {
            CaptureChipText = state.Capture.Health switch
            {
                HealthLevel.Ok => "Capture healthy",
                HealthLevel.Degraded => "Capture degraded",
                HealthLevel.Error => "Capture error",
                _ => "Capture idle",
            };
            CaptureChipColor = state.Capture.Health switch
            {
                HealthLevel.Ok => "#33F078",
                HealthLevel.Degraded => "#FFD43B",
                HealthLevel.Error => "#FF4F7B",
                _ => "#A86C9E",
            };
            CompanionLine = FormatCompanionLine(state.Companions);
            var activeSessionId = state.ActiveSession?.SessionId;
            SessionIndicator = state.ActiveSession is { } s
                ? $"Session #{s.SessionId}"
                : "No active session";
            if (activeSessionId != _activeSessionId)
            {
                _activeSessionId = activeSessionId;
                OnPropertyChanged(nameof(HasActiveSession));
                ApplyVisibleAttempts(selectFirst: false);
            }

            var tracking = state.Tracking;
            if (tracking.LatestAttemptId is { } latestAttemptId && latestAttemptId != _latestAttemptId)
            {
                _latestAttemptId = latestAttemptId;
                _ = ReloadFirstPageAsync();
            }

            TosuChipText = !state.Companions.OsuRunning
                ? "tosu waiting for osu!"
                : tracking switch
                {
                    { TosuConnected: true } => "tosu running",
                    { Detail: { } detail } => Truncate(detail, 44),
                    _ => "tosu starting...",
                };
            TosuChipColor = !state.Companions.OsuRunning
                ? "#A86C9E"
                : tracking.Health switch
                {
                    HealthLevel.Ok => "#33F078",
                    HealthLevel.Degraded => "#FFD43B",
                    HealthLevel.Error => "#FF4F7B",
                    _ => "#A86C9E",
                };
            IsTosuLaunchVisible = !tracking.TosuConnected;
            IsUpdateAvailable = state.ApplicationUpdate.IsAvailable;
            UpdateAvailableText = string.IsNullOrWhiteSpace(state.ApplicationUpdate.Version)
                ? "Update available"
                : $"Update {state.ApplicationUpdate.Version} available";
        });
    }

    private static string FormatCompanionLine(CompanionStatus status)
    {
        if (!status.OsuRunning)
        {
            return "Waiting for osu!";
        }
        var otd = !status.OpenTabletDriverEnabled
            ? "OTD off"
            : status.OpenTabletDriverLaunched
                ? "OTD ready"
                : status.OpenTabletDriverDetail ?? "OTD pending";
        var dual = !status.DualModeEnabled
            ? "dual mode off"
            : status.DualModeActive
                ? "dual mode active"
                : status.DualModeDetail ?? "dual mode pending";
        return $"osu! detected · {otd} · {dual}";
    }
}
