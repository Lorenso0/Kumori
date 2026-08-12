using System.Text.Json;
using Kumori.Skins;

namespace Kumori.SkinStudio;

internal sealed record StudioNativeSkinCacheEntry(
    Guid DraftId,
    long Revision,
    Guid SkinId,
    DateTimeOffset UpdatedAt);

internal sealed class StudioNativeSkinCache
{
    private readonly string path;

    public StudioNativeSkinCache(string workspacePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workspacePath);
        path = Path.Combine(
            Path.GetFullPath(workspacePath),
            "native-skin-cache.json");
    }

    public bool TryGet(
        Guid draftId,
        long revision,
        out Guid skinId)
    {
        var entry = load().FirstOrDefault(candidate =>
            candidate.DraftId == draftId
            && candidate.Revision == revision);
        skinId = entry?.SkinId ?? Guid.Empty;
        return entry is not null;
    }

    public void Set(Guid draftId, long revision, Guid skinId)
    {
        var entries = load()
            .Where(entry => entry.DraftId != draftId)
            .Append(new StudioNativeSkinCacheEntry(
                draftId,
                revision,
                skinId,
                DateTimeOffset.UtcNow))
            .OrderBy(entry => entry.DraftId)
            .ToArray();
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        string temporary = path + $".{Guid.NewGuid():N}.tmp";
        try
        {
            File.WriteAllText(
                temporary,
                JsonSerializer.Serialize(
                    entries,
                    SkinStudioLaunchContract.JsonOptions));
            File.Move(temporary, path, overwrite: true);
        }
        finally
        {
            try { File.Delete(temporary); } catch { }
        }
    }

    public void Remove(Guid draftId)
    {
        var entries = load()
            .Where(entry => entry.DraftId != draftId)
            .ToArray();
        if (entries.Length == 0)
        {
            try { File.Delete(path); } catch { }
            return;
        }
        string temporary = path + $".{Guid.NewGuid():N}.tmp";
        try
        {
            File.WriteAllText(
                temporary,
                JsonSerializer.Serialize(
                    entries,
                    SkinStudioLaunchContract.JsonOptions));
            File.Move(temporary, path, overwrite: true);
        }
        finally
        {
            try { File.Delete(temporary); } catch { }
        }
    }

    private IReadOnlyList<StudioNativeSkinCacheEntry> load()
    {
        try
        {
            if (!File.Exists(path))
                return [];
            return JsonSerializer.Deserialize<StudioNativeSkinCacheEntry[]>(
                       File.ReadAllText(path),
                       SkinStudioLaunchContract.JsonOptions)
                   ?? [];
        }
        catch
        {
            return [];
        }
    }
}
