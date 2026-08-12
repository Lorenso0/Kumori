using Kumori.Skins;

namespace Kumori.Tracking;

public sealed record LazerSkinPublishVerificationResult(
    Guid SkinId,
    string Name,
    string Creator,
    int FileCount,
    TimeSpan Elapsed);

public sealed class LazerSkinPublishVerificationService
{
    private readonly ILazerSkinRealmService realm;

    public LazerSkinPublishVerificationService(
        ILazerSkinRealmService? realm = null)
    {
        this.realm = realm ?? new LazerSkinRealmService();
    }

    public async Task<LazerSkinPublishVerificationResult> WaitForImportAsync(
        string playerRoot,
        IReadOnlySet<Guid> preImportSkinIds,
        string expectedName,
        string expectedCreator,
        IReadOnlyDictionary<string, byte[]> expectedFiles,
        TimeSpan timeout,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(playerRoot);
        ArgumentNullException.ThrowIfNull(preImportSkinIds);
        ArgumentException.ThrowIfNullOrWhiteSpace(expectedName);
        ArgumentNullException.ThrowIfNull(expectedFiles);
        if (expectedFiles.Count == 0)
            throw new InvalidDataException(
                "A published skin must contain at least one file.");
        if (timeout <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(timeout));

        var started = DateTimeOffset.UtcNow;
        var deadline = started + timeout;
        Exception? lastReadFailure = null;
        while (DateTimeOffset.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var catalog = realm.LoadCatalog(playerRoot);
                LazerSkinInfo? imported = null;
                foreach (var candidate in catalog.Skins.Where(skin =>
                             !preImportSkinIds.Contains(skin.Id)
                             && Matches(
                                 skin,
                                 expectedName,
                                 expectedCreator,
                                 expectedFiles)))
                {
                    var actualIni = candidate.Files.Single(file =>
                        file.Filename.Equals(
                            "skin.ini",
                            StringComparison.OrdinalIgnoreCase));
                    if (SkinIniMatchesAfterImport(
                            expectedFiles["skin.ini"],
                            realm.ReadFile(playerRoot, actualIni.Hash),
                            candidate.Name,
                            candidate.Creator))
                    {
                        imported = candidate;
                        break;
                    }
                }
                if (imported is not null)
                {
                    return new LazerSkinPublishVerificationResult(
                        imported.Id,
                        imported.Name,
                        imported.Creator,
                        imported.Files.Count,
                        DateTimeOffset.UtcNow - started);
                }
                lastReadFailure = null;
            }
            catch (Exception ex) when (
                ex is IOException
                or UnauthorizedAccessException
                or InvalidDataException)
            {
                lastReadFailure = ex;
            }
            await Task.Delay(500, cancellationToken);
        }

        throw new TimeoutException(
            "osu!lazer did not expose a new skin with the exported file "
            + $"catalog within {timeout.TotalSeconds:0} seconds."
            + (lastReadFailure is null
                ? ""
                : $" Last catalog error: {lastReadFailure.Message}"));
    }

    public static bool Matches(
        LazerSkinInfo skin,
        string expectedName,
        string expectedCreator,
        IReadOnlyDictionary<string, byte[]> expectedFiles)
    {
        ArgumentNullException.ThrowIfNull(skin);
        ArgumentException.ThrowIfNullOrWhiteSpace(expectedName);
        ArgumentNullException.ThrowIfNull(expectedFiles);
        if (!matchesImportedName(skin.Name, expectedName)
            || !skin.Creator.Equals(
                expectedCreator.Trim(),
                StringComparison.Ordinal))
        {
            return false;
        }
        if (skin.Files.Count != expectedFiles.Count)
            return false;
        var actual = skin.Files.ToDictionary(
            file => SkinDraftWorkspaceService.NormalizeSkinFilename(
                file.Filename),
            StringComparer.OrdinalIgnoreCase);
        foreach (var (filename, bytes) in expectedFiles)
        {
            var normalized =
                SkinDraftWorkspaceService.NormalizeSkinFilename(filename);
            if (normalized.Equals(
                    "skin.ini",
                    StringComparison.OrdinalIgnoreCase))
            {
                if (!actual.ContainsKey(normalized))
                    return false;
                continue;
            }
            if (!actual.TryGetValue(normalized, out var file)
                || file.SizeBytes != bytes.LongLength
                || !file.Hash.Equals(
                    SkinDraftWorkspaceService.Hash(bytes),
                    StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }
        }
        return true;
    }

    public static bool SkinIniMatchesAfterImport(
        byte[] expectedBytes,
        byte[] importedBytes,
        string importedName,
        string importedCreator)
    {
        ArgumentNullException.ThrowIfNull(expectedBytes);
        ArgumentNullException.ThrowIfNull(importedBytes);
        var expected = SkinIniDocument.Parse(expectedBytes);
        var imported = SkinIniDocument.Parse(importedBytes);
        if (!string.Equals(
                imported.GetValue("General", "Name"),
                importedName,
                StringComparison.Ordinal)
            || !string.Equals(
                imported.GetValue("General", "Author"),
                importedCreator,
                StringComparison.Ordinal))
        {
            return false;
        }

        foreach (var (_, keys) in SkinIniSchema.Sections())
        {
            foreach (var key in keys)
            {
                if (key.Key is "Name" or "Author")
                    continue;
                var expectedValue = expected.GetValue(
                    key.Section,
                    key.Key);
                if (expectedValue is not null
                    && !string.Equals(
                        expectedValue,
                        imported.GetValue(key.Section, key.Key),
                        StringComparison.Ordinal))
                {
                    return false;
                }
            }
        }

        var expectedMania = expected.GetSections("Mania");
        var importedMania = imported.GetSections("Mania");
        foreach (var expectedSection in expectedMania)
        {
            var importedSection = importedMania.FirstOrDefault(section =>
                section.ManiaKeys == expectedSection.ManiaKeys);
            if (importedSection is null
                || expectedSection.Values.Any(pair =>
                    !importedSection.Values.TryGetValue(
                        pair.Key,
                        out var value)
                    || !value.Equals(
                        pair.Value,
                        StringComparison.Ordinal)))
            {
                return false;
            }
        }
        return true;
    }

    private static bool matchesImportedName(
        string actual,
        string expected)
    {
        var normalized = expected.Trim();
        return actual.Equals(normalized, StringComparison.Ordinal)
               || (actual.StartsWith(
                       normalized + " [",
                       StringComparison.Ordinal)
                   && actual.EndsWith(']'));
    }
}
