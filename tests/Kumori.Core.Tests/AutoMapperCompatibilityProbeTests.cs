using Kumori.ReplayViewer;
using Xunit;

namespace Kumori.Core.Tests;

public sealed class AutoMapperCompatibilityProbeTests
{
    [Fact]
    public void SecuredAutoMapperInitializesOsuRealmMappings()
    {
        AutoMapperCompatibilityResult result = AutoMapperCompatibilityProbe.Run();

        Assert.True(result.Compatible, result.Error);
        Assert.StartsWith("15.1.1", result.Version, StringComparison.Ordinal);
    }
}
