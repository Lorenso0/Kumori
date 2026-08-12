using System.Runtime.CompilerServices;

namespace Kumori.App.Tests;

internal static class RealmSchemaTestBootstrap
{
    [ModuleInitializer]
    internal static void Initialize()
    {
        // This test assembly covers both the WPF tracking process and the
        // separate osu!-native process. Their Realm model assemblies normally
        // never coexist, but xUnit loads tests in parallel in one process.
        // Initialise both schemas deterministically before any test opens a
        // Realm or races another module initializer.
        RuntimeHelpers.RunModuleConstructor(
            typeof(Kumori.Tracking.LazerSkin).Module.ModuleHandle);
        RuntimeHelpers.RunModuleConstructor(
            typeof(osu.Game.Database.RealmAccess).Module.ModuleHandle);
    }
}
