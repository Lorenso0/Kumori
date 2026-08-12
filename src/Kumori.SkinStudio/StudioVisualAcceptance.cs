using System.Security.Cryptography;
using System.Text.Json;
using Kumori.Skins;
using osu.Framework.Logging;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;

namespace Kumori.SkinStudio;

internal sealed record StudioVisualAcceptanceTarget(
    string Kind,
    string Name,
    double Time,
    string? FamilyId = null,
    string? Component = null,
    int? ManiaKeyCount = null);

internal static class StudioVisualAcceptancePlan
{
    public const double NativeMockupTime = 5_100;

    public static IReadOnlyList<StudioVisualAcceptanceTarget> Targets { get; } =
    [
        .. StudioSkinCoverageCatalog.Categories.Take(1).Select(category =>
            new StudioVisualAcceptanceTarget(
                "workbench",
                category.Title,
                0)),
        new("mockup", "gameplay-mockup", NativeMockupTime),
        .. StudioSkinCoverageCatalog.Categories.Skip(1).Select(category =>
            new StudioVisualAcceptanceTarget(
                "workbench",
                category.Title,
                0)),
        new("semantic", "hitcircle-numbers-1-through-10", 900, "osu.number-font", "default-0"),
        new("semantic", "followpoints-only", 900, "osu.followpoints", "followpoint"),
        new("semantic", "score-font-context", 900, "osu.number-font", "score-0"),
        new("semantic", "combo-font-context", 900, "osu.number-font", "combo-0"),
        new("semantic", "leaderboard-font-context", 900, "osu.number-font", "scoreentry-0"),
        new("semantic", "interface-ranking", 900, "interface.ranking", "ranking-panel"),
        new("semantic", "catch-fruits", 900, "catch.fruits", "fruit-pear"),
        new("semantic", "catch-catcher", 900, "catch.catcher", "fruit-catcher-idle"),
        new("semantic", "taiko-notes", 900, "taiko.notes", "taikohitcircle"),
        new("semantic", "taiko-roll", 900, "taiko.rolls", "taiko-roll-middle"),
        new("semantic", "mania-keys-4k", 900, "mania.keys", "mania-key", 4),
        new("semantic", "mania-holds-7k", 900, "mania.holds", "mania-hold-body", 7),
        new("semantic", "hitsound-hitcircle-loop", 900, "audio.hitsounds.normal", "normal-hitnormal"),
        new("semantic", "countdown-sound-event", 900, "audio.countdown", "count1s"),
        new("gameplay", "circle-and-hud", 900),
        new("gameplay", "slider-and-follow-points", 2_550),
        new("gameplay", "break-and-hud", 2_900),
        new("gameplay", "curved-slider-and-cursor", 5_100),
        new("gameplay", "spinner", 7_900),
        new("gameplay", "combo-colours-and-judgements", 11_050),
    ];
}

public partial class KumoriSkinStudioGame
{
    private async void startVisualAcceptanceCapture()
    {
        if (acceptanceOutputPath is null || gameHost is null || workbench is null)
            return;

        try
        {
            Directory.CreateDirectory(acceptanceOutputPath);
            var entries = new List<object>();
            var index = 0;
            foreach (var target in StudioVisualAcceptancePlan.Targets)
            {
                if (target.Kind == "workbench")
                {
                    await runOnUpdateThread(() =>
                    {
                        showWorkbench();
                        acceptanceWorkbenchClock!.CurrentTime = target.Time;
                        workbench.SetAcceptanceCategory(target.Name);
                    });
                }
                else if (target.Kind == "mockup")
                {
                    await runOnUpdateThread(showMockup);
                    await waitForAcceptanceAsync(
                        () => player?.CanSeekForAcceptance == true,
                        TimeSpan.FromSeconds(15));
                    await runOnUpdateThread(() =>
                        player!.SeekAndPauseForAcceptance(target.Time));
                    await waitForAcceptanceAsync(
                        () => player?.IsAcceptanceFrameReady(target.Time) == true,
                        TimeSpan.FromSeconds(15));
                }
                else if (target.Kind == "semantic")
                {
                    await runOnUpdateThread(showGameplay);
                    await waitForAcceptanceAsync(
                        () => player?.CanSeekForAcceptance == true,
                        TimeSpan.FromSeconds(15));
                    await runOnUpdateThread(() =>
                    {
                        var semantic = SkinStudioSemanticPreviewCatalog.Resolve(
                            target.Component,
                            target.FamilyId,
                            target.ManiaKeyCount);
                        rendererPreviewTarget = semantic;
                        rendererScene = semantic.Scene;
                        rendererInspectionFamily = semantic.FamilyId;
                        rendererInspectionComponents.Clear();
                        rendererInspectionComponents.Add(semantic.ComponentName);
                        rendererAutoMotion = false;
                        rendererPlaying = false;
                        configureRendererScene();
                        player!.SeekAndPauseForAcceptance(target.Time);
                    });
                    await waitForAcceptanceAsync(
                        () => player?.IsAcceptanceFrameReady(target.Time) == true,
                        TimeSpan.FromSeconds(15));
                }
                else
                {
                    await runOnUpdateThread(showGameplay);
                    await waitForAcceptanceAsync(
                        () => player?.CanSeekForAcceptance == true,
                        TimeSpan.FromSeconds(15));
                    await runOnUpdateThread(() =>
                        player!.SeekAndPauseForAcceptance(target.Time));
                    await waitForAcceptanceAsync(
                        () => player?.IsAcceptanceFrameReady(target.Time) == true,
                        TimeSpan.FromSeconds(15));
                }

                await Task.Delay(250);
                var filename =
                    $"{++index:00}-{target.Kind}-{safeTargetName(target.Name)}.png";
                var path = Path.Combine(acceptanceOutputPath, filename);
                using var image = await gameHost.TakeScreenshotAsync()
                                  ?? throw new InvalidOperationException(
                                      "The desktop renderer returned no screenshot.");
                if (image.Width < 800 || image.Height < 600)
                {
                    throw new InvalidDataException(
                        $"Visual target {target.Name} rendered at an invalid "
                        + $"{image.Width}×{image.Height} size.");
                }

                var sampledColours = new HashSet<Rgba32>();
                var semanticSampledColours = new HashSet<Rgba32>();
                image.ProcessPixelRows(accessor =>
                {
                    var yStep = Math.Max(1, accessor.Height / 24);
                    var xStep = Math.Max(1, accessor.Width / 32);
                    for (var y = 0; y < accessor.Height; y += yStep)
                    {
                        var row = accessor.GetRowSpan(y);
                        for (var x = 0; x < row.Length; x += xStep)
                        {
                            sampledColours.Add(row[x]);
                        }
                    }
                    if (target.Kind == "semantic")
                    {
                        var semanticYStep = Math.Max(1, accessor.Height / 96);
                        var semanticXStep = Math.Max(1, accessor.Width / 128);
                        for (var y = (int)(accessor.Height * 0.10);
                             y <= accessor.Height * 0.95;
                             y += semanticYStep)
                        {
                            var row = accessor.GetRowSpan(y);
                            for (var x = (int)(accessor.Width * 0.20);
                                 x <= accessor.Width * 0.75;
                                 x += semanticXStep)
                            {
                                semanticSampledColours.Add(row[x]);
                            }
                        }
                    }
                });
                if (sampledColours.Count < 12)
                {
                    throw new InvalidDataException(
                        $"Visual target {target.Name} was unexpectedly blank.");
                }
                if (target.Kind == "semantic"
                    && semanticSampledColours.Count < 3)
                {
                    throw new InvalidDataException(
                        $"Semantic target {target.Name} rendered an empty preview viewport.");
                }

                await image.SaveAsPngAsync(path);
                entries.Add(new
                {
                    target.Kind,
                    target.Name,
                    fixed_time_ms = target.Time,
                    file = filename,
                    width = image.Width,
                    height = image.Height,
                    sampled_colours = sampledColours.Count,
                    semantic_sampled_colours = target.Kind == "semantic"
                        ? (int?)semanticSampledColours.Count
                        : null,
                    sha256 = hashFile(path),
                });
            }

            // Studio scenes are deliberately not plays or replays, so passive
            // hit objects must not synthesize gameplay input. Verify audio via
            // the same explicit skinnable audition path exposed to WPF.
            await runOnUpdateThread(() => auditionRendererSample("pause-loop"));
            await waitForAcceptanceAsync(
                () => rendererAuditionSound?.IsPlaying == true,
                TimeSpan.FromSeconds(10));
            await runOnUpdateThread(stopRendererAudio);

            var manifestPath = Path.Combine(
                acceptanceOutputPath,
                "visual-acceptance-manifest.json");
            File.WriteAllText(
                manifestPath,
                JsonSerializer.Serialize(
                    new
                    {
                        format = 1,
                        lazer_revision = Program.LazerRevision,
                        fixed_clock = true,
                        target_count = entries.Count,
                        targets = entries,
                        audio_event_capture = new
                        {
                            mode = "explicit-audition",
                            component = "pause-loop",
                            pipeline = "SkinnableSound",
                            verification = "passed",
                        },
                        verification = "passed",
                    },
                    Kumori.Skins.SkinStudioLaunchContract.JsonOptions));
            gameHost.Exit();
        }
        catch (Exception ex)
        {
            Logger.Error(ex, "Fixed-clock visual acceptance capture failed.");
            try
            {
                File.WriteAllText(
                    Path.Combine(
                        acceptanceOutputPath,
                        "visual-acceptance-failure.json"),
                    JsonSerializer.Serialize(
                        new
                        {
                            verification = "failed",
                            type = ex.GetType().FullName,
                            message = ex.Message,
                        },
                        Kumori.Skins.SkinStudioLaunchContract.JsonOptions));
            }
            catch
            {
            }
            gameHost.Exit();
        }
    }

    private Task runOnUpdateThread(Action action)
    {
        var completion = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        Scheduler.Add(() =>
        {
            try
            {
                action();
                completion.SetResult();
            }
            catch (Exception ex)
            {
                completion.SetException(ex);
            }
        });
        return completion.Task;
    }

    private async Task waitForAcceptanceAsync(
        Func<bool> condition,
        TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            var reached = false;
            await runOnUpdateThread(() => reached = condition());
            if (reached)
                return;
            await Task.Delay(25);
        }
        throw new TimeoutException(
            "The authoritative gameplay frame did not settle at its fixed time.");
    }

    private static string safeTargetName(string value) =>
        new string(value.Select(character =>
                char.IsAsciiLetterOrDigit(character)
                    ? char.ToLowerInvariant(character)
                    : '-')
            .ToArray())
            .Trim('-');

    private static string hashFile(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexStringLower(SHA256.HashData(stream));
    }
}
