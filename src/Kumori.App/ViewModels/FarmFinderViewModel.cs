using System.Collections.ObjectModel;
using System.Globalization;
using System.Windows.Data;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Kumori.App.FarmFinder;
using Kumori.Core.Settings;
using Kumori.FarmFinder;

namespace Kumori.App.ViewModels;

public sealed class FarmModOptionViewModel : ObservableObject
{
    private ModRequirement requirement;

    public FarmModOptionViewModel(RankedModDescriptor descriptor)
    {
        Acronym = descriptor.Acronym;
        Name = descriptor.Name;
        CycleRequirementCommand = new RelayCommand(CycleRequirement);
    }

    public string Acronym { get; }
    public string Name { get; }
    public IRelayCommand CycleRequirementCommand { get; }
    public string RequirementLabel => Requirement switch
    {
        ModRequirement.Required => "Include",
        ModRequirement.Excluded => "Exclude",
        ModRequirement.Wildcard => "Wildcard",
        _ => "Any",
    };
    public string AccessibleLabel => $"{Name} ({Acronym}), {RequirementLabel}. Click to change.";

    public ModRequirement Requirement
    {
        get => requirement;
        set
        {
            if (!SetProperty(ref requirement, value))
                return;

            OnPropertyChanged(nameof(RequirementLabel));
            OnPropertyChanged(nameof(AccessibleLabel));
        }
    }

    private void CycleRequirement() =>
        Requirement = Requirement switch
        {
            ModRequirement.Ignore => ModRequirement.Required,
            ModRequirement.Required => ModRequirement.Excluded,
            ModRequirement.Excluded => ModRequirement.Wildcard,
            _ => ModRequirement.Ignore,
        };
}

public sealed class FarmFinderViewModel : ObservableObject, IDisposable
{
    private static readonly string[] visibleModAcronyms = ["DT", "HD", "HR", "FL"];
    private static readonly HashSet<string> persistedFilterProperties = new(StringComparer.Ordinal)
    {
        nameof(MinimumRankText), nameof(MaximumRankText),
        nameof(MinimumPpText), nameof(MaximumPpText),
        nameof(MinimumBpmText), nameof(MaximumBpmText),
        nameof(MinimumLengthText), nameof(MaximumLengthText),
        nameof(MinimumStarsText), nameof(MaximumStarsText),
        nameof(RankedFromText), nameof(RankedToText),
        nameof(MinimumPlayersText), nameof(TextSearch),
        nameof(MapStatus), nameof(MatchMode), nameof(TreatNightcoreAsDoubleTime),
        nameof(SortField), nameof(SortDirection), nameof(IsFilterPanelExpanded),
    };
    private readonly IFarmFinderService service;
    private readonly IFarmFinderRepository repository;
    private readonly IOsuCredentialsStore credentials;
    private readonly OsuApiClient apiClient;
    private readonly IFarmFinderCacheInstaller? cacheInstaller;
    private readonly IFarmBeatmapMetadataProvider? beatmapMetadataProvider;
    private readonly IExternalUrlLauncher urlLauncher;
    private readonly SettingsService? settings;
    private readonly SynchronizationContext? synchronizationContext;
    private bool restoringSavedFilters;
    private CancellationTokenSource? operationCancellation;
    private CancellationTokenSource? countdownCancellation;
    private CancellationTokenSource? selectedMapDetailsCancellation;
    private DateTimeOffset progressPhaseStartedAt;
    private FarmFinderProgressPhase? currentProgressPhase;
    private int progressPhaseStartCurrent;
    private string minimumRankText = "";
    private string maximumRankText = "";
    private string minimumPpText = "";
    private string maximumPpText = "";
    private string minimumBpmText = "";
    private string maximumBpmText = "";
    private string minimumLengthText = "";
    private string maximumLengthText = "";
    private string minimumStarsText = "";
    private string maximumStarsText = "";
    private string rankedFromText = "";
    private string rankedToText = "";
    private string minimumPlayersText = "";
    private string textSearch = "";
    private FarmMapStatus mapStatus;
    private ModMatchMode matchMode;
    private bool treatNightcoreAsDoubleTime = true;
    private bool hiddenWildcard;
    private bool isBusy;
    private bool isSearching;
    private bool isFetchingCache;
    private bool isUpdatingIndex;
    private bool isProgressIndeterminate;
    private bool hasCredentials;
    private bool isFilterPanelExpanded = true;
    private bool forceRefresh;
    private bool hasRankedDateFilter;
    private string clientIdText = "";
    private string clientSecret = "";
    private string validationMessage = "";
    private string statusText = "Search your local score index to show results.";
    private string coverageText = "No Farm Finder index coverage yet.";
    private string progressDetailsText = "Ready to build from Hinamizawa.";
    private string estimatedTimeRemainingText = "";
    private string rateLimitText = "";
    private string resumableJobText = "";
    private string repairScoreMetadataMenuText = "Repair cached score data";
    private double progressValue;
    private double progressMaximum = 1;
    private FarmMapResult? selectedResult;
    private bool isLoadingSelectedMapDetails;
    private string selectedMapDetailsStatus = "";
    private FarmSortField sortField = FarmSortField.UniquePlayers;
    private FarmSortDirection sortDirection = FarmSortDirection.Descending;

    public FarmFinderViewModel(
        IFarmFinderService service,
        IFarmFinderRepository repository,
        IOsuCredentialsStore credentials,
        OsuApiClient apiClient,
        IRankedModCatalog rankedMods,
        IExternalUrlLauncher urlLauncher,
        SettingsService? settings = null,
        IFarmFinderCacheInstaller? cacheInstaller = null,
        IFarmBeatmapMetadataProvider? beatmapMetadataProvider = null)
    {
        this.service = service;
        this.repository = repository;
        this.credentials = credentials;
        this.apiClient = apiClient;
        this.cacheInstaller = cacheInstaller;
        this.beatmapMetadataProvider = beatmapMetadataProvider;
        this.urlLauncher = urlLauncher;
        this.settings = settings;
        synchronizationContext = SynchronizationContext.Current;
        var rankedModLookup = rankedMods.GetRankedMods()
            .ToDictionary(descriptor => descriptor.Acronym, StringComparer.OrdinalIgnoreCase);
        Mods = new ObservableCollection<FarmModOptionViewModel>(
            visibleModAcronyms
                .Where(rankedModLookup.ContainsKey)
                .Select(acronym => new FarmModOptionViewModel(rankedModLookup[acronym])));
        foreach (var mod in Mods)
            mod.PropertyChanged += (_, args) =>
            {
                if (args.PropertyName == nameof(FarmModOptionViewModel.Requirement))
                {
                    enforceModRules(mod);
                    persistFilterSettings();
                }
            };

        restoreSavedFilters();
        PropertyChanged += (_, args) =>
        {
            if (!restoringSavedFilters && args.PropertyName is { } property
                && persistedFilterProperties.Contains(property))
                persistFilterSettings();
        };

        SearchCommand = new AsyncRelayCommand(SearchAsync, () => !IsBusy);
        BuildFullIndexCommand =
            new AsyncRelayCommand(BuildFullIndexAsync, () => !IsBusy);
        RepairScoreMetadataCommand =
            new AsyncRelayCommand(RepairScoreMetadataAsync, () => !IsBusy);
        FetchCacheCommand =
            new AsyncRelayCommand(FetchCacheAsync, () => !IsBusy);
        UpdateCommand = BuildFullIndexCommand;
        CancelCommand = new RelayCommand(Cancel, () => IsBusy);
        ClearFiltersCommand = new RelayCommand(ClearFilters, () => !IsBusy);
        SaveCredentialsCommand = new AsyncRelayCommand(SaveCredentialsAsync, () => !IsBusy);
        TestConnectionCommand = new AsyncRelayCommand(TestConnectionAsync, () => !IsBusy && HasCredentials);
        RemoveCredentialsCommand = new AsyncRelayCommand(RemoveCredentialsAsync, () => !IsBusy && HasCredentials);
        OpenBeatmapCommand = new RelayCommand<FarmMapResult>(
            result => openUrl(result?.OsuDirectUrl));
        OpenBeatmapInBrowserCommand = new RelayCommand<FarmMapResult>(
            result => openUrl(result?.BeatmapUrl));
        CopyBeatmapUrlCommand = new RelayCommand<FarmMapResult>(
            result => copyUrl(result?.BeatmapUrl));
        OpenPlayerCommand = new RelayCommand<FarmScoreDetail>(
            detail => openUrl(detail?.PlayerUrl));
        OpenScoreCommand = new RelayCommand<FarmScoreDetail>(
            // A play row opens the corresponding difficulty on osu! in the
            // browser instead of entering an in-app score/detail view.
            detail => openUrl(detail?.BeatmapUrl));
        SortCommand = new RelayCommand<string>(ApplySort);
        Initialization = InitializeAsync();
    }

    public Func<int, int, Task<bool>>? ConfirmUpdateAsync { get; set; }
    public Func<FarmScoreMetadataRepairStatus, Task<bool>>? ConfirmMetadataRepairAsync { get; set; }
    public Task Initialization { get; }

    public ObservableCollection<FarmModOptionViewModel> Mods { get; }
    public ObservableCollection<FarmMapResult> Results { get; } = [];
    public IReadOnlyList<FarmMapStatus> MapStatuses { get; } = Enum.GetValues<FarmMapStatus>();
    public IReadOnlyList<ModMatchMode> MatchModes { get; } = Enum.GetValues<ModMatchMode>();
    public IReadOnlyList<FarmSortField> SortFields { get; } = Enum.GetValues<FarmSortField>();
    public IReadOnlyList<FarmSortDirection> SortDirections { get; } = Enum.GetValues<FarmSortDirection>();

    public IAsyncRelayCommand SearchCommand { get; }
    public IAsyncRelayCommand BuildFullIndexCommand { get; }
    public IAsyncRelayCommand RepairScoreMetadataCommand { get; }
    public IAsyncRelayCommand FetchCacheCommand { get; }
    public IAsyncRelayCommand UpdateCommand { get; }
    public IRelayCommand CancelCommand { get; }
    public IRelayCommand ClearFiltersCommand { get; }
    public IAsyncRelayCommand SaveCredentialsCommand { get; }
    public IAsyncRelayCommand TestConnectionCommand { get; }
    public IAsyncRelayCommand RemoveCredentialsCommand { get; }
    public IRelayCommand<FarmMapResult> OpenBeatmapCommand { get; }
    public IRelayCommand<FarmMapResult> OpenBeatmapInBrowserCommand { get; }
    public IRelayCommand<FarmMapResult> CopyBeatmapUrlCommand { get; }
    public IRelayCommand<FarmScoreDetail> OpenPlayerCommand { get; }
    public IRelayCommand<FarmScoreDetail> OpenScoreCommand { get; }
    public IRelayCommand<string> SortCommand { get; }
    public string RepairScoreMetadataMenuText
    {
        get => repairScoreMetadataMenuText;
        private set => SetProperty(ref repairScoreMetadataMenuText, value);
    }

    public string MinimumRankText { get => minimumRankText; set => SetProperty(ref minimumRankText, value); }
    public string MaximumRankText { get => maximumRankText; set => SetProperty(ref maximumRankText, value); }
    public string MinimumPpText { get => minimumPpText; set => SetProperty(ref minimumPpText, value); }
    public string MaximumPpText { get => maximumPpText; set => SetProperty(ref maximumPpText, value); }
    public string MinimumBpmText { get => minimumBpmText; set => SetProperty(ref minimumBpmText, value); }
    public string MaximumBpmText { get => maximumBpmText; set => SetProperty(ref maximumBpmText, value); }
    public string MinimumLengthText { get => minimumLengthText; set => SetProperty(ref minimumLengthText, value); }
    public string MaximumLengthText { get => maximumLengthText; set => SetProperty(ref maximumLengthText, value); }
    public string MinimumStarsText { get => minimumStarsText; set => SetProperty(ref minimumStarsText, value); }
    public string MaximumStarsText { get => maximumStarsText; set => SetProperty(ref maximumStarsText, value); }
    public string RankedFromText { get => rankedFromText; set => SetProperty(ref rankedFromText, value); }
    public string RankedToText { get => rankedToText; set => SetProperty(ref rankedToText, value); }
    public string MinimumPlayersText { get => minimumPlayersText; set => SetProperty(ref minimumPlayersText, value); }
    public string TextSearch { get => textSearch; set => SetProperty(ref textSearch, value); }
    public FarmMapStatus MapStatus { get => mapStatus; set => SetProperty(ref mapStatus, value); }
    public ModMatchMode MatchMode { get => matchMode; set => SetProperty(ref matchMode, value); }
    public bool TreatNightcoreAsDoubleTime
    {
        get => true;
        set => SetProperty(ref treatNightcoreAsDoubleTime, true);
    }
    public bool HiddenWildcard
    {
        get => hiddenWildcard;
        set
        {
            if (hasExplicitHiddenFilter() && value)
                value = false;
            SetProperty(ref hiddenWildcard, value);
        }
    }
    public bool IsFilterPanelExpanded { get => isFilterPanelExpanded; set => SetProperty(ref isFilterPanelExpanded, value); }
    public bool ForceRefresh { get => forceRefresh; set => SetProperty(ref forceRefresh, value); }
    public bool HasRankedDateFilter
    {
        get => hasRankedDateFilter;
        private set => SetProperty(ref hasRankedDateFilter, value);
    }
    public string ClientIdText { get => clientIdText; set => SetProperty(ref clientIdText, value); }
    public string ClientSecret { get => clientSecret; set => SetProperty(ref clientSecret, value); }
    public string ValidationMessage { get => validationMessage; private set => SetProperty(ref validationMessage, value); }
    public string StatusText { get => statusText; private set => SetProperty(ref statusText, value); }
    public string CoverageText { get => coverageText; private set => SetProperty(ref coverageText, value); }
    public string ProgressDetailsText { get => progressDetailsText; private set => SetProperty(ref progressDetailsText, value); }
    public string EstimatedTimeRemainingText { get => estimatedTimeRemainingText; private set => SetProperty(ref estimatedTimeRemainingText, value); }
    public string RateLimitText { get => rateLimitText; private set => SetProperty(ref rateLimitText, value); }
    public string ResumableJobText { get => resumableJobText; private set => SetProperty(ref resumableJobText, value); }
    public double ProgressValue
    {
        get => progressValue;
        private set
        {
            if (SetProperty(ref progressValue, value))
                OnPropertyChanged(nameof(ProgressPercentage));
        }
    }
    public double ProgressMaximum
    {
        get => progressMaximum;
        private set
        {
            if (SetProperty(ref progressMaximum, value))
                OnPropertyChanged(nameof(ProgressPercentage));
        }
    }
    public double ProgressPercentage =>
        ProgressMaximum <= 0 ? 0 : ProgressValue * 100d / ProgressMaximum;
    public FarmMapResult? SelectedResult
    {
        get => selectedResult;
        set
        {
            if (!SetProperty(ref selectedResult, value))
                return;

            selectedMapDetailsCancellation?.Cancel();
            selectedMapDetailsCancellation?.Dispose();
            selectedMapDetailsCancellation = null;
            IsLoadingSelectedMapDetails = false;
            SelectedMapDetailsStatus = "";
            if (value is null || beatmapMetadataProvider is null || HasDifficultyStats(value.Beatmap))
                return;

            selectedMapDetailsCancellation = new CancellationTokenSource();
            _ = EnrichSelectedMapAsync(value, selectedMapDetailsCancellation.Token);
        }
    }
    public bool IsLoadingSelectedMapDetails
    {
        get => isLoadingSelectedMapDetails;
        private set => SetProperty(ref isLoadingSelectedMapDetails, value);
    }
    public string SelectedMapDetailsStatus
    {
        get => selectedMapDetailsStatus;
        private set => SetProperty(ref selectedMapDetailsStatus, value);
    }
    public FarmSortField SortField { get => sortField; set => SetProperty(ref sortField, value); }
    public FarmSortDirection SortDirection { get => sortDirection; set => SetProperty(ref sortDirection, value); }
    public bool HasCredentials
    {
        get => hasCredentials;
        private set
        {
            if (SetProperty(ref hasCredentials, value))
                notifyCommandStates();
        }
    }
    public bool IsBusy
    {
        get => isBusy;
        private set
        {
            if (!SetProperty(ref isBusy, value))
                return;
            OnPropertyChanged(nameof(IsIdle));
            notifyCommandStates();
        }
    }
    public bool IsUpdatingIndex
    {
        get => isUpdatingIndex;
        private set => SetProperty(ref isUpdatingIndex, value);
    }
    public bool IsProgressIndeterminate
    {
        get => isProgressIndeterminate;
        private set => SetProperty(ref isProgressIndeterminate, value);
    }
    public bool IsSearching
    {
        get => isSearching;
        private set
        {
            if (SetProperty(ref isSearching, value))
                OnPropertyChanged(nameof(IsResultsOperationActive));
        }
    }
    public bool IsFetchingCache
    {
        get => isFetchingCache;
        private set
        {
            if (SetProperty(ref isFetchingCache, value))
                OnPropertyChanged(nameof(IsResultsOperationActive));
        }
    }
    public bool IsResultsOperationActive => IsSearching || IsFetchingCache;
    public bool IsIdle => !IsBusy;
    public bool HasResults => Results.Count > 0;
    public bool HasValidationError => !string.IsNullOrWhiteSpace(ValidationMessage);
    public bool HasRateLimit => !string.IsNullOrWhiteSpace(RateLimitText);
    public bool HasResumableJob => !string.IsNullOrWhiteSpace(ResumableJobText);

    public async Task InitializeAsync()
    {
        try
        {
            await repository.InitializeAsync();
            HasCredentials = (await credentials.LoadAsync())?.IsConfigured == true;
            var job = await repository.GetResumableJobAsync();
            ResumableJobText = job is null
                ? ""
                : $"Resumable update: ranks #{job.MinimumRank:N0}–#{job.MaximumRank:N0}, " +
                  $"{job.PlayersCompleted:N0} completed, {job.PlayersFailed:N0} failed.";
            OnPropertyChanged(nameof(HasResumableJob));
            await refreshScoreMetadataRepairStatusAsync();
        }
        catch (Exception exception)
        {
            StatusText = $"Farm Finder setup could not be loaded: {exception.Message}";
        }
    }

    public async Task SearchAsync()
    {
        if (!TryBuildQuery(requireRanks: false, out var query))
            return;
        await runOperationAsync(async token =>
        {
            IsSearching = true;
            try
            {
                StatusText = "Searching cached Farm Finder data…";
                var result = await service.SearchCachedAsync(query!, createProgress(), token);
                SelectedResult = null;
                Results.Clear();
                foreach (var item in result.Results)
                    Results.Add(item);
                SelectedResult = Results.FirstOrDefault();
                HasRankedDateFilter |= result.Results.Any(item => item.Beatmap.RankedAt is not null);
                updateCoverage(result.Coverage);
                StatusText = Results.Count == 0
                    ? "No matching ranked-mod farm maps were found in the cached coverage."
                    : Results.Count == query!.MaximumResults
                        ? $"Showing the top {Results.Count:N0} ranked-mod map groups."
                    : $"Found {Results.Count:N0} ranked-mod map groups.";
                OnPropertyChanged(nameof(HasResults));
            }
            finally
            {
                IsSearching = false;
            }
        });
    }

    public Task UpdateAsync() => BuildFullIndexAsync();

    public async Task RepairScoreMetadataAsync()
    {
        var status = await repository.GetScoreMetadataRepairStatusAsync();
        if (status.IsComplete)
        {
            StatusText = "Cached score metadata is already current.";
            RepairScoreMetadataMenuText = "Score metadata is up to date";
            return;
        }
        if (ConfirmMetadataRepairAsync is not null &&
            !await ConfirmMetadataRepairAsync(status))
            return;

        await runOperationAsync(async token =>
        {
            IsUpdatingIndex = true;
            try
            {
                StatusText =
                    $"Repairing score metadata for {status.PendingPlayers:N0} cached players...";
                ProgressDetailsText =
                    "Refreshing top scores from Hinamizawa. Completed players are resumable.";
                EstimatedTimeRemainingText = "";
                var result = await service.RepairScoreMetadataAsync(
                    createProgress(),
                    token);
                StatusText = result.PlayersFailed == 0
                    ? $"Score metadata repaired for {result.PlayersCompleted:N0} players."
                    : $"Score metadata repaired for {result.PlayersCompleted:N0} players; " +
                      $"{result.PlayersFailed:N0} will retry next time.";
                ProgressDetailsText =
                    $"Refreshed {result.ScoresRefreshed:N0} top scores with origin, " +
                    "Classic, score-total, legacy-ID, and client-build metadata.";
            }
            finally
            {
                IsUpdatingIndex = false;
                await refreshScoreMetadataRepairStatusAsync(CancellationToken.None);
            }
        });
    }

    public async Task FetchCacheAsync()
    {
        if (cacheInstaller?.IsConfigured != true)
        {
            StatusText =
                "Pre-built cache download is not available yet. The server URL still needs to be configured.";
            ProgressDetailsText = "You can keep using the local cache or build the full index manually.";
            return;
        }

        await runOperationAsync(async token =>
        {
            IsFetchingCache = true;
            try
            {
                StatusText = "Preparing to fetch the pre-built Farm Finder cache…";
                ProgressValue = 0;
                ProgressMaximum = 1;
                var downloadProgress = new Progress<FarmCacheDownloadProgress>(progress =>
                {
                    ProgressValue = progress.BytesReceived;
                    ProgressMaximum = Math.Max(1, progress.TotalBytes);
                    StatusText = progress.Text;
                    if (progress.TotalBytes > 1)
                    {
                        var rate = progress.BytesPerSecond > 0
                            ? $" · {formatDownloadBytes(progress.BytesPerSecond)}/s"
                            : "";
                        var remaining = progress.EstimatedRemaining is { } eta &&
                                        eta > TimeSpan.Zero
                            ? $" · {formatDuration(eta)} remaining"
                            : "";
                        ProgressDetailsText =
                            $"{formatDownloadBytes(progress.BytesReceived)} of " +
                            $"{formatDownloadBytes(progress.TotalBytes)} · " +
                            $"{ProgressPercentage:0.#}%{rate}{remaining}";
                        EstimatedTimeRemainingText =
                            progress.EstimatedRemaining is { } estimate &&
                            estimate > TimeSpan.Zero
                                ? $"About {formatDuration(estimate)} remaining"
                                : "";
                    }
                    else
                    {
                        ProgressDetailsText = string.IsNullOrWhiteSpace(progress.Detail)
                            ? "Checking cache compatibility and integrity."
                            : progress.Detail;
                        EstimatedTimeRemainingText = "";
                    }
                });
                var installed = await cacheInstaller.FetchAndInstallAsync(
                    downloadProgress,
                    token);

                StatusText = "Pre-built cache installed. Loading results…";
                ProgressDetailsText =
                    "Running your current filters against the newly installed cache.";
                EstimatedTimeRemainingText = "";
                var query = TryBuildQuery(requireRanks: false, out var selected)
                    ? selected!
                    : new FarmFinderQuery();
                var result = await service.SearchCachedAsync(
                    query,
                    createProgress(),
                    token);
                SelectedResult = null;
                Results.Clear();
                foreach (var item in result.Results)
                    Results.Add(item);
                SelectedResult = Results.FirstOrDefault();
                updateCoverage(result.Coverage);
                HasRankedDateFilter |= result.Results.Any(
                    item => item.Beatmap.RankedAt is not null);
                StatusText =
                    $"Pre-built cache from {installed.GeneratedAt.LocalDateTime:g} installed · " +
                    $"{Results.Count:N0} matching map groups.";
                ProgressDetailsText = installed.PreviousCacheRetained
                    ? "The previous local cache was retained as a rollback copy."
                    : "Cache download and database verification completed.";
                EstimatedTimeRemainingText = "";
                OnPropertyChanged(nameof(HasResults));
            }
            finally
            {
                IsFetchingCache = false;
            }
        });
    }

    public async Task BuildFullIndexAsync()
    {
        if (!TryBuildQuery(requireRanks: true, out var query))
            return;
        if (!HasCredentials)
        {
            ValidationMessage = "Save osu! API credentials before updating. Cached search remains available.";
            OnPropertyChanged(nameof(HasValidationError));
            return;
        }
        var minimum = query!.MinimumGlobalRank!.Value;
        var maximum = query.MaximumGlobalRank!.Value;
        if (ConfirmUpdateAsync is not null &&
            !await ConfirmUpdateAsync(minimum, maximum))
            return;
        await runOperationAsync(async token =>
        {
            IsUpdatingIndex = true;
            try
            {
                StatusText = maximum > OsuApiLimits.MaximumPerformanceRankingEntries
                    ? $"Discovering #{minimum:N0}–#{maximum:N0} through country leaderboards…"
                    : $"Preparing the complete #{minimum:N0}–#{maximum:N0} rank range…";
                ProgressDetailsText =
                    "Player discovery uses osu! rankings. Top scores are downloaded from Hinamizawa.";
                EstimatedTimeRemainingText = "";
                var coverage = await service.UpdateIndexAsync(
                    query,
                    ForceRefresh,
                    createProgress(),
                    token);
                updateCoverage(coverage);
                ResumableJobText = "";
                OnPropertyChanged(nameof(HasResumableJob));
                StatusText = "Full index built. Searching the refreshed cache…";
                IsSearching = true;
                FarmFinderSearchResult result;
                try
                {
                    result = await service.SearchCachedAsync(
                        query,
                        createProgress(),
                        token);
                }
                finally
                {
                    IsSearching = false;
                }
                SelectedResult = null;
                Results.Clear();
                foreach (var item in result.Results)
                    Results.Add(item);
                SelectedResult = Results.FirstOrDefault();
                HasRankedDateFilter |= result.Results.Any(
                    item => item.Beatmap.RankedAt is not null);
                updateCoverage(result.Coverage);
                StatusText = result.Coverage.CountryGaps is { Count: > 0 }
                    ? $"Index built with partial country coverage · {Results.Count:N0} matching map groups."
                    : $"Index ready · {Results.Count:N0} matching map groups.";
                EstimatedTimeRemainingText = "";
                OnPropertyChanged(nameof(HasResults));
            }
            finally
            {
                IsUpdatingIndex = false;
            }
        });
    }

    public void Cancel() => operationCancellation?.Cancel();

    public void ClearFilters()
    {
        MinimumRankText = "";
        MaximumRankText = "";
        MinimumPpText = MaximumPpText = MinimumBpmText = MaximumBpmText = "";
        MinimumLengthText = MaximumLengthText = MinimumStarsText = MaximumStarsText = "";
        RankedFromText = RankedToText = "";
        MinimumPlayersText = "";
        TextSearch = "";
        MapStatus = FarmMapStatus.Any;
        MatchMode = ModMatchMode.ContainsRequired;
        TreatNightcoreAsDoubleTime = true;
        HiddenWildcard = false;
        ForceRefresh = false;
        foreach (var mod in Mods)
            mod.Requirement = ModRequirement.Ignore;
        ValidationMessage = "";
        OnPropertyChanged(nameof(HasValidationError));
    }

    public async Task SaveCredentialsAsync()
    {
        if (!long.TryParse(ClientIdText, NumberStyles.None, CultureInfo.InvariantCulture, out var clientId) ||
            clientId <= 0 || string.IsNullOrWhiteSpace(ClientSecret))
        {
            ValidationMessage = "Enter a positive Client ID and the client secret.";
            OnPropertyChanged(nameof(HasValidationError));
            return;
        }
        try
        {
            IsBusy = true;
            await credentials.SaveAsync(new OsuApiCredentials(clientId, ClientSecret));
            apiClient.InvalidateToken();
            ClientSecret = "";
            HasCredentials = true;
            ValidationMessage = "";
            OnPropertyChanged(nameof(HasValidationError));
            StatusText = "Credentials saved with Windows current-user encryption.";
        }
        catch (Exception exception)
        {
            ValidationMessage = $"Credentials could not be saved: {exception.Message}";
            OnPropertyChanged(nameof(HasValidationError));
        }
        finally
        {
            IsBusy = false;
        }
    }

    public async Task TestConnectionAsync()
    {
        await runOperationAsync(async token =>
        {
            StatusText = "Testing the osu! API connection…";
            await apiClient.TestConnectionAsync(token);
            StatusText = "osu! API connection succeeded.";
        });
    }

    public async Task RemoveCredentialsAsync()
    {
        try
        {
            IsBusy = true;
            await credentials.DeleteAsync();
            apiClient.InvalidateToken();
            ClientIdText = "";
            ClientSecret = "";
            HasCredentials = false;
            StatusText = "Farm Finder credentials removed. Cached search is still available.";
        }
        finally
        {
            IsBusy = false;
        }
    }

    public void ApplySort(string? fieldName)
    {
        if (!Enum.TryParse(fieldName, out FarmSortField requested))
            return;
        if (SortField == requested)
            SortDirection = SortDirection == FarmSortDirection.Descending
                ? FarmSortDirection.Ascending
                : FarmSortDirection.Descending;
        else
        {
            SortField = requested;
            SortDirection = requested is FarmSortField.ArtistTitle or FarmSortField.RankedDate
                ? FarmSortDirection.Ascending
                : FarmSortDirection.Descending;
        }
        _ = SearchAsync();
    }

    public void Dispose()
    {
        operationCancellation?.Cancel();
        operationCancellation?.Dispose();
        countdownCancellation?.Cancel();
        countdownCancellation?.Dispose();
        selectedMapDetailsCancellation?.Cancel();
        selectedMapDetailsCancellation?.Dispose();
        if (service is IDisposable disposableService)
            disposableService.Dispose();
        else
            apiClient.Dispose();
        if (cacheInstaller is IDisposable disposableCacheInstaller)
            disposableCacheInstaller.Dispose();
    }

    private async Task EnrichSelectedMapAsync(
        FarmMapResult requested,
        CancellationToken cancellationToken)
    {
        IsLoadingSelectedMapDetails = true;
        SelectedMapDetailsStatus = "Loading map stats…";
        try
        {
            var beatmap = await beatmapMetadataProvider!.EnrichAsync(
                requested.Beatmap,
                cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            if (!ReferenceEquals(selectedResult, requested))
                return;

            if (!HasDifficultyStats(beatmap))
            {
                SelectedMapDetailsStatus = "Map stats unavailable";
                return;
            }

            var enriched = requested with { Beatmap = beatmap };
            var index = Results.IndexOf(requested);
            if (index >= 0)
                Results[index] = enriched;
            IsLoadingSelectedMapDetails = false;
            selectedResult = enriched;
            OnPropertyChanged(nameof(SelectedResult));
            SelectedMapDetailsStatus = "";
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch
        {
            if (ReferenceEquals(selectedResult, requested))
                SelectedMapDetailsStatus = "Map stats unavailable";
        }
        finally
        {
            if (ReferenceEquals(selectedResult, requested))
                IsLoadingSelectedMapDetails = false;
        }
    }

    private static bool HasDifficultyStats(FarmBeatmap beatmap) =>
        beatmap.CircleSize is not null &&
        beatmap.ApproachRate is not null &&
        beatmap.OverallDifficulty is not null &&
        beatmap.DrainRate is not null;

    private async Task runOperationAsync(Func<CancellationToken, Task> action)
    {
        if (IsBusy)
            return;
        operationCancellation = new CancellationTokenSource();
        currentProgressPhase = null;
        progressPhaseStartedAt = DateTimeOffset.UtcNow;
        progressPhaseStartCurrent = 0;
        IsProgressIndeterminate = false;
        IsBusy = true;
        ValidationMessage = "";
        OnPropertyChanged(nameof(HasValidationError));
        try
        {
            await action(operationCancellation.Token);
        }
        catch (OperationCanceledException)
        {
            StatusText = "Operation cancelled. Completed players remain cached and the update is resumable.";
            await refreshResumableJobAsync();
        }
        catch (OsuApiAuthenticationException exception)
        {
            StatusText = "The osu! API rejected the credentials.";
            ValidationMessage = exception.Message;
            OnPropertyChanged(nameof(HasValidationError));
        }
        catch (Exception exception)
        {
            StatusText = "Farm Finder could not complete the operation.";
            ValidationMessage = exception.Message;
            OnPropertyChanged(nameof(HasValidationError));
        }
        finally
        {
            operationCancellation.Dispose();
            operationCancellation = null;
            IsProgressIndeterminate = false;
            IsBusy = false;
        }
    }

    private IProgress<FarmFinderProgress> createProgress() =>
        new Progress<FarmFinderProgress>(progress =>
        {
            if (currentProgressPhase != progress.Phase)
            {
                currentProgressPhase = progress.Phase;
                progressPhaseStartedAt = DateTimeOffset.UtcNow;
                progressPhaseStartCurrent = progress.Current;
            }
            ProgressValue = progress.Current;
            ProgressMaximum = Math.Max(1, progress.Total);
            IsProgressIndeterminate =
                progress.Phase is FarmFinderProgressPhase.SearchingCache
                    or FarmFinderProgressPhase.AggregatingResults
                || (progress.Phase != FarmFinderProgressPhase.Completed
                    && progress.Phase != FarmFinderProgressPhase.CalculatingStars
                    && progress.Current <= 0);
            StatusText = progress.Text;
            updateProgressDetails(progress);
            if (progress.RateLimitedUntil is { } until)
                startRateLimitCountdown(until);
        });

    private void updateProgressDetails(FarmFinderProgress progress)
    {
        var percentage = progress.Total <= 0
            ? 0
            : progress.Current * 100d / progress.Total;
        ProgressDetailsText = progress.Phase switch
        {
            FarmFinderProgressPhase.DiscoveringPlayers =>
                $"{progress.Current:N0} of {progress.Total:N0} ranking feeds · {percentage:0.#}%",
            FarmFinderProgressPhase.FetchingScores =>
                $"{percentage:0.#}% · {progress.PlayersFetched:N0} downloaded · " +
                $"{progress.PlayersLoadedFromCache:N0} cached · " +
                $"{progress.ScoresExamined:N0} ranked top scores · " +
                $"{progress.PlayersFailed:N0} failed",
            FarmFinderProgressPhase.AggregatingResults =>
                $"{progress.ScoresExamined:N0} cached scores are being grouped.",
            FarmFinderProgressPhase.CalculatingStars =>
                $"{progress.Current:N0} of {progress.Total:N0} result groups calculated · {percentage:0.#}%",
            FarmFinderProgressPhase.Completed =>
                $"{progress.ResultCount:N0} result groups ready.",
            _ => "Reading the local Farm Finder index.",
        };

        if (progress.Phase != FarmFinderProgressPhase.FetchingScores ||
            progress.Current >= progress.Total)
        {
            EstimatedTimeRemainingText = "";
            return;
        }

        var completedThisPhase = progress.Current - progressPhaseStartCurrent;
        var elapsed = DateTimeOffset.UtcNow - progressPhaseStartedAt;
        if (completedThisPhase <= 0 || elapsed < TimeSpan.FromSeconds(2))
        {
            EstimatedTimeRemainingText = "Estimating time remaining…";
            return;
        }

        var remaining = progress.Total - progress.Current;
        var seconds = elapsed.TotalSeconds / completedThisPhase * remaining;
        EstimatedTimeRemainingText =
            $"About {formatDuration(TimeSpan.FromSeconds(seconds))} remaining";
    }

    private void startRateLimitCountdown(DateTimeOffset until)
    {
        countdownCancellation?.Cancel();
        countdownCancellation?.Dispose();
        countdownCancellation = new CancellationTokenSource();
        var token = countdownCancellation.Token;
        _ = Task.Run(async () =>
        {
            while (!token.IsCancellationRequested)
            {
                var remaining = until - DateTimeOffset.UtcNow;
                post(() =>
                {
                    RateLimitText = remaining > TimeSpan.Zero
                        ? $"Rate limit: retrying in {Math.Ceiling(remaining.TotalSeconds):N0}s"
                        : "";
                    OnPropertyChanged(nameof(HasRateLimit));
                });
                if (remaining <= TimeSpan.Zero)
                    break;
                await Task.Delay(TimeSpan.FromSeconds(1), token);
            }
        }, token);
    }

    private bool TryBuildQuery(bool requireRanks, out FarmFinderQuery? query)
    {
        var errors = new List<string>();
        int? minRank = parseInt(MinimumRankText, "Minimum rank", errors);
        int? maxRank = parseInt(MaximumRankText, "Maximum rank", errors);
        var minimumPlayers = parseInt(MinimumPlayersText, "Minimum players", errors) ?? 1;
        if (requireRanks && (minRank is null || maxRank is null))
            errors.Add("Both rank bounds are required for an update.");

        query = new FarmFinderQuery
        {
            MinimumGlobalRank = minRank,
            MaximumGlobalRank = maxRank,
            MinimumPp = parseDouble(MinimumPpText, "Minimum PP", errors),
            MaximumPp = parseDouble(MaximumPpText, "Maximum PP", errors),
            MinimumEffectiveBpm = parseDouble(MinimumBpmText, "Minimum BPM", errors),
            MaximumEffectiveBpm = parseDouble(MaximumBpmText, "Maximum BPM", errors),
            MinimumEffectiveLengthSeconds = parseDuration(MinimumLengthText, "Minimum hit length", errors),
            MaximumEffectiveLengthSeconds = parseDuration(MaximumLengthText, "Maximum hit length", errors),
            MinimumStarRating = parseDouble(MinimumStarsText, "Minimum stars", errors),
            MaximumStarRating = parseDouble(MaximumStarsText, "Maximum stars", errors),
            RankedFrom = parseDate(RankedFromText, "Ranked from", errors, upperBound: false),
            RankedTo = parseDate(RankedToText, "Ranked to", errors, upperBound: true),
            MinimumUniquePlayers = minimumPlayers,
            MapStatus = MapStatus,
            TextSearch = TextSearch,
            Mods = Mods.Where(mod => mod.Requirement != ModRequirement.Ignore)
                       .Select(mod => new FarmModFilter(mod.Acronym, mod.Requirement))
                       .ToArray(),
            ExactModScope = visibleModAcronyms,
            ModMatchMode = MatchMode,
            TreatNightcoreAsDoubleTime = TreatNightcoreAsDoubleTime,
            HiddenWildcard = HiddenWildcard,
            SortField = SortField,
            SortDirection = SortDirection,
        };
        errors.AddRange(requireRanks
            ? FarmFinderValidation.ValidateIndexUpdate(query)
            : FarmFinderValidation.Validate(query));
        ValidationMessage = string.Join(Environment.NewLine, errors.Distinct());
        OnPropertyChanged(nameof(HasValidationError));
        if (errors.Count == 0)
            return true;
        query = null;
        return false;
    }

    private void enforceModRules(FarmModOptionViewModel changed)
    {
        if (changed.Requirement == ModRequirement.Required && changed.Acronym != "NM")
        {
            var noMod = Mods.FirstOrDefault(mod => mod.Acronym == "NM");
            if (noMod?.Requirement == ModRequirement.Required)
                noMod.Requirement = ModRequirement.Ignore;
        }
        if (changed.Acronym == "HD" && changed.Requirement != ModRequirement.Ignore)
            HiddenWildcard = false;
    }

    private bool hasExplicitHiddenFilter() =>
        Mods.Any(mod => mod.Acronym == "HD" && mod.Requirement != ModRequirement.Ignore);

    private void restoreSavedFilters()
    {
        if (settings is null)
            return;

        var saved = settings.Current.FarmFinder;
        restoringSavedFilters = true;
        try
        {
            MinimumRankText = saved.MinimumRankText;
            MaximumRankText = saved.MaximumRankText;
            MinimumPpText = saved.MinimumPpText;
            MaximumPpText = saved.MaximumPpText;
            MinimumBpmText = saved.MinimumBpmText;
            MaximumBpmText = saved.MaximumBpmText;
            MinimumLengthText = saved.MinimumLengthText;
            MaximumLengthText = saved.MaximumLengthText;
            MinimumStarsText = saved.MinimumStarsText;
            MaximumStarsText = saved.MaximumStarsText;
            RankedFromText = saved.RankedFromText;
            RankedToText = saved.RankedToText;
            MinimumPlayersText = "";
            TextSearch = "";
            MapStatus = FarmMapStatus.Any;
            MatchMode = ModMatchMode.ContainsRequired;
            TreatNightcoreAsDoubleTime = true;
            SortField = FarmSortField.UniquePlayers;
            SortDirection = FarmSortDirection.Descending;
            IsFilterPanelExpanded = saved.IsFilterPanelExpanded;

            foreach (var mod in Mods)
            {
                if (saved.ModRequirements.TryGetValue(mod.Acronym, out var requirement)
                    && Enum.TryParse<ModRequirement>(requirement, out var parsedRequirement))
                    mod.Requirement = parsedRequirement;
            }
        }
        finally
        {
            restoringSavedFilters = false;
        }
    }

    private void persistFilterSettings()
    {
        if (settings is null || restoringSavedFilters)
            return;

        settings.Update(current =>
        {
            var saved = current.FarmFinder;
            saved.MinimumRankText = MinimumRankText;
            saved.MaximumRankText = MaximumRankText;
            saved.MinimumPpText = MinimumPpText;
            saved.MaximumPpText = MaximumPpText;
            saved.MinimumBpmText = MinimumBpmText;
            saved.MaximumBpmText = MaximumBpmText;
            saved.MinimumLengthText = MinimumLengthText;
            saved.MaximumLengthText = MaximumLengthText;
            saved.MinimumStarsText = MinimumStarsText;
            saved.MaximumStarsText = MaximumStarsText;
            saved.RankedFromText = RankedFromText;
            saved.RankedToText = RankedToText;
            saved.MinimumPlayersText = MinimumPlayersText;
            saved.TextSearch = TextSearch;
            saved.MapStatus = MapStatus.ToString();
            saved.MatchMode = MatchMode.ToString();
            saved.TreatNightcoreAsDoubleTime = TreatNightcoreAsDoubleTime;
            saved.ModRequirements = Mods
                .Where(mod => mod.Requirement != ModRequirement.Ignore)
                .ToDictionary(mod => mod.Acronym, mod => mod.Requirement.ToString(), StringComparer.OrdinalIgnoreCase);
            saved.SortField = SortField.ToString();
            saved.SortDirection = SortDirection.ToString();
            saved.IsFilterPanelExpanded = IsFilterPanelExpanded;
        });
    }

    private void updateCoverage(CoverageSummary coverage)
    {
        var percentage = coverage.AvailablePlayers == 0
            ? 0
            : coverage.ScannedPlayers * 100d / coverage.AvailablePlayers;
        var gaps = coverage.CountryGaps ?? [];
        var gapText = "";
        if (gaps.Count != 0)
        {
            var visible = gaps.Take(3)
                .Select(gap =>
                    $"{gap.CountryCode} after #{gap.CoveredThroughGlobalRank:N0}");
            gapText = $" · country gaps: {string.Join(", ", visible)}";
            if (gaps.Count > 3)
                gapText += $" +{gaps.Count - 3:N0} more";
        }
        CoverageText =
            $"{coverage.ScannedPlayers:N0} examined / {coverage.AvailablePlayers:N0} available " +
            $"({percentage:0.#}%) · {coverage.FailedPlayers:N0} failed" +
            (coverage.LastUpdatedAt is { } last ? $" · updated {last.ToLocalTime():g}" : "") +
            gapText;
    }

    private async Task refreshResumableJobAsync()
    {
        var job = await repository.GetResumableJobAsync();
        ResumableJobText = job is null
            ? ""
            : $"Resumable update: ranks #{job.MinimumRank:N0}–#{job.MaximumRank:N0}, " +
              $"{job.PlayersCompleted:N0} completed, {job.PlayersFailed:N0} failed.";
        OnPropertyChanged(nameof(HasResumableJob));
    }

    private async Task refreshScoreMetadataRepairStatusAsync(
        CancellationToken cancellationToken = default)
    {
        var status = await repository.GetScoreMetadataRepairStatusAsync(cancellationToken);
        RepairScoreMetadataMenuText = status.IsComplete
            ? "Score metadata is up to date"
            : $"Repair cached score data ({status.PendingPlayers:N0} players)";
    }

    private void openUrl(string? url)
    {
        if (string.IsNullOrWhiteSpace(url))
            return;
        try
        {
            urlLauncher.Open(url);
        }
        catch (Exception exception)
        {
            ValidationMessage = $"The link could not be opened: {exception.Message}";
            OnPropertyChanged(nameof(HasValidationError));
        }
    }

    private void copyUrl(string? url)
    {
        if (string.IsNullOrWhiteSpace(url))
            return;
        try
        {
            urlLauncher.Copy(url);
            StatusText = "Beatmap URL copied.";
        }
        catch (Exception exception)
        {
            ValidationMessage = $"The link could not be copied: {exception.Message}";
            OnPropertyChanged(nameof(HasValidationError));
        }
    }

    private void notifyCommandStates()
    {
        SearchCommand.NotifyCanExecuteChanged();
        BuildFullIndexCommand.NotifyCanExecuteChanged();
        RepairScoreMetadataCommand.NotifyCanExecuteChanged();
        FetchCacheCommand.NotifyCanExecuteChanged();
        CancelCommand.NotifyCanExecuteChanged();
        ClearFiltersCommand.NotifyCanExecuteChanged();
        SaveCredentialsCommand.NotifyCanExecuteChanged();
        TestConnectionCommand.NotifyCanExecuteChanged();
        RemoveCredentialsCommand.NotifyCanExecuteChanged();
    }

    private void post(Action action)
    {
        if (synchronizationContext is null)
            action();
        else
            synchronizationContext.Post(_ => action(), null);
    }

    private static int? parseInt(string text, string name, ICollection<string> errors)
    {
        if (string.IsNullOrWhiteSpace(text))
            return null;
        if (int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value))
            return value;
        errors.Add($"{name} must be a whole number.");
        return null;
    }

    private static string formatDuration(TimeSpan duration)
    {
        if (duration < TimeSpan.Zero)
            duration = TimeSpan.Zero;
        if (duration.TotalHours >= 1)
            return $"{(int)duration.TotalHours}h {duration.Minutes}m";
        if (duration.TotalMinutes >= 1)
            return $"{Math.Max(1, (int)Math.Ceiling(duration.TotalMinutes))}m";
        return $"{Math.Max(1, (int)Math.Ceiling(duration.TotalSeconds))}s";
    }

    private static string formatDownloadBytes(double bytes)
    {
        if (bytes >= 1024d * 1024 * 1024)
            return $"{bytes / (1024d * 1024 * 1024):0.##} GB";
        if (bytes >= 1024d * 1024)
            return $"{bytes / (1024d * 1024):0.##} MB";
        if (bytes >= 1024d)
            return $"{bytes / 1024d:0.##} KB";
        return $"{bytes:0} B";
    }

    private static double? parseDouble(string text, string name, ICollection<string> errors)
    {
        if (string.IsNullOrWhiteSpace(text))
            return null;
        if (double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var value))
            return value;
        errors.Add($"{name} must be a number.");
        return null;
    }

    private static double? parseDuration(string text, string name, ICollection<string> errors)
    {
        if (string.IsNullOrWhiteSpace(text))
            return null;
        if (double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var seconds))
            return seconds;
        if (TimeSpan.TryParseExact(text, [@"m\:ss", @"h\:mm\:ss"], CultureInfo.InvariantCulture, out var duration))
            return duration.TotalSeconds;
        errors.Add($"{name} must use seconds, m:ss, or h:mm:ss.");
        return null;
    }

    private static DateTimeOffset? parseDate(
        string text,
        string name,
        ICollection<string> errors,
        bool upperBound)
    {
        if (string.IsNullOrWhiteSpace(text))
            return null;
        if (DateTimeOffset.TryParse(
                text,
                CultureInfo.CurrentCulture,
                DateTimeStyles.AssumeLocal,
                out var date))
        {
            if (upperBound && date.TimeOfDay == TimeSpan.Zero && !text.Contains(':'))
                return date.AddDays(1).AddTicks(-1);
            return date;
        }
        errors.Add($"{name} must be a valid date.");
        return null;
    }
}
