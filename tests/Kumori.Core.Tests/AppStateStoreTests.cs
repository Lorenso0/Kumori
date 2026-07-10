using Kumori.Core.State;
using Xunit;

namespace Kumori.Core.Tests;

public class AppStateStoreTests
{
    [Fact]
    public void Update_PublishesNewSnapshot()
    {
        var store = new AppStateStore();
        AppState? observed = null;
        store.StateChanged += s => observed = s;

        store.Update(s => s with
        {
            Tracking = s.Tracking with { TosuConnected = true },
        });

        Assert.NotNull(observed);
        Assert.True(observed!.Tracking.TosuConnected);
        Assert.True(store.Current.Tracking.TosuConnected);
    }

    [Fact]
    public void Update_SameReference_DoesNotNotify()
    {
        var store = new AppStateStore();
        var notified = false;
        store.StateChanged += _ => notified = true;

        store.Update(s => s);

        Assert.False(notified);
    }
}
