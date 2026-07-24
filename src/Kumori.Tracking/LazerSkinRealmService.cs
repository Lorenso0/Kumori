using System.Security.Cryptography;
using Realms;
using Realms.Exceptions;

namespace Kumori.Tracking;

public sealed record LazerSkinFileInfo(string Filename, string Hash, long SizeBytes);

public sealed record LazerSkinInfo(
    Guid Id,
    string Name,
    string Creator,
    IReadOnlyList<LazerSkinFileInfo> Files)
{
    public string DisplayName => string.IsNullOrWhiteSpace(Creator) ? Name : $"{Name} ({Creator})";
}

public sealed record LazerSkinCatalog(string RootPath, IReadOnlyList<LazerSkinInfo> Skins);

public enum LazerSkinWriteStatus
{
    Unchanged,
    Saved,
    Added,
    Replaced,
    Conflict,
    Missing,
}

public sealed record LazerSkinWriteResult(
    LazerSkinWriteStatus Status,
    string Hash,
    string? CurrentHash = null,
    string? Message = null)
{
    public bool Changed => Status is LazerSkinWriteStatus.Saved
        or LazerSkinWriteStatus.Added
        or LazerSkinWriteStatus.Replaced;
}

/// <summary>
/// Reads and writes osu!lazer skins through Realm's dynamic schema. Dynamic mode is deliberate:
/// the embedded schema in client.realm remains authoritative as lazer evolves.
/// </summary>
public interface ILazerSkinRealmService
{
    LazerSkinCatalog LoadCatalog(string? rootOverride = null);
    byte[] ReadFile(string rootPath, string hash);
    LazerSkinWriteResult CommitFile(
        string rootPath,
        Guid skinId,
        string filename,
        byte[] bytes,
        string expectedHash);
    LazerSkinWriteResult AddOrReplaceFile(
        string rootPath,
        Guid skinId,
        string filename,
        byte[] bytes,
        string? expectedHash);
    string CreateBackup(string rootPath, string destinationDirectory);
}

public sealed class LazerSkinRealmService : ILazerSkinRealmService
{
    private static readonly object realmGate = new();

    public LazerSkinCatalog LoadCatalog(string? rootOverride = null)
    {
        lock (realmGate)
        {
            var root = ResolveRoot(rootOverride);
            using var realm = OpenRealm(root, readOnly: true);
            var filesRoot = FilesRoot(root);
            var result = new List<LazerSkinInfo>();

            foreach (dynamic skin in (IEnumerable<dynamic>)realm.DynamicApi.All("Skin"))
            {
                if (ReadBool(skin, "DeletePending"))
                    continue;

                var files = ReadFiles((IEnumerable<dynamic>)skin.Files, filesRoot);
                if (files.Count == 0)
                    continue;

                result.Add(new LazerSkinInfo(
                    (Guid)skin.ID,
                    ReadString(skin, "Name", "Unnamed skin"),
                    ReadString(skin, "Creator", ""),
                    files));
            }

            return new LazerSkinCatalog(
                root,
                result.OrderBy(skin => skin.DisplayName, StringComparer.OrdinalIgnoreCase).ToArray());
        }
    }

    public byte[] ReadFile(string rootPath, string hash)
    {
        ValidateHash(hash);
        return File.ReadAllBytes(PathForHash(rootPath, hash));
    }

    public LazerSkinWriteResult CommitFile(
        string rootPath,
        Guid skinId,
        string filename,
        byte[] bytes,
        string expectedHash)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filename);
        ValidateHash(expectedHash);

        lock (realmGate)
        {
            var root = ResolveRoot(rootPath);
            using var realm = OpenRealm(root, readOnly: false);
            dynamic? skin = realm.DynamicApi.Find("Skin", skinId);
            if (skin is null)
                return Missing(expectedHash, "The selected skin no longer exists.");

            dynamic? usage = FindUsage(skin, filename);
            if (usage is null)
                return Missing(expectedHash, $"'{filename}' no longer exists in this skin.");

            string currentHash = (string)usage.File.Hash;
            if (!string.Equals(currentHash, expectedHash, StringComparison.OrdinalIgnoreCase))
                return Conflict(expectedHash, currentHash);

            string newHash = ComputeHash(bytes);
            if (string.Equals(newHash, currentHash, StringComparison.OrdinalIgnoreCase))
                return new LazerSkinWriteResult(LazerSkinWriteStatus.Unchanged, currentHash);

            ImportBlob(root, newHash, bytes);
            var conflicted = false;
            string? transactionCurrentHash = null;
            realm.Write(() =>
            {
                dynamic? currentSkin = realm.DynamicApi.Find("Skin", skinId);
                dynamic? currentUsage = currentSkin is null ? null : FindUsage(currentSkin, filename);
                if (currentUsage is null)
                {
                    conflicted = true;
                    return;
                }

                transactionCurrentHash = (string)currentUsage.File.Hash;
                if (!string.Equals(transactionCurrentHash, expectedHash, StringComparison.OrdinalIgnoreCase))
                {
                    conflicted = true;
                    return;
                }

                currentUsage.File = FindOrCreateFile(realm, newHash);
            });

            return conflicted
                ? Conflict(expectedHash, transactionCurrentHash)
                : new LazerSkinWriteResult(LazerSkinWriteStatus.Saved, newHash);
        }
    }

    public LazerSkinWriteResult AddOrReplaceFile(
        string rootPath,
        Guid skinId,
        string filename,
        byte[] bytes,
        string? expectedHash)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filename);
        if (expectedHash is not null)
            ValidateHash(expectedHash);

        lock (realmGate)
        {
            var root = ResolveRoot(rootPath);
            using var realm = OpenRealm(root, readOnly: false);
            dynamic? skin = realm.DynamicApi.Find("Skin", skinId);
            if (skin is null)
                return Missing(expectedHash ?? "", "The selected skin no longer exists.");

            dynamic? existing = FindUsage(skin, filename);
            string? currentHash = existing is null ? null : (string)existing.File.Hash;
            if (expectedHash is null && existing is not null)
                return Conflict("", currentHash);
            if (expectedHash is not null
                && !string.Equals(expectedHash, currentHash, StringComparison.OrdinalIgnoreCase))
                return Conflict(expectedHash, currentHash);

            string newHash = ComputeHash(bytes);
            if (string.Equals(newHash, currentHash, StringComparison.OrdinalIgnoreCase))
                return new LazerSkinWriteResult(LazerSkinWriteStatus.Unchanged, newHash);

            ImportBlob(root, newHash, bytes);
            var added = existing is null;
            var conflicted = false;
            string? transactionCurrentHash = null;

            realm.Write(() =>
            {
                dynamic? currentSkin = realm.DynamicApi.Find("Skin", skinId);
                if (currentSkin is null)
                {
                    conflicted = true;
                    return;
                }

                dynamic? currentUsage = FindUsage(currentSkin, filename);
                transactionCurrentHash = currentUsage is null ? null : (string)currentUsage.File.Hash;
                if (expectedHash is null ? currentUsage is not null
                    : !string.Equals(expectedHash, transactionCurrentHash, StringComparison.OrdinalIgnoreCase))
                {
                    conflicted = true;
                    return;
                }

                dynamic fileRow = FindOrCreateFile(realm, newHash);
                if (currentUsage is not null)
                {
                    currentUsage.File = fileRow;
                }
                else
                {
                    dynamic usage = realm.DynamicApi.AddEmbeddedObjectToList(currentSkin.Files);
                    usage.File = fileRow;
                    usage.Filename = filename.Replace('\\', '/');
                }
            });

            if (conflicted)
                return Conflict(expectedHash ?? "", transactionCurrentHash);

            return new LazerSkinWriteResult(
                added ? LazerSkinWriteStatus.Added : LazerSkinWriteStatus.Replaced,
                newHash);
        }
    }

    public string CreateBackup(string rootPath, string destinationDirectory)
    {
        lock (realmGate)
        {
            var root = ResolveRoot(rootPath);
            Directory.CreateDirectory(destinationDirectory);
            var destination = Path.Combine(
                destinationDirectory,
                $"client.realm.{DateTimeOffset.UtcNow:yyyyMMdd-HHmmss-fff}.realm");

            using var realm = OpenRealm(root, readOnly: true);
            var destinationConfig = new RealmConfiguration(destination) { IsDynamic = true };
            realm.WriteCopy(destinationConfig);
            return destination;
        }
    }

    private static Realm OpenRealm(string root, bool readOnly)
    {
        try
        {
            return Realm.GetInstance(new RealmConfiguration(RealmPath(root))
            {
                IsDynamic = true,
                IsReadOnly = readOnly,
            });
        }
        catch (Exception ex) when (ex is RealmException or IOException or UnauthorizedAccessException)
        {
            throw new InvalidOperationException(
                "Kumori could not open osu!lazer's client.realm. If osu! is running, its Realm "
                + "core may be incompatible with this editor session; close osu! and refresh.",
                ex);
        }
    }

    private static List<LazerSkinFileInfo> ReadFiles(IEnumerable<dynamic> usages, string filesRoot)
    {
        var files = new List<LazerSkinFileInfo>();
        foreach (dynamic usage in usages)
        {
            string filename = (string)usage.Filename;
            string hash = (string)usage.File.Hash;
            long size = 0;
            try
            {
                ValidateHash(hash);
                var path = Path.Combine(filesRoot, hash[..1], hash[..2], hash);
                if (File.Exists(path))
                    size = new FileInfo(path).Length;
            }
            catch
            {
                // Keep the catalog usable when one blob is damaged.
            }

            files.Add(new LazerSkinFileInfo(filename, hash, size));
        }

        return files.OrderBy(file => file.Filename, StringComparer.OrdinalIgnoreCase).ToList();
    }

    private static dynamic? FindUsage(dynamic skin, string filename) =>
        ((IEnumerable<dynamic>)skin.Files).FirstOrDefault(
            file => string.Equals((string)file.Filename, filename, StringComparison.OrdinalIgnoreCase));

    private static dynamic FindOrCreateFile(Realm realm, string hash) =>
        realm.DynamicApi.Find("File", hash) ?? realm.DynamicApi.CreateObject("File", hash);

    private static string ResolveRoot(string? overridePath)
    {
        string? root = string.IsNullOrWhiteSpace(overridePath)
            ? LazerStorage.GetRoot()
            : Path.GetFullPath(overridePath);
        if (string.IsNullOrWhiteSpace(root)
            || !File.Exists(RealmPath(root))
            || !Directory.Exists(FilesRoot(root)))
        {
            throw new DirectoryNotFoundException(
                "No osu!lazer storage root containing client.realm and files was found.");
        }

        return root;
    }

    private static string RealmPath(string root) => Path.Combine(root, "client.realm");
    private static string FilesRoot(string root) => Path.Combine(root, "files");

    private static string PathForHash(string root, string hash)
    {
        ValidateHash(hash);
        return Path.Combine(FilesRoot(ResolveRoot(root)), hash[..1], hash[..2], hash);
    }

    private static void ImportBlob(string root, string hash, byte[] bytes)
    {
        var destination = Path.Combine(FilesRoot(root), hash[..1], hash[..2], hash);
        if (File.Exists(destination))
            return;

        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        var pending = destination + $".kumori-{Guid.NewGuid():N}.tmp";
        try
        {
            File.WriteAllBytes(pending, bytes);
            File.Move(pending, destination, overwrite: false);
        }
        catch (IOException) when (File.Exists(destination))
        {
            // Another writer imported the same content concurrently.
        }
        finally
        {
            try { File.Delete(pending); } catch { }
        }
    }

    private static string ComputeHash(byte[] bytes) =>
        Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

    private static void ValidateHash(string hash)
    {
        if (hash.Length != 64 || hash.Any(ch => !Uri.IsHexDigit(ch)))
            throw new InvalidDataException($"'{hash}' is not a valid osu!lazer content hash.");
    }

    private static bool ReadBool(dynamic value, string property)
    {
        try { return property == "DeletePending" && (bool)value.DeletePending; }
        catch { return false; }
    }

    private static string ReadString(dynamic value, string property, string fallback)
    {
        try
        {
            string? result = property switch
            {
                "Name" => (string)value.Name,
                "Creator" => (string)value.Creator,
                _ => null,
            };
            return string.IsNullOrWhiteSpace(result) ? fallback : result;
        }
        catch
        {
            return fallback;
        }
    }

    private static LazerSkinWriteResult Conflict(string expectedHash, string? currentHash) =>
        new(
            LazerSkinWriteStatus.Conflict,
            expectedHash,
            currentHash,
            "The file changed in osu!lazer after Kumori loaded it. Reload before overwriting it.");

    private static LazerSkinWriteResult Missing(string hash, string message) =>
        new(LazerSkinWriteStatus.Missing, hash, Message: message);
}

[MapTo("Skin")]
internal partial class LazerSkin : RealmObject
{
    [PrimaryKey]
    [MapTo("ID")]
    public Guid Id { get; set; }

    [MapTo("Name")]
    public string Name { get; set; } = "";

    [MapTo("Creator")]
    public string Creator { get; set; } = "";

    [MapTo("DeletePending")]
    public bool DeletePending { get; set; }

    [MapTo("Files")]
    public IList<LazerNamedFileUsage> Files { get; } = null!;
}
