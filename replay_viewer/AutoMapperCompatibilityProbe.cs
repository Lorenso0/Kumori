using System.Reflection;
using System.Runtime.CompilerServices;
using AutoMapper;
using osu.Game.Database;

namespace Kumori.ReplayViewer;

internal sealed record AutoMapperCompatibilityResult(
    bool Compatible,
    string Version,
    string? Error);

internal static class AutoMapperCompatibilityProbe
{
    private const string PatchMarkerType = "Kumori.Build.AutoMapperCompatibilityMarker";

    internal static AutoMapperCompatibilityResult Run()
    {
        Assembly autoMapperAssembly = typeof(MapperConfiguration).Assembly;
        string version = autoMapperAssembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?
            .InformationalVersion ?? autoMapperAssembly.GetName().Version?.ToString() ?? "unknown";

        if (typeof(MapperConfiguration).GetConstructor(
                [typeof(Action<IMapperConfigurationExpression>)]) is not null)
        {
            return new AutoMapperCompatibilityResult(
                false,
                version,
                "The vulnerable legacy AutoMapper constructor is still present.");
        }

        Assembly osuGameAssembly = typeof(RealmObjectExtensions).Assembly;
        if (osuGameAssembly.GetType(PatchMarkerType, throwOnError: false) is null)
        {
            return new AutoMapperCompatibilityResult(
                false,
                version,
                "osu.Game.dll is missing Kumori's secured AutoMapper compatibility patch.");
        }

        try
        {
            // Force osu!'s three Realm mapper configurations to initialize. This proves
            // the patched constructor calls and the newer AutoMapper API work together.
            RuntimeHelpers.RunClassConstructor(typeof(RealmObjectExtensions).TypeHandle);
            return new AutoMapperCompatibilityResult(true, version, null);
        }
        catch (Exception ex)
        {
            return new AutoMapperCompatibilityResult(
                false,
                version,
                $"osu! mapper initialization failed: {ex.GetBaseException().Message}");
        }
    }
}
