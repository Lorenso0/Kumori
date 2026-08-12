using System.IO.Compression;
using Kumori.Skins;
using osu.Framework.Graphics;
using osu.Framework.Graphics.Containers;
using osu.Framework.Graphics.Shapes;
using osu.Framework.Graphics.Sprites;
using osu.Game.Graphics.Containers;
using osu.Game.Graphics.UserInterface;
using osuTK;
using osuTK.Graphics;

namespace Kumori.SkinStudio;

internal partial class StudioExtrasOverlay : CompositeDrawable
{
    private readonly string extrasRoot;
    private readonly Action<SkinExtraPackDescriptor, SkinDraftExtrasSelection> apply;
    private readonly Func<
        SkinExtraPackDescriptor,
        SkinDraftExtrasSelection,
        string> compare;
    private readonly Action<SkinExtraPackDescriptor> export;
    private readonly Action<SkinExtraPackDescriptor> delete;
    private readonly Action restore;
    private readonly Action<SkinExtraPackDescriptor> browseAudio;
    private readonly Action<string> report;
    private readonly OsuTextBox search;
    private readonly FillFlowContainer packsFlow;
    private readonly FillFlowContainer selectionOptionsFlow;
    private readonly SpriteText selection;
    private readonly StudioTextPromptOverlay renamePrompt;
    private readonly StudioActionButton applyButton;
    private readonly StudioActionButton compareButton;
    private readonly StudioActionButton exportButton;
    private readonly StudioActionButton renameButton;
    private readonly StudioActionButton validateButton;
    private readonly StudioActionButton repairButton;
    private readonly StudioActionButton favoriteButton;
    private readonly StudioActionButton audioBrowserButton;
    private readonly StudioActionButton deleteButton;
    private readonly StudioActionButton restoreButton;
    private readonly StudioActionButton replaceModeButton;
    private readonly StudioActionButton synchronizeButton;
    private readonly StudioActionButton cancelSynchronizationButton;
    private readonly SpriteText synchronizationStatus;
    private readonly SkinExtrasCatalogSyncService catalogSync;
    private readonly HashSet<string> selectedTargets =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<SkinExtraIniPatchEntry> selectedSettings = [];
    private IReadOnlyList<SkinExtraPackDescriptor> packs = [];
    private SkinExtraPackDescriptor? selected;
    private bool favoritesOnly;
    private bool lazerUsedOnly;
    private bool replaceEntireFamily = true;

    public StudioExtrasOverlay(
        string extrasRoot,
        Action<SkinExtraPackDescriptor, SkinDraftExtrasSelection> apply,
        Func<SkinExtraPackDescriptor, SkinDraftExtrasSelection, string> compare,
        Action<SkinExtraPackDescriptor> export,
        Action<SkinExtraPackDescriptor> delete,
        Action restore,
        Action<SkinExtraPackDescriptor> browseAudio,
        Action<string> report,
        SkinExtrasCatalogSyncService? catalogSync = null)
    {
        this.extrasRoot = extrasRoot;
        this.apply = apply;
        this.compare = compare;
        this.export = export;
        this.delete = delete;
        this.restore = restore;
        this.browseAudio = browseAudio;
        this.report = report;
        this.catalogSync = catalogSync
                           ?? new SkinExtrasCatalogSyncService(
                               extrasRoot: extrasRoot);
        RelativeSizeAxes = Axes.Both;
        Depth = -92;
        InternalChildren =
        [
            new Box
            {
                RelativeSizeAxes = Axes.Both,
                Colour = Colour4.Black.Opacity(0.76f),
            },
            new Container
            {
                RelativeSizeAxes = Axes.Both,
                Padding = new MarginPadding
                {
                    Horizontal = 90,
                    Vertical = 54,
                },
                Child = new Container
                {
                    RelativeSizeAxes = Axes.Both,
                    Masking = true,
                    CornerRadius = 12,
                    Children =
                    [
                        new Box
                        {
                            RelativeSizeAxes = Axes.Both,
                            Colour = Colour4.FromHex("#1B1925"),
                        },
                        new Container
                        {
                            RelativeSizeAxes = Axes.X,
                            Height = 174,
                            Padding = new MarginPadding(24),
                            Child = new FillFlowContainer
                            {
                                RelativeSizeAxes = Axes.X,
                                AutoSizeAxes = Axes.Y,
                                Direction = FillDirection.Vertical,
                                Spacing = new Vector2(0, 8),
                                Children =
                                [
                                    label("EXTRAS LIBRARY", 21, true),
                                    search = new OsuTextBox
                                    {
                                        RelativeSizeAxes = Axes.X,
                                        PlaceholderText = "Search pack, family, area, author, or tag",
                                    },
                                    selection = label("No pack selected", 11, false),
                                ],
                            },
                        },
                        new OsuScrollContainer(Direction.Vertical)
                        {
                            RelativeSizeAxes = Axes.Both,
                            Padding = new MarginPadding
                            {
                                Top = 174,
                                Bottom = 342,
                                Horizontal = 24,
                            },
                            ScrollbarVisible = true,
                            Child = packsFlow = new FillFlowContainer
                            {
                                RelativeSizeAxes = Axes.X,
                                AutoSizeAxes = Axes.Y,
                                Direction = FillDirection.Vertical,
                                Spacing = new Vector2(0, 8),
                            },
                        },
                        new Container
                        {
                            RelativeSizeAxes = Axes.X,
                            Height = 342,
                            Anchor = Anchor.BottomLeft,
                            Origin = Anchor.BottomLeft,
                            Children =
                            [
                                new Box
                                {
                                    RelativeSizeAxes = Axes.Both,
                                    Colour = Colour4.FromHex("#1B1925"),
                                },
                                new OsuScrollContainer(Direction.Vertical)
                                {
                                    RelativeSizeAxes = Axes.Both,
                                    Padding = new MarginPadding(24),
                                    ScrollbarVisible = true,
                                    Child = new FillFlowContainer
                                    {
                                        RelativeSizeAxes = Axes.X,
                                        AutoSizeAxes = Axes.Y,
                                        Direction = FillDirection.Vertical,
                                        Spacing = new Vector2(0, 8),
                                        Padding = new MarginPadding { Right = 10, Bottom = 8 },
                                        Children =
                                        [
                                            selectionOptionsFlow = new FillFlowContainer
                                            {
                                                RelativeSizeAxes = Axes.X,
                                                AutoSizeAxes = Axes.Y,
                                                Direction = FillDirection.Vertical,
                                                Spacing = new Vector2(0, 8),
                                            },
                                            replaceModeButton = new StudioActionButton(
                                                "Replace entire family: on",
                                                toggleReplaceMode,
                                                enabled: false),
                                            compareButton = new StudioActionButton(
                                                "Compare selection with current draft",
                                                compareSelected,
                                                enabled: false),
                                            applyButton = new StudioActionButton(
                                                "Apply selected files / settings",
                                                applySelected,
                                                accent: true,
                                                enabled: false),
                                            exportButton = new StudioActionButton(
                                                "Export selected package",
                                                exportSelected,
                                                enabled: false),
                                            renameButton = new StudioActionButton(
                                                "Rename selected pack",
                                                renameSelected,
                                                enabled: false),
                                            validateButton = new StudioActionButton(
                                                "Validate selected pack",
                                                validateSelected,
                                                enabled: false),
                                            repairButton = new StudioActionButton(
                                                "Repair selected pack",
                                                repairSelected,
                                                enabled: false),
                                            favoriteButton = new StudioActionButton(
                                                "Toggle selected favourite",
                                                toggleFavorite,
                                                enabled: false),
                                            audioBrowserButton = new StudioActionButton(
                                                "Browse / preview pack audio",
                                                browseSelectedAudio,
                                                enabled: false),
                                            new StudioActionButton("Show all / favourites", toggleFavoritesOnly),
                                            new StudioActionButton(
                                                "Show all / lazer-used only",
                                                toggleLazerUsedOnly),
                                            deleteButton = new StudioActionButton(
                                                "Move selected pack to trash",
                                                deleteSelected,
                                                enabled: false),
                                            restoreButton = new StudioActionButton(
                                                "Restore latest deleted pack",
                                                restore,
                                                enabled: false),
                                            synchronizationStatus = label(
                                                "Catalog synchronization is idle.",
                                                10,
                                                false),
                                            synchronizeButton = new StudioActionButton(
                                                "Check / synchronize catalog",
                                                synchronizeCatalog),
                                            cancelSynchronizationButton = new StudioActionButton(
                                                "Cancel synchronization",
                                                cancelCatalogSynchronization,
                                                enabled: false),
                                            new StudioActionButton("Refresh library", refresh),
                                            new StudioActionButton("Close Extras", Hide),
                                        ],
                                    },
                                },
                            ],
                        },
                        renamePrompt = new StudioTextPromptOverlay(),
                    ],
                },
            },
        ];
        search.Current.BindValueChanged(_ => rebuild());
        this.catalogSync.ProgressChanged += (_, progress) =>
            Schedule(() => updateSynchronizationProgress(progress));
        this.catalogSync.LibraryChanged += (_, _) =>
            Schedule(refresh);
        if (this.catalogSync.CurrentProgress is { } currentProgress)
            updateSynchronizationProgress(currentProgress);
        rebuildSelectionOptions();
        Hide();
    }

    public void Present()
    {
        refresh();
        Show();
    }

    public void RefreshLibrary() => refresh();

    private async void synchronizeCatalog()
    {
        synchronizeButton.SetEnabled(false);
        cancelSynchronizationButton.SetEnabled(true);
        try
        {
            var result = await catalogSync.SynchronizeAsync(manual: true);
            Schedule(() =>
            {
                refresh();
                report(result.Message);
            });
        }
        catch (OperationCanceledException)
        {
            Schedule(() => report(
                "Extras synchronization was canceled safely. Use Check / synchronize catalog to resume."));
        }
        catch (Exception ex)
        {
            Schedule(() => report(
                $"Extras synchronization failed without replacing the library: {ex.Message}"));
        }
    }

    private void cancelCatalogSynchronization()
    {
        if (!catalogSync.CancelActiveSynchronization())
            report("No Extras synchronization is currently running.");
    }

    private void updateSynchronizationProgress(SkinExtrasSyncProgress progress)
    {
        synchronizationStatus.Text = progress.Message;
        var running = IsSynchronizationRunning(progress.Stage);
        synchronizeButton.SetEnabled(!running);
        cancelSynchronizationButton.SetEnabled(running);
    }

    internal static bool IsSynchronizationRunning(SkinExtrasSyncStage stage) =>
        stage is SkinExtrasSyncStage.Checking
            or SkinExtrasSyncStage.Planning
            or SkinExtrasSyncStage.Downloading
            or SkinExtrasSyncStage.Installing;

    private void refresh()
    {
        try
        {
            Directory.CreateDirectory(extrasRoot);
            packs = SkinExtrasPersistentIndex.ScanCached(extrasRoot);
            if (selected is not null)
            {
                selected = packs.FirstOrDefault(pack =>
                    pack.Manifest.Fingerprint.Equals(
                        selected.Manifest.Fingerprint,
                        StringComparison.OrdinalIgnoreCase));
                if (selected is null)
                    clearSelectionOptions();
            }
            rebuild();
            report($"Extras library loaded {packs.Count} pack(s).");
        }
        catch (Exception ex)
        {
            packs = [];
            selected = null;
            clearSelectionOptions();
            rebuild();
            report($"Extras library failed: {ex.Message}");
        }
    }

    private void rebuild()
    {
        packsFlow.Clear();
        var states = SkinExtrasLibraryStateStore.GetAll(extrasRoot);
        var term = search.Current.Value.Trim();
        var visible = packs.Where(pack =>
        {
            states.TryGetValue(pack.Manifest.Fingerprint, out var state);
            if (favoritesOnly && state?.Favorite != true)
                return false;
            if (lazerUsedOnly
                && !SkinExtraLazerCompatibility.HasLazerUsedContent(
                    pack.Manifest))
            {
                return false;
            }
            if (term.Length == 0)
                return true;
            return pack.Manifest.DisplayName.Contains(term, StringComparison.OrdinalIgnoreCase)
                   || pack.Manifest.FamilyName.Contains(term, StringComparison.OrdinalIgnoreCase)
                   || pack.Manifest.Area.Contains(term, StringComparison.OrdinalIgnoreCase)
                   || (pack.Manifest.SourceAuthor?.Contains(
                       term,
                       StringComparison.OrdinalIgnoreCase) ?? false)
                   || (state?.Tags.Any(tag =>
                       tag.Contains(term, StringComparison.OrdinalIgnoreCase)) ?? false);
        }).ToArray();

        foreach (var pack in visible)
        {
            states.TryGetValue(pack.Manifest.Fingerprint, out var state);
            var marker = state?.Favorite == true ? "★" : "☆";
            var compatibility =
                SkinExtraLazerCompatibility.CompatibilityBadge(pack.Manifest);
            var button = new StudioActionButton(
                $"{marker} {pack.Manifest.DisplayName}  ·  {pack.Manifest.FamilyName}"
                + (compatibility.Length == 0 ? "" : $"  ·  {compatibility}"),
                () => select(pack));
            button.SetSelected(selected?.Manifest.Fingerprint.Equals(
                pack.Manifest.Fingerprint,
                StringComparison.OrdinalIgnoreCase) == true);
            packsFlow.Add(button);
        }
        if (visible.Length == 0)
            packsFlow.Add(label("No Extras packs match this view.", 13, false));
        updateActionStates();
    }

    private void select(SkinExtraPackDescriptor pack)
    {
        selected = pack;
        selectedTargets.Clear();
        foreach (var file in pack.Manifest.Files)
        {
            selectedTargets.Add(
                SkinDraftWorkspaceService.NormalizeSkinFilename(
                    file.TargetFilename));
        }
        selectedSettings.Clear();
        selectedSettings.UnionWith(pack.Manifest.IniPatch);
        replaceEntireFamily = true;
        selection.Text =
            $"{pack.Manifest.DisplayName} · {pack.Manifest.Area} / {pack.Manifest.FamilyName} · {pack.Manifest.Files.Count} file(s)";
        rebuildSelectionOptions();
        rebuild();
    }

    private void updateActionStates()
    {
        var hasSelection = selected is not null;
        applyButton.SetEnabled(
            hasSelection
            && (selectedTargets.Count > 0 || selectedSettings.Count > 0));
        compareButton.SetEnabled(
            hasSelection
            && (selectedTargets.Count > 0 || selectedSettings.Count > 0));
        exportButton.SetEnabled(hasSelection);
        renameButton.SetEnabled(hasSelection);
        validateButton.SetEnabled(hasSelection);
        repairButton.SetEnabled(hasSelection);
        favoriteButton.SetEnabled(hasSelection);
        audioBrowserButton.SetEnabled(
            hasSelection
            && selected!.Manifest.Files.Any(file =>
                SkinMediaTypes.IsAudio(file.TargetFilename)));
        deleteButton.SetEnabled(hasSelection);
        replaceModeButton.SetEnabled(hasSelection);
        restoreButton.SetEnabled(
            new SkinExtraPackTrashService().List(extrasRoot).Count > 0);
    }

    private void rebuildSelectionOptions()
    {
        selectionOptionsFlow.Clear();
        if (selected is null)
        {
            selectionOptionsFlow.Add(label(
                "Select a pack to choose logical elements and settings.",
                11,
                false));
            replaceModeButton.SetText("Replace entire family: on");
            replaceModeButton.SetSelected(false);
            return;
        }

        selectionOptionsFlow.Add(label("FILES", 11, true));
        foreach (var group in selected.Manifest.Files
                     .GroupBy(file => SkinDraftAssetService.ComponentName(
                         file.TargetFilename))
                     .OrderBy(group => group.Key, StringComparer.OrdinalIgnoreCase))
        {
            var targets = group.Select(file =>
                    SkinDraftWorkspaceService.NormalizeSkinFilename(
                        file.TargetFilename))
                .ToArray();
            var button = new StudioActionButton(
                $"{group.Key} · {targets.Length} file(s)",
                () => toggleTargets(targets));
            button.SetSelected(targets.All(selectedTargets.Contains));
            selectionOptionsFlow.Add(button);
        }
        if (selected.Manifest.Files.Count == 0)
            selectionOptionsFlow.Add(label("No files in this pack.", 11, false));

        selectionOptionsFlow.Add(label("SKIN.INI SETTINGS", 11, true));
        foreach (var setting in selected.Manifest.IniPatch)
        {
            var button = new StudioActionButton(
                $"{setting.Section} / {setting.Key}: {setting.Value}",
                () => toggleSetting(setting));
            button.SetSelected(selectedSettings.Contains(setting));
            selectionOptionsFlow.Add(button);
        }
        if (selected.Manifest.IniPatch.Count == 0)
            selectionOptionsFlow.Add(label("No settings in this pack.", 11, false));

        replaceModeButton.SetText(
            replaceEntireFamily
                ? "Replace entire family: on"
                : "Replace selected elements only");
        replaceModeButton.SetSelected(replaceEntireFamily);
        updateActionStates();
    }

    private void toggleTargets(IReadOnlyCollection<string> targets)
    {
        if (targets.All(selectedTargets.Contains))
            selectedTargets.ExceptWith(targets);
        else
            selectedTargets.UnionWith(targets);
        rebuildSelectionOptions();
    }

    private void toggleSetting(SkinExtraIniPatchEntry setting)
    {
        if (!selectedSettings.Add(setting))
            selectedSettings.Remove(setting);
        rebuildSelectionOptions();
    }

    private void toggleReplaceMode()
    {
        replaceEntireFamily = !replaceEntireFamily;
        rebuildSelectionOptions();
    }

    private void clearSelectionOptions()
    {
        selectedTargets.Clear();
        selectedSettings.Clear();
        replaceEntireFamily = true;
        rebuildSelectionOptions();
    }

    private void applySelected()
    {
        if (selected is null)
        {
            report("Select an Extras pack first.");
            return;
        }
        apply(
            selected,
            new SkinDraftExtrasSelection(
                selectedTargets.ToArray(),
                selectedSettings.ToArray(),
                replaceEntireFamily));
    }

    private void compareSelected()
    {
        if (selected is null)
        {
            report("Select an Extras pack first.");
            return;
        }
        try
        {
            var summary = compare(
                selected,
                new SkinDraftExtrasSelection(
                    selectedTargets.ToArray(),
                    selectedSettings.ToArray(),
                    replaceEntireFamily));
            selection.Text = summary;
            report(summary);
        }
        catch (Exception ex)
        {
            report($"Extras comparison failed: {ex.Message}");
        }
    }

    private void exportSelected()
    {
        if (selected is null)
        {
            report("Select an Extras pack first.");
            return;
        }
        export(selected);
    }

    private void deleteSelected()
    {
        if (selected is null)
        {
            report("Select an Extras pack first.");
            return;
        }
        delete(selected);
        selected = null;
        clearSelectionOptions();
        refresh();
    }

    private void renameSelected()
    {
        if (selected is null)
        {
            report("Select an Extras pack first.");
            return;
        }
        var original = selected;
        renamePrompt.Present(
            "Rename Extras pack",
            "Choose a unique name within this Extras family.",
            original.Manifest.DisplayName,
            requested =>
            {
                selected = SkinExtraPackRenamer.Rename(
                    extrasRoot,
                    original,
                    requested);
                var renamed = selected.Manifest.DisplayName;
                refresh();
                report($"Renamed Extras pack to “{renamed}”.");
                return true;
            });
    }

    private void validateSelected()
    {
        if (selected is null)
        {
            report("Select an Extras pack first.");
            return;
        }
        try
        {
            var health = SkinExtraPackValidator.Validate(selected);
            var summary = health.Issues.Count == 0
                ? "Healthy: no validation issues."
                : $"{health.Errors} error(s), {health.Warnings} warning(s): "
                  + string.Join(
                      " · ",
                      health.Issues.Take(3).Select(issue =>
                          $"{issue.Code}: {issue.Message}"));
            selection.Text = summary;
            report($"{selected.Manifest.DisplayName}: {summary}");
        }
        catch (Exception ex)
        {
            report($"Extras validation failed: {ex.Message}");
        }
    }

    private void repairSelected()
    {
        if (selected is null)
        {
            report("Select an Extras pack first.");
            return;
        }
        try
        {
            var before = SkinExtraPackValidator.Validate(selected);
            if (before.Issues.Count == 0)
            {
                report($"{selected.Manifest.DisplayName} is already healthy.");
                return;
            }
            var packDirectory = SkinExtraPackDeletion.ResolvePackDirectory(
                extrasRoot,
                selected.DirectoryPath);
            var backupDirectory = Path.Combine(
                extrasRoot,
                ".kumori",
                "repair-backups");
            Directory.CreateDirectory(backupDirectory);
            var backupPath = Path.Combine(
                backupDirectory,
                $"{DateTimeOffset.UtcNow:yyyyMMdd-HHmmss}-{Guid.NewGuid():N}.zip");
            ZipFile.CreateFromDirectory(
                packDirectory,
                backupPath,
                CompressionLevel.Optimal,
                includeBaseDirectory: false);
            using (var archive = ZipFile.OpenRead(backupPath))
            {
                if (archive.Entries.Count == 0)
                    throw new InvalidDataException("The pre-repair backup is empty.");
            }
            selected = SkinExtraPackValidator.Repair(selected);
            var after = SkinExtraPackValidator.Validate(selected);
            refresh();
            report(
                $"Repaired {selected?.Manifest.DisplayName ?? "Extras pack"} after a verified backup. "
                + $"Remaining: {after.Errors} error(s), {after.Warnings} warning(s). Backup: {backupPath}");
        }
        catch (Exception ex)
        {
            report($"Extras repair stopped: {ex.Message}");
        }
    }

    private void toggleFavorite()
    {
        if (selected is null)
        {
            report("Select an Extras pack first.");
            return;
        }
        var fingerprint = selected.Manifest.Fingerprint;
        var current = SkinExtrasLibraryStateStore.Get(extrasRoot, fingerprint);
        SkinExtrasLibraryStateStore.Update(
            extrasRoot,
            fingerprint,
            state => state.Favorite = !current.Favorite);
        rebuild();
        report($"{selected.Manifest.DisplayName} favourite: {!current.Favorite}.");
    }

    private void browseSelectedAudio()
    {
        if (selected is null)
        {
            report("Select an Extras pack first.");
            return;
        }
        if (!selected.Manifest.Files.Any(file =>
                SkinMediaTypes.IsAudio(file.TargetFilename)))
        {
            report("The selected Extras pack contains no audio files.");
            return;
        }
        browseAudio(selected);
    }

    private void toggleFavoritesOnly()
    {
        favoritesOnly = !favoritesOnly;
        rebuild();
        report(favoritesOnly ? "Showing favourite Extras packs." : "Showing all Extras packs.");
    }

    private void toggleLazerUsedOnly()
    {
        lazerUsedOnly = !lazerUsedOnly;
        rebuild();
        report(
            lazerUsedOnly
                ? $"Showing only Extras used by lazer {SkinExtraLazerCompatibility.AuditedOsuVersion}."
                : "Showing all Extras compatibility levels.");
    }

    internal int AcceptancePackCount => packs.Count;

    internal int AcceptanceVisibleEntryCount => packsFlow.Count;

    internal string? AcceptanceSelectedFingerprint =>
        selected?.Manifest.Fingerprint;

    internal string? AcceptanceSelectedDisplayName =>
        selected?.Manifest.DisplayName;

    internal int AcceptanceSelectedTargetCount => selectedTargets.Count;

    internal bool AcceptanceReplaceEntireFamily => replaceEntireFamily;

    internal void ToggleFirstAcceptanceLogicalElement()
    {
        if (selected is null)
            throw new InvalidOperationException(
                "Select an Extras pack before toggling an element.");
        var targets = selected.Manifest.Files
            .GroupBy(file => SkinDraftAssetService.ComponentName(
                file.TargetFilename))
            .OrderBy(group => group.Key, StringComparer.OrdinalIgnoreCase)
            .First()
            .Select(file => SkinDraftWorkspaceService.NormalizeSkinFilename(
                file.TargetFilename))
            .ToArray();
        toggleTargets(targets);
    }

    internal void ToggleAcceptanceReplaceMode() => toggleReplaceMode();

    internal void SelectFirstAcceptancePack()
    {
        if (packs.Count == 0)
            throw new InvalidOperationException(
                "The Extras acceptance library is empty.");
        select(packs[0]);
    }

    internal void SelectFirstAcceptanceAudioPack()
    {
        var audioPack = packs.FirstOrDefault(pack =>
            pack.Manifest.Files.Any(file =>
                SkinMediaTypes.IsAudio(file.TargetFilename)));
        if (audioPack is null)
            throw new InvalidOperationException(
                "The Extras acceptance library contains no audio pack.");
        select(audioPack);
    }

    internal void SelectFirstAcceptanceLongTrackPack()
    {
        var longTrackPack = packs.FirstOrDefault(pack =>
            pack.Manifest.Files.Any(file =>
                SkinExtraFamilyRegistry.ForFile(file.TargetFilename)?.Id
                    is "audio.applause"
                    or "audio.failsound"
                    or "audio.welcome"));
        if (longTrackPack is null)
            throw new InvalidOperationException(
                "The Extras acceptance library contains no long-track pack.");
        select(longTrackPack);
    }

    internal void SelectAcceptanceFamily(string familyId)
    {
        var pack = packs.FirstOrDefault(candidate =>
            candidate.Manifest.FamilyId.Equals(
                familyId,
                StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidOperationException(
                $"The Extras acceptance library has no '{familyId}' pack.");
        select(pack);
    }

    internal void BrowseAcceptanceAudio() => browseSelectedAudio();

    internal void SetAcceptanceSearch(string value) =>
        search.Current.Value = value;

    internal void CompareAcceptanceSelection() => compareSelected();

    internal void ApplyAcceptanceSelection() => applySelected();

    internal void ExportAcceptanceSelection() => exportSelected();

    internal void ValidateAcceptanceSelection() => validateSelected();

    internal void RepairAcceptanceSelection() => repairSelected();

    internal void ToggleAcceptanceFavourite() => toggleFavorite();

    internal void ToggleAcceptanceFavouritesOnly() => toggleFavoritesOnly();

    internal void ToggleAcceptanceLazerUsedOnly() => toggleLazerUsedOnly();

    internal bool AcceptanceLazerUsedOnly => lazerUsedOnly;

    internal SkinExtrasSyncProgress? AcceptanceSynchronizationProgress =>
        catalogSync.CurrentProgress;

    internal string AcceptanceSynchronizationStatus =>
        synchronizationStatus.Text.ToString();

    internal void StartAcceptanceSynchronization() => synchronizeCatalog();

    internal void CancelAcceptanceSynchronization() =>
        cancelCatalogSynchronization();

    internal void RenameAcceptanceSelection(string name)
    {
        if (selected is null)
            throw new InvalidOperationException(
                "Select an Extras pack before renaming it.");
        selected = SkinExtraPackRenamer.Rename(
            extrasRoot,
            selected,
            name);
        refresh();
        report($"Renamed Extras pack to \"{name}\".");
    }

    internal void DeleteAcceptanceSelection() => deleteSelected();

    internal void RestoreAcceptanceSelection() => restore();

    private static SpriteText label(string text, float size, bool bold) => new()
    {
        Text = text,
        Font = FontUsage.Default.With(size: size, weight: bold ? "SemiBold" : "Regular"),
        Colour = bold ? Colour4.White : Colour4.FromHex("#C6A8BA"),
    };
}
