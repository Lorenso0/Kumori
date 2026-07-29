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
    Deleted,
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
        or LazerSkinWriteStatus.Replaced
        or LazerSkinWriteStatus.Deleted;
}

public sealed record LazerSkinBatchMutation(
    string Filename,
    byte[] Bytes,
    string? ExpectedHash,
    bool IsDeletion = false);

public sealed record LazerSkinBatchWriteResult(
    bool Succeeded,
    IReadOnlyList<LazerSkinWriteResult> Results,
    string? FailedFilename = null,
    string? Message = null);

/// <summary>
/// Reads and writes osu!lazer skins through Realm's dynamic schema. Dynamic mode is deliberate:
/// the embedded schema in client.realm remains authoritative as lazer evolves.
/// </summary>
public interface ILazerSkinRealmService
{
    LazerSkinCatalog LoadCatalog(string? rootOverride = null);
    LazerSkinInfo CreateSkin(string rootPath, string name, string creator, byte[] skinIniContents);
    LazerSkinWriteResult UpdateSkinIdentity(
        string rootPath,
        Guid skinId,
        string name,
        string creator,
        byte[] skinIniContents,
        string? expectedSkinIniHash);
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
    LazerSkinWriteResult DeleteFile(
        string rootPath,
        Guid skinId,
        string filename,
        string expectedHash);
    LazerSkinBatchWriteResult ApplyBatch(
        string rootPath,
        Guid skinId,
        IReadOnlyList<LazerSkinBatchMutation> mutations);
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

    /// <summary>
    /// Creates an editable lazer skin with its initial skin.ini in one Realm transaction.
    /// A skin without files is ignored by lazer's catalog, so the initial file is required.
    /// </summary>
    public LazerSkinInfo CreateSkin(string rootPath, string name, string creator, byte[] skinIniContents)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(skinIniContents);

        lock (realmGate)
        {
            var root = ResolveRoot(rootPath);
            var skinId = Guid.NewGuid();
            var hash = ComputeHash(skinIniContents);
            ImportBlob(root, hash, skinIniContents);

            using var realm = OpenRealm(root, readOnly: false);
            realm.Write(() =>
            {
                dynamic skin = realm.DynamicApi.CreateObject("Skin", skinId);
                skin.Name = name.Trim();
                skin.Creator = creator.Trim();
                skin.DeletePending = false;

                dynamic usage = realm.DynamicApi.AddEmbeddedObjectToList(skin.Files);
                usage.File = FindOrCreateFile(realm, hash);
                usage.Filename = "skin.ini";
            });

            return new LazerSkinInfo(
                skinId,
                name.Trim(),
                creator.Trim(),
                [new LazerSkinFileInfo("skin.ini", hash, skinIniContents.LongLength)]);
        }
    }

    /// <summary>
    /// Keeps the lazer catalog identity and the [General] identity in skin.ini
    /// in lockstep. The catalog row and skin.ini usage change in one Realm
    /// transaction after the current skin.ini hash has been checked.
    /// </summary>
    public LazerSkinWriteResult UpdateSkinIdentity(
        string rootPath,
        Guid skinId,
        string name,
        string creator,
        byte[] skinIniContents,
        string? expectedSkinIniHash)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(skinIniContents);
        if (expectedSkinIniHash is not null)
            ValidateHash(expectedSkinIniHash);

        lock (realmGate)
        {
            var root = ResolveRoot(rootPath);
            var newHash = ComputeHash(skinIniContents);
            ImportBlob(root, newHash, skinIniContents);
            using var realm = OpenRealm(root, readOnly: false);
            var conflicted = false;
            var missing = false;
            string? currentHash = null;
            var added = false;
            realm.Write(() =>
            {
                dynamic? skin = realm.DynamicApi.Find("Skin", skinId);
                if (skin is null)
                {
                    missing = true;
                    return;
                }

                dynamic? usage = FindUsage(skin, "skin.ini");
                currentHash = usage is null ? null : (string)usage.File.Hash;
                if (expectedSkinIniHash is null ? usage is not null
                    : !string.Equals(expectedSkinIniHash, currentHash, StringComparison.OrdinalIgnoreCase))
                {
                    conflicted = true;
                    return;
                }

                skin.Name = name.Trim();
                skin.Creator = creator.Trim();
                dynamic file = FindOrCreateFile(realm, newHash);
                if (usage is null)
                {
                    usage = realm.DynamicApi.AddEmbeddedObjectToList(skin.Files);
                    usage.Filename = "skin.ini";
                    added = true;
                }
                usage.File = file;
            });

            if (missing)
                return Missing(expectedSkinIniHash ?? "", "The selected skin no longer exists.");
            if (conflicted)
                return Conflict(expectedSkinIniHash ?? "", currentHash);
            return new LazerSkinWriteResult(
                added ? LazerSkinWriteStatus.Added : LazerSkinWriteStatus.Replaced,
                newHash);
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

    public LazerSkinWriteResult DeleteFile(
        string rootPath,
        Guid skinId,
        string filename,
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

            dynamic? existing = FindUsage(skin, filename);
            if (existing is null)
                return Missing(expectedHash, $"'{filename}' no longer exists in this skin.");

            string currentHash = (string)existing.File.Hash;
            if (!string.Equals(expectedHash, currentHash, StringComparison.OrdinalIgnoreCase))
                return Conflict(expectedHash, currentHash);

            var conflicted = false;
            var missing = false;
            string? transactionCurrentHash = null;
            realm.Write(() =>
            {
                dynamic? currentSkin = realm.DynamicApi.Find("Skin", skinId);
                dynamic? currentUsage = currentSkin is null ? null : FindUsage(currentSkin, filename);
                if (currentUsage is null)
                {
                    missing = true;
                    return;
                }

                transactionCurrentHash = (string)currentUsage.File.Hash;
                if (!string.Equals(expectedHash, transactionCurrentHash, StringComparison.OrdinalIgnoreCase))
                {
                    conflicted = true;
                    return;
                }

                currentSkin!.Files.Remove(currentUsage);
            });

            if (missing)
                return Missing(expectedHash, $"'{filename}' no longer exists in this skin.");
            return conflicted
                ? Conflict(expectedHash, transactionCurrentHash)
                : new LazerSkinWriteResult(LazerSkinWriteStatus.Deleted, expectedHash);
        }
    }

    /// <summary>
    /// Applies the complete Skin Studio draft in one Realm transaction. Blob
    /// imports are content-addressed and may happen first, but no skin usage is
    /// changed unless every optimistic-concurrency check succeeds.
    /// </summary>
    public LazerSkinBatchWriteResult ApplyBatch(
        string rootPath,
        Guid skinId,
        IReadOnlyList<LazerSkinBatchMutation> mutations)
    {
        ArgumentNullException.ThrowIfNull(mutations);
        if (mutations.Count == 0)
            return new LazerSkinBatchWriteResult(true, []);
        var duplicate = mutations.GroupBy(
                mutation => mutation.Filename,
                StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault(group => group.Count() > 1);
        if (duplicate is not null)
            throw new ArgumentException($"The batch contains duplicate filename '{duplicate.Key}'.");
        foreach (var mutation in mutations)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(mutation.Filename);
            if (mutation.IsDeletion && mutation.ExpectedHash is null)
                throw new ArgumentException($"Deletion of '{mutation.Filename}' requires an expected hash.");
            if (mutation.ExpectedHash is not null)
                ValidateHash(mutation.ExpectedHash);
        }

        lock (realmGate)
        {
            var root = ResolveRoot(rootPath);
            var newHashes = mutations.Select(mutation =>
                    mutation.IsDeletion ? null : ComputeHash(mutation.Bytes))
                .ToArray();
            for (var index = 0; index < mutations.Count; index++)
                if (newHashes[index] is not null)
                    ImportBlob(root, newHashes[index]!, mutations[index].Bytes);

            using var realm = OpenRealm(root, readOnly: false);
            var results = new LazerSkinWriteResult[mutations.Count];
            string? failedFilename = null;
            LazerSkinWriteResult? failure = null;
            try
            {
                realm.Write(() =>
                {
                    dynamic? skin = realm.DynamicApi.Find("Skin", skinId);
                    if (skin is null)
                    {
                        failedFilename = mutations[0].Filename;
                        failure = Missing(
                            mutations[0].ExpectedHash ?? "",
                            "The selected skin no longer exists.");
                        throw new BatchPreflightException();
                    }

                    var usages = new dynamic?[mutations.Count];
                    var currentHashes = new string?[mutations.Count];
                    for (var index = 0; index < mutations.Count; index++)
                    {
                        var mutation = mutations[index];
                        dynamic? usage = FindUsage(skin, mutation.Filename);
                        usages[index] = usage;
                        currentHashes[index] = usage is null ? null : (string)usage.File.Hash;
                        var valid = mutation.IsDeletion
                            ? usage is not null
                              && string.Equals(
                                  mutation.ExpectedHash,
                                  currentHashes[index],
                                  StringComparison.OrdinalIgnoreCase)
                            : mutation.ExpectedHash is null
                                ? usage is null
                                : usage is not null
                                  && string.Equals(
                                      mutation.ExpectedHash,
                                      currentHashes[index],
                                      StringComparison.OrdinalIgnoreCase);
                        if (valid) continue;
                        failedFilename = mutation.Filename;
                        failure = mutation.IsDeletion && usage is null
                            ? Missing(mutation.ExpectedHash!, $"'{mutation.Filename}' no longer exists in this skin.")
                            : Conflict(mutation.ExpectedHash ?? "", currentHashes[index]);
                        throw new BatchPreflightException();
                    }

                    for (var index = 0; index < mutations.Count; index++)
                    {
                        var mutation = mutations[index];
                        dynamic? usage = usages[index];
                        var currentHash = currentHashes[index];
                        if (mutation.IsDeletion)
                        {
                            skin.Files.Remove(usage!);
                            results[index] = new LazerSkinWriteResult(
                                LazerSkinWriteStatus.Deleted,
                                mutation.ExpectedHash!);
                            continue;
                        }

                        var newHash = newHashes[index]!;
                        if (string.Equals(newHash, currentHash, StringComparison.OrdinalIgnoreCase))
                        {
                            results[index] = new LazerSkinWriteResult(
                                LazerSkinWriteStatus.Unchanged,
                                newHash);
                            continue;
                        }
                        dynamic fileRow = FindOrCreateFile(realm, newHash);
                        if (usage is null)
                        {
                            dynamic newUsage = realm.DynamicApi.AddEmbeddedObjectToList(skin.Files);
                            newUsage.File = fileRow;
                            newUsage.Filename = mutation.Filename.Replace('\\', '/');
                            results[index] = new LazerSkinWriteResult(
                                LazerSkinWriteStatus.Added,
                                newHash);
                        }
                        else
                        {
                            usage.File = fileRow;
                            results[index] = new LazerSkinWriteResult(
                                LazerSkinWriteStatus.Replaced,
                                newHash);
                        }
                    }
                });
            }
            catch (BatchPreflightException)
            {
                return new LazerSkinBatchWriteResult(
                    false,
                    failure is null ? [] : [failure],
                    failedFilename,
                    failure?.Message);
            }
            return new LazerSkinBatchWriteResult(true, results);
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

    private sealed class BatchPreflightException : Exception;
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
