using Kumori.ReplayViewer;
using Newtonsoft.Json;
using osu.Game.Skinning;
using Xunit;

namespace Kumori.Core.Tests;

public sealed class ReplayViewerSkinImportTests : IDisposable
{
    private readonly string root = Path.Combine(
        Path.GetTempPath(),
        $"kumori-viewer-skin-{Guid.NewGuid():N}");

    [Fact]
    public void PrepareSkinImportPath_CopiesArchiveInsteadOfGivingImporterLibrarySource()
    {
        Directory.CreateDirectory(root);
        string source = Path.Combine(root, "persistent.osk");
        File.WriteAllBytes(source, [1, 2, 3, 4]);

        string importPath = ReplayViewerGame.PrepareSkinImportPath(source);
        try
        {
            Assert.NotEqual(source, importPath);
            Assert.Equal(File.ReadAllBytes(source), File.ReadAllBytes(importPath));

            File.Delete(importPath);
            Assert.True(File.Exists(source));
        }
        finally
        {
            if (File.Exists(importPath)) File.Delete(importPath);
        }
    }

    [Fact]
    public void SkinLayoutDependencies_ResolveRulesetOwnedHudComponents()
    {
        ReplayViewerGame.EnsureSkinLayoutDependenciesLoaded();

        const string aimErrorMeter =
            "osu.Game.Rulesets.Osu.HUD.AimErrorMeter, osu.Game.Rulesets.Osu, Version=1.0.0.0, Culture=neutral, PublicKeyToken=null";
        Assert.NotNull(Type.GetType(aimErrorMeter, throwOnError: false));
    }

    [Fact]
    public void RulesetAssembly_UsesLazerSkinCompatibleIdentity()
    {
        ReplayViewerGame.EnsureSkinLayoutDependenciesLoaded();

        Assert.Equal(
            new Version(1, 0, 0, 0),
            typeof(osu.Game.Rulesets.Osu.OsuRuleset).Assembly.GetName().Version);
    }

    [Fact]
    public void SkinLayoutDependencies_DeserializeRulesetOwnedHudComponents()
    {
        ReplayViewerGame.EnsureSkinLayoutDependenciesLoaded();

        const string layout = """
        {
          "Version": 1,
          "DrawableInfo": {
            "osu": [{
              "Type": "osu.Game.Rulesets.Osu.HUD.AimErrorMeter, osu.Game.Rulesets.Osu, Version=1.0.0.0, Culture=neutral, PublicKeyToken=null",
              "Position": { "x": 0, "y": 0 },
              "Rotation": 0,
              "Scale": { "x": 1, "y": 1 },
              "Width": null,
              "Height": null,
              "Anchor": 0,
              "Origin": 0,
              "UsesFixedAnchor": false,
              "Settings": {},
              "Children": []
            }]
          }
        }
        """;

        var deserialized = JsonConvert.DeserializeObject<SkinLayoutInfo>(layout);

        Assert.Equal("AimErrorMeter", Assert.Single(deserialized!.DrawableInfo["osu"]).Type.Name);
    }

    public void Dispose()
    {
        if (Directory.Exists(root))
            Directory.Delete(root, recursive: true);
    }
}
