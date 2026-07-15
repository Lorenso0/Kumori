using Kumori.App;
using Xunit;

namespace Kumori.App.Tests;

public sealed class TosuLifecyclePolicyTests
{
    [Fact]
    public void RecoveryNeverLaunchesTosuBeforeOsu()
    {
        Assert.False(App.ShouldLaunchTosuForRecovery(osuRunning: false));
        Assert.True(App.ShouldLaunchTosuForRecovery(osuRunning: true));
    }
}
