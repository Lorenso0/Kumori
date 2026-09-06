using Kumori.Native;
using Kumori.Tracking;
using System.Buffers.Binary;
using Xunit;

namespace Kumori.App.Tests;

public sealed class LazerMemoryReadPolicyTests
{
    [Theory]
    [InlineData("2026.518.0-tachyon", "2026.518.0")]
    [InlineData("2026.525.0-lazer", "2026.525.0")]
    [InlineData("2026.525.0", "2026.525.0")]
    [InlineData("../invalid", null)]
    public void ClientOffsetsUseNumericBuildFromDependencies(string release, string? expected)
    {
        var json = System.Text.Json.JsonSerializer.Serialize(new
        {
            libraries = new Dictionary<string, object> { ["osu!/" + release] = new { } },
        });
        Assert.Equal(expected, LazerMemoryOffsets.ParseClientVersion(json));
    }

    [Fact]
    public async Task ClientLayoutAcceptsScreenStackFieldUsedByNewerBuilds()
    {
        var root = NewTempDirectory();
        try
        {
            var expected = Offsets("2026.518.0", 20);
            var path = Path.Combine(root, "offsets.json");
            await File.WriteAllTextAsync(path, OffsetJson(expected)
                .Replace("<ScreenStack>k__BackingField", "ScreenStack"));
            Assert.Equal(expected, LazerMemoryOffsets.LoadCached(path));
        }
        finally { Directory.Delete(root, recursive: true); }
    }
    [Fact]
    public async Task TachyonLoadsExactCachedLayoutWithoutDownloadingLatestLazer()
    {
        var root = NewTempDirectory();
        try
        {
            await File.WriteAllTextAsync(Path.Combine(root, "osu!.deps.json"),
                """{"libraries":{"osu!/2026.518.0-tachyon":{}}}""");
            var expected = Offsets("2026.518.0", 20);
            await File.WriteAllTextAsync(Path.Combine(root, "2026.518.0.json"), OffsetJson(expected));
            var actual = await LazerMemoryOffsets.LoadForClientAsync(
                Path.Combine(root, "osu!.exe"), root,
                (_, _) => throw new InvalidOperationException("Must use exact cache."));
            Assert.Equal(expected, actual);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public async Task MissingClientLayoutDownloadsExactBuildAndRejectsMismatchedResponse()
    {
        var root = NewTempDirectory();
        try
        {
            await File.WriteAllTextAsync(Path.Combine(root, "osu!.deps.json"),
                """{"libraries":{"osu!/2026.518.0-tachyon":{}}}""");
            var expected = Offsets("2026.518.0", 20);
            await Assert.ThrowsAsync<InvalidDataException>(() => LazerMemoryOffsets.LoadForClientAsync(
                Path.Combine(root, "osu!.exe"), root,
                (_, _) => Task.FromResult(OffsetJson(Offsets("2026.525.0", 30)))));
            Assert.False(File.Exists(Path.Combine(root, "2026.518.0.json")));
            var actual = await LazerMemoryOffsets.LoadForClientAsync(
                Path.Combine(root, "osu!.exe"), root,
                (version, _) =>
                {
                    Assert.Equal(expected.OsuVersion, version);
                    return Task.FromResult(OffsetJson(expected));
                });
            Assert.Equal(expected, actual);
            Assert.Equal(expected, LazerMemoryOffsets.LoadCached(Path.Combine(root, "2026.518.0.json")));
        }
        finally { Directory.Delete(root, recursive: true); }
    }
    [Fact]
    public void OffsetRefreshPolicyAcceptsNewerAndCorrectedButNotOlderDocuments()
    {
        var current = Offsets("2026.711.0", 10);

        Assert.False(LazerMemoryOffsetRefreshPolicy.ShouldReplace(current, current));
        Assert.True(LazerMemoryOffsetRefreshPolicy.ShouldReplace(current, Offsets("2026.711.0", 20)));
        Assert.True(LazerMemoryOffsetRefreshPolicy.ShouldReplace(current, Offsets("2026.712.0", 20)));
        Assert.False(LazerMemoryOffsetRefreshPolicy.ShouldReplace(current, Offsets("2026.710.0", 20)));
        Assert.Equal(TimeSpan.FromHours(6), LazerMemoryReplayFrameSource.OffsetRefreshInterval);
        Assert.Equal(TimeSpan.FromMinutes(15), LazerMemoryReplayFrameSource.OffsetRefreshRetryInterval);
    }

    [Fact]
    public async Task OffsetRefreshAtomicallyStoresANewerValidatedDocument()
    {
        var root = NewTempDirectory();
        try
        {
            var path = Path.Combine(root, "offsets.json");
            var current = Offsets("2026.710.0", 10);
            var candidate = Offsets("2026.711.0", 20);
            await File.WriteAllTextAsync(path, OffsetJson(current));

            var result = await LazerMemoryOffsets.RefreshCachedAsync(
                current,
                path,
                _ => Task.FromResult(OffsetJson(candidate)));

            Assert.True(result.Updated);
            Assert.Equal(candidate, result.Offsets);
            Assert.Equal(candidate, LazerMemoryOffsets.LoadCached(path));
            Assert.Empty(Directory.EnumerateFiles(root, "*.new-*"));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task MalformedOffsetRefreshPreservesLastKnownGoodCache()
    {
        var root = NewTempDirectory();
        try
        {
            var path = Path.Combine(root, "offsets.json");
            var current = Offsets("2026.711.0", 10);
            var originalJson = OffsetJson(current);
            await File.WriteAllTextAsync(path, originalJson);

            await Assert.ThrowsAsync<InvalidDataException>(() =>
                LazerMemoryOffsets.RefreshCachedAsync(
                    current,
                    path,
                    _ => Task.FromResult("{\"OsuVersion\":\"2026.712.0\"}")));

            Assert.Equal(originalJson, await File.ReadAllTextAsync(path));
            Assert.Equal(current, LazerMemoryOffsets.LoadCached(path));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void ParsesLatestTosuSessionAndHexGameBaseFromBoundedSegments()
    {
        const string head = """
            00:00:01.103 [debug] Starting regular data loop for client 111
            00:00:02.555 [debug] lazer GameBase address updated: undefined => aaaabbbb
            """;
        const string tail = """
            00:10:01.103 [debug] Starting regular data loop for client 51016
            00:10:02.555 [debug] lazer GameBase address updated: undefined => bf388910
            """;

        Assert.True(TosuGameBaseLogHintParser.TryParse(head, tail, out var hint));
        Assert.Equal(51016, hint.ProcessId);
        Assert.Equal(unchecked((nint)0xbf388910L), hint.GameBase);
        Assert.Equal(32 * 1024, TosuGameBaseLogHintReader.MaximumHeadBytes);
        Assert.Equal(64 * 1024, TosuGameBaseLogHintReader.MaximumTailBytes);
    }

    [Fact]
    public void TosuHintNeverSurvivesNewSessionOrUndefinedAddress()
    {
        const string head = """
            Starting regular data loop for client 111
            lazer GameBase address updated: undefined => aaaabbbb
            """;
        const string newSessionWithoutAddress = "Starting regular data loop for client 222";
        const string addressBecameUndefined =
            "lazer GameBase address updated: aaaabbbb => undefined";
        const string orphanAddressAfterSkippedMiddle =
            "lazer GameBase address updated: undefined => bf388910";
        const string genericTailAfterSkippedMiddle =
            "lazer Current attributes updated to 4.94 stars";

        Assert.False(TosuGameBaseLogHintParser.TryParse(
            head,
            newSessionWithoutAddress,
            out _));
        Assert.False(TosuGameBaseLogHintParser.TryParse(
            head,
            addressBecameUndefined,
            out _));
        Assert.False(TosuGameBaseLogHintParser.TryParse(
            head,
            orphanAddressAfterSkippedMiddle,
            out _));
        Assert.False(TosuGameBaseLogHintParser.TryParse(
            head,
            genericTailAfterSkippedMiddle,
            out _));
    }

    [Fact]
    public void TosuHintAdoptionRequiresNativeVtableAndScreenStackValidation()
    {
        var candidate = unchecked((nint)0xbf388910L);
        Assert.False(TosuGameBaseAdoptionPolicy.ShouldAdopt(0, true, true));
        Assert.False(TosuGameBaseAdoptionPolicy.ShouldAdopt(candidate, false, true));
        Assert.False(TosuGameBaseAdoptionPolicy.ShouldAdopt(candidate, true, false));
        Assert.True(TosuGameBaseAdoptionPolicy.ShouldAdopt(candidate, true, true));
    }

    [Fact]
    public void TosuPidWinsAcrossAliasesOtherwiseNewestLazerWins()
    {
        var candidates = new[]
        {
            new LazerProcessCandidate(100, new DateTime(2026, 7, 14, 7, 0, 0)),
            new LazerProcessCandidate(200, new DateTime(2026, 7, 14, 8, 0, 0)),
        };

        Assert.Equal(100, LazerProcessSelectionPolicy.Select(candidates, 100));
        Assert.Equal(200, LazerProcessSelectionPolicy.Select(candidates, 999));
        Assert.Equal(200, LazerProcessSelectionPolicy.Select(candidates, null));
    }

    [Fact]
    public void NewReplayGenerationClearsOldAttemptSnapshotBeforeAppending()
    {
        var attemptFrames = Enumerable.Range(1, 100)
            .Select(sequence => new LazerReplayFrame { Sequence = sequence })
            .ToList();
        var replacement = new[]
        {
            new LazerReplayFrame { Sequence = 1 },
            new LazerReplayFrame { Sequence = 2 },
        };
        var changed = LazerAttemptFrameBufferPolicy.BeginsNewGeneration(
            framesListChanged: true,
            previousSequence: 100,
            replacement);

        LazerAttemptFrameBufferPolicy.Append(
            attemptFrames,
            replacement,
            attemptActive: true,
            beginsNewGeneration: changed);

        Assert.True(changed);
        Assert.Equal(new long?[] { 1, 2 }, attemptFrames.Select(frame => frame.Sequence));
        Assert.True(LazerAttemptFrameBufferPolicy.BeginsNewGeneration(
            framesListChanged: false,
            previousSequence: 100,
            replacement));
    }

    [Fact]
    public void MissingGameBaseEnablesBoundedDiscoveryDuringLiveCapture()
    {
        Assert.True(LazerMemoryReadPolicy.ShouldDiscover(0));
        Assert.False(LazerMemoryReadPolicy.ShouldDiscover((nint)0x10000));
        Assert.True(LazerMemoryReadPolicy.ShouldRearmDiscovery(0, discoveryExhausted: true));
        Assert.False(LazerMemoryReadPolicy.ShouldRearmDiscovery(0, discoveryExhausted: false));
        Assert.False(LazerMemoryReadPolicy.ShouldRearmDiscovery((nint)0x10000, discoveryExhausted: true));
        Assert.Equal(1024 * 1024, LazerMemoryReadPolicy.DiscoveryBytesPerStep);
        Assert.Equal(TimeSpan.FromMilliseconds(16), LazerMemoryReadPolicy.DiscoveryStepInterval);
        Assert.True(
            LazerMemoryReadPolicy.DiscoveryBytesPerStep /
            LazerMemoryReadPolicy.DiscoveryStepInterval.TotalSeconds
            >= 60 * 1024 * 1024,
            "Continuous bounded discovery must make meaningful progress before a short map finishes.");
        Assert.InRange(LazerMemoryReadPolicy.DiscoveryReadBudget.TotalMilliseconds, 3, 4);
    }

    [Fact]
    public void SoftDeadlineAlwaysAllowsOneProgressUnit()
    {
        Assert.True(LazerMemoryReadPolicy.MayAttemptUnit(isFirst: true, budgetExpired: true));
        Assert.True(LazerMemoryReadPolicy.MayAttemptUnit(isFirst: false, budgetExpired: false));
        Assert.False(LazerMemoryReadPolicy.MayAttemptUnit(isFirst: false, budgetExpired: true));
    }

    [Fact]
    public void ReplayDetectionOnlyUsesTopmostCurrentPlayer()
    {
        nint[] players = [(nint)0x30000, (nint)0x20000];

        Assert.False(LazerReplayFrameMemoryReader.CurrentPlayerHasReplayScore(
            players,
            player => player == (nint)0x20000));
        Assert.True(LazerReplayFrameMemoryReader.CurrentPlayerHasReplayScore(
            players,
            player => player == (nint)0x30000));
        Assert.False(LazerReplayFrameMemoryReader.CurrentPlayerHasReplayScore(
            [],
            _ => true));
    }

    [Fact]
    public void PointerFallbackStaysAlignedAndCanResumePastAStaleMatch()
    {
        const long pointer = 0x1020_3040_5060_7080;
        var buffer = new byte[40];
        BinaryPrimitives.WriteInt64LittleEndian(buffer.AsSpan(0, sizeof(long)), pointer);
        BinaryPrimitives.WriteInt64LittleEndian(buffer.AsSpan(16, sizeof(long)), pointer);

        Assert.Equal(0, LazerMemoryReadPolicy.FindAlignedPointerOffset(buffer, pointer, 0));
        Assert.Equal(16, LazerMemoryReadPolicy.FindAlignedPointerOffset(buffer, pointer, sizeof(long)));
        Assert.Equal(16, LazerMemoryReadPolicy.FindAlignedPointerOffset(buffer, pointer, 9));
        Assert.Equal(-1, LazerMemoryReadPolicy.FindAlignedPointerOffset(buffer, pointer, 24));
    }

    private static LazerMemoryOffsets Offsets(string version, int seed) => new(
        version,
        GameBaseVtable: 1000 + seed,
        OsuGameScreenStack: 10 + seed,
        ScreenStackStack: 20 + seed,
        PlayerScore: 30 + seed,
        ExternalLinkOpenerApi: 40 + seed,
        ApiAccessGame: 50 + seed,
        PlayerDrawableRuleset: 60 + seed,
        DrawableRulesetReplayScore: 70 + seed);

    private static string OffsetJson(LazerMemoryOffsets offsets) => $$"""
        {
          "OsuVersion": "{{offsets.OsuVersion}}",
          "GameBaseVtable": {{offsets.GameBaseVtable}},
          "osu.Game.OsuGame": {
            "<ScreenStack>k__BackingField": {{offsets.OsuGameScreenStack}}
          },
          "osu.Framework.Screens.ScreenStack": {
            "stack": {{offsets.ScreenStackStack}}
          },
          "osu.Game.Screens.Play.Player": {
            "<Score>k__BackingField": {{offsets.PlayerScore}},
            "<DrawableRuleset>k__BackingField": {{offsets.PlayerDrawableRuleset}}
          },
          "osu.Game.Online.Chat.ExternalLinkOpener": {
            "<api>k__BackingField": {{offsets.ExternalLinkOpenerApi}}
          },
          "osu.Game.Online.API.APIAccess": {
            "game": {{offsets.ApiAccessGame}}
          },
          "osu.Game.Rulesets.UI.DrawableRuleset": {
            "<ReplayScore>k__BackingField": {{offsets.DrawableRulesetReplayScore}}
          }
        }
        """;

    private static string NewTempDirectory()
    {
        var path = Path.Combine(Path.GetTempPath(), $"kumori-lazer-offsets-{Guid.NewGuid():N}");
        Directory.CreateDirectory(path);
        return path;
    }
}
