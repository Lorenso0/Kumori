using Kumori.App.Skins;
using Xunit;

namespace Kumori.App.Tests;

public sealed class SkinElementBackupCatalogTests : IDisposable
{
    private readonly string root = Path.Combine(
        Path.GetTempPath(),
        $"kumori-element-backups-{Guid.NewGuid():N}");

    [Fact]
    public void Scan_returns_current_skin_backups_newest_first_with_selectable_files()
    {
        var skinId = Guid.NewGuid();
        createSession(
            "older",
            skinId,
            new DateTimeOffset(2026, 8, 12, 10, 0, 0, TimeSpan.Zero),
            ("cursor.png", new byte[] { 1, 2, 3 }));
        var newer = createSession(
            "newer",
            skinId,
            new DateTimeOffset(2026, 8, 13, 11, 0, 0, TimeSpan.Zero),
            ("audio/hitnormal.wav", new byte[] { 4, 5 }));
        Directory.CreateDirectory(Path.Combine(newer, "realm"));
        createSession(
            "different-skin",
            Guid.NewGuid(),
            DateTimeOffset.UtcNow,
            ("ignored.png", new byte[] { 9 }));
        Directory.CreateDirectory(Path.Combine(root, "incomplete", "elements"));

        var sessions = SkinElementBackupCatalog.Scan(root, skinId);

        Assert.Equal(2, sessions.Count);
        Assert.Equal("newer", Path.GetFileName(sessions[0].DirectoryPath));
        Assert.True(sessions[0].HasRealmRestorePoint);
        var file = Assert.Single(sessions[0].Files);
        Assert.Equal("audio/hitnormal.wav", file.Filename);
        Assert.Equal(2, file.Size);
        Assert.Equal("older", Path.GetFileName(sessions[1].DirectoryPath));
    }

    [Fact]
    public void Scan_ignores_empty_and_malformed_sessions()
    {
        var skinId = Guid.NewGuid();
        var empty = Path.Combine(root, "empty");
        Directory.CreateDirectory(Path.Combine(empty, "elements"));
        File.WriteAllText(
            Path.Combine(empty, "backup.txt"),
            $"Skin ID: {skinId}{Environment.NewLine}Created: {DateTimeOffset.UtcNow:O}");
        var malformed = Path.Combine(root, "malformed");
        Directory.CreateDirectory(Path.Combine(malformed, "elements"));
        File.WriteAllText(Path.Combine(malformed, "backup.txt"), "Skin ID: nope");
        File.WriteAllBytes(Path.Combine(malformed, "cursor.png"), [1]);

        Assert.Empty(SkinElementBackupCatalog.Scan(root, skinId));
    }

    private string createSession(
        string name,
        Guid skinId,
        DateTimeOffset created,
        params (string Filename, byte[] Bytes)[] files)
    {
        var session = Path.Combine(root, name);
        var elements = Path.Combine(session, "elements");
        Directory.CreateDirectory(elements);
        File.WriteAllText(
            Path.Combine(session, "backup.txt"),
            $"Skin: Test{Environment.NewLine}"
            + $"Skin ID: {skinId}{Environment.NewLine}"
            + $"Created: {created:O}{Environment.NewLine}");
        foreach (var file in files)
        {
            var path = Path.Combine(
                elements,
                file.Filename.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllBytes(path, file.Bytes);
        }
        return session;
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(root))
                Directory.Delete(root, true);
        }
        catch
        {
            // Best-effort cleanup for locked test files.
        }
    }
}
