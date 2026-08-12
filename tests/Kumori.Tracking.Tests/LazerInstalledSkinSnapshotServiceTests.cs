using System.IO.Compression;
using System.Security.Cryptography;
using Kumori.Tracking;
using Xunit;

namespace Kumori.Tracking.Tests;

public sealed class LazerInstalledSkinSnapshotServiceTests : IDisposable
{
    private readonly string root = Path.Combine(
        Path.GetTempPath(),
        $"kumori-installed-skin-{Guid.NewGuid():N}");

    [Fact]
    public void Snapshot_is_hash_verified_and_contains_only_catalogued_files()
    {
        var ini = "[General]\nName: Installed\n"u8.ToArray();
        var cursor = new byte[] { 1, 2, 3 };
        var blobs = new Dictionary<string, byte[]>
        {
            [hash(ini)] = ini,
            [hash(cursor)] = cursor,
        };
        var skin = new LazerSkinInfo(
            Guid.NewGuid(),
            "Installed",
            "Kumori",
            [
                new LazerSkinFileInfo("skin.ini", hash(ini), ini.Length),
                new LazerSkinFileInfo("cursor.png", hash(cursor), cursor.Length),
            ]);
        var destination = Path.Combine(root, "snapshot.osk");

        LazerInstalledSkinSnapshotService.CreateVerifiedOsk(
            skin,
            digest => blobs[digest],
            destination);

        using var archive = ZipFile.OpenRead(destination);
        Assert.Equal(
            ["cursor.png", "skin.ini"],
            archive.Entries.Select(entry => entry.FullName).Order());
    }

    [Fact]
    public void Snapshot_stops_when_blob_no_longer_matches_realm_hash()
    {
        var expected = new byte[] { 1 };
        var skin = new LazerSkinInfo(
            Guid.NewGuid(),
            "Changed",
            "",
            [new LazerSkinFileInfo("skin.ini", hash(expected), expected.Length)]);

        Assert.Throws<InvalidDataException>(() =>
            LazerInstalledSkinSnapshotService.CreateVerifiedOsk(
                skin,
                _ => [2],
                Path.Combine(root, "changed.osk")));
    }

    private static string hash(byte[] bytes) =>
        Convert.ToHexStringLower(SHA256.HashData(bytes));

    public void Dispose()
    {
        try { if (Directory.Exists(root)) Directory.Delete(root, true); } catch { }
    }
}
