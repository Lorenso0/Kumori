using Kumori.Native;
using Xunit;

namespace Kumori.App.Tests;

public sealed class StartupRegistrationTests
{
    [Fact]
    public void BuildCommandAddsMinimizedArgumentOnlyWhenRequested()
    {
        const string executable = @"C:\Program Files\Kumori\Kumori.exe";

        Assert.Equal(
            "\"C:\\Program Files\\Kumori\\Kumori.exe\"",
            StartupRegistration.BuildCommand(executable, startMinimized: false));
        Assert.Equal(
            "\"C:\\Program Files\\Kumori\\Kumori.exe\" --start-minimized",
            StartupRegistration.BuildCommand(executable, startMinimized: true));
    }

    [Theory]
    [InlineData("\"C:\\Program Files\\Kumori\\Kumori.exe\" --start-minimized", @"C:\Program Files\Kumori\Kumori.exe")]
    [InlineData(@"C:\Kumori\Kumori.exe --start-minimized", @"C:\Kumori\Kumori.exe")]
    [InlineData("\"D:\\Apps\\Kumori.exe\"", @"D:\Apps\Kumori.exe")]
    public void ParseExecutablePathReturnsRegisteredExecutable(string command, string expected)
    {
        Assert.Equal(expected, StartupRegistration.ParseExecutablePath(command));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void ParseExecutablePathReturnsNullForMissingRegistration(string? command)
    {
        Assert.Null(StartupRegistration.ParseExecutablePath(command));
    }
}
