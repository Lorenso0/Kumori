using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Kumori.Skins;
using osu.Framework.Graphics;
using osu.Framework.Logging;
using osuTK.Graphics;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;

namespace Kumori.SkinStudio;

internal sealed record StudioCommandAcceptanceEntry(
    string Command,
    int BeforeChanges,
    int AfterChanges,
    string Status,
    string Verification);

internal sealed record StudioCommandAcceptanceCapture(
    string Name,
    string File,
    int Width,
    int Height,
    string Sha256);

public partial class KumoriSkinStudioGame
{
    private async void startCommandAcceptanceCapture()
    {
        if (commandAcceptanceOutputPath is null || gameHost is null)
            return;

        var entries = new List<StudioCommandAcceptanceEntry>();
        var captures = new List<StudioCommandAcceptanceCapture>();
        try
        {
            Directory.CreateDirectory(commandAcceptanceOutputPath);
            File.Delete(Path.Combine(
                commandAcceptanceOutputPath,
                "command-acceptance-manifest.json"));
            File.Delete(Path.Combine(
                commandAcceptanceOutputPath,
                "command-acceptance-failure.json"));
            var inputs = Path.Combine(commandAcceptanceOutputPath, "inputs");
            Directory.CreateDirectory(inputs);
            var fixture = createCommandAcceptanceSkin(inputs);
            var malformed = Path.Combine(inputs, "malformed-empty.osk");
            File.Delete(malformed);
            using (ZipFile.Open(malformed, ZipArchiveMode.Create))
            {
            }

            var originalDraft = await currentDraftAsync();
            await runCommandAcceptanceAsync(
                entries,
                "reject-malformed-osk",
                () => importSkin(malformed),
                () => draft?.DraftId == originalDraft.DraftId
                      && statusContains("Skin import failed"));

            await runCommandAcceptanceAsync(
                entries,
                "import-osk",
                () => importSkin(fixture),
                () => draft is not null
                      && draft.SourceFingerprint
                      == SkinPackageService.Fingerprint(fixture));
            await captureCommandAcceptanceAsync(
                captures,
                "imported-workbench");

            await runCommandAcceptanceAsync(
                entries,
                "search-workbench-assets",
                () => workbench!.SetAcceptanceSearch("cursortrail"),
                () => workbench!.AcceptanceVisibleComponents
                    .SequenceEqual(["cursortrail"]));
            await runCommandAcceptanceAsync(
                entries,
                "filter-workbench-category",
                () =>
                {
                    workbench!.SetAcceptanceSearch("");
                    workbench.SetAcceptanceCategory("Hit objects");
                },
                () => workbench!.ActiveCategoryTitle == "Hit objects"
                      && workbench.AcceptanceVisibleComponents.Contains(
                          "hitcircle"));
            await runCommandAcceptanceAsync(
                entries,
                "hide-fallback-only-assets",
                () => workbench!.ToggleAcceptanceFallbackFilter(),
                () => workbench!.AcceptanceHidesFallbackOnly
                      && workbench.AcceptanceVisibleComponents.Contains(
                          "hitcircle")
                      && !workbench.AcceptanceVisibleComponents.Contains(
                          "approachcircle"));
            await captureCommandAcceptanceAsync(
                captures,
                "workbench-filter-state");
            await runOnUpdateThread(() =>
                workbench!.ClearAcceptanceFilters());
            await runCommandAcceptanceAsync(
                entries,
                "hide-transparent-placeholder",
                () =>
                {
                    workbench!.SetAcceptanceCategory(
                        "Cursor and trail");
                    workbench.ToggleAcceptanceFallbackFilter();
                },
                () => materialized().ContainsKey("cursormiddle.png")
                      && workbench!.AcceptanceVisibleComponents.Contains(
                          "cursor",
                          StringComparer.OrdinalIgnoreCase)
                      && !workbench.AcceptanceVisibleComponents.Contains(
                          "cursormiddle",
                          StringComparer.OrdinalIgnoreCase));
            await runOnUpdateThread(() =>
                workbench!.ClearAcceptanceFilters());

            await runCommandAcceptanceAsync(
                entries,
                "select-image-family",
                () => selectAsset("hitcircle"),
                () => copyAssetButton?.ActionEnabled == true
                      && deleteAssetButton?.ActionEnabled == true
                      && transformAssetButton?.ActionEnabled == true
                      && normalizeAudioButton?.ActionEnabled == false);

            var elementExport = Path.Combine(
                commandAcceptanceOutputPath,
                "element-export");
            await runCommandAcceptanceAsync(
                entries,
                "export-selected-family",
                () => assertAcceptance(
                    exportSelectedAssetTo(elementExport),
                    "Selected family export returned false."),
                () => File.Exists(Path.Combine(elementExport, "hitcircle.png"))
                      && File.Exists(Path.Combine(
                          elementExport,
                          "hitcircle@2x.png")));

            var originalHitcircleHashes = materialized()
                .Where(pair => pair.Key.StartsWith(
                    "hitcircle",
                    StringComparison.OrdinalIgnoreCase))
                .ToDictionary(
                    pair => pair.Key,
                    pair => SkinDraftWorkspaceService.Hash(pair.Value),
                    StringComparer.OrdinalIgnoreCase);
            await runCommandAcceptanceAsync(
                entries,
                "open-image-transform",
                transformSelectedImage,
                () => imageTransformOverlay?.Alpha > 0);
            await runCommandAcceptanceAsync(
                entries,
                "graphical-colour-picker-sync",
                () => imageTransformOverlay!.SetAcceptancePickerColour(
                    Colour4.FromHex("#35A7FF")),
                () => imageTransformOverlay!.AcceptanceColour == "#35A7FF");
            await runOnUpdateThread(() =>
            {
                imageTransformOverlay!.SetAcceptanceColour("#35A7FF");
                imageTransformOverlay.SaveAcceptanceSwatch();
                imageTransformOverlay.CycleAcceptanceScope();
            });
            await captureCommandAcceptanceAsync(captures, "image-transform");
            await runCommandAcceptanceAsync(
                entries,
                "apply-colorize-primary-pair",
                () => imageTransformOverlay!.ApplyAcceptanceTransform(
                    SkinImageTransformMode.Colorize),
                () =>
                {
                    var current = materialized();
                    return imageTransformOverlay!.Alpha == 0
                           && originalHitcircleHashes.All(pair =>
                               SkinDraftWorkspaceService.Hash(
                                   current[pair.Key]) != pair.Value)
                           && new SkinStudioSwatchStore(
                               contract.WorkspacePath).List()
                               .Any(swatch => swatch.Hex == "#35A7FF");
                });
            await runCommandAcceptanceAsync(
                entries,
                "non-destructive-image-reset",
                resetSelectedAsset,
                () =>
                {
                    var current = materialized();
                    return originalHitcircleHashes.All(pair =>
                        SkinDraftWorkspaceService.Hash(
                            current[pair.Key]) == pair.Value);
                });

            foreach (var transformCase in new[]
                     {
                         (
                             Name: "luminance-tint",
                             Mode: SkinImageTransformMode.Tint,
                             Colour: "#FFB347",
                             Hue: "0",
                             Saturation: "1",
                             Lightness: "1"),
                         (
                             Name: "multiplicative-tint",
                             Mode: SkinImageTransformMode.MultiplicativeTint,
                             Colour: "#8FD3FF",
                             Hue: "0",
                             Saturation: "1",
                             Lightness: "1"),
                         (
                             Name: "hsl-transform",
                             Mode: SkinImageTransformMode.HueSaturationLightness,
                             Colour: "#FFFFFF",
                             Hue: "37",
                             Saturation: "1.25",
                             Lightness: "0.82"),
                     })
            {
                await runCommandAcceptanceAsync(
                    entries,
                    $"open-{transformCase.Name}",
                    transformSelectedImage,
                    () => imageTransformOverlay?.Alpha > 0);
                await runCommandAcceptanceAsync(
                    entries,
                    $"apply-{transformCase.Name}",
                    () =>
                    {
                        imageTransformOverlay!.SetAcceptanceColour(
                            transformCase.Colour);
                        imageTransformOverlay.SetAcceptanceHsl(
                            transformCase.Hue,
                            transformCase.Saturation,
                            transformCase.Lightness);
                        imageTransformOverlay.ApplyAcceptanceTransform(
                            transformCase.Mode);
                    },
                    () =>
                    {
                        var current = materialized();
                        return imageTransformOverlay!.Alpha == 0
                               && originalHitcircleHashes.All(pair =>
                                   SkinDraftWorkspaceService.Hash(
                                       current[pair.Key]) != pair.Value);
                    });
                await runCommandAcceptanceAsync(
                    entries,
                    $"reset-{transformCase.Name}",
                    resetSelectedAsset,
                    () =>
                    {
                        var current = materialized();
                        return originalHitcircleHashes.All(pair =>
                            SkinDraftWorkspaceService.Hash(
                                current[pair.Key]) == pair.Value);
                    });
            }

            var replacement2x = Path.Combine(inputs, "replacement@2x.png");
            writePng(
                replacement2x,
                128,
                new Rgba32(245, 210, 75, 255));
            await runCommandAcceptanceAsync(
                entries,
                "replace-targeted-2x-variant",
                () =>
                {
                    pendingAssetTarget = "hitcircle";
                    importAsset(replacement2x);
                },
                () =>
                {
                    var current = materialized();
                    return SkinDraftWorkspaceService.Hash(
                               current["hitcircle.png"])
                           == originalHitcircleHashes["hitcircle.png"]
                           && SkinDraftWorkspaceService.Hash(
                               current["hitcircle@2x.png"])
                           != originalHitcircleHashes["hitcircle@2x.png"]
                           && draft!.Changes.Count(change =>
                               change.Filename.Equals(
                                   "hitcircle@2x.png",
                                   StringComparison.OrdinalIgnoreCase)) == 1;
                });
            await runCommandAcceptanceAsync(
                entries,
                "reset-filtered-category",
                () =>
                {
                    workbench!.SetAcceptanceCategory("Hit objects");
                    resetFilteredCategory();
                },
                () =>
                {
                    var current = materialized();
                    return originalHitcircleHashes.All(pair =>
                        SkinDraftWorkspaceService.Hash(
                            current[pair.Key]) == pair.Value);
                });
            await runOnUpdateThread(() =>
                workbench!.ClearAcceptanceFilters());

            await runCommandAcceptanceAsync(
                entries,
                "prepare-isolated-external-image-edit",
                () => prepareSelectedAssetExternalEdit(openExternal: false),
                () => File.Exists(externalAssetPath)
                      && externalAssetFilename == "hitcircle.png"
                      && applyExternalAssetButton?.ActionEnabled == false);
            await runCommandAcceptanceAsync(
                entries,
                "watch-external-image-change",
                () =>
                {
                    writePng(
                        externalAssetPath!,
                        64,
                        new Rgba32(65, 225, 150, 255));
                    refreshExternalAssetWatchState();
                },
                () => externalAssetChanged
                      && applyExternalAssetButton?.ActionEnabled == true
                      && statusContains("External changes detected"));
            await runCommandAcceptanceAsync(
                entries,
                "apply-validated-external-image-edit",
                applyExternalAssetEdit,
                () =>
                {
                    var current = materialized();
                    return !externalAssetChanged
                           && statusContains("validated")
                           && SkinDraftWorkspaceService.Hash(
                               current["hitcircle.png"])
                           != originalHitcircleHashes["hitcircle.png"];
                });
            var validExternalHash = SkinDraftWorkspaceService.Hash(
                materialized()["hitcircle.png"]);
            await runCommandAcceptanceAsync(
                entries,
                "reject-malformed-external-image",
                () =>
                {
                    prepareSelectedAssetExternalEdit(openExternal: false);
                    File.WriteAllText(
                        externalAssetPath!,
                        "not an encoded image");
                    refreshExternalAssetWatchState();
                    applyExternalAssetEdit();
                },
                () => statusContains("could not be decoded")
                      && SkinDraftWorkspaceService.Hash(
                          materialized()["hitcircle.png"])
                      == validExternalHash);
            string? concurrentEditHash = null;
            await runCommandAcceptanceAsync(
                entries,
                "reject-stale-external-image",
                () =>
                {
                    prepareSelectedAssetExternalEdit(openExternal: false);
                    writePng(
                        externalAssetPath!,
                        64,
                        new Rgba32(235, 180, 75, 255));
                    refreshExternalAssetWatchState();
                    var current = materialized()["hitcircle.png"];
                    using var replacement =
                        new Image<Rgba32>(
                            64,
                            64,
                            new Rgba32(105, 80, 235, 255));
                    using var output = new MemoryStream();
                    replacement.SaveAsPng(output);
                    var concurrentBytes = output.ToArray();
                    concurrentEditHash =
                        SkinDraftWorkspaceService.Hash(concurrentBytes);
                    draft = drafts.StageFile(
                        draft!.DraftId,
                        "hitcircle.png",
                        concurrentBytes,
                        SkinDraftWorkspaceService.Hash(current),
                        "Acceptance concurrent image edit");
                    applyExternalAssetEdit();
                },
                () => concurrentEditHash is not null
                      && SkinDraftWorkspaceService.Hash(
                          materialized()["hitcircle.png"])
                      == concurrentEditHash
                      && externalAssetExpectedHash != concurrentEditHash);
            await captureCommandAcceptanceAsync(
                captures,
                "external-image-conflict");
            await runCommandAcceptanceAsync(
                entries,
                "reset-after-external-image-tests",
                resetSelectedAsset,
                () => originalHitcircleHashes.All(pair =>
                    SkinDraftWorkspaceService.Hash(
                        materialized()[pair.Key]) == pair.Value));

            await runCommandAcceptanceAsync(
                entries,
                "copy-paste-family",
                () =>
                {
                    copySelectedAsset();
                    selectAsset("approachcircle");
                    pasteAssetIntoSelected();
                },
                () => assets.Family(
                        draft!.DraftId,
                        "approachcircle").Count == 2
                      && undoButton?.ActionEnabled == true);

            await runCommandAcceptanceAsync(
                entries,
                "undo-family-paste",
                undo,
                () => assets.Family(
                    draft!.DraftId,
                    "approachcircle").Count == 0
                      && redoButton?.ActionEnabled == true);
            await runCommandAcceptanceAsync(
                entries,
                "redo-family-paste",
                redo,
                () => assets.Family(
                    draft!.DraftId,
                    "approachcircle").Count == 2);
            await runCommandAcceptanceAsync(
                entries,
                "reset-selected-family",
                resetSelectedAsset,
                () => assets.Family(
                    draft!.DraftId,
                    "approachcircle").Count == 0);

            await runCommandAcceptanceAsync(
                entries,
                "delete-family",
                () =>
                {
                    selectAsset("hitcircle");
                    deleteSelectedAsset();
                },
                () => draft!.Changes.Any(change =>
                    change.Kind == SkinDraftChangeKind.Delete
                    && change.Filename.Equals(
                        "hitcircle.png",
                        StringComparison.OrdinalIgnoreCase)));
            await runCommandAcceptanceAsync(
                entries,
                "undo-family-delete",
                undo,
                () => assets.Family(draft!.DraftId, "hitcircle").Count == 2);

            var folder = createCommandAcceptanceAssetFolder(inputs);
            await runCommandAcceptanceAsync(
                entries,
                "import-asset-folder",
                () => assertAcceptance(
                    importAssetFolder(folder),
                    "Multi-file asset import returned false."),
                () => assets.Family(draft!.DraftId, "followpoint").Count == 4
                      && assets.Family(
                          draft.DraftId,
                          "normal-hitnormal").Count == 1
                      && assets.Family(draft.DraftId, "applause").Count == 1);

            var originalAudioHash = SkinDraftWorkspaceService.Hash(
                materialized()["normal-hitnormal.wav"]);
            var replacementAudio = Path.Combine(
                inputs,
                "replacement-hitnormal.wav");
            File.WriteAllBytes(
                replacementAudio,
                createWaveFixture(660));
            await runCommandAcceptanceAsync(
                entries,
                "replace-targeted-audio",
                () =>
                {
                    pendingAssetTarget = "normal-hitnormal";
                    importAsset(replacementAudio);
                },
                () => SkinDraftWorkspaceService.Hash(
                          materialized()["normal-hitnormal.wav"])
                      != originalAudioHash
                      && assets.Family(
                          draft!.DraftId,
                          "normal-hitnormal").Count == 1);
            await runCommandAcceptanceAsync(
                entries,
                "play-common-skin-sample",
                () =>
                {
                    workbench!.SetAcceptanceCategory("Audio samples");
                    workbench.ToggleAcceptanceAudio("normal-hitnormal");
                },
                () => workbench!.IsAcceptanceAudioPlaying(
                          "normal-hitnormal")
                      && statusContains("Playing Normal hit"));
            await runCommandAcceptanceAsync(
                entries,
                "route-exclusive-audio-sample",
                () => workbench!.ToggleAcceptanceAudio("applause"),
                () => workbench!.IsAcceptanceAudioPlaying("applause")
                      && !workbench.IsAcceptanceAudioPlaying(
                          "normal-hitnormal")
                      && statusContains("Playing Applause"));
            await runCommandAcceptanceAsync(
                entries,
                "stop-common-skin-sample",
                () => workbench!.ToggleAcceptanceAudio("applause"),
                () => !workbench!.IsAcceptanceAudioPlaying("applause")
                      && statusContains("Stopped Applause"));
            await runOnUpdateThread(() =>
                workbench!.ClearAcceptanceFilters());

            await runCommandAcceptanceAsync(
                entries,
                "normalize-audio-family",
                () =>
                {
                    selectAsset("normal-hitnormal");
                    normalizeSelectedAudio();
                },
                () => statusContains("Normalized 1 audio variant"));
            await runCommandAcceptanceAsync(
                entries,
                "open-audio-transport",
                openSelectedAudioTransport,
                () => audioTransportOverlay?.Alpha > 0);
            await captureCommandAcceptanceAsync(captures, "audio-transport");
            await runCommandAcceptanceAsync(
                entries,
                "audio-transport-play",
                () => audioTransportOverlay!.PlayAcceptance(),
                () => audioTransportOverlay!.AcceptanceIsRunning
                      && audioTransportOverlay.AcceptanceCurrentTime > 0);
            await runCommandAcceptanceAsync(
                entries,
                "audio-transport-seek",
                () => audioTransportOverlay!.SeekAcceptance(5_000),
                () => audioTransportOverlay!.AcceptanceCurrentTime >= 4_500);
            await runCommandAcceptanceAsync(
                entries,
                "audio-transport-pause",
                () => audioTransportOverlay!.PauseAcceptance(),
                () => !audioTransportOverlay!.AcceptanceIsRunning
                      && audioTransportOverlay.AcceptanceCurrentTime >= 4_500);
            await runCommandAcceptanceAsync(
                entries,
                "audio-transport-restart",
                () => audioTransportOverlay!.RestartAcceptance(),
                () => audioTransportOverlay!.AcceptanceIsRunning
                      && audioTransportOverlay.AcceptanceCurrentTime < 1_000);
            await runCommandAcceptanceAsync(
                entries,
                "audio-transport-stop",
                () => audioTransportOverlay!.StopAcceptance(),
                () => !audioTransportOverlay!.AcceptanceIsRunning
                      && audioTransportOverlay.AcceptanceCurrentTime < 25);
            await runOnUpdateThread(() => audioTransportOverlay?.Hide());

            await runCommandAcceptanceAsync(
                entries,
                "insert-animation-frame",
                () =>
                {
                    selectAsset("followpoint");
                    assertAcceptance(
                        insertAnimationFrame("0,1"),
                        "Animation insertion returned false.");
                },
                () => animationFrames("followpoint").SequenceEqual([0, 1, 2]));
            await runCommandAcceptanceAsync(
                entries,
                "move-animation-frame",
                () => assertAcceptance(
                    moveAnimationFrame("2,0"),
                    "Animation move returned false."),
                () => animationFrames("followpoint").SequenceEqual([0, 1, 2]));
            await runCommandAcceptanceAsync(
                entries,
                "delete-animation-frame",
                () => assertAcceptance(
                    deleteAnimationFrame("1"),
                    "Animation deletion returned false."),
                () => animationFrames("followpoint").SequenceEqual([0, 2]));
            await runCommandAcceptanceAsync(
                entries,
                "set-animation-framerate",
                () => assertAcceptance(
                    setAnimationFrameRate("24"),
                    "Animation frame-rate update returned false."),
                () => currentSkinIni()
                    .GetValue("General", "AnimationFramerate") == "24");

            await runCommandAcceptanceAsync(
                entries,
                "structured-skin-ini-commit",
                () =>
                {
                    var document = currentSkinIni();
                    document.SetValue(
                        "General",
                        "Name",
                        "Command Acceptance Edited");
                    assertAcceptance(
                        commitSkinIni(document.ToBytes(), structured: true),
                        "Structured skin.ini commit returned false.");
                },
                () =>
                {
                    var bytes = materialized()["skin.ini"];
                    var text = Encoding.UTF8.GetString(bytes);
                    return text.Contains(
                               "; preserved acceptance comment",
                               StringComparison.Ordinal)
                           && text.Contains(
                               "UnknownAcceptance: retained",
                               StringComparison.Ordinal)
                           && text.Contains("\r\n", StringComparison.Ordinal)
                           && currentSkinIni().GetValue(
                               "General",
                               "Name") == "Command Acceptance Edited";
                });

            await runCommandAcceptanceAsync(
                entries,
                "open-structured-skin-ini",
                editSkinIniStructured,
                () => skinIniOverlay?.Alpha > 0);
            await captureCommandAcceptanceAsync(captures, "structured-skin-ini");
            await runCommandAcceptanceAsync(
                entries,
                "structured-skin-ini-ui-save",
                () =>
                {
                    skinIniOverlay!.SetAcceptanceValue(
                        "General",
                        "Author",
                        "Kumori Acceptance UI");
                    skinIniOverlay.SaveAcceptance();
                },
                () => skinIniOverlay!.Alpha == 0
                      && currentSkinIni().GetValue(
                          "General",
                          "Author") == "Kumori Acceptance UI");
            await runCommandAcceptanceAsync(
                entries,
                "open-raw-skin-ini",
                editSkinIniRaw,
                () => rawSkinIniOverlay?.Alpha > 0);
            await captureCommandAcceptanceAsync(captures, "raw-skin-ini");
            await runCommandAcceptanceAsync(
                entries,
                "raw-skin-ini-insert-reorder-save",
                () =>
                {
                    var values = rawSkinIniOverlay!.AcceptanceLines.ToList();
                    values.Insert(1, "; inserted through native raw editor");
                    var unknownIndex = values.FindIndex(value =>
                        value.StartsWith(
                            "UnknownAcceptance:",
                            StringComparison.OrdinalIgnoreCase));
                    var moved = StudioRawSkinIniOverlay.MoveRawLine(
                        values,
                        unknownIndex,
                        -1);
                    rawSkinIniOverlay.SetAcceptanceLines(moved);
                    rawSkinIniOverlay.SaveAcceptance();
                },
                () =>
                {
                    var bytes = materialized()["skin.ini"];
                    var text = Encoding.UTF8.GetString(bytes);
                    return rawSkinIniOverlay!.Alpha == 0
                           && rawSkinIniOverlay.AcceptanceValidation.Length == 0
                           && bytes.AsSpan().StartsWith(
                               new byte[] { 0xEF, 0xBB, 0xBF })
                           && text.Contains(
                               "; inserted through native raw editor\r\n",
                               StringComparison.Ordinal)
                           && text.Contains(
                               "UnknownAcceptance: retained",
                               StringComparison.Ordinal)
                           && text.Contains("\r\n", StringComparison.Ordinal);
                });
            await runCommandAcceptanceAsync(
                entries,
                "structured-skin-ini-context-link",
                () =>
                {
                    editSkinIniStructured();
                    skinIniOverlay!.FocusAcceptanceContext(
                        "General",
                        "CursorRotate");
                },
                () => skinIniOverlay!.Alpha == 0
                      && statusContains(
                          "Focused workbench context for cursor")
                      && workbench!.AcceptanceVisibleComponents.Contains(
                          "Cursor",
                          StringComparer.OrdinalIgnoreCase));
            await runOnUpdateThread(() =>
                workbench!.ClearAcceptanceFilters());

            await runCommandAcceptanceAsync(
                entries,
                "prepare-selected-family-extras-extraction",
                () =>
                {
                    selectAsset("hitcircle");
                    extractSelectedAssetToExtras();
                },
                () => extrasExtractionOverlay?.Alpha > 0
                      && extrasExtractionOverlay.AcceptanceFamilyCount > 0
                      && extrasExtractionOverlay.AcceptanceSelectedCount == 1);
            await runOnUpdateThread(() =>
                extrasExtractionOverlay?.Hide());
            await runCommandAcceptanceAsync(
                entries,
                "prepare-category-extras-extraction",
                () =>
                {
                    workbench!.SetAcceptanceCategory("Hit objects");
                    extractFilteredCategoryToExtras();
                },
                () => extrasExtractionOverlay?.Alpha > 0
                      && extrasExtractionOverlay.AcceptanceFamilyCount > 0
                      && extrasExtractionOverlay.AcceptanceSelectedCount > 1
                      && extrasExtractionOverlay.AcceptanceSelectedCount
                          <= extrasExtractionOverlay.AcceptanceFamilyCount);
            await runOnUpdateThread(() =>
            {
                extrasExtractionOverlay?.Hide();
                workbench!.ClearAcceptanceFilters();
            });

            await runCommandAcceptanceAsync(
                entries,
                "prepare-extras-extraction",
                extractDraftToExtras,
                () => extrasExtractionOverlay?.Alpha > 0
                      && extrasExtractionOverlay.AcceptanceFamilyCount > 0
                      && extrasExtractionOverlay.AcceptanceSelectedCount > 0);
            await runOnUpdateThread(() =>
                extrasExtractionOverlay!.ToggleAcceptanceLazerUsedOnly());
            await captureCommandAcceptanceAsync(captures, "extras-extraction");
            await runCommandAcceptanceAsync(
                entries,
                "extract-draft-to-extras",
                () => extrasExtractionOverlay!.ExtractAcceptanceSelection(),
                () => extrasExtractionOverlay!.Alpha == 0
                      && SkinExtrasPersistentIndex.ScanCached(extrasRoot).Count > 0
                      && statusContains("Extras extraction completed"));

            await runCommandAcceptanceAsync(
                entries,
                "open-isolated-extras-library",
                () => extrasOverlay!.Present(),
                () => extrasOverlay!.Alpha > 0
                      && extrasOverlay.AcceptancePackCount > 0);
            await runCommandAcceptanceAsync(
                entries,
                "select-extras-pack",
                () => extrasOverlay!.SelectFirstAcceptancePack(),
                () => extrasOverlay!.AcceptanceSelectedFingerprint is not null);
            var selectedExtrasName =
                extrasOverlay!.AcceptanceSelectedDisplayName!;
            var selectedExtrasFingerprint =
                extrasOverlay.AcceptanceSelectedFingerprint!;
            var selectedTargetCount =
                extrasOverlay.AcceptanceSelectedTargetCount;
            await runCommandAcceptanceAsync(
                entries,
                "toggle-extras-logical-element",
                () => extrasOverlay!.ToggleFirstAcceptanceLogicalElement(),
                () => selectedTargetCount > 0
                      && extrasOverlay!.AcceptanceSelectedTargetCount
                          < selectedTargetCount);
            await runCommandAcceptanceAsync(
                entries,
                "toggle-extras-resolution-policy",
                () =>
                {
                    extrasOverlay!.ToggleFirstAcceptanceLogicalElement();
                    extrasOverlay.ToggleAcceptanceReplaceMode();
                },
                () => extrasOverlay!.AcceptanceSelectedTargetCount
                          == selectedTargetCount
                      && !extrasOverlay.AcceptanceReplaceEntireFamily);
            await runCommandAcceptanceAsync(
                entries,
                "search-extras-library",
                () => extrasOverlay!.SetAcceptanceSearch(selectedExtrasName),
                () => extrasOverlay!.AcceptanceVisibleEntryCount > 0);
            await runCommandAcceptanceAsync(
                entries,
                "favourite-extras-pack",
                () => extrasOverlay!.ToggleAcceptanceFavourite(),
                () => SkinExtrasLibraryStateStore.Get(
                    extrasRoot,
                    selectedExtrasFingerprint).Favorite);
            await runCommandAcceptanceAsync(
                entries,
                "filter-favourite-extras",
                () => extrasOverlay!.ToggleAcceptanceFavouritesOnly(),
                () => extrasOverlay!.AcceptanceVisibleEntryCount > 0);
            await runOnUpdateThread(() =>
            {
                extrasOverlay!.ToggleAcceptanceFavouritesOnly();
                extrasOverlay.SetAcceptanceSearch("");
            });
            await runCommandAcceptanceAsync(
                entries,
                "filter-lazer-used-extras",
                () => extrasOverlay!.ToggleAcceptanceLazerUsedOnly(),
                () => extrasOverlay!.AcceptanceLazerUsedOnly
                      && extrasOverlay.AcceptanceVisibleEntryCount > 0
                      && statusContains("only Extras used by lazer"));
            await runOnUpdateThread(() =>
                extrasOverlay!.ToggleAcceptanceLazerUsedOnly());
            await runCommandAcceptanceAsync(
                entries,
                "compare-extras-selection",
                () => extrasOverlay!.CompareAcceptanceSelection(),
                () => statusContains("identical")
                      || statusContains("setting"));
            await runCommandAcceptanceAsync(
                entries,
                "validate-extras-selection",
                () =>
                {
                    extrasOverlay!.SelectAcceptanceFamily(
                        "osu.followpoints");
                    extrasOverlay.ValidateAcceptanceSelection();
                },
                () => statusContains("0 error(s)")
                      && statusContains("warning(s)")
                      && statusContains("followpoint-sequence-gap"));
            await runCommandAcceptanceAsync(
                entries,
                "repair-extras-selection",
                () => extrasOverlay!.RepairAcceptanceSelection(),
                () => statusContains("Repaired")
                      && statusContains("after a verified backup")
                      && statusContains("Remaining: 0 error(s), 1 warning(s)"));
            var extrasExportCount = Directory.Exists(Path.Combine(
                contract.WorkspacePath,
                "extras-exports"))
                ? Directory.EnumerateFiles(
                    Path.Combine(contract.WorkspacePath, "extras-exports"),
                    "*.zip").Count()
                : 0;
            await runCommandAcceptanceAsync(
                entries,
                "export-portable-extras-package",
                () => extrasOverlay!.ExportAcceptanceSelection(),
                () => Directory.EnumerateFiles(
                        Path.Combine(
                            contract.WorkspacePath,
                            "extras-exports"),
                        "*.zip").Count() == extrasExportCount + 1);
            var portableExtrasPackage = Directory.EnumerateFiles(
                    Path.Combine(contract.WorkspacePath, "extras-exports"),
                    "*.zip")
                .OrderByDescending(File.GetLastWriteTimeUtc)
                .First();
            await runCommandAcceptanceAsync(
                entries,
                "apply-extras-selection",
                () => extrasOverlay!.ApplyAcceptanceSelection(),
                () => extrasOverlay!.Alpha == 0
                      && statusContains("Applied"));
            await runCommandAcceptanceAsync(
                entries,
                "extras-composition-readiness",
                showExtrasCompositionReadiness,
                () => extrasCompositionOverlay?.Alpha > 0
                      && extrasCompositionOverlay.AcceptanceSummary
                          is { ExportReady: true, FamilyCount: > 1 });
            await captureCommandAcceptanceAsync(
                captures,
                "extras-composition-readiness");
            await runOnUpdateThread(() =>
                extrasCompositionOverlay?.Hide());
            await runCommandAcceptanceAsync(
                entries,
                "rename-extras-pack",
                () =>
                {
                    extrasOverlay!.Present();
                    extrasOverlay.SelectFirstAcceptancePack();
                    extrasOverlay.RenameAcceptanceSelection(
                        "Command Acceptance Renamed");
                },
                () => SkinExtrasPersistentIndex.ScanCached(extrasRoot)
                    .Any(pack => pack.Manifest.DisplayName
                        == "Command Acceptance Renamed"));
            var packCountBeforeDelete =
                SkinExtrasPersistentIndex.ScanCached(extrasRoot).Count;
            await runCommandAcceptanceAsync(
                entries,
                "delete-extras-pack",
                () => extrasOverlay!.DeleteAcceptanceSelection(),
                () => SkinExtrasPersistentIndex.ScanCached(extrasRoot).Count
                          == packCountBeforeDelete - 1
                      && new SkinExtraPackTrashService().List(extrasRoot).Count > 0);
            await runCommandAcceptanceAsync(
                entries,
                "restore-extras-pack",
                () => extrasOverlay!.RestoreAcceptanceSelection(),
                () => SkinExtrasPersistentIndex.ScanCached(extrasRoot).Count
                      == packCountBeforeDelete);
            await runCommandAcceptanceAsync(
                entries,
                "import-portable-extras-package",
                () => importExtrasPackage(portableExtrasPackage),
                () => statusContains("duplicate")
                      || statusContains("exact copy")
                      || statusContains("Imported Extras package"));

            await runCommandAcceptanceAsync(
                entries,
                "catalog-sync-install",
                () =>
                {
                    extrasCatalogAcceptanceController!.UseRevision(1);
                    extrasOverlay!.Present();
                    extrasOverlay.StartAcceptanceSynchronization();
                },
                () =>
                    extrasOverlay!.AcceptanceSynchronizationProgress?.Stage
                        == SkinExtrasSyncStage.UpToDate
                    && SkinExtrasRemoteRegistryStore.Read(extrasRoot)
                        .Installs.Values.Any(install =>
                            install.Revision == 1)
                    && extrasOverlay.AcceptanceSynchronizationStatus.Contains(
                        "synchronized",
                        StringComparison.OrdinalIgnoreCase));
            await runCommandAcceptanceAsync(
                entries,
                "catalog-sync-offline-cache",
                () =>
                {
                    extrasCatalogAcceptanceController!.UseOfflineCache();
                    extrasOverlay!.StartAcceptanceSynchronization();
                },
                () =>
                    extrasOverlay!.AcceptanceSynchronizationProgress?.Stage
                        == SkinExtrasSyncStage.Offline
                    && extrasOverlay.AcceptanceSynchronizationStatus.Contains(
                        "last verified catalog",
                        StringComparison.OrdinalIgnoreCase));
            await runCommandAcceptanceAsync(
                entries,
                "catalog-sync-start-cancelable",
                () =>
                {
                    extrasCatalogAcceptanceController!.HoldForCancellation();
                    extrasOverlay!.StartAcceptanceSynchronization();
                },
                () =>
                    extrasOverlay!.AcceptanceSynchronizationProgress?.Stage
                        == SkinExtrasSyncStage.Checking);
            await runCommandAcceptanceAsync(
                entries,
                "catalog-sync-cancel",
                () => extrasOverlay!.CancelAcceptanceSynchronization(),
                () =>
                    extrasOverlay!.AcceptanceSynchronizationProgress?.Stage
                        == SkinExtrasSyncStage.Paused
                    && extrasOverlay.AcceptanceSynchronizationStatus.Contains(
                        "canceled",
                        StringComparison.OrdinalIgnoreCase));
            await runCommandAcceptanceAsync(
                entries,
                "catalog-sync-retry-update",
                () =>
                {
                    extrasCatalogAcceptanceController!.UseRevision(2);
                    extrasOverlay!.StartAcceptanceSynchronization();
                },
                () =>
                    extrasOverlay!.AcceptanceSynchronizationProgress?.Stage
                        == SkinExtrasSyncStage.UpToDate
                    && SkinExtrasRemoteRegistryStore.Read(extrasRoot)
                        .Installs.Values.Any(install =>
                            install.Revision == 2)
                    && extrasOverlay.AcceptanceSynchronizationStatus.Contains(
                        "updated",
                        StringComparison.OrdinalIgnoreCase));
            await captureCommandAcceptanceAsync(
                captures,
                "extras-catalog-synchronization");

            await runCommandAcceptanceAsync(
                entries,
                "open-extras-audio-browser",
                () =>
                {
                    extrasOverlay!.Present();
                    extrasOverlay.SelectFirstAcceptanceLongTrackPack();
                    extrasOverlay.BrowseAcceptanceAudio();
                },
                () => extrasAudioBrowserOverlay?.Alpha > 0
                      && extrasAudioBrowserOverlay
                          .AcceptanceAudioFileCount > 0);
            await captureCommandAcceptanceAsync(
                captures,
                "extras-audio-browser");
            await runCommandAcceptanceAsync(
                entries,
                "preview-extras-long-track",
                () => extrasAudioBrowserOverlay!
                    .OpenFirstAcceptanceTrack(),
                () => audioTransportOverlay?.Alpha > 0
                      && audioTransportOverlay.AcceptanceTitle.Contains(
                          "applause.wav",
                          StringComparison.OrdinalIgnoreCase)
                      && audioTransportOverlay.AcceptanceLength >= 5_500
                      && statusContains("Opened Extras audio"));
            await runOnUpdateThread(() =>
            {
                audioTransportOverlay?.Hide();
                extrasAudioBrowserOverlay?.Hide();
            });
            await captureCommandAcceptanceAsync(captures, "extras-library");
            await runOnUpdateThread(() => extrasOverlay?.Hide());

            var backupCount = backups.List().Count;
            await runCommandAcceptanceAsync(
                entries,
                "manual-backup",
                createDraftBackup,
                () => backups.List().Count == backupCount + 1
                      && statusContains("Created and verified draft backup"));

            await runCommandAcceptanceAsync(
                entries,
                "review-changes",
                reviewChanges,
                () => changeReviewOverlay?.Alpha > 0);
            await captureCommandAcceptanceAsync(captures, "change-review");
            await runOnUpdateThread(() => changeReviewOverlay?.Hide());

            var stagedBeforeDiscard = (await currentDraftAsync()).Changes.Count;
            assertAcceptance(
                stagedBeforeDiscard > 0,
                "Acceptance draft unexpectedly had no staged changes.");
            await runCommandAcceptanceAsync(
                entries,
                "discard-all-with-safety-backup",
                discardAllChanges,
                () => draft!.Changes.Count == 0
                      && statusContains("verified backup"));
            await runCommandAcceptanceAsync(
                entries,
                "undo-discard-all",
                undo,
                () => draft!.Changes.Count == stagedBeforeDiscard);

            await runCommandAcceptanceAsync(
                entries,
                "source-conflict-unchanged",
                checkSourceConflict,
                () => statusContains("unchanged"));
            using (var archive = ZipFile.Open(fixture, ZipArchiveMode.Update))
            {
                var entry = archive.CreateEntry(
                    "external-origin-change.txt",
                    CompressionLevel.NoCompression);
                using var writer = new StreamWriter(
                    entry.Open(),
                    new UTF8Encoding(false));
                writer.Write("external change");
            }
            await runCommandAcceptanceAsync(
                entries,
                "source-conflict-changed",
                checkSourceConflict,
                () => statusContains("Conflict detected"));

            var countBeforeDuplicate = drafts.List().Count;
            await runCommandAcceptanceAsync(
                entries,
                "duplicate-draft",
                duplicateDraft,
                () => drafts.List().Count == countBeforeDuplicate + 1
                      && draft!.SourcePath is not null);
            await runCommandAcceptanceAsync(
                entries,
                "rename-draft-and-author",
                () =>
                {
                    draft = drafts.UpdateIdentity(
                        draft!.DraftId,
                        "Renamed Acceptance Draft",
                        "Kumori Acceptance");
                    updateDraftPresentation(
                        "Updated identity through the native command callback.");
                },
                () => draft!.Name == "Renamed Acceptance Draft"
                      && draft.Creator == "Kumori Acceptance"
                      && currentSkinIni().GetValue(
                          "General",
                          "Name") == "Renamed Acceptance Draft");

            var renamedDraftId = (await currentDraftAsync()).DraftId;
            await runCommandAcceptanceAsync(
                entries,
                "open-draft-browser",
                browseDrafts,
                () => draftBrowserOverlay?.Alpha > 0);
            await captureCommandAcceptanceAsync(captures, "draft-browser");
            await runOnUpdateThread(() => draftBrowserOverlay?.Hide());
            await runCommandAcceptanceAsync(
                entries,
                "select-next-draft",
                selectNextDraft,
                () => draft!.DraftId != renamedDraftId);
            await runCommandAcceptanceAsync(
                entries,
                "reopen-draft",
                () => openDraft(renamedDraftId),
                () => draft!.DraftId == renamedDraftId
                      && draft.Name == "Renamed Acceptance Draft");

            var export = Path.Combine(
                commandAcceptanceOutputPath,
                "command-acceptance-export.osk");
            await runCommandAcceptanceAsync(
                entries,
                "export-osk",
                () => assertAcceptance(
                    exportDraftTo(export),
                    "Draft export returned false."),
                () => File.Exists(export)
                      && packageHasExpectedEntries(export));

            if (!string.IsNullOrWhiteSpace(contract.PlayerRoot))
            {
                var blocked = Path.Combine(
                    contract.PlayerRoot,
                    "skins",
                    "command-acceptance-must-not-exist.osk");
                await runCommandAcceptanceAsync(
                    entries,
                    "block-export-to-player-root",
                    () => assertAcceptance(
                        !exportDraftTo(blocked),
                        "Player-root export was not blocked."),
                    () => !File.Exists(blocked)
                          && statusContains("Export failed"));
            }

            await runCommandAcceptanceAsync(
                entries,
                "live-sync-disabled",
                syncLivePreview,
                () => statusContains("Live sync is disabled"));

            await runCommandAcceptanceAsync(
                entries,
                "real-gameplay-mode",
                showGameplay,
                () => gameplayMode && player is not null);
            await waitForAcceptanceAsync(
                () => player?.CanSeekForAcceptance == true,
                TimeSpan.FromSeconds(15));
            await captureCommandAcceptanceAsync(captures, "real-gameplay");
            await runCommandAcceptanceAsync(
                entries,
                "all-elements-workbench-mode",
                showWorkbench,
                () => !gameplayMode
                      && workbenchContainer?.Alpha > 0
                      && gameplayContainer?.Alpha == 0);

            await runCommandAcceptanceAsync(
                entries,
                "source-conflict-duplicate-snapshot",
                checkSourceConflict,
                () => statusContains("unchanged"));

            if (!string.IsNullOrWhiteSpace(contract.PlayerRoot))
            {
                await runCommandAcceptanceAsync(
                    entries,
                    "open-installed-skin-browser-read-only",
                    browseInstalledSkins,
                    () => installedSkinCatalog?.Skins.Count > 0
                          && installedSkinBrowserOverlay?.Alpha > 0
                          && statusContains("without writing"));
                await captureCommandAcceptanceAsync(
                    captures,
                    "installed-skin-browser");
                await runOnUpdateThread(() =>
                    installedSkinBrowserOverlay?.Hide());
                var draftCountBeforeInstalledImport = drafts.List().Count;
                await runCommandAcceptanceAsync(
                    entries,
                    "import-installed-skin-snapshot",
                    () => importInstalledSkin(
                        installedSkinCatalog!.Skins[0].Id),
                    () => drafts.List().Count
                              == draftCountBeforeInstalledImport + 1
                          && draft!.OriginPath is null
                          && draft.SourcePath is not null
                          && SkinStudioWriteBoundary.IsNormalWriteAllowed(
                              contract.PlayerRoot,
                              draft.SourcePath)
                          && statusContains("player root remains read-only"));
            }

            var recoveryDraftId = (await currentDraftAsync()).DraftId;
            var recoveryDirectory = Path.Combine(
                contract.WorkspacePath,
                "drafts",
                recoveryDraftId.ToString("N"));
            var committedManifest = Path.Combine(
                recoveryDirectory,
                "manifest.json");
            File.Copy(
                committedManifest,
                committedManifest + ".new",
                overwrite: true);
            await runCommandAcceptanceAsync(
                entries,
                "recover-interrupted-draft-save",
                recoverInterruptedDraft,
                () => !File.Exists(committedManifest + ".new")
                      && Directory.EnumerateFiles(
                          Path.Combine(
                              recoveryDirectory,
                              "recovery-backups"),
                          "*.json").Any()
                      && statusContains("Recovered interrupted save"));

            var deletedBefore = drafts.ListDeleted().Count;
            await runCommandAcceptanceAsync(
                entries,
                "delete-draft-two-step",
                () =>
                {
                    deleteDraftRecoverably();
                    deleteDraftRecoverably();
                },
                () => drafts.ListDeleted().Count == deletedBefore + 1);
            await runCommandAcceptanceAsync(
                entries,
                "restore-deleted-draft",
                restoreLastDeletedDraft,
                () => drafts.ListDeleted().Count == deletedBefore
                      && statusContains("Restored draft"));
            await captureCommandAcceptanceAsync(captures, "final-workbench");

            var manifestPath = Path.Combine(
                commandAcceptanceOutputPath,
                "command-acceptance-manifest.json");
            File.WriteAllText(
                manifestPath,
                JsonSerializer.Serialize(
                    new
                    {
                        format = 1,
                        lazer_revision = Program.LazerRevision,
                        packaged_runtime_required = true,
                        command_count = entries.Count,
                        commands = entries,
                        captures,
                        exported_osk = new
                        {
                            file = Path.GetFileName(export),
                            size = new FileInfo(export).Length,
                            sha256 = hashFile(export),
                        },
                        player_root_write_block_verified =
                            !string.IsNullOrWhiteSpace(contract.PlayerRoot),
                        verification = "passed",
                    },
                    SkinStudioLaunchContract.JsonOptions));
            gameHost.Exit();
        }
        catch (Exception ex)
        {
            Logger.Error(ex, "Native command acceptance capture failed.");
            try
            {
                File.WriteAllText(
                    Path.Combine(
                        commandAcceptanceOutputPath,
                        "command-acceptance-failure.json"),
                    JsonSerializer.Serialize(
                        new
                        {
                            verification = "failed",
                            type = ex.GetType().FullName,
                            message = ex.Message,
                            commands = entries,
                            captures,
                        },
                        SkinStudioLaunchContract.JsonOptions));
            }
            catch
            {
            }
            gameHost.Exit();
        }
    }

    private async Task runCommandAcceptanceAsync(
        ICollection<StudioCommandAcceptanceEntry> entries,
        string name,
        Action command,
        Func<bool> verify)
    {
        var before = await currentDraftAsync();
        await runOnUpdateThread(command);
        var verified = false;
        string status = "";
        SkinDraftManifest after = before;
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(8);
        do
        {
            await Task.Delay(50);
            await runOnUpdateThread(() =>
            {
                after = draft is null ? before : drafts.Load(draft.DraftId);
                draft = after;
                status = statusText?.Text.ToString() ?? "";
                verified = verify();
            });
        } while (!verified && DateTime.UtcNow < deadline);
        assertAcceptance(
            verified,
            $"Command acceptance verification failed for '{name}'. Status: {status}");
        entries.Add(new StudioCommandAcceptanceEntry(
            name,
            before.Changes.Count,
            after.Changes.Count,
            status,
            "passed"));
    }

    private async Task<SkinDraftManifest> currentDraftAsync()
    {
        SkinDraftManifest? current = null;
        await runOnUpdateThread(() =>
        {
            if (draft is null)
                throw new InvalidOperationException(
                    "Command acceptance requires an active draft.");
            current = drafts.Load(draft.DraftId);
            draft = current;
        });
        return current!;
    }

    private async Task captureCommandAcceptanceAsync(
        ICollection<StudioCommandAcceptanceCapture> captures,
        string name)
    {
        await Task.Delay(200);
        var filename = $"{captures.Count + 1:00}-{safeTargetName(name)}.png";
        var path = Path.Combine(commandAcceptanceOutputPath!, filename);
        using var image = await gameHost!.TakeScreenshotAsync()
                          ?? throw new InvalidOperationException(
                              "The desktop renderer returned no command-acceptance screenshot.");
        if (image.Width < 800 || image.Height < 600)
        {
            throw new InvalidDataException(
                $"Command acceptance capture '{name}' rendered at "
                + $"{image.Width}Ã—{image.Height}.");
        }
        await image.SaveAsPngAsync(path);
        captures.Add(new StudioCommandAcceptanceCapture(
            name,
            filename,
            image.Width,
            image.Height,
            hashFile(path)));
    }

    private bool statusContains(string value) =>
        statusText?.Text.ToString().Contains(
            value,
            StringComparison.OrdinalIgnoreCase) == true;

    private IReadOnlyDictionary<string, byte[]> materialized() =>
        new SkinPackageService(drafts).Materialize(
            draft?.DraftId
            ?? throw new InvalidOperationException("No active draft."));

    private SkinIniDocument currentSkinIni() =>
        SkinIniDocument.Parse(materialized()["skin.ini"]);

    private int[] animationFrames(string component) =>
        assets.Family(draft!.DraftId, component)
            .Where(asset => asset.AnimationFrame is not null)
            .Select(asset => asset.AnimationFrame!.Value)
            .Distinct()
            .Order()
            .ToArray();

    private static void assertAcceptance(bool condition, string message)
    {
        if (!condition)
            throw new InvalidDataException(message);
    }

    private static bool packageHasExpectedEntries(string path)
    {
        SkinPackageService.ValidatePackage(path);
        using var archive = ZipFile.OpenRead(path);
        var names = archive.Entries
            .Select(entry => entry.FullName)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        return names.Contains("skin.ini")
               && names.Contains("hitcircle.png")
               && names.Contains("normal-hitnormal.wav");
    }

    private static string createCommandAcceptanceSkin(string directory)
    {
        var skinDirectory = Path.Combine(directory, "source-skin");
        Directory.CreateDirectory(skinDirectory);
        File.WriteAllText(
            Path.Combine(skinDirectory, "skin.ini"),
            "; preserved acceptance comment\r\n"
            + "[General]\r\n"
            + "Name: Command Acceptance\r\n"
            + "Author: Kumori\r\n"
            + "Version: 2.7\r\n"
            + "UnknownAcceptance: retained\r\n",
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
        writePng(
            Path.Combine(skinDirectory, "hitcircle.png"),
            64,
            new Rgba32(255, 80, 160, 255));
        writePng(
            Path.Combine(skinDirectory, "hitcircle@2x.png"),
            128,
            new Rgba32(80, 190, 255, 255));
        writePng(
            Path.Combine(skinDirectory, "cursor.png"),
            64,
            new Rgba32(255, 255, 255, 255));
        writePng(
            Path.Combine(skinDirectory, "cursormiddle.png"),
            1,
            new Rgba32(0, 0, 0, 0));
        var package = Path.Combine(directory, "Command Acceptance.osk");
        if (File.Exists(package))
            File.Delete(package);
        ZipFile.CreateFromDirectory(
            skinDirectory,
            package,
            CompressionLevel.Optimal,
            includeBaseDirectory: false);
        return package;
    }

    private static string createCommandAcceptanceAssetFolder(string directory)
    {
        var folder = Path.Combine(directory, "asset-folder");
        Directory.CreateDirectory(folder);
        writePng(
            Path.Combine(folder, "followpoint-0.png"),
            32,
            new Rgba32(255, 255, 255, 255));
        writePng(
            Path.Combine(folder, "followpoint-0@2x.png"),
            64,
            new Rgba32(255, 255, 255, 255));
        writePng(
            Path.Combine(folder, "followpoint-1.png"),
            32,
            new Rgba32(255, 170, 220, 255));
        writePng(
            Path.Combine(folder, "followpoint-1@2x.png"),
            64,
            new Rgba32(255, 170, 220, 255));
        File.WriteAllBytes(
            Path.Combine(folder, "normal-hitnormal.wav"),
            createWaveFixture());
        File.WriteAllBytes(
            Path.Combine(folder, "applause.wav"),
            createWaveFixture());
        return folder;
    }

    private static void writePng(string path, int size, Rgba32 colour)
    {
        using var image = new Image<Rgba32>(size, size, colour);
        image.SaveAsPng(path);
    }

    private static byte[] createWaveFixture(double frequency = 440)
    {
        const int sampleRate = 44_100;
        const int sampleCount = sampleRate * 6;
        const short channels = 1;
        const short bits = 16;
        var dataLength = sampleCount * channels * (bits / 8);
        using var stream = new MemoryStream(44 + dataLength);
        using var writer = new BinaryWriter(stream, Encoding.ASCII, leaveOpen: true);
        writer.Write("RIFF"u8.ToArray());
        writer.Write(36 + dataLength);
        writer.Write("WAVE"u8.ToArray());
        writer.Write("fmt "u8.ToArray());
        writer.Write(16);
        writer.Write((short)1);
        writer.Write(channels);
        writer.Write(sampleRate);
        writer.Write(sampleRate * channels * (bits / 8));
        writer.Write((short)(channels * (bits / 8)));
        writer.Write(bits);
        writer.Write("data"u8.ToArray());
        writer.Write(dataLength);
        for (var i = 0; i < sampleCount; i++)
        {
            var sample = (short)(Math.Sin(
                2 * Math.PI * frequency * i / sampleRate)
                * short.MaxValue
                * 0.35);
            writer.Write(sample);
        }
        writer.Flush();
        return stream.ToArray();
    }
}
