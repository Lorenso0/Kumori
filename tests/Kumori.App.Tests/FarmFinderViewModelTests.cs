using System.Net;
using System.Collections.Concurrent;
using Kumori.App.FarmFinder;
using Kumori.App.ViewModels;
using Kumori.Core.Settings;
using Kumori.FarmFinder;
using Kumori.Storage;
using Xunit;

namespace Kumori.App.Tests;

public sealed class FarmFinderViewModelTests : IDisposable
{
    private readonly string databasePath =
        Path.Combine(Path.GetTempPath(), $"kumori-farm-vm-{Guid.NewGuid():N}.sqlite3");

    [Fact]
    public async Task Search_ValidatesInlineBeforeCallingService()
    {
        var service = new FakeService();
        using var viewModel = Create(service);
        await viewModel.Initialization;
        viewModel.MinimumRankText = "200";
        viewModel.MaximumRankText = "100";

        await viewModel.SearchAsync();

        Assert.Equal(0, service.SearchCalls);
        Assert.True(viewModel.HasValidationError);
        Assert.Contains("Global rank", viewModel.ValidationMessage);
    }

    [Fact]
    public async Task SelectingALegacyCachedResultLoadsMapStatsWithoutRebuildingTheIndex()
    {
        var service = new FakeService
        {
            SearchResult = new FarmFinderSearchResult(
                [Result()],
                new CoverageSummary(0, 0, 0, 0, 0, 0, 0, 0, null, null, null)),
        };
        var metadata = new FakeMetadataProvider();
        using var viewModel = Create(service, metadataProvider: metadata);
        await viewModel.Initialization;

        await viewModel.SearchAsync();
        await WaitForAsync(() => viewModel.SelectedResult?.Beatmap.CircleSize is not null);

        Assert.Equal(1, metadata.Calls);
        Assert.Equal(4, viewModel.SelectedResult!.Beatmap.CircleSize);
        Assert.Equal(9, viewModel.SelectedResult.Beatmap.ApproachRate);
        Assert.False(viewModel.IsLoadingSelectedMapDetails);
        Assert.Empty(viewModel.SelectedMapDetailsStatus);
        Assert.Equal(0, service.UpdateCalls);
    }

    [Fact]
    public async Task Update_AllowsCountryUnionRangeAndConfirms()
    {
        var service = new FakeService();
        using var viewModel = Create(service);
        await viewModel.Initialization;
        viewModel.ClientIdText = "123";
        viewModel.ClientSecret = "secret";
        await viewModel.SaveCredentialsAsync();
        viewModel.MinimumRankText = "20000";
        viewModel.MaximumRankText = "60000";
        var confirmations = 0;
        viewModel.ConfirmUpdateAsync = (_, _) =>
        {
            confirmations++;
            return Task.FromResult(true);
        };

        await viewModel.UpdateAsync();

        Assert.Equal(1, service.UpdateCalls);
        Assert.Equal(1, confirmations);
        Assert.Empty(viewModel.ValidationMessage);
    }

    [Fact]
    public async Task RepairScoreMetadataConfirmsAndRunsWithoutOsuCredentials()
    {
        var repository = new FarmFinderRepository(databasePath);
        var now = DateTimeOffset.UtcNow;
        var job = await repository.BeginOrResumeJobAsync(1, 100);
        await repository.UpsertRankingPlayersAsync(
            job.Id,
            [new FarmPlayer(1, "Player", 10, 12_000, now)]);
        var service = new FakeService();
        using var viewModel = Create(service);
        await viewModel.Initialization;
        FarmScoreMetadataRepairStatus? confirmation = null;
        viewModel.ConfirmMetadataRepairAsync = status =>
        {
            confirmation = status;
            return Task.FromResult(true);
        };

        await viewModel.RepairScoreMetadataAsync();

        Assert.Equal(1, confirmation?.PendingPlayers);
        Assert.Equal(1, service.RepairCalls);
        Assert.Contains("1 player", viewModel.StatusText);
        Assert.Empty(viewModel.ValidationMessage);
    }

    [Fact]
    public async Task ModFiltersExposeOnlyPrimaryModsAndDisableHiddenWildcard()
    {
        using var viewModel = Create(new FakeService());
        await viewModel.Initialization;
        Assert.Equal(
            ["DT", "HD", "HR", "FL"],
            viewModel.Mods.Select(mod => mod.Acronym));

        var hd = viewModel.Mods.Single(mod => mod.Acronym == "HD");

        viewModel.HiddenWildcard = true;
        hd.Requirement = ModRequirement.Excluded;
        Assert.False(viewModel.HiddenWildcard);
        viewModel.HiddenWildcard = true;
        Assert.False(viewModel.HiddenWildcard);
    }

    [Fact]
    public async Task OptionalFilterFieldsStartEmpty()
    {
        using var viewModel = Create(new FakeService());
        await viewModel.Initialization;

        Assert.Empty(viewModel.MinimumRankText);
        Assert.Empty(viewModel.MaximumRankText);
        Assert.Empty(viewModel.MinimumPpText);
        Assert.Empty(viewModel.MaximumPpText);
        Assert.Empty(viewModel.MinimumBpmText);
        Assert.Empty(viewModel.MaximumBpmText);
        Assert.Empty(viewModel.MinimumLengthText);
        Assert.Empty(viewModel.MaximumLengthText);
        Assert.Empty(viewModel.MinimumStarsText);
        Assert.Empty(viewModel.MaximumStarsText);
        Assert.Empty(viewModel.MinimumPlayersText);
    }

    [Fact]
    public async Task NightcoreAlwaysMatchesTheDoubleTimeFamily()
    {
        var service = new FakeService();
        using var viewModel = Create(service);
        await viewModel.Initialization;

        viewModel.TreatNightcoreAsDoubleTime = false;
        await viewModel.SearchAsync();

        Assert.True(viewModel.TreatNightcoreAsDoubleTime);
        Assert.True(service.LastQuery?.TreatNightcoreAsDoubleTime);
    }

    [Fact]
    public async Task FilterValuesPersistAndRestore()
    {
        var root = Path.Combine(Path.GetTempPath(), $"kumori-farm-settings-{Guid.NewGuid():N}");
        var settingsFile = Path.Combine(root, "settings.v2.json");
        var legacyFile = Path.Combine(root, "settings.json");
        try
        {
            var settings = new SettingsService(settingsFile, legacyFile);
            settings.Load();
            using (var viewModel = Create(new FakeService(), settings))
            {
                await viewModel.Initialization;
                viewModel.MinimumPpText = "300";
                viewModel.TextSearch = "mapper";
                viewModel.MatchMode = ModMatchMode.Exact;
                viewModel.TreatNightcoreAsDoubleTime = false;
                viewModel.Mods.Single(mod => mod.Acronym == "HD").Requirement = ModRequirement.Wildcard;
            }

            var reopened = new SettingsService(settingsFile, legacyFile);
            reopened.Load();
            using var restored = Create(new FakeService(), reopened);
            await restored.Initialization;

            Assert.Equal("300", restored.MinimumPpText);
            Assert.Empty(restored.TextSearch);
            Assert.Equal(ModMatchMode.ContainsRequired, restored.MatchMode);
            Assert.True(restored.TreatNightcoreAsDoubleTime);
            Assert.Equal(ModRequirement.Wildcard, restored.Mods.Single(mod => mod.Acronym == "HD").Requirement);
        }
        finally
        {
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task SearchScopesExactMatchingToVisiblePrimaryMods()
    {
        var service = new FakeService();
        using var viewModel = Create(service);
        await viewModel.Initialization;
        viewModel.MatchMode = ModMatchMode.Exact;
        viewModel.Mods.Single(mod => mod.Acronym == "DT").Requirement =
            ModRequirement.Required;

        await viewModel.SearchAsync();

        Assert.Equal(
            ["DT", "HD", "HR", "FL"],
            service.LastQuery?.ExactModScope);
        Assert.Equal(
            [new FarmModFilter("DT", ModRequirement.Required)],
            service.LastQuery?.Mods);
    }

    [Fact]
    public void ModTileCyclesThroughEveryState()
    {
        var mod = new FarmModOptionViewModel(
            new RankedModDescriptor("DT", "Double Time"));

        Assert.Equal(ModRequirement.Ignore, mod.Requirement);
        Assert.Equal("Any", mod.RequirementLabel);

        mod.CycleRequirementCommand.Execute(null);
        Assert.Equal(ModRequirement.Required, mod.Requirement);
        Assert.Equal("Include", mod.RequirementLabel);

        mod.CycleRequirementCommand.Execute(null);
        Assert.Equal(ModRequirement.Excluded, mod.Requirement);
        Assert.Equal("Exclude", mod.RequirementLabel);

        mod.CycleRequirementCommand.Execute(null);
        Assert.Equal(ModRequirement.Wildcard, mod.Requirement);
        Assert.Equal("Wildcard", mod.RequirementLabel);

        mod.CycleRequirementCommand.Execute(null);
        Assert.Equal(ModRequirement.Ignore, mod.Requirement);
        Assert.Equal("Any", mod.RequirementLabel);
    }

    [Fact]
    public async Task Search_UpdatesResultsCoverageAndPassesSelectedSort()
    {
        var service = new FakeService
        {
            SearchResult = new FarmFinderSearchResult(
                [Result()],
                new CoverageSummary(
                    100, 80, 80, 0, 2, 500, 48, 1,
                    DateTimeOffset.Parse("2026-01-01"), 1, 100,
                    [new CountryCoverageGap("US", 51_207, 60_000)])),
        };
        using var viewModel = Create(service);
        await viewModel.Initialization;
        viewModel.SortField = FarmSortField.EffectiveBpm;
        viewModel.SortDirection = FarmSortDirection.Ascending;

        await viewModel.SearchAsync();

        Assert.Single(viewModel.Results);
        Assert.Same(viewModel.Results[0], viewModel.SelectedResult);
        Assert.True(viewModel.HasResults);
        Assert.Contains("80 examined / 100 available", viewModel.CoverageText);
        Assert.Contains("2 failed", viewModel.CoverageText);
        Assert.Contains("US after #51", viewModel.CoverageText);
        Assert.Equal(FarmSortField.EffectiveBpm, service.LastQuery?.SortField);
        Assert.Equal(FarmSortDirection.Ascending, service.LastQuery?.SortDirection);
        Assert.False(viewModel.IsBusy);
    }

    [Fact]
    public async Task Cancel_TransitionsToResumableCancelledState()
    {
        var service = new FakeService { WaitForCancellation = true };
        using var viewModel = Create(service);
        await viewModel.Initialization;

        var search = viewModel.SearchAsync();
        await service.Started.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.True(viewModel.IsBusy);
        viewModel.Cancel();
        await search;

        Assert.False(viewModel.IsBusy);
        Assert.Contains("cancelled", viewModel.StatusText, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task BuildFullIndex_ShowsDetailedHinamizawaProgress()
    {
        var service = new FakeService();
        using var viewModel = Create(service);
        await viewModel.Initialization;
        viewModel.ClientIdText = "123";
        viewModel.ClientSecret = "secret";
        viewModel.MinimumRankText = "1";
        viewModel.MaximumRankText = "1000";
        await viewModel.SaveCredentialsAsync();
        var progressStates = new ConcurrentQueue<string>();
        viewModel.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName == nameof(FarmFinderViewModel.ProgressDetailsText))
                progressStates.Enqueue(viewModel.ProgressDetailsText);
        };

        await viewModel.BuildFullIndexAsync();
        await WaitForAsync(
            () => progressStates.Any(value =>
                value.Contains("downloaded", StringComparison.Ordinal) &&
                value.Contains("failed", StringComparison.Ordinal)));

        Assert.Equal(1, service.UpdateCalls);
        Assert.Contains(
            progressStates.ToArray(),
            value => value.Contains("downloaded", StringComparison.Ordinal) &&
                     value.Contains("failed", StringComparison.Ordinal));
        Assert.Same(viewModel.BuildFullIndexCommand, viewModel.UpdateCommand);
    }

    [Fact]
    public async Task FetchCache_WhenUrlIsPending_ShowsAUsefulStatus()
    {
        using var viewModel = Create(
            new FakeService(),
            cacheInstaller: new FakeCacheInstaller { IsConfigured = false });
        await viewModel.Initialization;

        await viewModel.FetchCacheAsync();

        Assert.Contains(
            "URL",
            viewModel.StatusText,
            StringComparison.OrdinalIgnoreCase);
        Assert.False(viewModel.IsBusy);
    }

    [Fact]
    public async Task FetchCache_InstallsThenLoadsMatchingResults()
    {
        var service = new FakeService
        {
            SearchResult = new FarmFinderSearchResult(
                [Result()],
                new CoverageSummary(
                    100, 100, 100, 0, 0, 500, 48, 1,
                    DateTimeOffset.Parse("2026-07-30"),
                    1,
                    100)),
        };
        var installer = new FakeCacheInstaller { IsConfigured = true };
        using var viewModel = Create(service, cacheInstaller: installer);
        await viewModel.Initialization;
        var progressStates = new ConcurrentQueue<string>();
        viewModel.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName == nameof(FarmFinderViewModel.ProgressDetailsText))
                progressStates.Enqueue(viewModel.ProgressDetailsText);
        };

        await viewModel.FetchCacheAsync();
        await WaitForAsync(() => progressStates.Any(value =>
            value.Contains("MB/s", StringComparison.Ordinal)));
        await WaitForAsync(() => progressStates.Any(value =>
            value.Contains("database structure", StringComparison.Ordinal)));

        Assert.Equal(1, installer.FetchCalls);
        Assert.Equal(1, service.SearchCalls);
        Assert.Single(viewModel.Results);
        Assert.False(viewModel.IsFetchingCache);
        Assert.False(viewModel.IsBusy);
        Assert.Contains("installed", viewModel.StatusText, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task OpenBeatmapCommand_OpensTheResultInOsuDirect()
    {
        var launcher = new FakeLauncher();
        using var viewModel = Create(new FakeService(), launcher: launcher);
        await viewModel.Initialization;
        var result = Result();

        viewModel.OpenBeatmapCommand.Execute(result);

        Assert.Equal($"osu://b/{result.Beatmap.BeatmapId}", launcher.OpenedUrl);
    }

    [Fact]
    public async Task OpenBeatmapInBrowserCommand_OpensTheResultOnTheWebsite()
    {
        var launcher = new FakeLauncher();
        using var viewModel = Create(new FakeService(), launcher: launcher);
        await viewModel.Initialization;
        var result = Result();

        viewModel.OpenBeatmapInBrowserCommand.Execute(result);

        Assert.Equal(result.BeatmapUrl, launcher.OpenedUrl);
        Assert.StartsWith("https://osu.ppy.sh/beatmaps/", launcher.OpenedUrl);
    }

    private static async Task WaitForAsync(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(2);
        while (!condition() && DateTime.UtcNow < deadline)
            await Task.Delay(10);
        Assert.True(condition(), "The expected asynchronous progress update was not delivered.");
    }

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        if (File.Exists(databasePath))
            File.Delete(databasePath);
        if (File.Exists(databasePath + "-wal"))
            File.Delete(databasePath + "-wal");
        if (File.Exists(databasePath + "-shm"))
            File.Delete(databasePath + "-shm");
    }

    private FarmFinderViewModel Create(
        FakeService service,
        SettingsService? settings = null,
        IFarmFinderCacheInstaller? cacheInstaller = null,
        FakeLauncher? launcher = null,
        IFarmBeatmapMetadataProvider? metadataProvider = null)
    {
        var repository = new FarmFinderRepository(databasePath);
        var credentials = new MemoryCredentials();
        var api = new OsuApiClient(
            credentials,
            new FakeCatalog(),
            new ClockRateCalculator(),
            new HttpClient(new NeverHandler()));
        return new FarmFinderViewModel(
            service,
            repository,
            credentials,
            api,
            new FakeCatalog(),
            launcher ?? new FakeLauncher(),
            settings,
            cacheInstaller,
            metadataProvider);
    }

    private static FarmMapResult Result()
    {
        var date = DateTimeOffset.Parse("2026-01-01");
        return new FarmMapResult
        {
            Beatmap = new FarmBeatmap(
                1, 1, "Artist", "Title", "Insane", "Mapper",
                180, 100, 120, 6, "ranked", date, ""),
            NormalizedMods = "DT",
            ClockRate = 1.5,
            UniquePlayers = 48,
            CohortPercentage = 60,
            AveragePp = 300,
            MinimumPp = 250,
            MaximumPp = 350,
            EffectiveBpm = 270,
            EffectiveLengthSeconds = 100d / 1.5,
            MedianAccuracy = .98,
            AverageMissCount = .2,
            FullComboCount = 40,
            FullComboPercentage = 83.3,
            MedianPlayerRank = 50,
            EarliestScoreDate = date,
            MostRecentScoreDate = date,
            Players = [],
        };
    }

    private sealed class FakeService : IFarmFinderService
    {
        public int SearchCalls { get; private set; }
        public int UpdateCalls { get; private set; }
        public int RepairCalls { get; private set; }
        public FarmFinderQuery? LastQuery { get; private set; }
        public bool WaitForCancellation { get; init; }
        public TaskCompletionSource Started { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public FarmFinderSearchResult SearchResult { get; init; } =
            new([], new CoverageSummary(0, 0, 0, 0, 0, 0, 0, 0, null, null, null));

        public async Task<FarmFinderSearchResult> SearchCachedAsync(
            FarmFinderQuery query,
            IProgress<FarmFinderProgress>? progress = null,
            CancellationToken cancellationToken = default)
        {
            SearchCalls++;
            LastQuery = query;
            Started.TrySetResult();
            if (WaitForCancellation)
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            progress?.Report(new FarmFinderProgress(1, 1, "Done"));
            return SearchResult;
        }

        public Task<CoverageSummary> UpdateIndexAsync(
            FarmFinderQuery query,
            bool forceRefresh,
            IProgress<FarmFinderProgress>? progress = null,
            CancellationToken cancellationToken = default)
        {
            UpdateCalls++;
            progress?.Report(new FarmFinderProgress(
                50,
                100,
                "Indexing from Hinamizawa…",
                PlayersLoadedFromCache: 10,
                PlayersFetched: 38,
                ScoresExamined: 3_800,
                PlayersFailed: 2,
                Phase: FarmFinderProgressPhase.FetchingScores,
                SourceName: "Hinamizawa"));
            return Task.FromResult(SearchResult.Coverage);
        }

        public Task<FarmScoreMetadataRepairResult> RepairScoreMetadataAsync(
            IProgress<FarmFinderProgress>? progress = null,
            CancellationToken cancellationToken = default)
        {
            RepairCalls++;
            progress?.Report(new FarmFinderProgress(
                1,
                1,
                "Score metadata repaired.",
                PlayersFetched: 1,
                ScoresExamined: 100,
                Phase: FarmFinderProgressPhase.Completed,
                SourceName: "Hinamizawa"));
            return Task.FromResult(new FarmScoreMetadataRepairResult(1, 1, 0, 100));
        }
    }

    private sealed class MemoryCredentials : IOsuCredentialsStore
    {
        public Task<OsuApiCredentials?> LoadAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<OsuApiCredentials?>(null);
        public Task SaveAsync(OsuApiCredentials credentials, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;
        public Task DeleteAsync(CancellationToken cancellationToken = default) =>
            Task.CompletedTask;
    }

    private sealed class FakeCatalog : IRankedModCatalog
    {
        public IReadOnlyList<RankedModDescriptor> GetRankedMods() =>
        [
            new("NM", "No Mod"),
            new("HD", "Hidden"),
            new("DT", "Double Time"),
            new("HR", "Hard Rock"),
            new("FL", "Flashlight"),
        ];
        public RankedModEvaluation Evaluate(FarmMod mod) =>
            new(true, mod.NormalizedAcronym, mod.SettingsJson);
    }

    private sealed class FakeMetadataProvider : IFarmBeatmapMetadataProvider
    {
        public int Calls { get; private set; }

        public Task<FarmBeatmap> EnrichAsync(
            FarmBeatmap beatmap,
            CancellationToken cancellationToken = default)
        {
            Calls++;
            return Task.FromResult(beatmap with
            {
                CircleSize = 4,
                ApproachRate = 9,
                OverallDifficulty = 8,
                DrainRate = 5,
            });
        }
    }

    private sealed class FakeLauncher : IExternalUrlLauncher
    {
        public string? OpenedUrl { get; private set; }

        public void Open(string url) => OpenedUrl = url;
        public void Copy(string text) { }
    }

    private sealed class FakeCacheInstaller : IFarmFinderCacheInstaller
    {
        public bool IsConfigured { get; init; }
        public int FetchCalls { get; private set; }

        public Task<FarmCacheInstallResult> FetchAndInstallAsync(
            IProgress<FarmCacheDownloadProgress>? progress = null,
            CancellationToken cancellationToken = default)
        {
            FetchCalls++;
            progress?.Report(new FarmCacheDownloadProgress(
                5_000_000,
                10_000_000,
                "Downloading pre-built cache…",
                2_000_000,
                TimeSpan.FromSeconds(2.5)));
            progress?.Report(new FarmCacheDownloadProgress(
                1,
                1,
                "Download complete · Running final checks…",
                Detail: "Checking the database structure before installation."));
            return Task.FromResult(new FarmCacheInstallResult(
                1_000,
                new string('a', 64),
                4,
                DateTimeOffset.Parse("2026-07-30"),
                true));
        }
    }

    private sealed class NeverHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
    }
}
