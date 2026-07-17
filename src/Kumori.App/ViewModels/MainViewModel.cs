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
    private readonly Func<Task>? _checkForUpdates;

    /// <summary>Attempt rows only — used for compare selection and the inspector.</summary>
    public ObservableCollection<AttemptRowViewModel> Attempts { get; } = new();

    /// <summary>Interleaved session separators + attempt rows bound by the list.</summary>
    public ObservableCollection<object> Rows { get; } = new();
    public ObservableCollection<ModFilterOptionViewModel> AvailableMods { get; } = new();
    public ObservableCollection<PerformanceDayViewModel> PerformanceDays { get; } = new();
    public List<MapCardViewModel> MapCards { get; } = new();
    public IReadOnlyList<MapCardRowViewModel> MapRows
    {
        get
        {
            var rows = new List<MapCardRowViewModel>((MapCards.Count + 1) / 2);
            for (var index = 0; index < MapCards.Count; index += 2)
            {
                rows.Add(new MapCardRowViewModel(
                    MapCards[index],
                    index + 1 < MapCards.Count ? MapCards[index + 1] : null));
            }

            return rows;
        }
    }
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
    [ObservableProperty] private bool _canStartTosu = true;
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
    [ObservableProperty] private string _selectedModFilterMode = "Contains";
    [ObservableProperty] private string _selectedArtworkMode = "Thumbnail cards";

    private readonly List<AttemptSummary> _loadedAttempts = new();
    private readonly List<AttemptSummary> _mapAttempts = new();
    private readonly Dictionary<long, SessionSummary> _sessions = new();
    private readonly HashSet<long> _collapsedSessions = new();
    private long _dbBytes;
    private long _cacheBytes;
    private DateTimeOffset _cacheMeasuredAtUtc;
    private AnalyticsSummary _currentAnalytics = new();
    private bool _usingLazerRealm;
    private long? _activeSessionId;
    private long? _latestAttemptId;
    private Func<Task<bool>>? _endLiveSession;
    private Action? _dashboardRefreshRequested;
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
    private AppState? _pendingUiState;
    private int _stateDispatchScheduled;
    private bool _updatingModFilter;

    public MainViewModel(
        AppStateStore store,
        AttemptRepository attempts,
        AttemptDetailsRepository details,
        AnalyticsRepository analytics,
        SettingsService settings,
        ReplayViewerContractService? replayViewer = null,
        TrackingMaintenanceRepository? maintenance = null,
        SessionRepository? sessions = null,
        Func<Task>? checkForUpdates = null)
    {
        _store = store;
        _appState = store;
        _attempts = attempts;
        _analytics = analytics;
        _settings = settings;
        _detailsRepository = details;
        _replayViewer = replayViewer;
        _checkForUpdates = checkForUpdates;
        _maintenance = maintenance ?? new TrackingMaintenanceRepository(new SqliteConnectionFactory(AppPaths.TrackingDatabase, readOnly: false));
        _sessionsRepo = sessions ?? new SessionRepository(new SqliteConnectionFactory(AppPaths.TrackingDatabase, readOnly: true));
        Inspector = new AttemptDetailsViewModel(details, replayViewer);
        _activeSessionId = store.Current.ActiveSession?.SessionId;
        IsGroupSessions = settings.Current.Appearance.GroupSessions;
        _store.StateChanged += OnStateChanged;
    }

    public void SetEndLiveSessionHandler(Func<Task<bool>>? handler) => _endLiveSession = handler;

    public void SetDashboardRefreshHandler(Action handler) =>
        _dashboardRefreshRequested = handler;

    public bool IsThumbnailArtwork => SelectedArtworkMode == "Thumbnail cards";
    public string ResultsText => $"{Attempts.Count:N0} results";
    public string ResultsShortText => $"{Attempts.Count:N0}";
    public bool HasAvailableMods => AvailableMods.Count > 0;
    public bool IsModFilterActive => AvailableMods.Any(mod => mod.IsSelected);
    public string ModsFilterLabel
    {
        get
        {
            var selected = AvailableMods.Where(mod => mod.IsSelected).Select(mod => mod.Acronym).ToArray();
            return selected.Length switch
            {
                0 => "Mods",
                <= 2 => $"Mods · {string.Join(" + ", selected)}",
                _ => $"Mods · {selected.Length}",
            };
        }
    }
    public string ModFilterModeDescription => SelectedModFilterMode == "Exact"
        ? "Only plays with exactly the selected combination."
        : "Plays may include other mods in addition to your selection.";
    public bool HasNoPerformanceData => PerformanceDays.Count == 0;
    public bool HasNoMapData => MapCards.Count == 0;
    public bool CanLaunchTosu => CanStartTosu && !IsLaunchingTosu;
    public bool HasActiveSession => _activeSessionId is not null;
    private bool CanMaintainTrackingData() => !HasActiveSession;
    public string AppVersionText => $"v{typeof(MainViewModel).Assembly.GetName().Version?.ToString(3) ?? "0.4.7"}";

    partial void OnCanStartTosuChanged(bool value) => LaunchTosuCommand.NotifyCanExecuteChanged();
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
    partial void OnSelectedModFilterModeChanged(string value)
    {
        OnPropertyChanged(nameof(ModFilterModeDescription));
        ApplyVisibleAttempts();
    }

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

    public Task HydrateAsync(CancellationToken cancellationToken = default) =>
        ReloadFirstPageAsync(cancellationToken);

    public Task RefreshDashboardAsync(CancellationToken cancellationToken = default) =>
        ReloadFirstPageAsync(cancellationToken);

    /// <summary>
    /// Movement persistence does not change dashboard totals. Refresh only the
    /// affected row instead of rebuilding the full history a second time after
    /// every completed play.
    /// </summary>
    public async Task RefreshAttemptMovementAsync(
        long attemptId,
        CancellationToken cancellationToken = default)
    {
        var refreshed = await Task.Run(
            () => _attempts.GetAttempt(attemptId),
            cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        if (refreshed is null)
        {
            return;
        }

        ReplaceAttemptModel(_loadedAttempts, refreshed);
        ReplaceAttemptModel(_mapAttempts, refreshed);

        var oldRow = Attempts.FirstOrDefault(row => row.Id == attemptId);
        if (oldRow is null)
        {
            return;
        }

        var replacement = new AttemptRowViewModel(refreshed);
        var attemptIndex = Attempts.IndexOf(oldRow);
        if (attemptIndex >= 0)
        {
            Attempts[attemptIndex] = replacement;
        }

        var rowIndex = Rows.IndexOf(oldRow);
        if (rowIndex >= 0)
        {
            Rows[rowIndex] = replacement;
        }

        if (SelectedAttempt?.Id == attemptId)
        {
            SelectedAttempt = replacement;
        }
    }

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
        try
        {
            await ReloadFirstPageAsync(token);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
        }
    }

    private async Task ReloadFirstPageAsync(CancellationToken cancellationToken = default)
    {
        var generation = Interlocked.Increment(ref _reloadGeneration);
        await _reloadGate.WaitAsync(cancellationToken);
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (generation != Volatile.Read(ref _reloadGeneration)) return;
            var search = _activeSearch;
            var page = await Task.Run(() =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                return _attempts.GetRecentAttempts(null, PageSize, search, mapKey: _mapFilterKey);
            }, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            if (search != _activeSearch || generation != Volatile.Read(ref _reloadGeneration))
            {
                return;
            }

            // Session headers are needed for the visible page, but aggregating
            // every historical session made startup and every auto-refresh
            // increasingly expensive as the database grew.
            var sessionIds = page.Select(attempt => attempt.SessionId).Distinct().ToArray();
            var sessions = await Task.Run(
                () => _sessionsRepo.GetSessions(sessionIds),
                cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            if (search != _activeSearch || generation != Volatile.Read(ref _reloadGeneration))
            {
                return;
            }

            _sessions.Clear();
            foreach (var session in sessions)
            {
                _sessions[session.Id] = session;
            }
            _reachedEnd = page.Count < PageSize;
            OnPropertyChanged(nameof(LoadOlderVisible));
            var destination = _mapFilterKey is null ? _loadedAttempts : _mapAttempts;
            destination.Clear();
            destination.AddRange(page);
            ApplyVisibleAttempts(selectFirst: false);
            if (Attempts.Count > 0 && (SelectedAttempt is null || !Attempts.Any(a => a.Id == SelectedAttempt.Id)))
            {
                SelectedAttempt = Attempts[0];
            }
            else if (Attempts.Count == 0)
            {
                SelectedAttempt = null;
            }
            _oldestLoadedId = page.Count > 0 ? page[^1].Id : null;
            HistoryStatus = page.Count == 0
                ? (search is not null
                    ? "No attempts match the search"
                    : File.Exists(AppPaths.TrackingDatabase)
                        ? "No attempts recorded yet"
                        : "No tracking database found - play a map with the tracker running")
                : search is not null
                    ? $"{page.Count} matching attempts"
                    : $"{page.Count} recent attempts";

            // History is visible now. Slower global summaries, map cards, and
            // the recursive media-size scan hydrate afterward without holding
            // the first useful database result hostage.
            var secondary = await Task.Run(() =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                var maps = _attempts.GetMapSummaries();
                cancellationToken.ThrowIfCancellationRequested();
                var analytics = _analytics.GetSummary();
                cancellationToken.ThrowIfCancellationRequested();
                var modKeys = _attempts.GetDistinctModsKeys();
                cancellationToken.ThrowIfCancellationRequested();
                var dbBytes = SafeFileSize(AppPaths.TrackingDatabase);
                var cacheBytes = GetCachedMediaBytes(cancellationToken);
                cancellationToken.ThrowIfCancellationRequested();
                var usingLazerRealm = LazerStorage.GetDiagnostics().RealmOpened;
                cancellationToken.ThrowIfCancellationRequested();
                return new
                {
                    Maps = maps,
                    Analytics = analytics,
                    ModKeys = modKeys,
                    DbBytes = dbBytes,
                    CacheBytes = cacheBytes,
                    UsingLazerRealm = usingLazerRealm,
                };
            }, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            if (search != _activeSearch || generation != Volatile.Read(ref _reloadGeneration))
            {
                return;
            }

            MapCards.Clear();
            foreach (var map in secondary.Maps)
            {
                MapCards.Add(new MapCardViewModel(map));
            }
            OnPropertyChanged(nameof(MapRows));
            OnPropertyChanged(nameof(HasNoMapData));
            UpdateAvailableMods(secondary.ModKeys);
            _dbBytes = secondary.DbBytes;
            _cacheBytes = secondary.CacheBytes;
            _currentAnalytics = secondary.Analytics;
            _usingLazerRealm = secondary.UsingLazerRealm;
            ApplyDashboard(secondary.Analytics, page, secondary.DbBytes, secondary.CacheBytes);
        }
        catch (OperationCanceledException)
        {
            throw;
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
        SyncLine = Invariant($"Dashboard updated {synced}");

        var first = page.FirstOrDefault();
        GroupHeader = first is null
            ? "Recent plays"
            : LocalTimeDisplay.Date(first.StartedAt, "Recent plays");
        var completed = page.Count(a => a.Outcome == "completed");
        var averageAccuracy = page.Count == 0 ? 0 : page.Average(a => a.Accuracy);
        var bestPp = page.Count == 0 ? 0 : page.Max(a => a.Pp);
        GroupStats = Invariant($"{page.Count} plays  -  {completed} completed  -  {averageAccuracy:0.00}%  -  {bestPp:0.0}pp");

        PerformanceDays.Clear();
        foreach (var trend in analytics.Daily)
        {
            PerformanceDays.Add(new PerformanceDayViewModel(trend, LoadPerformanceDayAttemptsAsync));
        }
        OnPropertyChanged(nameof(HasNoPerformanceData));
    }

    private void UpdateAvailableMods(IEnumerable<string> modKeys)
    {
        var selected = AvailableMods
            .Where(mod => mod.IsSelected)
            .Select(mod => mod.Acronym)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var acronyms = ModDisplayOrder.Sort(modKeys
            .SelectMany(ModDisplayText.AcronymsFromKey)
            .Where(acronym => !string.Equals(acronym, "NM", StringComparison.OrdinalIgnoreCase))
            .Distinct(StringComparer.OrdinalIgnoreCase));

        _updatingModFilter = true;
        try
        {
            AvailableMods.Clear();
            foreach (var acronym in acronyms)
            {
                var option = new ModFilterOptionViewModel(acronym)
                {
                    IsSelected = selected.Contains(acronym),
                };
                option.PropertyChanged += (_, args) =>
                {
                    if (args.PropertyName == nameof(ModFilterOptionViewModel.IsSelected))
                    {
                        OnModFilterSelectionChanged();
                    }
                };
                AvailableMods.Add(option);
            }
        }
        finally
        {
            _updatingModFilter = false;
        }

        OnPropertyChanged(nameof(HasAvailableMods));
        OnPropertyChanged(nameof(IsModFilterActive));
        OnPropertyChanged(nameof(ModsFilterLabel));
        if (selected.Count > 0)
        {
            ApplyVisibleAttempts(selectFirst: false);
        }
    }

    private void OnModFilterSelectionChanged()
    {
        if (_updatingModFilter)
        {
            return;
        }

        OnPropertyChanged(nameof(IsModFilterActive));
        OnPropertyChanged(nameof(ModsFilterLabel));
        ApplyVisibleAttempts();
    }

    private void ClearSelectedMods()
    {
        _updatingModFilter = true;
        try
        {
            foreach (var mod in AvailableMods)
            {
                mod.IsSelected = false;
            }
        }
        finally
        {
            _updatingModFilter = false;
        }

        OnPropertyChanged(nameof(IsModFilterActive));
        OnPropertyChanged(nameof(ModsFilterLabel));
        ApplyVisibleAttempts();
    }

    private async Task<IReadOnlyList<AttemptRowViewModel>> LoadPerformanceDayAttemptsAsync(string day)
    {
        var attempts = await Task.Run(() => _attempts.GetAttemptsForDay(day));
        return attempts.Select(attempt => new AttemptRowViewModel(attempt)).ToArray();
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

    private long GetCachedMediaBytes(CancellationToken cancellationToken)
    {
        var now = DateTimeOffset.UtcNow;
        if (_cacheMeasuredAtUtc != default && now - _cacheMeasuredAtUtc < TimeSpan.FromMinutes(1))
        {
            return _cacheBytes;
        }

        var bytes = CacheStorageUsage.GetAdditionalBytes(AppPaths.BeatmapMediaDir, cancellationToken);
        _cacheMeasuredAtUtc = now;
        return bytes;
    }

    private void OnStateChanged(AppState state)
    {
        Interlocked.Exchange(ref _pendingUiState, state);
        ScheduleStateDispatch();
    }

    private void ScheduleStateDispatch()
    {
        if (Interlocked.CompareExchange(ref _stateDispatchScheduled, 1, 0) != 0)
        {
            return;
        }

        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is null || dispatcher.HasShutdownStarted)
        {
            Interlocked.Exchange(ref _pendingUiState, null);
            Interlocked.Exchange(ref _stateDispatchScheduled, 0);
            return;
        }
        dispatcher.InvokeAsync(ApplyPendingState);
    }

    private void ApplyPendingState()
    {
        try
        {
            var state = Interlocked.Exchange(ref _pendingUiState, null);
            if (state is not null)
            {
                ApplyState(state);
            }
        }
        finally
        {
            Interlocked.Exchange(ref _stateDispatchScheduled, 0);
            if (Volatile.Read(ref _pendingUiState) is not null)
            {
                ScheduleStateDispatch();
            }
        }
    }

    private void ApplyState(AppState state)
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
            DeleteEntriesBeforeCommand.NotifyCanExecuteChanged();
            DeleteAllTrackingDataCommand.NotifyCanExecuteChanged();
            CleanupInvalidAttemptsCommand.NotifyCanExecuteChanged();
            DeleteShortPlaysCommand.NotifyCanExecuteChanged();
            ApplyVisibleAttempts(selectFirst: false);
        }

        var tracking = state.Tracking;
        if (tracking.LatestAttemptId is { } latestAttemptId && latestAttemptId != _latestAttemptId)
        {
            _latestAttemptId = latestAttemptId;
            try
            {
                _dashboardRefreshRequested?.Invoke();
            }
            catch (Exception ex)
            {
                Log.Debug(ex, "Could not queue the completed-attempt dashboard refresh");
            }
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
        CanStartTosu = !tracking.TosuConnected;
        IsUpdateAvailable = state.ApplicationUpdate.IsAvailable;
        UpdateAvailableText = string.IsNullOrWhiteSpace(state.ApplicationUpdate.Version)
            ? "Update available"
            : $"Update {state.ApplicationUpdate.Version} available";
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
