using System.IO;
using System.Text.Json;
using Kumori.Core;

namespace Kumori.App.Skins;

/// <summary>
/// Keeps an unapplied Studio write set across an app restart. The draft remains
/// local until the user explicitly restores it for the same lazer skin.
/// </summary>
internal static class SkinDraftRecovery
{
    private static readonly JsonSerializerOptions options = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = false,
    };

    public static void Save(Guid skinId, string rootPath, IEnumerable<SkinDraftChange> changes)
    {
        var entries = changes.Select(change => new Entry(
            change.Filename,
            change.ExpectedHash,
            change.Bytes,
            change.Description,
            change.Operation)).ToArray();
        var path = PathFor(skinId);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var pending = path + $".{Guid.NewGuid():N}.tmp";
        try
        {
            File.WriteAllBytes(
                pending,
                JsonSerializer.SerializeToUtf8Bytes(new Snapshot(rootPath, entries), options));
            File.Move(pending, path, true);
        }
        finally
        {
            try { File.Delete(pending); } catch { }
        }
    }

    public static IReadOnlyList<SkinDraftChange> Load(Guid skinId, string rootPath)
    {
        var path = PathFor(skinId);
        if (!File.Exists(path)) return [];
        try
        {
            var snapshot = JsonSerializer.Deserialize<Snapshot>(File.ReadAllText(path), options);
            if (snapshot is null
                || !string.Equals(
                    Path.GetFullPath(snapshot.RootPath),
                    Path.GetFullPath(rootPath),
                    StringComparison.OrdinalIgnoreCase))
                return [];
            return snapshot.Changes.Select(change => new SkinDraftChange(
                change.Filename,
                change.ExpectedHash,
                change.Bytes,
                change.Description,
                change.Operation)).ToArray();
        }
        catch (Exception)
        {
            // A malformed or old cache must never prevent opening a skin.
            return [];
        }
    }

    public static void Clear(Guid skinId)
    {
        var path = PathFor(skinId);
        if (File.Exists(path))
            File.Delete(path);
    }

    private static string PathFor(Guid skinId) => Path.Combine(
        AppPaths.SkinEditorDataDir,
        "drafts",
        $"{skinId:N}.json");

    private sealed record Snapshot(string RootPath, IReadOnlyList<Entry> Changes);

    private sealed record Entry(
        string Filename,
        string? ExpectedHash,
        byte[] Bytes,
        string Description,
        SkinDraftOperation Operation);
}
