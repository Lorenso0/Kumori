using Xunit;

namespace Kumori.App.Tests;

public sealed class StartupUpdateSchedulingTests
{
    [Fact]
    public async Task AutomaticCheckRunsAfterHydrationWithoutAGameplayMaintenanceGate()
    {
        var hydration = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var checkStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        Task scheduled = App.RunKumoriStartupUpdateCheckAsync(
            hydration.Task,
            _ =>
            {
                checkStarted.TrySetResult();
                return Task.CompletedTask;
            },
            CancellationToken.None);

        Assert.False(checkStarted.Task.IsCompleted);
        hydration.TrySetResult();

        await scheduled.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.True(checkStarted.Task.IsCompletedSuccessfully);
    }
}
