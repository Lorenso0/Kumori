using System.Text.Json;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Kumori.App.Skins;
using Xunit;

namespace Kumori.App.Tests;

public sealed class SkinExtrasLibraryTests
{
    [Fact]
    public void Storage_layout_uses_one_osu_root_without_a_redundant_osu_area()
    {
        var root = Path.Combine("Extras", "osu");

        Assert.Equal(
            Path.Combine(root, "Hit circles"),
            SkinExtraNaming.StorageParent(root, "osu!", "Hit circles"));
        Assert.Equal(
            Path.Combine(root, "Interface", "Scorebar"),
            SkinExtraNaming.StorageParent(root, "Interface", "Scorebar"));
        Assert.Equal(
            Path.Combine(root, "Audio", "Combobreak"),
            SkinExtraNaming.StorageParent(root, "Audio", "Combobreak"));
    }

    [Fact]
    public void Persistent_index_reuses_cache_and_refreshes_changed_manifests()
    {
        var root = TempDirectory();
        try
        {
            var descriptor = WritePack(root, "First");
            var first = Assert.Single(SkinExtraPackIndex.Scan(root));
            Assert.Equal("First", first.Manifest.DisplayName);
            Assert.True(File.Exists(Path.Combine(root, ".kumori", "index-v1.json")));

            var manifestPath = Path.Combine(descriptor.DirectoryPath, "extras.json");
            File.WriteAllText(
                manifestPath,
                File.ReadAllText(manifestPath).Replace(
                    "\"displayName\": \"First\"",
                    "\"displayName\": \"Renamed\"",
                    StringComparison.Ordinal));

            var refreshed = Assert.Single(SkinExtraPackIndex.Scan(root));
            Assert.Equal("Renamed", refreshed.Manifest.DisplayName);

            var indexPath = Path.Combine(root, ".kumori", "index-v1.json");
            var stableWriteTime = new DateTime(2024, 1, 2, 3, 4, 5, DateTimeKind.Utc);
            File.SetLastWriteTimeUtc(indexPath, stableWriteTime);
            _ = SkinExtraPackIndex.Scan(root);
            Assert.Equal(stableWriteTime, File.GetLastWriteTimeUtc(indexPath));
        }
        finally { Delete(root); }
    }

    [Fact]
    public void Object_store_materializes_one_object_for_reused_bytes()
    {
        var root = TempDirectory();
        try
        {
            var bytes = new byte[] { 4, 8, 15, 16, 23, 42 };
            SkinExtraObjectStore.Materialize(root, Path.Combine(root, "a", "sound.wav"), bytes);
            SkinExtraObjectStore.Materialize(root, Path.Combine(root, "b", "sound.wav"), bytes);

            var stats = SkinExtraObjectStore.GetStatistics(root);
            Assert.Equal(1, stats.Objects);
            Assert.Equal(bytes, File.ReadAllBytes(Path.Combine(root, "a", "sound.wav")));
            Assert.Equal(bytes, File.ReadAllBytes(Path.Combine(root, "b", "sound.wav")));
        }
        finally { Delete(root); }
    }

    [Fact]
    public void Favorites_tags_and_recent_usage_survive_reload()
    {
        var root = TempDirectory();
        try
        {
            var used = DateTimeOffset.UtcNow;
            SkinExtrasLibraryStateStore.Update(root, "abc", state =>
            {
                state.Favorite = true;
                state.Tags = [" clean ", "HD", "clean"];
                state.LastUsedUtc = used;
                state.DisplayNameOverride = "My local name";
            });

            var state = SkinExtrasLibraryStateStore.Get(root, "abc");
            Assert.True(state.Favorite);
            Assert.Equal(["clean", "HD"], state.Tags);
            Assert.Equal(used, state.LastUsedUtc);
            Assert.Equal("My local name", state.DisplayNameOverride);

            var snapshot = SkinExtrasLibraryStateStore.GetAll(root);
            var snapshotted = Assert.Single(snapshot).Value;
            Assert.True(snapshotted.Favorite);
            Assert.Equal(["clean", "HD"], snapshotted.Tags);

            snapshotted.Favorite = false;
            Assert.True(SkinExtrasLibraryStateStore.Get(root, "abc").Favorite);
        }
        finally { Delete(root); }
    }

    [Fact]
    public void Renaming_a_pack_updates_its_folder_and_manifest_without_losing_identity()
    {
        var root = TempDirectory();
        try
        {
            var original = WritePack(root, "Original");
            var fingerprint = original.Manifest.Fingerprint;
            SkinExtrasLibraryStateStore.Update(
                root,
                fingerprint,
                state => state.Favorite = true);

            var renamed = SkinExtraPackRenamer.Rename(root, original, "Renamed Pack");

            Assert.False(Directory.Exists(original.DirectoryPath));
            Assert.True(Directory.Exists(renamed.DirectoryPath));
            Assert.Equal("Renamed Pack", renamed.Manifest.DisplayName);
            Assert.Equal(fingerprint, renamed.Manifest.Fingerprint);
            Assert.Equal(
                "Renamed Pack",
                SkinExtraManifestSerializer.TryRead(renamed.DirectoryPath)!.DisplayName);
            Assert.True(SkinExtrasLibraryStateStore.Get(root, fingerprint).Favorite);
            Assert.Equal(
                renamed.DirectoryPath,
                Assert.Single(SkinExtraPackIndex.Scan(root)).DirectoryPath);
        }
        finally { Delete(root); }
    }

    [Fact]
    public void Delete_target_validation_allows_only_pack_folders_inside_extras()
    {
        var root = TempDirectory();
        try
        {
            var pack = Path.Combine(root, "osu!", "Cursor", "Pack");
            Directory.CreateDirectory(pack);

            Assert.Equal(
                Path.GetFullPath(pack),
                SkinExtraPackDeletion.ResolvePackDirectory(root, pack));
            Assert.Throws<InvalidOperationException>(() =>
                SkinExtraPackDeletion.ResolvePackDirectory(root, root));
            Assert.Throws<InvalidOperationException>(() =>
                SkinExtraPackDeletion.ResolvePackDirectory(
                    root,
                    Path.Combine(root, ".kumori", "objects")));
            Assert.Throws<InvalidOperationException>(() =>
                SkinExtraPackDeletion.ResolvePackDirectory(
                    root,
                    Directory.GetParent(root)!.FullName));
        }
        finally { Delete(root); }
    }

    [Fact]
    public void Validator_finds_incomplete_number_fonts_and_rejects_unsafe_paths()
    {
        var root = TempDirectory();
        try
        {
            var manifest = new SkinExtraPackManifest
            {
                Id = "font",
                DisplayName = "Partial font",
                FamilyId = "osu.number-font",
                Area = "osu!",
                FamilyName = "Number fonts",
                Fingerprint = "unused",
                Files =
                [
                    new SkinExtraManifestFile(
                        "default-0.png",
                        "default-0.png",
                        "default-0.png",
                        "a",
                        "a"),
                    new SkinExtraManifestFile(
                        "../outside.png",
                        "../outside.png",
                        "outside.png",
                        "b",
                        "b"),
                ],
            };
            var report = SkinExtraPackValidator.Validate(
                new SkinExtraPackDescriptor(root, manifest, false),
                verifyContent: false);

            Assert.Contains(report.Issues, issue => issue.Code == "unsafe-path");
            Assert.Equal(9, report.Issues.Count(issue => issue.Code == "missing-digit"));
        }
        finally { Delete(root); }
    }

    [Fact]
    public void Validator_warns_for_followpoint_timing_gaps_that_staging_can_restore()
    {
        var root = TempDirectory();
        try
        {
            SkinExtraHealthReport Validate(params string[] filenames)
            {
                var manifest = new SkinExtraPackManifest
                {
                    Id = "followpoints",
                    DisplayName = "Followpoints",
                    FamilyId = "osu.followpoints",
                    Area = "osu!",
                    FamilyName = "Followpoints",
                    Fingerprint = "unused",
                    Files = filenames.Select(filename => new SkinExtraManifestFile(
                        filename,
                        filename,
                        filename,
                        "unused",
                        "unused")).ToList(),
                };
                return SkinExtraPackValidator.Validate(
                    new SkinExtraPackDescriptor(root, manifest, false),
                    verifyContent: false);
            }

            var late = Validate("followpoint-26.png", "followpoint-27@2x.png");
            var gap = Validate("followpoint-0.png", "followpoint-2.png");

            Assert.Contains(
                late.Issues,
                issue => issue.Code == "followpoint-sequence-start"
                         && issue.Severity == SkinExtraHealthSeverity.Warning);
            Assert.Contains(
                gap.Issues,
                issue => issue.Code == "followpoint-sequence-gap"
                         && issue.Severity == SkinExtraHealthSeverity.Warning);
            Assert.DoesNotContain(
                late.Issues,
                issue => issue.Code == "followpoint-sequence-start"
                         && issue.Severity == SkinExtraHealthSeverity.Error);
            Assert.DoesNotContain(
                gap.Issues,
                issue => issue.Code == "followpoint-sequence-gap"
                         && issue.Severity == SkinExtraHealthSeverity.Error);
            Assert.DoesNotContain(
                Validate("followpoint.png").Issues,
                issue => issue.Code.StartsWith(
                    "followpoint-sequence",
                    StringComparison.Ordinal));
            Assert.DoesNotContain(
                Validate(
                    "followpoint-0.png",
                    "followpoint-0@2x.png",
                    "followpoint-1.png").Issues,
                issue => issue.Code.StartsWith(
                    "followpoint-sequence",
                    StringComparison.Ordinal));
        }
        finally { Delete(root); }
    }

    [Fact]
    public void Repair_collapses_duplicate_manifest_targets_to_the_real_pack_files()
    {
        var root = TempDirectory();
        try
        {
            var directory = Path.Combine(root, "osu!", "Hit circles", "Pack");
            Directory.CreateDirectory(directory);
            var bytes = new byte[] { 1, 2, 3, 4 };
            File.WriteAllBytes(Path.Combine(directory, "hitcircle.png"), bytes);
            var stale = SkinExtraFingerprint.Describe(
                "Backups/old/hitcircle.png",
                "hitcircle.png",
                [9]);
            var current = SkinExtraFingerprint.Describe(
                "hitcircle.png",
                "hitcircle.png",
                bytes);
            var manifest = new SkinExtraPackManifest
            {
                Id = "duplicate",
                DisplayName = "Pack",
                FamilyId = "osu.hitcircles",
                Area = "osu!",
                FamilyName = "Hit circles",
                Fingerprint = "stale",
                Files = [stale, current],
            };
            File.WriteAllBytes(
                Path.Combine(directory, "extras.json"),
                SkinExtraManifestSerializer.Serialize(manifest));
            var pack = new SkinExtraPackDescriptor(directory, manifest, false);

            var repaired = SkinExtraPackValidator.Repair(pack);

            var file = Assert.Single(repaired.Manifest.Files);
            Assert.Equal("hitcircle.png", file.TargetFilename);
            Assert.Equal(current.ByteHash, file.ByteHash);
            Assert.DoesNotContain(
                SkinExtraPackValidator.Validate(repaired).Issues,
                issue => issue.Code == "duplicate-target");
        }
        finally { Delete(root); }
    }

    [Fact]
    public void Portable_package_round_trips_and_blocks_a_second_copy()
    {
        var sourceRoot = TempDirectory();
        var targetRoot = TempDirectory();
        var package = Path.Combine(Path.GetTempPath(), $"kumori-{Guid.NewGuid():N}.kextra");
        try
        {
            var source = WritePack(sourceRoot, "Portable");
            SkinExtraPortablePackage.Export(source, package);

            var imported = SkinExtraPortablePackage.Import(package, targetRoot);
            Assert.False(imported.WasDuplicate);
            Assert.Equal(source.Manifest.Fingerprint, imported.Pack.Manifest.Fingerprint);
            Assert.True(File.Exists(Path.Combine(imported.Pack.DirectoryPath, "combobreak.wav")));

            // A package fingerprint is only a hint. Duplicate protection must
            // derive identity from the verified image/audio payloads.
            using (var archive = ZipFile.Open(package, ZipArchiveMode.Update))
            {
                var entry = Assert.Single(archive.Entries.Where(candidate =>
                    candidate.FullName.Equals(
                        "extras.json",
                        StringComparison.OrdinalIgnoreCase)));
                string json;
                using (var reader = new StreamReader(entry.Open()))
                    json = reader.ReadToEnd();
                entry.Delete();
                var replacement = archive.CreateEntry(
                    "extras.json",
                    CompressionLevel.Optimal);
                using var writer = new StreamWriter(replacement.Open());
                writer.Write(json.Replace(
                    source.Manifest.Fingerprint,
                    new string('f', 64),
                    StringComparison.OrdinalIgnoreCase));
            }

            var duplicate = SkinExtraPortablePackage.Import(package, targetRoot);
            Assert.True(duplicate.WasDuplicate);
            Assert.Equal(imported.Pack.DirectoryPath, duplicate.Pack.DirectoryPath);
        }
        finally
        {
            Delete(sourceRoot);
            Delete(targetRoot);
            try { File.Delete(package); } catch { }
        }
    }

    [Fact]
    public void Portable_package_export_is_byte_for_byte_deterministic()
    {
        var root = TempDirectory();
        var first = Path.Combine(Path.GetTempPath(), $"kumori-first-{Guid.NewGuid():N}.kextra");
        var second = Path.Combine(Path.GetTempPath(), $"kumori-second-{Guid.NewGuid():N}.kextra");
        try
        {
            var pack = WritePack(root, "Deterministic");
            SkinExtraPortablePackage.Export(pack, first);
            Thread.Sleep(1100);
            SkinExtraPortablePackage.Export(pack, second);

            Assert.Equal(
                SHA256.HashData(File.ReadAllBytes(first)),
                SHA256.HashData(File.ReadAllBytes(second)));
            using var archive = ZipFile.OpenRead(first);
            Assert.All(
                archive.Entries,
                entry => Assert.Equal(
                    new DateTime(1980, 1, 1, 0, 0, 0, DateTimeKind.Unspecified),
                    entry.LastWriteTime.DateTime));
        }
        finally
        {
            Delete(root);
            try { File.Delete(first); } catch { }
            try { File.Delete(second); } catch { }
        }
    }

    [Fact]
    public void Portable_cursor_packages_drop_cursor_middle_on_import_and_export()
    {
        var targetRoot = TempDirectory();
        var catalogRoot = TempDirectory();
        var package = Path.Combine(
            Path.GetTempPath(),
            $"kumori-cursor-{Guid.NewGuid():N}.kextra");
        var exported = Path.Combine(
            Path.GetTempPath(),
            $"kumori-cursor-export-{Guid.NewGuid():N}.kextra");
        try
        {
            var cursorBytes = PngWithDpi(96);
            var middleBytes = PngWithDpi(120);
            var cursor = SkinExtraFingerprint.Describe(
                "cursor.png",
                "cursor.png",
                cursorBytes);
            var middle = SkinExtraFingerprint.Describe(
                "cursormiddle.png",
                "cursormiddle.png",
                middleBytes);
            var manifest = new SkinExtraPackManifest
            {
                Id = "legacy-cursor",
                DisplayName = "Legacy cursor",
                FamilyId = "osu.cursor",
                Area = "osu!",
                FamilyName = "Cursor",
                Fingerprint = SkinExtraFingerprint.ForPack(
                    "osu.cursor",
                    [cursor, middle]),
                Files = [cursor, middle],
            };
            using (var archive = ZipFile.Open(package, ZipArchiveMode.Create))
            {
                using (var output = archive.CreateEntry("extras.json").Open())
                    output.Write(SkinExtraManifestSerializer.Serialize(manifest));
                using (var output = archive.CreateEntry("assets/cursor.png").Open())
                    output.Write(cursorBytes);
                using (var output = archive.CreateEntry("assets/cursormiddle.png").Open())
                    output.Write(middleBytes);
            }

            var imported = SkinExtraPortablePackage.Import(package, targetRoot);
            Assert.Equal(manifest.Fingerprint, imported.SourceFingerprint);
            Assert.NotEqual(
                manifest.Fingerprint,
                imported.Pack.Manifest.Fingerprint);
            Assert.Equal(
                ["cursor.png"],
                imported.Pack.Manifest.Files.Select(file => file.TargetFilename));
            Assert.False(File.Exists(Path.Combine(
                imported.Pack.DirectoryPath,
                "cursormiddle.png")));

            var phases = new List<string>();
            var catalogImported = SkinExtraPortablePackage.ImportForCatalog(
                package,
                catalogRoot,
                CancellationToken.None,
                phases.Add);
            Assert.Equal(manifest.Fingerprint, catalogImported.SourceFingerprint);
            Assert.DoesNotContain(catalogImported.Pack.Manifest.Files, file =>
                SkinCursorMiddlePolicy.IsCursorMiddle(file.TargetFilename));
            Assert.Contains("Validating package", phases);
            Assert.Contains("Writing files", phases);
            Assert.Contains("Verifying installed files", phases);
            Assert.DoesNotContain("Checking the library", phases);

            SkinExtraPortablePackage.Export(imported.Pack, exported);
            using var exportedArchive = ZipFile.OpenRead(exported);
            Assert.DoesNotContain(exportedArchive.Entries, entry =>
                entry.FullName.Contains(
                    "cursormiddle",
                    StringComparison.OrdinalIgnoreCase));
        }
        finally
        {
            Delete(targetRoot);
            Delete(catalogRoot);
            try { File.Delete(package); } catch { }
            try { File.Delete(exported); } catch { }
        }
    }

    [Fact]
    public void Portable_package_rejects_undeclared_archive_entries()
    {
        var sourceRoot = TempDirectory();
        var targetRoot = TempDirectory();
        var package = Path.Combine(
            Path.GetTempPath(),
            $"kumori-undeclared-{Guid.NewGuid():N}.kextra");
        try
        {
            var source = WritePack(sourceRoot, "Portable");
            SkinExtraPortablePackage.Export(source, package);
            using (var archive = ZipFile.Open(package, ZipArchiveMode.Update))
            {
                using var writer = new StreamWriter(
                    archive.CreateEntry("assets/not-declared.png").Open());
                writer.Write("not declared");
            }

            var error = Assert.Throws<InvalidDataException>(() =>
            {
                _ = SkinExtraPortablePackage.Import(package, targetRoot);
            });
            Assert.Contains("undeclared", error.Message, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            if (Directory.Exists(sourceRoot)) Directory.Delete(sourceRoot, true);
            if (Directory.Exists(targetRoot)) Directory.Delete(targetRoot, true);
            if (File.Exists(package)) File.Delete(package);
        }
    }

    [Fact]
    public void Full_skin_folders_and_osk_archives_feed_the_same_extraction_pipeline()
    {
        var folder = TempDirectory();
        var archive = Path.Combine(Path.GetTempPath(), $"kumori-skin-{Guid.NewGuid():N}.osk");
        try
        {
            File.WriteAllText(
                Path.Combine(folder, "skin.ini"),
                "[General]\nName: Imported Skin\nAuthor: Tester\n");
            File.WriteAllBytes(Path.Combine(folder, "combobreak.wav"), [1, 2, 3, 4]);
            var backup = Path.Combine(folder, "Backups");
            Directory.CreateDirectory(backup);
            File.WriteAllBytes(Path.Combine(backup, "combobreak.wav"), [9, 9, 9, 9]);
            ZipFile.CreateFromDirectory(folder, archive);
            var service = new SkinExtrasExtractionService();

            var fromFolder = service.ReadFolder(folder);
            var fromArchive = service.ReadOsk(archive);

            Assert.Equal("Imported Skin", fromFolder.DisplayName);
            Assert.Equal("Imported Skin", fromArchive.DisplayName);
            Assert.Equal("Tester", fromArchive.Author);
            Assert.Single(fromFolder.Files.Where(file => file.Filename == "combobreak.wav"));
            Assert.Single(fromArchive.Files.Where(file => file.Filename == "combobreak.wav"));
            Assert.Contains(service.Analyze(fromFolder), family =>
                family.Definition.Id == "audio.combobreak");
            Assert.Contains(service.Analyze(fromArchive), family =>
                family.Definition.Id == "audio.combobreak");
        }
        finally
        {
            Delete(folder);
            try { File.Delete(archive); } catch { }
        }
    }

    [Fact]
    public void Colour_packs_receive_a_readable_human_name()
    {
        var combo = SkinExtraNaming.PackNameForFamily(
            "Loose files",
            "osu.combo-colours",
            [
                new SkinExtraIniPatchEntry("Colours", "Combo1", "80, 220, 255"),
                new SkinExtraIniPatchEntry("Colours", "Combo2", "255, 102, 180"),
            ]);
        var slider = SkinExtraNaming.PackNameForFamily(
            "Loose files",
            "osu.slider-colours",
            [new SkinExtraIniPatchEntry("Colours", "SliderBorder", "18, 18, 18")]);

        Assert.Equal("Cyan + Pink", combo);
        Assert.Equal("Black", slider);
    }

    [Fact]
    public void Existing_colour_pack_display_names_are_recomputed_from_their_values()
    {
        var manifest = new SkinExtraPackManifest
        {
            Id = "old-colours",
            DisplayName = "Old source skin - Red + Blue Hitcircle colours",
            FamilyId = "osu.combo-colours",
            Area = "osu!",
            FamilyName = "Combo colours",
            Fingerprint = new string('a', 64),
            IniPatch =
            [
                new SkinExtraIniPatchEntry("Colours", "Combo1", "255,0,0"),
                new SkinExtraIniPatchEntry("Colours", "Combo2", "0,80,255"),
            ],
        };

        Assert.Equal("Red + Blue", SkinExtraNaming.DisplayNameForPack(manifest));
    }

    [Fact]
    public void Audio_imports_omit_existing_identical_targets_but_keep_new_sounds()
    {
        var root = TempDirectory();
        try
        {
            var service = new SkinExtrasExtractionService();
            var family = SkinExtraFamilyRegistry.ById("audio.interface")!;
            SkinExtractionFamily Selection(params SkinExtractionFile[] files) => new()
            {
                Definition = family,
                Files = files,
                IniPatch = [],
            };
            SkinExtractionSource Source(string name, params SkinExtractionFile[] files) => new()
            {
                DisplayName = name,
                SourceLabel = name,
                Files = files,
            };

            var normal = new SkinExtractionFile("menuhit.wav", [1, 2, 3]);
            var clap = new SkinExtractionFile("menuclick.wav", [4, 5, 6]);
            var first = Assert.Single(service.Extract(
                Source("First", normal),
                [Selection(normal)],
                root,
                "First"));
            Assert.Equal(SkinExtraExtractionStatus.Extracted, first.Status);

            var second = Assert.Single(service.Extract(
                Source("Second", normal, clap),
                [Selection(normal, clap)],
                root,
                "Second"));
            Assert.Equal(SkinExtraExtractionStatus.Extracted, second.Status);
            Assert.Contains("Omitted 1 identical audio asset", second.Message);
            var delta = SkinExtraManifestSerializer.TryRead(second.DirectoryPath!);
            Assert.NotNull(delta);
            Assert.Equal("menuclick.wav", Assert.Single(delta!.Files).TargetFilename);

            var third = Assert.Single(service.Extract(
                Source("Third", normal),
                [Selection(normal)],
                root,
                "Third"));
            Assert.Equal(SkinExtraExtractionStatus.ExactDuplicateSkipped, third.Status);
        }
        finally { Delete(root); }
    }

    [Fact]
    public void Bulk_image_import_skips_reencoded_pixels_within_the_same_transaction()
    {
        var root = TempDirectory();
        try
        {
            var service = new SkinExtrasExtractionService();
            var family = SkinExtraFamilyRegistry.ById("osu.cursor")!;
            var firstBytes = PngWithDpi(96);
            var reencodedBytes = PngWithDpi(144);
            var first = new SkinExtractionFile("cursor.png", firstBytes);
            var reencoded = new SkinExtractionFile("cursor.png", reencodedBytes);
            SkinExtractionFamily Selection(SkinExtractionFile file) => new()
            {
                Definition = family,
                Files = [file],
                IniPatch = [],
            };
            var source = new SkinExtractionSource
            {
                DisplayName = "Bulk",
                SourceLabel = "memory",
                Files = [first, reencoded],
            };

            var firstDescription = SkinExtraFingerprint.Describe(
                first.Filename,
                first.Filename,
                first.Bytes);
            var secondDescription = SkinExtraFingerprint.Describe(
                reencoded.Filename,
                reencoded.Filename,
                reencoded.Bytes);
            var results = service.Extract(
                source,
                [Selection(first), Selection(reencoded)],
                root,
                "Pixels");

            Assert.NotEqual(firstDescription.ByteHash, secondDescription.ByteHash);
            Assert.Equal(firstDescription.SemanticHash, secondDescription.SemanticHash);
            Assert.Collection(
                results,
                result => Assert.Equal(SkinExtraExtractionStatus.Extracted, result.Status),
                result => Assert.Equal(
                    SkinExtraExtractionStatus.ExactDuplicateSkipped,
                    result.Status));
            Assert.Single(SkinExtraPackIndex.Scan(root));
        }
        finally { Delete(root); }
    }

    [Fact]
    public void Audio_duplicate_hash_ignores_equivalent_wave_header_shapes()
    {
        var root = TempDirectory();
        try
        {
            var service = new SkinExtrasExtractionService();
            var family = SkinExtraFamilyRegistry.ById("audio.interface")!;
            var standard = new SkinExtractionFile("menuclick.wav", PcmWave(extendedFormat: false));
            var extended = new SkinExtractionFile("menuclick.wav", PcmWave(extendedFormat: true));
            SkinExtractionFamily Selection(SkinExtractionFile file) => new()
            {
                Definition = family,
                Files = [file],
                IniPatch = [],
            };
            SkinExtractionSource Source(string name, SkinExtractionFile file) => new()
            {
                DisplayName = name,
                SourceLabel = "memory",
                Files = [file],
            };

            var standardDescription = SkinExtraFingerprint.Describe(
                standard.Filename,
                standard.Filename,
                standard.Bytes);
            var extendedDescription = SkinExtraFingerprint.Describe(
                extended.Filename,
                extended.Filename,
                extended.Bytes);
            var first = Assert.Single(service.Extract(
                Source("Standard", standard),
                [Selection(standard)],
                root,
                "Standard"));
            var duplicate = Assert.Single(service.Extract(
                Source("Extended", extended),
                [Selection(extended)],
                root,
                "Extended"));

            Assert.NotEqual(standardDescription.ByteHash, extendedDescription.ByteHash);
            Assert.Equal(standardDescription.SemanticHash, extendedDescription.SemanticHash);
            Assert.Equal(SkinExtraExtractionStatus.Extracted, first.Status);
            Assert.Equal(SkinExtraExtractionStatus.ExactDuplicateSkipped, duplicate.Status);
            Assert.Single(SkinExtraPackIndex.Scan(root));
        }
        finally { Delete(root); }
    }

    [Fact]
    public void Audio_import_rejects_perceptually_equivalent_gain_and_padding_variants()
    {
        var root = TempDirectory();
        try
        {
            static short[] Tone(int amplitude, int padding)
            {
                var content = Enumerable.Range(0, 4_410)
                    .Select(index =>
                    {
                        var envelope = Math.Sin(Math.PI * index / 4_409d);
                        var tone = Math.Sin(2 * Math.PI * 440 * index / 44_100d)
                                   + 0.35 * Math.Sin(2 * Math.PI * 880 * index / 44_100d);
                        return (short)(amplitude * envelope * tone);
                    });
                return Enumerable.Repeat((short)0, padding)
                    .Concat(content)
                    .Concat(Enumerable.Repeat((short)0, padding))
                    .ToArray();
            }

            var quiet = new SkinExtractionFile(
                "spinnerbonus.wav",
                PcmWave(extendedFormat: false, samples: Tone(4_000, 0)));
            var loudPadded = new SkinExtractionFile(
                "spinnerbonus.ogg",
                PcmWave(extendedFormat: false, samples: Tone(12_000, 220)));
            var quietDescription = SkinExtraFingerprint.Describe(
                quiet.Filename,
                quiet.Filename,
                quiet.Bytes);
            var loudDescription = SkinExtraFingerprint.Describe(
                loudPadded.Filename,
                loudPadded.Filename,
                loudPadded.Bytes);
            var differentTone = SkinExtraFingerprint.Describe(
                "spinnerbonus.mp3",
                "spinnerbonus.mp3",
                PcmWave(
                    extendedFormat: false,
                    samples: Enumerable.Range(0, 4_410)
                        .Select(index => (short)(8_000
                            * Math.Sin(2 * Math.PI * 1_760 * index / 44_100d)))
                        .ToArray()));

            Assert.NotEqual(quietDescription.SemanticHash, loudDescription.SemanticHash);
            Assert.True(SkinExtraFingerprint.EquivalentFileContent(
                quietDescription,
                loudDescription));
            Assert.False(SkinExtraFingerprint.EquivalentFileContent(
                quietDescription,
                differentTone));

            var service = new SkinExtrasExtractionService();
            var family = SkinExtraFamilyRegistry.ById("audio.spinner")!;
            SkinExtractionFamily Selection(SkinExtractionFile file) => new()
            {
                Definition = family,
                Files = [file],
                IniPatch = [],
            };
            SkinExtractionSource Source(string name, SkinExtractionFile file) => new()
            {
                DisplayName = name,
                SourceLabel = "memory",
                Files = [file],
            };

            var first = Assert.Single(service.Extract(
                Source("Quiet", quiet),
                [Selection(quiet)],
                root,
                "Quiet"));
            var duplicate = Assert.Single(service.Extract(
                Source("Loud padded", loudPadded),
                [Selection(loudPadded)],
                root,
                "Loud padded"));

            Assert.Equal(SkinExtraExtractionStatus.Extracted, first.Status);
            Assert.Equal(SkinExtraExtractionStatus.ExactDuplicateSkipped, duplicate.Status);
            Assert.Single(SkinExtraPackIndex.Scan(root));
        }
        finally { Delete(root); }
    }

    [Fact]
    public void Silent_audio_has_one_identity_across_empty_duration_and_format_variants()
    {
        var empty = SkinExtraFingerprint.Describe(
            "pause-loop.wav",
            "pause-loop.wav",
            []);
        var shortWave = SkinExtraFingerprint.Describe(
            "pause-loop.wav",
            "pause-loop.wav",
            PcmWave(
                extendedFormat: false,
                sampleRate: 22_050,
                samples: new short[2_205]));
        var longWave = SkinExtraFingerprint.Describe(
            "pause-loop.wav",
            "pause-loop.wav",
            PcmWave(
                extendedFormat: true,
                sampleRate: 44_100,
                samples: new short[8_820]));

        Assert.Equal(SkinAudioCanonicalizer.SilentHash, empty.SemanticHash);
        Assert.Equal(empty.SemanticHash, shortWave.SemanticHash);
        Assert.Equal(empty.SemanticHash, longWave.SemanticHash);
        Assert.NotEqual(shortWave.ByteHash, longWave.ByteHash);
    }

    [Fact]
    public void Silent_audio_import_deduplicates_zero_byte_and_decodable_placeholders()
    {
        var root = TempDirectory();
        try
        {
            var service = new SkinExtrasExtractionService();
            var family = SkinExtraFamilyRegistry.ById("audio.gameplay")!;
            SkinExtractionFamily Selection(SkinExtractionFile file) => new()
            {
                Definition = family,
                Files = [file],
                IniPatch = [],
            };
            SkinExtractionSource Source(string name, SkinExtractionFile file) => new()
            {
                DisplayName = name,
                SourceLabel = "memory",
                Files = [file],
            };
            var empty = new SkinExtractionFile("pause-loop.wav", []);
            var encodedSilence = new SkinExtractionFile(
                "pause-loop.wav",
                PcmWave(
                    extendedFormat: false,
                    samples: new short[4_410]));

            var first = Assert.Single(service.Extract(
                Source("Empty", empty),
                [Selection(empty)],
                root,
                "Empty"));
            var duplicate = Assert.Single(service.Extract(
                Source("Encoded silence", encodedSilence),
                [Selection(encodedSilence)],
                root,
                "Encoded silence"));

            Assert.Equal(SkinExtraExtractionStatus.Extracted, first.Status);
            Assert.Equal(SkinExtraExtractionStatus.ExactDuplicateSkipped, duplicate.Status);
            Assert.Single(SkinExtraPackIndex.Scan(root));
        }
        finally { Delete(root); }
    }

    [Fact]
    public void Persistent_index_refreshes_existing_audio_semantics_for_silent_deduplication()
    {
        var root = TempDirectory();
        try
        {
            var directory = Path.Combine(root, "Audio", "Gameplay sounds", "Old pack");
            Directory.CreateDirectory(directory);
            var bytes = PcmWave(
                extendedFormat: false,
                samples: new short[4_410]);
            File.WriteAllBytes(Path.Combine(directory, "pause-loop.wav"), bytes);
            var stale = new SkinExtraPackManifest
            {
                Id = "old-audio",
                DisplayName = "Old pack",
                FamilyId = "audio.gameplay",
                Area = "Audio",
                FamilyName = "Gameplay sounds",
                Fingerprint = new string('f', 64),
                Files =
                [
                    new SkinExtraManifestFile(
                        "pause-loop.wav",
                        "pause-loop.wav",
                        "pause-loop.wav",
                        new string('a', 64),
                        new string('b', 64)),
                ],
            };
            File.WriteAllBytes(
                Path.Combine(directory, "extras.json"),
                SkinExtraManifestSerializer.Serialize(stale));

            var first = Assert.Single(SkinExtraPackIndex.Scan(root));
            var refreshed = Assert.Single(first.Manifest.Files);
            Assert.Equal(SkinAudioCanonicalizer.SilentHash, refreshed.SemanticHash);
            Assert.Equal(stale.Id, first.Manifest.Id);
            Assert.NotEqual(stale.Fingerprint, first.Manifest.Fingerprint);

            var cached = Assert.Single(SkinExtraPackIndex.Scan(root));
            Assert.Equal(
                SkinAudioCanonicalizer.SilentHash,
                Assert.Single(cached.Manifest.Files).SemanticHash);
        }
        finally { Delete(root); }
    }

    [Fact]
    public void Audio_preview_uses_readable_names_and_loops_all_auditioned_sounds()
    {
        Assert.Equal("Combo break", SkinExtrasPickerWindow.AudioPadLabel("combobreak.wav"));
        Assert.Equal("Spinner spin", SkinExtrasPickerWindow.AudioPadLabel("spinnerspin.ogg"));
        Assert.Equal(
            "Normal · Hit whistle",
            SkinExtrasPickerWindow.AudioPadLabel("normal-hitwhistle.wav"));
        Assert.True(SkinExtrasPickerWindow.ShouldLoopAudio("spinnerspin.ogg"));
        Assert.True(SkinExtrasPickerWindow.ShouldLoopAudio("pause-loop.mp3"));
        Assert.True(SkinExtrasPickerWindow.ShouldLoopAudio("soft-sliderslide.wav"));
        Assert.True(SkinExtrasPickerWindow.ShouldLoopAudio("spinnerbonus.ogg"));
        Assert.True(SkinExtrasPickerWindow.ShouldLoopAudio("normal-hitnormal.wav"));
        Assert.False(SkinExtrasPickerWindow.ShouldLoopAudio("not-a-sound.png"));
    }

    [Theory]
    [InlineData("menuclick.wav", "menuclick.ogg")]
    [InlineData("cursor.png", "cursor.jpg")]
    public void Equivalent_media_targets_ignore_container_extensions(string left, string right)
    {
        Assert.True(SkinExtraFingerprint.EquivalentTargetFilename(left, right));
    }

    [Fact]
    public async Task Concurrent_identical_extractions_commit_only_one_pack()
    {
        var root = TempDirectory();
        try
        {
            var service = new SkinExtrasExtractionService();
            var family = SkinExtraFamilyRegistry.ById("osu.cursor")!;
            var file = new SkinExtractionFile("cursor.png", PngWithDpi(96));
            var selection = new SkinExtractionFamily
            {
                Definition = family,
                Files = [file],
                IniPatch = [],
            };
            var source = new SkinExtractionSource
            {
                DisplayName = "Concurrent",
                SourceLabel = "memory",
                Files = [file],
            };

            var results = await Task.WhenAll(
                    Task.Run(() => Assert.Single(service.Extract(
                        source,
                        [selection],
                        root,
                        "Concurrent"))),
                    Task.Run(() => Assert.Single(service.Extract(
                        source,
                        [selection],
                        root,
                        "Concurrent"))));

            Assert.Single(results.Where(result =>
                result.Status == SkinExtraExtractionStatus.Extracted));
            Assert.Single(results.Where(result =>
                result.Status == SkinExtraExtractionStatus.ExactDuplicateSkipped));
            Assert.Single(SkinExtraPackIndex.Scan(root));
        }
        finally { Delete(root); }
    }

    [Fact]
    public void Bulk_duplicate_stress_keeps_one_image_and_one_audio_pack()
    {
        var root = TempDirectory();
        try
        {
            var service = new SkinExtrasExtractionService();
            var image = new SkinExtractionFile("cursor.png", PngWithDpi(96));
            var audio = new SkinExtractionFile("menuclick.wav", PcmWave(extendedFormat: false));
            var cursorFamily = SkinExtraFamilyRegistry.ById("osu.cursor")!;
            var audioFamily = SkinExtraFamilyRegistry.ById("audio.interface")!;
            var selections = Enumerable.Range(0, 100)
                .SelectMany(_ => new[]
                {
                    new SkinExtractionFamily
                    {
                        Definition = cursorFamily,
                        Files = [image],
                        IniPatch = [],
                    },
                    new SkinExtractionFamily
                    {
                        Definition = audioFamily,
                        Files = [audio],
                        IniPatch = [],
                    },
                })
                .ToArray();
            var source = new SkinExtractionSource
            {
                DisplayName = "Stress",
                SourceLabel = "memory",
                Files = [image, audio],
            };

            var results = service.Extract(source, selections, root, "Stress");

            Assert.Equal(200, results.Count);
            Assert.Equal(2, results.Count(result =>
                result.Status == SkinExtraExtractionStatus.Extracted));
            Assert.Equal(198, results.Count(result =>
                result.Status == SkinExtraExtractionStatus.ExactDuplicateSkipped));
            Assert.Equal(2, SkinExtraPackIndex.Scan(root).Count);
        }
        finally { Delete(root); }
    }

    [Fact]
    public void Hitsound_imports_keep_the_complete_pack_when_one_target_is_new()
    {
        var root = TempDirectory();
        try
        {
            var service = new SkinExtrasExtractionService();
            var family = SkinExtraFamilyRegistry.ById("audio.hitsounds.normal")!;
            SkinExtractionFamily Selection(params SkinExtractionFile[] files) => new()
            {
                Definition = family,
                Files = files,
                IniPatch = [],
            };
            SkinExtractionSource Source(string name, params SkinExtractionFile[] files) => new()
            {
                DisplayName = name,
                SourceLabel = name,
                Files = files,
            };

            var normal = new SkinExtractionFile("normal-hitnormal.wav", [1, 2, 3]);
            var clap = new SkinExtractionFile("normal-hitclap.wav", [4, 5, 6]);
            _ = service.Extract(Source("First", normal), [Selection(normal)], root, "First");

            var second = Assert.Single(service.Extract(
                Source("Second", normal, clap),
                [Selection(normal, clap)],
                root,
                "Second"));

            Assert.Equal(SkinExtraExtractionStatus.Extracted, second.Status);
            var complete = SkinExtraManifestSerializer.TryRead(second.DirectoryPath!);
            Assert.NotNull(complete);
            Assert.Equal(2, complete!.Files.Count);
        }
        finally { Delete(root); }
    }

    [Fact]
    public void Folder_import_uses_only_root_assets_and_ignores_cursor_lookalikes()
    {
        var folder = TempDirectory();
        try
        {
            File.WriteAllText(Path.Combine(folder, "skin.ini"), "[General]\nName: Root Skin\n");
            File.WriteAllBytes(Path.Combine(folder, "cursor.png"), [1]);
            File.WriteAllBytes(Path.Combine(folder, "cursor2.png"), [2]);
            var backup = Path.Combine(folder, "Backups");
            Directory.CreateDirectory(backup);
            File.WriteAllBytes(Path.Combine(backup, "cursortrail.png"), [3]);
            var service = new SkinExtrasExtractionService();

            var source = service.ReadFolder(folder);
            var families = service.Analyze(source);
            var cursor = Assert.Single(families.Where(family =>
                family.Definition.Id == "osu.cursor"));

            var file = Assert.Single(cursor.Files);
            Assert.Equal("cursor.png", file.Filename);
            Assert.DoesNotContain(source.Files, entry =>
                entry.Filename.Contains("Backups", StringComparison.OrdinalIgnoreCase));
            Assert.DoesNotContain(families.SelectMany(family => family.Files), entry =>
                entry.Filename.Equals("cursor2.png", StringComparison.OrdinalIgnoreCase));
        }
        finally { Delete(folder); }
    }

    [Fact]
    public void Skin_ini_compatibility_corpus_preserves_required_extras_settings()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "Fixtures", "skin-compatibility-corpus.json");
        var cases = JsonSerializer.Deserialize<List<IniFixture>>(File.ReadAllText(path), new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true,
        })!;

        Assert.True(cases.Count >= 4);
        foreach (var fixture in cases)
        {
            var document = SkinIniDocument.Parse(System.Text.Encoding.UTF8.GetBytes(fixture.Ini));
            foreach (var expected in fixture.Expected)
            {
                if (expected.Key.StartsWith("Mania[", StringComparison.Ordinal))
                {
                    var close = expected.Key.IndexOf(']');
                    var maniaKeys = int.Parse(expected.Key.AsSpan(6, close - 6));
                    var maniaKey = expected.Key[(close + 2)..];
                    var maniaSection = Assert.Single(document.GetSections("Mania")
                        .Where(instance => instance.ManiaKeys == maniaKeys));
                    Assert.Equal(expected.Value, maniaSection.Values[maniaKey]);
                    continue;
                }
                var separator = expected.Key.IndexOf('.');
                var section = expected.Key[..separator];
                var key = expected.Key[(separator + 1)..];
                Assert.Equal(expected.Value, document.GetValue(section, key));
            }
        }
    }

    private static SkinExtraPackDescriptor WritePack(string root, string displayName)
    {
        var directory = Path.Combine(root, "Audio", "Combobreak", displayName);
        Directory.CreateDirectory(directory);
        var bytes = new byte[] { 82, 73, 70, 70, 1, 2, 3, 4 };
        var file = SkinExtraFingerprint.Describe("combobreak.wav", "combobreak.wav", bytes);
        File.WriteAllBytes(Path.Combine(directory, "combobreak.wav"), bytes);
        var fingerprint = SkinExtraFingerprint.ForPack("audio.combobreak", [file]);
        var manifest = new SkinExtraPackManifest
        {
            Id = fingerprint[..16],
            DisplayName = displayName,
            FamilyId = "audio.combobreak",
            Area = "Audio",
            FamilyName = "Combobreak",
            Fingerprint = fingerprint,
            Files = [file],
        };
        File.WriteAllBytes(
            Path.Combine(directory, "extras.json"),
            SkinExtraManifestSerializer.Serialize(manifest));
        return new SkinExtraPackDescriptor(directory, manifest, false);
    }

    private static byte[] PngWithDpi(double dpi)
    {
        var pixels = new byte[]
        {
            20, 40, 240, 255,
            200, 80, 30, 255,
        };
        var bitmap = BitmapSource.Create(
            2,
            1,
            dpi,
            dpi,
            PixelFormats.Bgra32,
            null,
            pixels,
            8);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = new MemoryStream();
        encoder.Save(stream);
        return stream.ToArray();
    }

    private static byte[] PcmWave(
        bool extendedFormat,
        int sampleRate = 44_100,
        short[]? samples = null)
    {
        samples ??= [0, 1024, -1024, 8192, -8192, 0];
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream, System.Text.Encoding.UTF8, leaveOpen: true);
        writer.Write("RIFF"u8);
        writer.Write(0);
        writer.Write("WAVE"u8);
        writer.Write("fmt "u8);
        writer.Write(extendedFormat ? 18 : 16);
        writer.Write((ushort)1);
        writer.Write((ushort)1);
        writer.Write(sampleRate);
        writer.Write(sampleRate * 2);
        writer.Write((ushort)2);
        writer.Write((ushort)16);
        if (extendedFormat)
            writer.Write((ushort)0);
        writer.Write("data"u8);
        writer.Write(samples.Length * sizeof(short));
        foreach (var sample in samples)
            writer.Write(sample);
        writer.Flush();
        stream.Position = 4;
        writer.Write((int)stream.Length - 8);
        writer.Flush();
        return stream.ToArray();
    }

    private static string TempDirectory()
    {
        var path = Path.Combine(Path.GetTempPath(), $"kumori-extras-tests-{Guid.NewGuid():N}");
        Directory.CreateDirectory(path);
        return path;
    }

    private static void Delete(string path)
    {
        try { Directory.Delete(path, true); } catch { }
    }

    private sealed class IniFixture
    {
        public required string Name { get; init; }
        public required string Ini { get; init; }
        public required Dictionary<string, string> Expected { get; init; }
    }
}
