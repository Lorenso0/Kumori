using Kumori.App.FarmFinder;
using Kumori.FarmFinder;
using osu.Game.Rulesets.Osu;
using Xunit;

namespace Kumori.App.Tests;

public sealed class FarmFinderRankedModCatalogTests
{
    private readonly OsuRankedModCatalog catalog = new();

    [Fact]
    public void Catalog_IsPinnedOsuStandardUserPlayableRankedDefaultsPlusNm()
    {
        var expected = new OsuRuleset().CreateAllMods()
            .Where(mod => mod.UserPlayable && mod.Ranked)
            .Select(mod => mod.Acronym)
            .Append("NM")
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var actual = catalog.GetRankedMods()
            .Select(mod => mod.Acronym)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        Assert.Equal(expected, actual);
    }

    [Theory]
    [InlineData("RX")]
    [InlineData("AP")]
    [InlineData("DA")]
    [InlineData("NM")]
    [InlineData("not-a-mod")]
    public void UnrankedAndUnknownMods_FailClosed(string acronym)
    {
        Assert.False(catalog.Evaluate(new FarmMod(acronym)).IsEligible);
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("{\"no_slider_head_accuracy\":false}")]
    public void Classic_IsEligibleAsHiddenWildcardException(string settings)
    {
        var result = catalog.Evaluate(new FarmMod("CL", settings));

        Assert.True(result.IsEligible);
        Assert.Equal("CL", result.Acronym);
        Assert.DoesNotContain(
            catalog.GetRankedMods(),
            mod => mod.Acronym.Equals("CL", StringComparison.OrdinalIgnoreCase));
    }

    [Theory]
    [InlineData("HD", "{\"unknown_setting\":true}")]
    [InlineData("CL", "{\"unknown_setting\":true}")]
    [InlineData("DT", "{malformed")]
    [InlineData("DT", "{\"speed_change\":\"not-a-number\"}")]
    public void UnknownMalformedOrUnparseableSettings_FailClosed(string acronym, string settings)
    {
        Assert.False(catalog.Evaluate(new FarmMod(acronym, settings)).IsEligible);
    }

    [Theory]
    [InlineData("DT", "{\"speed_change\":1.25}")]
    [InlineData("HT", "{\"speed_change\":0.8}")]
    public void CustomSpeed_IsUnranked(string acronym, string settings)
    {
        Assert.False(catalog.Evaluate(new FarmMod(acronym, settings)).IsEligible);
    }

    [Theory]
    [InlineData("DT", "{\"adjust_pitch\":true}")]
    [InlineData("HT", "{\"adjust_pitch\":true}")]
    [InlineData("SD", "{\"fail_on_slider_tail\":true}")]
    [InlineData("PF", "{\"restart\":false}")]
    [InlineData("AC", "{\"minimum_accuracy\":0.95,\"accuracy_judge_mode\":\"Standard\"}")]
    public void RankedSettings_RemainEligible(string acronym, string settings)
    {
        var result = catalog.Evaluate(new FarmMod(acronym, settings));

        Assert.True(result.IsEligible);
        Assert.Equal(acronym, result.Acronym);
    }
}

public sealed class WindowsCredentialsStoreTests
{
    [Fact]
    public async Task Credentials_RoundTripThroughCurrentUserEncryptionAndCanBeRemoved()
    {
        var path = Path.Combine(
            Path.GetTempPath(),
            $"kumori-farm-credentials-{Guid.NewGuid():N}.bin");
        try
        {
            var store = new WindowsCredentialsStore(path);
            var expected = new OsuApiCredentials(12345, "a-secret-that-must-not-be-plaintext");

            await store.SaveAsync(expected);
            var bytes = await File.ReadAllBytesAsync(path);
            Assert.DoesNotContain(
                expected.ClientSecret,
                System.Text.Encoding.UTF8.GetString(bytes),
                StringComparison.Ordinal);
            Assert.Equal(expected, await store.LoadAsync());

            await store.DeleteAsync();
            Assert.False(File.Exists(path));
        }
        finally
        {
            if (File.Exists(path))
                File.Delete(path);
            if (File.Exists(path + ".tmp"))
                File.Delete(path + ".tmp");
        }
    }
}
