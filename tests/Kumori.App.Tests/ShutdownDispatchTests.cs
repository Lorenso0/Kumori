using System.Windows.Threading;
using Xunit;

namespace Kumori.App.Tests;

public sealed class ShutdownDispatchTests
{
    [Fact]
    public void Shutdown_starts_without_waiting_for_render_loop_idle()
    {
        Assert.Equal(DispatcherPriority.Normal, App.ShutdownStartPriority);
        Assert.True(
            App.ShutdownStartPriority > DispatcherPriority.ContextIdle);
    }
}
