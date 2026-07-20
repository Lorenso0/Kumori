using System.IO.Compression;
using System.Text;
using System.Text.Json.Nodes;
using Kumori.Core.Models;
using Kumori.Storage;
using Kumori.Tracking;
using Microsoft.Data.Sqlite;
using Xunit;

namespace Kumori.Storage.Tests;

public sealed class PlaySharePackageServiceTests : IDisposable
{
    private readonly string root = Path.Combine(
        Path.GetTempPath(), $"kumori-share-tests-{Guid.NewGuid():N}");
    private readonly string trackingDatabase;
    private readonly string importsDatabase;
    private readonly string assetsDirectory;
    private readonly string stagingDirectory;

    public PlaySharePackageServiceTests()
    {
        Directory.CreateDirectory(root);
        trackingDatabase = Path.Combine(root, "tracking.sqlite3");
        importsDatabase = Path.Combine(root, "imports.sqlite3");
        assetsDirectory = Path.Combine(root, "assets");
        stagingDirectory = Path.Combine(root, "staging");
    }

    [Fact]
    public async Task ExportImport_RoundTripsPortablePlayAndDetectsDuplicate()
    {
        (PlaySharePackageService service, long attemptId) = CreateServiceWithPlay();
        Assert.Equal("Sender", service.GetPlayerName(attemptId));
        string beatmap = Path.Combine(root, "Artist - Song (Mapper) [Insane].osu");
        string audio = Path.Combine(root, "audio file.mp3");
        string background = Path.Combine(root, "background.jpg");
        await File.WriteAllTextAsync(
            beatmap,
            """
            osu file format v14

            [General]
            AudioFilename: audio file.mp3

            [Metadata]
            Title:Song
            Artist:Artist
            Creator:Mapper
            Version:Insane
            """);
        byte[] sharedMediaPayload = Encoding.UTF8.GetBytes("test shared media payload");
        await File.WriteAllBytesAsync(audio, sharedMediaPayload);
        // Identical content under two logical names exercises hash-based asset reuse.
        await File.WriteAllBytesAsync(background, sharedMediaPayload);

        string package = Path.Combine(root, "Sender - Artist - Song [Insane].kumori");
        string exported = await service.ExportAsync(
            attemptId,
            "Sender",
            package,
            [
                new ShareMediaFile(Path.GetFileName(beatmap), "beatmap", beatmap),
                new ShareMediaFile(Path.GetFileName(audio), "audio", audio),
                new ShareMediaFile(Path.GetFileName(background), "background", background),
            ]);

        Assert.Equal(package, exported);
        Assert.True(File.Exists(package));
        KumoriPackagePreview preview = await service.PreviewAsync(package);
        Assert.Equal("Sender", preview.PlayerName);
        Assert.Equal("Song", preview.Play.Map.Title);
        Assert.Equal(987_654, preview.Play.Score);
        Assert.Equal(3, preview.Play.Movement.SampleCount);

        KumoriImportResult imported = await service.ImportAsync(package);
        Assert.False(imported.AlreadyImported);
        Assert.True(imported.Details.IsImported);
        Assert.Equal("Sender", imported.Details.SharedByPlayerName);
        Assert.Equal("HD", imported.Details.Summary.ModsKey);
        Assert.Equal(98.75, imported.Details.Summary.Accuracy, 6);
        Assert.Equal([-12.5, 3.25, 8.0], imported.Details.Timing!.Offsets);
        Assert.Equal(3, Assert.IsType<MovementSummary>(imported.Details.Movement).SampleCount);
        Assert.All(imported.Details.LocalMediaPaths.Values, AssertFileExists);
        Assert.Equal(2, imported.Details.LocalMediaPaths.Values.Distinct().Count());

        KumoriImportResult duplicate = await service.ImportAsync(package);
        Assert.True(duplicate.AlreadyImported);
        Assert.Equal(imported.ImportId, duplicate.ImportId);
        Assert.Single(service.GetImportedAttempts());

        string[] importedAssetPaths = imported.Details.LocalMediaPaths.Values.Distinct().ToArray();
        File.Delete(package);
        File.Delete(beatmap);
        File.Delete(audio);
        File.Delete(background);

        AttemptDetails retained = Assert.IsType<AttemptDetails>(
            service.GetImportedDetails(imported.ImportId));
        Assert.All(retained.LocalMediaPaths.Values, AssertFileExists);
        IReadOnlyList<MovementSample> movement = service.GetImportedMovement(imported.ImportId);
        Assert.Equal(3, movement.Count);
        Assert.Equal(320.5, movement[2].X, 6);
        Assert.Equal(48u, movement[2].Pressure);

        Assert.True(service.DeleteImport(imported.ImportId));
        Assert.Null(service.GetImportedDetails(imported.ImportId));
        Assert.All(importedAssetPaths, path => Assert.False(File.Exists(path)));
    }

    [Fact]
    public async Task Preview_RejectsAlteredPlayDataWithoutPersistingAnything()
    {
        (PlaySharePackageService service, long attemptId) = CreateServiceWithPlay();
        string beatmap = Path.Combine(root, "map.osu");
        string audio = Path.Combine(root, "audio.mp3");
        await File.WriteAllTextAsync(
            beatmap,
            "osu file format v14\n\n[General]\nAudioFilename: audio.mp3\n");
        await File.WriteAllBytesAsync(audio, [1, 2, 3, 4]);
        string package = Path.Combine(root, "corrupt.kumori");
        await service.ExportAsync(
            attemptId,
            "Sender",
            package,
            [
                new ShareMediaFile("map.osu", "beatmap", beatmap),
                new ShareMediaFile("audio.mp3", "audio", audio),
            ]);

        using (ZipArchive archive = ZipFile.Open(package, ZipArchiveMode.Update))
        {
            ZipArchiveEntry play = archive.GetEntry("play.json")!;
            play.Delete();
            ZipArchiveEntry replacement = archive.CreateEntry("play.json");
            await using Stream stream = replacement.Open();
            await stream.WriteAsync(Encoding.UTF8.GetBytes("{}"));
        }

        await Assert.ThrowsAsync<InvalidDataException>(() => service.PreviewAsync(package));
        Assert.Empty(service.GetImportedAttempts());
        Assert.False(Directory.Exists(assetsDirectory)
                     && Directory.EnumerateFiles(assetsDirectory).Any());
    }

    [Fact]
    public async Task Preview_RejectsUnsupportedVersionTraversalAndCompressionBomb()
    {
        (PlaySharePackageService service, long attemptId) = CreateServiceWithPlay();
        string valid = await ExportBasicPackageAsync(service, attemptId, "valid.kumori");

        string unsupported = Path.Combine(root, "unsupported.kumori");
        File.Copy(valid, unsupported);
        using (ZipArchive archive = ZipFile.Open(unsupported, ZipArchiveMode.Update))
        {
            ZipArchiveEntry manifestEntry = archive.GetEntry("manifest.json")!;
            JsonObject manifest;
            using (Stream input = manifestEntry.Open())
                manifest = JsonNode.Parse(input)!.AsObject();
            manifestEntry.Delete();
            manifest["version"] = 999;
            ZipArchiveEntry replacement = archive.CreateEntry("manifest.json");
            await using Stream output = replacement.Open();
            await output.WriteAsync(Encoding.UTF8.GetBytes(manifest.ToJsonString()));
        }
        await Assert.ThrowsAsync<InvalidDataException>(() => service.PreviewAsync(unsupported));

        string traversal = Path.Combine(root, "traversal.kumori");
        File.Copy(valid, traversal);
        using (ZipArchive archive = ZipFile.Open(traversal, ZipArchiveMode.Update))
        {
            ZipArchiveEntry entry = archive.CreateEntry("../outside.txt");
            await using Stream output = entry.Open();
            await output.WriteAsync(new byte[] { 1 });
        }
        await Assert.ThrowsAsync<InvalidDataException>(() => service.PreviewAsync(traversal));

        string bomb = Path.Combine(root, "bomb.kumori");
        File.Copy(valid, bomb);
        using (ZipArchive archive = ZipFile.Open(bomb, ZipArchiveMode.Update))
        {
            ZipArchiveEntry entry = archive.CreateEntry("media/compressed-bomb.bin", CompressionLevel.Optimal);
            await using Stream output = entry.Open();
            await output.WriteAsync(new byte[2 * 1024 * 1024]);
        }
        await Assert.ThrowsAsync<InvalidDataException>(() => service.PreviewAsync(bomb));
        Assert.Empty(service.GetImportedAttempts());
    }

    [Fact]
    public async Task Import_ReferencesMatchingLocalMapFilesWithoutCopyingOrOwningThem()
    {
        (PlaySharePackageService exporter, long attemptId) = CreateServiceWithPlay();
        string beatmap = Path.Combine(root, "local-map.osu");
        string audio = Path.Combine(root, "local-audio.mp3");
        await File.WriteAllTextAsync(
            beatmap,
            "osu file format v14\n\n[General]\nAudioFilename: local-audio.mp3\n");
        await File.WriteAllBytesAsync(audio, Encoding.UTF8.GetBytes("locally cached audio"));
        string package = Path.Combine(root, "reuse-local.kumori");
        await exporter.ExportAsync(
            attemptId,
            "Sender",
            package,
            [
                new ShareMediaFile("local-map.osu", "beatmap", beatmap),
                new ShareMediaFile("local-audio.mp3", "audio", audio),
            ]);

        var factory = new SqliteConnectionFactory(trackingDatabase, readOnly: false);
        var importer = new PlaySharePackageService(
            new AttemptDetailsRepository(factory),
            new MovementRepository(factory),
            new SessionRepository(factory),
            importsDatabase,
            assetsDirectory,
            stagingDirectory,
            _ => [beatmap, audio]);
        KumoriImportResult result = await importer.ImportAsync(package);

        Assert.Equal(2, result.ReusedLocalAssetCount);
        Assert.Equal(new FileInfo(beatmap).Length + new FileInfo(audio).Length, result.ReusedLocalAssetBytes);
        File.Delete(package);
        AttemptDetails retained = Assert.IsType<AttemptDetails>(
            importer.GetImportedDetails(result.ImportId));
        Assert.All(retained.LocalMediaPaths.Values, AssertFileExists);
        Assert.Contains(beatmap, retained.LocalMediaPaths.Values);
        Assert.Contains(audio, retained.LocalMediaPaths.Values);
        Assert.True(importer.DeleteImport(result.ImportId));
        Assert.True(File.Exists(beatmap));
        Assert.True(File.Exists(audio));
    }

    [Fact]
    public void RememberPlayerName_UpdatesOnlyTheSelectedPlay()
    {
        (PlaySharePackageService service, long attemptId) = CreateServiceWithPlay();

        service.RememberPlayerName(attemptId, "Legacy Player");

        Assert.Equal("Legacy Player", service.GetPlayerName(attemptId));
        using var connection = new SqliteConnection($"Data Source={trackingDatabase}");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT a.player_name, s.player_name
            FROM attempts a JOIN sessions s ON s.id = a.session_id
            WHERE a.id = @id
            """;
        command.Parameters.AddWithValue("@id", attemptId);
        using SqliteDataReader reader = command.ExecuteReader();
        Assert.True(reader.Read());
        Assert.Equal("Legacy Player", reader.GetString(0));
        Assert.True(reader.IsDBNull(1));
    }

    private (PlaySharePackageService Service, long AttemptId) CreateServiceWithPlay()
    {
        var factory = new SqliteConnectionFactory(trackingDatabase, readOnly: false);
        var sink = new AttemptSqliteSink(
            factory,
            (_, work) => work(CancellationToken.None));
        sink.StartAttempt(new AttemptStart
        {
            Identity = "checksum-1",
            WallTime = 1_788_000_000,
            PlayerName = "Sender",
            Artist = "Artist",
            Title = "Song",
            Mapper = "Mapper",
            Difficulty = "Insane",
            Checksum = "checksum-1",
            BeatmapId = 123,
            BeatmapSetId = 456,
            ModsKey = "HD",
            Mods = [new AttemptMod("HD", "{}")],
            BeatmapStats = new BeatmapStats
            {
                BaseStars = 5.2,
                Stars = 5.4,
                ApproachRate = 9,
                CircleSize = 4,
                OverallDifficulty = 8.5,
                DrainRate = 6,
                Bpm = 180,
                MaxCombo = 1_234,
            },
        });
        long attemptId = Assert.IsType<long>(sink.CurrentAttemptId);
        sink.Finalize(new AttemptFinalization(
            "completed",
            "results_screen",
            new AttemptSnapshot
            {
                Identity = "checksum-1",
                WallTime = 1_788_000_120,
                PlayerName = "Sender",
                DurationSeconds = 120,
                Score = 987_654,
                Accuracy = 98.75,
                Grade = "S",
                Pp = 245.5,
                FcPp = 260,
                MaxPp = 280,
                Combo = 1_100,
                N300 = 650,
                N100 = 12,
                N50 = 1,
                Misses = 2,
                Geki = 20,
                Katu = 3,
                SliderBreaks = 1,
                UnstableRate = 88.2,
                Progress = 1,
                TimingOffsets = [-12.5, 3.25, 8.0],
                ModsKey = "HD",
                Mods = [new AttemptMod("HD", "{}")],
                BeatmapStats = new BeatmapStats
                {
                    BaseStars = 5.2,
                    Stars = 5.4,
                    ApproachRate = 9,
                    CircleSize = 4,
                    OverallDifficulty = 8.5,
                    DrainRate = 6,
                    Bpm = 180,
                    MaxCombo = 1_234,
                },
            },
            Ordinal: 1));

        var capture = new MovementCaptureStore(factory);
        capture.Start(attemptId);
        capture.AddSamples(
        [
            new MovementSample
            {
                MapTimeMs = 0,
                MonotonicMs = 1_000,
                X = 256,
                Y = 192,
                RawX = 100,
                RawY = 101,
                Buttons = 0,
                Pressure = 12,
            },
            new MovementSample
            {
                MapTimeMs = 16,
                MonotonicMs = 1_016,
                X = 280.25,
                Y = 180.5,
                RawX = 110,
                RawY = 111,
                Buttons = 0x10,
                Pressure = 32,
            },
            new MovementSample
            {
                MapTimeMs = 32,
                MonotonicMs = 1_032,
                X = 320.5,
                Y = 160.75,
                RawX = 120,
                RawY = 121,
                Buttons = 0,
                Pressure = 48,
            },
        ]);
        capture.Complete(0, "live", "{}");

        return (
            new PlaySharePackageService(
                new AttemptDetailsRepository(factory),
                new MovementRepository(factory),
                new SessionRepository(factory),
                importsDatabase,
                assetsDirectory,
                stagingDirectory),
            attemptId);
    }

    private async Task<string> ExportBasicPackageAsync(
        PlaySharePackageService service,
        long attemptId,
        string packageName)
    {
        string beatmap = Path.Combine(root, $"{Guid.NewGuid():N}.osu");
        string audio = Path.Combine(root, $"{Guid.NewGuid():N}.mp3");
        await File.WriteAllTextAsync(
            beatmap,
            $"osu file format v14\n\n[General]\nAudioFilename: {Path.GetFileName(audio)}\n");
        await File.WriteAllBytesAsync(audio, [1, 2, 3, 4]);
        return await service.ExportAsync(
            attemptId,
            "Sender",
            Path.Combine(root, packageName),
            [
                new ShareMediaFile(Path.GetFileName(beatmap), "beatmap", beatmap),
                new ShareMediaFile(Path.GetFileName(audio), "audio", audio),
            ]);
    }

    private static void AssertFileExists(string path) => Assert.True(File.Exists(path), path);

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try
        {
            Directory.Delete(root, recursive: true);
        }
        catch
        {
            // Best-effort cleanup for a failed test retaining a file handle.
        }
    }
}
