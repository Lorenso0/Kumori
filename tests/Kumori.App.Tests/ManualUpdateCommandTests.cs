using Kumori.App.ViewModels;
using Kumori.Core.Settings;
using Kumori.Core.State;
using Kumori.Storage;
using Xunit;

namespace Kumori.App.Tests;

public sealed class ManualUpdateCommandTests
{
    [Fact]
    public async Task HeaderAndSettingsCommandsUseApplicationInstallerFlow()
    {
        var directory = Directory.CreateTempSubdirectory();
        try
        {
            var database = Path.Combine(directory.FullName, "updates.sqlite3");
            var settings = new SettingsService(
                Path.Combine(directory.FullName, "settings.v2.json"),
                Path.Combine(directory.FullName, "legacy.json"));
            settings.Load();
            var factory = new SqliteConnectionFactory(database, readOnly: false);
            var calls = 0;
            Task CheckForUpdates()
            {
                calls++;
                return Task.CompletedTask;
            }

            var viewModel = new MainViewModel(
                new AppStateStore(),
                new AttemptRepository(factory),
                new AttemptDetailsRepository(factory),
                new AnalyticsRepository(factory),
                settings,
                maintenance: new TrackingMaintenanceRepository(factory),
                sessions: new SessionRepository(factory),
                checkForUpdates: CheckForUpdates);

            await viewModel.OpenAvailableUpdateCommand.ExecuteAsync(null);
            await viewModel.CheckForUpdatesCommand.ExecuteAsync(null);

            Assert.Equal(2, calls);
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }
}
