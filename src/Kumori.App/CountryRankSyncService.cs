using Kumori.App.FarmFinder;
using Kumori.Core;
using Kumori.FarmFinder;
using Kumori.Storage;
using Serilog;

namespace Kumori.App;

/// <summary>Periodically enriches tosu profile snapshots with osu! API country rank.</summary>
internal sealed class CountryRankSyncService(
    ProfileTelemetryStore profiles,
    TimeSpan? refreshInterval = null,
    TimeSpan? identityRetryInterval = null)
{
    private static readonly TimeSpan minimumRefreshInterval = TimeSpan.FromMinutes(1);
    private readonly TimeSpan refreshInterval = refreshInterval ?? TimeSpan.FromHours(1);
    private readonly TimeSpan identityRetryInterval = identityRetryInterval ?? TimeSpan.FromSeconds(30);

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        var credentials = new WindowsCredentialsStore(AppPaths.FarmFinderCredentialsFile);
        using var api = new OsuApiClient(
            credentials,
            new OsuRankedModCatalog(),
            new ClockRateCalculator());
        using var profileUpdate = new SemaphoreSlim(0, 1);
        var recordingCountryRank = 0;
        var refreshPending = 0;
        void RequestProfileRefresh()
        {
            if (Volatile.Read(ref recordingCountryRank) == 0
                && Interlocked.Exchange(ref refreshPending, 1) == 0)
                profileUpdate.Release();
        }
        profiles.ProfileUpdated += RequestProfileRefresh;
        var nextApiRefreshAt = DateTimeOffset.MinValue;

        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                var cooldown = nextApiRefreshAt - DateTimeOffset.UtcNow;
                if (cooldown > TimeSpan.Zero)
                    await Task.Delay(cooldown, cancellationToken);

                var delay = refreshInterval;
                try
                {
                    var identity = profiles.GetCurrentIdentity();
                    if (identity is null)
                    {
                        delay = identityRetryInterval;
                    }
                    else if ((await credentials.LoadAsync(cancellationToken))?.IsConfigured != true)
                    {
                        // Farm Finder owns these credentials. Recheck so saving
                        // them while Kumori is open enables rank capture.
                        delay = TimeSpan.FromMinutes(5);
                    }
                    else
                    {
                        var stats = await api.GetUserProfileStatsAsync(
                            identity.PlayerId,
                            cancellationToken);
                        nextApiRefreshAt = DateTimeOffset.UtcNow + minimumRefreshInterval;
                        if (stats.CountryRank is > 0)
                        {
                            Interlocked.Exchange(ref recordingCountryRank, 1);
                            try
                            {
                                profiles.RecordCountryRank(
                                    identity.PlayerId,
                                    stats.CountryRank.Value,
                                    stats.CountryCode,
                                    DateTimeOffset.UtcNow,
                                    cancellationToken);
                            }
                            finally
                            {
                                Interlocked.Exchange(ref recordingCountryRank, 0);
                            }
                        }
                    }
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception exception)
                {
                    Log.Debug(exception, "Country-rank sync will retry later");
                }

                // Normal rank/profile changes wake this wait immediately. The
                // hourly timeout remains a fallback for server-side rank moves
                // that happen without a local profile change.
                await profileUpdate.WaitAsync(delay, cancellationToken);
                Interlocked.Exchange(ref refreshPending, 0);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        finally
        {
            profiles.ProfileUpdated -= RequestProfileRefresh;
        }
    }
}
