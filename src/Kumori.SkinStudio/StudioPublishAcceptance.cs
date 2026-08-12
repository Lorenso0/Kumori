using System.Security.Cryptography;
using System.Text.Json;
using Kumori.Skins;
using Kumori.Tracking;
using SixLabors.ImageSharp;

namespace Kumori.SkinStudio;

public partial class KumoriSkinStudioGame
{
    private async void startPublishAcceptance()
    {
        if (publishAcceptanceOutputPath is null || gameHost is null)
            return;
        Directory.CreateDirectory(publishAcceptanceOutputPath);
        var manifestPath = Path.Combine(
            publishAcceptanceOutputPath,
            "publish-acceptance-manifest.json");
        var failurePath = Path.Combine(
            publishAcceptanceOutputPath,
            "publish-acceptance-failure.json");
        if (await recoverPreviousTimedOutImportAsync(
                manifestPath,
                failurePath))
        {
            gameHost.Exit();
            return;
        }
        File.Delete(manifestPath);
        File.Delete(failurePath);
        try
        {
            await runOnUpdateThread(publishDraft);
            var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(145);
            while (!publishFinished && DateTime.UtcNow < deadline)
                await Task.Delay(100);
            if (!publishFinished)
                throw new TimeoutException(
                    "The verified publish workflow did not finish in time.");
            if (lastPublishFailure is not null)
                throw new InvalidOperationException(
                    "The verified publish workflow failed.",
                    lastPublishFailure);
            var verification = lastPublishVerification
                               ?? throw new InvalidDataException(
                                   "Publish finished without a verified imported skin.");
            var backup = lastPublishBackup
                         ?? throw new InvalidDataException(
                             "Publish finished without a verified pre-import backup.");
            var archive = lastPublishArchivePath;
            if (string.IsNullOrWhiteSpace(archive)
                || !File.Exists(archive)
                || !File.Exists(backup.ManifestPath))
            {
                throw new InvalidDataException(
                    "The retained publish archive or backup manifest is missing.");
            }

            var screenshotPath = Path.Combine(
                publishAcceptanceOutputPath,
                "verified-publish.png");
            using (var screenshot = await gameHost.TakeScreenshotAsync()
                                    ?? throw new InvalidOperationException(
                                        "The renderer returned no publish screenshot."))
            {
                await screenshot.SaveAsPngAsync(screenshotPath);
            }

            File.WriteAllText(
                manifestPath,
                JsonSerializer.Serialize(
                    new
                    {
                        format = 1,
                        lazer_revision = Program.LazerRevision,
                        player_root = contract.PlayerRoot,
                        draft_id = draft!.DraftId,
                        draft_name = draft.Name,
                        retained_archive = archive,
                        retained_archive_sha256 = publishHashFile(archive),
                        backup_manifest = backup.ManifestPath,
                        backup_skin_count = backup.SkinCount,
                        backup_referenced_blob_count =
                            backup.ReferencedBlobCount,
                        imported_skin_id = verification.SkinId,
                        imported_name = verification.Name,
                        imported_creator = verification.Creator,
                        imported_file_count = verification.FileCount,
                        verification_elapsed_ms =
                            verification.Elapsed.TotalMilliseconds,
                        screenshot = screenshotPath,
                        verification = "passed",
                    },
                    Kumori.Skins.SkinStudioLaunchContract.JsonOptions));
        }
        catch (Exception ex)
        {
            File.WriteAllText(
                failurePath,
                JsonSerializer.Serialize(
                    new
                    {
                        verification = "failed",
                        type = ex.GetType().FullName,
                        message = ex.Message,
                        inner = ex.InnerException?.Message,
                        retained_archive = lastPublishArchivePath,
                        backup_manifest = lastPublishBackup?.ManifestPath,
                    },
                    Kumori.Skins.SkinStudioLaunchContract.JsonOptions));
        }
        finally
        {
            gameHost.Exit();
        }
    }

    private static string publishHashFile(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexStringLower(SHA256.HashData(stream));
    }

    private async Task<bool> recoverPreviousTimedOutImportAsync(
        string manifestPath,
        string failurePath)
    {
        if (!File.Exists(failurePath)
            || draft is null
            || gameHost is null
            || string.IsNullOrWhiteSpace(contract.PlayerRoot))
        {
            return false;
        }
        try
        {
            using var failure = JsonDocument.Parse(
                File.ReadAllText(failurePath));
            var archive = failure.RootElement
                .GetProperty("retained_archive")
                .GetString();
            var backupManifest = failure.RootElement
                .GetProperty("backup_manifest")
                .GetString();
            if (string.IsNullOrWhiteSpace(backupManifest)
                || !File.Exists(backupManifest))
            {
                backupManifest = Directory.Exists(Path.Combine(
                        contract.WorkspacePath,
                        "real-lazer-backups"))
                    ? Directory.EnumerateFiles(
                            Path.Combine(
                                contract.WorkspacePath,
                                "real-lazer-backups"),
                            "manifest.json",
                            SearchOption.AllDirectories)
                        .Where(path => (Path.GetFileName(
                                Path.GetDirectoryName(path) ?? "")
                            ?.EndsWith(
                                "-before-publish",
                                StringComparison.OrdinalIgnoreCase) ?? false))
                        .OrderByDescending(File.GetLastWriteTimeUtc)
                        .FirstOrDefault()
                    : null;
            }
            if (string.IsNullOrWhiteSpace(backupManifest)
                || !File.Exists(backupManifest))
            {
                return false;
            }
            if (string.IsNullOrWhiteSpace(archive)
                || !File.Exists(archive))
            {
                archive = new SkinPackageService(drafts).Export(
                    draft.DraftId,
                    Path.Combine(
                        contract.WorkspacePath,
                        "publish-queue",
                        $"{sanitizeFilename(draft.Name)}-retained-recovery.osk"));
            }

            using var backup = JsonDocument.Parse(
                File.ReadAllText(backupManifest));
            var beforeIds = backup.RootElement
                .GetProperty("skins")
                .EnumerateArray()
                .Select(item => item.GetProperty("id").GetGuid())
                .ToHashSet();
            var expectedFiles = new SkinPackageService(drafts).Materialize(
                draft.DraftId);
            var realm = new LazerSkinRealmService();
            var imported = realm
                .LoadCatalog(contract.PlayerRoot)
                .Skins
                .FirstOrDefault(skin =>
                    !beforeIds.Contains(skin.Id)
                    && LazerSkinPublishVerificationService.Matches(
                        skin,
                        draft.Name,
                        draft.Creator,
                        expectedFiles)
                    && LazerSkinPublishVerificationService
                        .SkinIniMatchesAfterImport(
                            expectedFiles["skin.ini"],
                            realm.ReadFile(
                                contract.PlayerRoot,
                                skin.Files.Single(file =>
                                    file.Filename.Equals(
                                        "skin.ini",
                                        StringComparison.OrdinalIgnoreCase))
                                    .Hash),
                            skin.Name,
                            skin.Creator));
            if (imported is null)
                return false;

            var screenshotPath = Path.Combine(
                publishAcceptanceOutputPath!,
                "verified-publish.png");
            using (var screenshot = await gameHost.TakeScreenshotAsync()
                                    ?? throw new InvalidOperationException(
                                        "The renderer returned no publish screenshot."))
            {
                await screenshot.SaveAsPngAsync(screenshotPath);
            }
            var blobCount = backup.RootElement
                .GetProperty("referenced_blobs")
                .GetArrayLength();
            File.WriteAllText(
                manifestPath,
                JsonSerializer.Serialize(
                    new
                    {
                        format = 1,
                        lazer_revision = Program.LazerRevision,
                        player_root = contract.PlayerRoot,
                        draft_id = draft.DraftId,
                        draft_name = draft.Name,
                        retained_archive = archive,
                        retained_archive_sha256 =
                            publishHashFile(archive),
                        backup_manifest = backupManifest,
                        backup_skin_count = beforeIds.Count,
                        backup_referenced_blob_count = blobCount,
                        imported_skin_id = imported.Id,
                        imported_name = imported.Name,
                        imported_creator = imported.Creator,
                        imported_file_count = imported.Files.Count,
                        recovered_from_previous_timeout = true,
                        screenshot = screenshotPath,
                        verification = "passed",
                    },
                    SkinStudioLaunchContract.JsonOptions));
            File.Delete(failurePath);
            return true;
        }
        catch
        {
            return false;
        }
    }
}
