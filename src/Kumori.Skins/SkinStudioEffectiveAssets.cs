using System.Text.RegularExpressions;

namespace Kumori.Skins;

public enum SkinStudioEffectiveAssetState
{
    Custom,
    Staged,
    Fallback,
    Transparent,
    Missing,
    BlockedFallback,
}

public sealed record SkinStudioEffectiveAsset(
    string RequestedComponent,
    string? ResolvedComponent,
    SkinStudioEffectiveAssetState State,
    string Label,
    string Detail)
{
    public bool IsAvailable => ResolvedComponent is not null;
}

public sealed record SkinStudioDeletionImpact(
    bool HasDependency,
    string Summary,
    IReadOnlyList<string> SafeFallbackComponents);

public sealed record SkinStudioHealthIssue(
    string Code,
    string Severity,
    string Message,
    string? Component = null);

public sealed record SkinStudioPreflightReport(
    IReadOnlyList<SkinStudioHealthIssue> Issues,
    int LazerUsedFiles,
    int StableOnlyFiles,
    int UnknownFiles)
{
    public bool HasErrors => Issues.Any(issue => issue.Severity == "Error");

    public string Summary
    {
        get
        {
            var warnings = Issues.Count(issue => issue.Severity == "Warning");
            var errors = Issues.Count(issue => issue.Severity == "Error");
            var health = errors == 0 && warnings == 0
                ? "No asset-health problems found."
                : $"{errors} error{(errors == 1 ? "" : "s")}, "
                  + $"{warnings} warning{(warnings == 1 ? "" : "s")}.";
            return $"{health} Lazer uses {LazerUsedFiles} file"
                   + (LazerUsedFiles == 1 ? "" : "s")
                   + $"; {StableOnlyFiles} are stable-only; {UnknownFiles} are unclassified.";
        }
    }
}

public static class SkinStudioEffectiveAssetResolver
{
    private static readonly string[] legacy_circle_prefixes =
        ["sliderstartcircle", "sliderendcircle"];

    public static SkinStudioEffectiveAsset Resolve(
        string component,
        IEnumerable<string> filenames,
        IEnumerable<string>? transparentComponents = null,
        IEnumerable<string>? stagedComponents = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(component);
        var available = filenames.Select(Stem)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var transparent = (transparentComponents ?? []).Select(Stem)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var staged = (stagedComponents ?? []).Select(Stem)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var requested = Stem(component);

        foreach (var prefix in legacy_circle_prefixes)
        {
            if (requested.Equals(prefix, StringComparison.OrdinalIgnoreCase))
                return resolveCircle(prefix, overlay: false);
            if (requested.Equals(prefix + "overlay", StringComparison.OrdinalIgnoreCase))
                return resolveCircle(prefix, overlay: true);
        }

        if (!available.Contains(requested))
            return missing(requested, $"No '{requested}' asset is present.");
        return present(requested, requested, SkinStudioEffectiveAssetState.Custom);

        SkinStudioEffectiveAsset resolveCircle(string prefix, bool overlay)
        {
            var customBase = available.Contains(prefix);
            var customComponent = overlay ? prefix + "overlay" : prefix;
            if (customBase)
            {
                if (!overlay || available.Contains(customComponent))
                    return present(requested, customComponent, SkinStudioEffectiveAssetState.Custom);
                return new SkinStudioEffectiveAsset(
                    requested,
                    null,
                    SkinStudioEffectiveAssetState.BlockedFallback,
                    "Fallback blocked",
                    $"'{prefix}' is custom, so osu! will not use hitcircleoverlay when "
                    + $"'{customComponent}' is absent.");
            }

            var fallback = overlay ? "hitcircleoverlay" : "hitcircle";
            if (!available.Contains(fallback))
                return missing(requested, $"Neither '{customComponent}' nor fallback '{fallback}' is present.");
            return present(requested, fallback, SkinStudioEffectiveAssetState.Fallback);
        }

        SkinStudioEffectiveAsset present(
            string requestedComponent,
            string resolved,
            SkinStudioEffectiveAssetState defaultState)
        {
            var state = transparent.Contains(resolved)
                ? SkinStudioEffectiveAssetState.Transparent
                : staged.Contains(resolved)
                    ? SkinStudioEffectiveAssetState.Staged
                    : defaultState;
            var label = state switch
            {
                SkinStudioEffectiveAssetState.Transparent => "In use · transparent",
                SkinStudioEffectiveAssetState.Staged => "In use · staged change",
                SkinStudioEffectiveAssetState.Fallback => $"In use · fallback to {resolved}",
                _ => "In use · custom asset",
            };
            var detail = state switch
            {
                SkinStudioEffectiveAssetState.Transparent =>
                    $"osu! resolves '{requestedComponent}' to transparent '{resolved}'.",
                SkinStudioEffectiveAssetState.Fallback =>
                    $"'{requestedComponent}' is absent; osu! resolves it to '{resolved}'.",
                SkinStudioEffectiveAssetState.Staged =>
                    $"The staged '{resolved}' file is the effective asset.",
                _ => $"osu! resolves this component directly to '{resolved}'.",
            };
            return new SkinStudioEffectiveAsset(
                requestedComponent,
                resolved,
                state,
                label,
                detail);
        }

        static SkinStudioEffectiveAsset missing(string requestedComponent, string detail) =>
            new(
                requestedComponent,
                null,
                SkinStudioEffectiveAssetState.Missing,
                "Not currently in use",
                detail);
    }

    public static SkinStudioDeletionImpact DescribeDeletion(
        string component,
        IEnumerable<string> filenames)
    {
        var stem = Stem(component);
        var available = filenames.Select(Stem)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var prefix in legacy_circle_prefixes)
        {
            if (!stem.Equals(prefix + "overlay", StringComparison.OrdinalIgnoreCase)
                || !available.Contains(prefix))
                continue;
            return new SkinStudioDeletionImpact(
                true,
                $"Deleting '{prefix}overlay' alone leaves custom '{prefix}' active. "
                + "osu! will intentionally suppress the normal hitcircle overlay; "
                + "if that base is transparent, the endpoint becomes invisible.",
                [prefix]);
        }
        return new SkinStudioDeletionImpact(false, "No fallback dependency detected.", []);
    }

    public static SkinStudioPreflightReport BuildPreflight(
        IEnumerable<SkinExtraManifestFile> files,
        string familyId = "")
    {
        var described = files.ToArray();
        var issues = new List<SkinStudioHealthIssue>();
        var stems = described.Select(file => Stem(file.TargetFilename))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var transparent = described
            .Where(file => file.SimilarityHash?.Equals(
                "transparent",
                StringComparison.OrdinalIgnoreCase) == true)
            .Select(file => Stem(file.TargetFilename))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        foreach (var component in transparent.Where(IsCriticalComponent))
        {
            issues.Add(new SkinStudioHealthIssue(
                "transparent-critical",
                "Warning",
                $"Critical asset '{component}' is fully transparent.",
                component));
        }

        foreach (var prefix in legacy_circle_prefixes)
        {
            if (!stems.Contains(prefix) || stems.Contains(prefix + "overlay"))
                continue;
            issues.Add(new SkinStudioHealthIssue(
                transparent.Contains(prefix)
                    ? "invisible-slider-endpoint"
                    : "blocked-slider-overlay-fallback",
                transparent.Contains(prefix) ? "Error" : "Warning",
                $"Custom '{prefix}' has no matching overlay, so osu! blocks "
                + "hitcircleoverlay fallback."
                + (transparent.Contains(prefix) ? " The endpoint will be invisible." : ""),
                prefix));
        }

        foreach (var overlay in stems.Where(stem => stem.EndsWith(
                     "overlay",
                     StringComparison.OrdinalIgnoreCase)))
        {
            var baseStem = overlay[..^"overlay".Length];
            if (!legacy_circle_prefixes.Contains(baseStem, StringComparer.OrdinalIgnoreCase)
                || stems.Contains(baseStem))
                continue;
            issues.Add(new SkinStudioHealthIssue(
                "orphan-overlay",
                "Warning",
                $"'{overlay}' is ignored because '{baseStem}' is absent.",
                overlay));
        }

        foreach (var group in described.GroupBy(
                     file => Stem(file.TargetFilename),
                     StringComparer.OrdinalIgnoreCase))
        {
            var extensions = group.Select(file => Path.GetExtension(file.TargetFilename))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
            if (extensions.Length > 1
                && group.All(file => SkinMediaTypes.IsAudio(file.TargetFilename)))
            {
                issues.Add(new SkinStudioHealthIssue(
                    "conflicting-audio-formats",
                    "Error",
                    $"'{group.Key}' exists in multiple audio formats: {string.Join(", ", extensions)}.",
                    group.Key));
            }
        }

        var frameGroups = described
            .Select(file => (File: file, Match: Regex.Match(
                Stem(file.TargetFilename),
                @"^(?<base>.+)-(?<index>[0-9]+)$")))
            .Where(item => item.Match.Success)
            .GroupBy(item => item.Match.Groups["base"].Value, StringComparer.OrdinalIgnoreCase);
        foreach (var group in frameGroups)
        {
            var indices = group.Select(item => int.TryParse(
                    item.Match.Groups["index"].Value,
                    out var index)
                ? index
                : -1)
                .Where(index => index >= 0)
                .Distinct()
                .Order()
                .ToArray();
            if (indices.Length < 2)
                continue;
            var missingIndex = Enumerable.Range(indices[0], indices[^1] - indices[0] + 1)
                .FirstOrDefault(index => !indices.Contains(index), -1);
            if (missingIndex >= 0)
            {
                issues.Add(new SkinStudioHealthIssue(
                    "animation-frame-gap",
                    "Warning",
                    $"Animation '{group.Key}' is missing frame {missingIndex}.",
                    group.Key));
            }
        }

        var lazer = 0;
        var stable = 0;
        var unknown = 0;
        foreach (var file in described)
        {
            var effectiveFamily = string.IsNullOrWhiteSpace(familyId)
                ? SkinExtraFamilyRegistry.ForFile(file.TargetFilename)?.Id ?? ""
                : familyId;
            switch (SkinExtraLazerCompatibility.Classify(
                        file.TargetFilename,
                        effectiveFamily))
            {
                case SkinExtraCompatibility.LazerUsed:
                    lazer++;
                    break;
                case SkinExtraCompatibility.StableOnly:
                    stable++;
                    break;
                default:
                    unknown++;
                    break;
            }
        }
        return new SkinStudioPreflightReport(issues, lazer, stable, unknown);
    }

    private static bool IsCriticalComponent(string component) =>
        component is "hitcircle" or "hitcircleoverlay" or "approachcircle"
            or "cursor" or "sliderstartcircle" or "sliderendcircle"
            or "sliderb" or "sliderb0";

    public static string Stem(string filename)
    {
        var stem = Path.GetFileNameWithoutExtension(filename.Replace('\\', '/'));
        return stem.EndsWith("@2x", StringComparison.OrdinalIgnoreCase)
            ? stem[..^3]
            : stem;
    }
}

public sealed record SkinStudioRandomPackCandidate(
    string Key,
    string FamilyId,
    bool IsCurrentlyInUse,
    bool IsCompatible = true);

public static class SkinStudioRandomMix
{
    private static readonly string[] hitsound_families =
    [
        "audio.hitsounds.normal",
        "audio.hitsounds.soft",
        "audio.hitsounds.drum",
    ];

    public static IReadOnlyList<string> ChooseHitsounds(
        IEnumerable<SkinStudioRandomPackCandidate> candidates,
        Random random) =>
        choose(candidates, hitsound_families, random);

    public static IReadOnlyList<string> ChooseFull(
        IEnumerable<SkinStudioRandomPackCandidate> candidates,
        Random random)
    {
        var available = candidates.Where(candidate => candidate.IsCompatible).ToArray();
        return choose(
            available,
            available.Select(candidate => candidate.FamilyId)
                .Distinct(StringComparer.OrdinalIgnoreCase),
            random);
    }

    private static IReadOnlyList<string> choose(
        IEnumerable<SkinStudioRandomPackCandidate> candidates,
        IEnumerable<string> families,
        Random random)
    {
        ArgumentNullException.ThrowIfNull(random);
        var all = candidates.Where(candidate => candidate.IsCompatible).ToArray();
        var result = new List<string>();
        foreach (var family in families)
        {
            var familyPacks = all.Where(candidate => candidate.FamilyId.Equals(
                    family,
                    StringComparison.OrdinalIgnoreCase))
                .ToArray();
            if (familyPacks.Length == 0)
                continue;
            var fresh = familyPacks.Where(candidate => !candidate.IsCurrentlyInUse).ToArray();
            var pool = fresh.Length > 0 ? fresh : familyPacks;
            result.Add(pool[random.Next(pool.Length)].Key);
        }
        return result;
    }
}
