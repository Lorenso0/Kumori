using System.Text.Json;
using System.Drawing;
using System.IO.Compression;
using System.Reflection;
using osu.Framework.Allocation;
using osu.Framework.Audio;
using osu.Framework.Audio.Track;
using osu.Framework.Bindables;
using osu.Framework.Configuration;
using osu.Framework.Extensions;
using osu.Framework.Graphics;
using osu.Framework.Graphics.Containers;
using osu.Framework.Graphics.Sprites;
using osu.Framework.Input;
using osu.Framework.Graphics.Textures;
using osu.Framework.IO.Stores;
using osu.Framework.Logging;
using osu.Framework.Platform;
using osu.Framework.Screens;
using osu.Game;
using osu.Game.Beatmaps;
using osu.Game.Beatmaps.Formats;
using osu.Game.Configuration;
using osu.Game.Database;
using osu.Game.IO;
using osu.Game.Online.API.Requests.Responses;
using osu.Game.Rulesets;
using osu.Game.Rulesets.Mods;
using osu.Game.Rulesets.Osu;
using osu.Game.Rulesets.Osu.Mods;
using osu.Game.Rulesets.Osu.Replays;
using osu.Game.Scoring;
using osu.Game.Screens;
using osu.Game.Screens.Play;
using osu.Game.Skinning;

namespace Kumori.ReplayViewer;

/// <summary>
/// A minimal osu! host which runs the official ReplayPlayer. ReplayPlayer
/// creates DrawableOsuRuleset and OsuFramedReplayInputHandler; selecting
/// Argon Pro through SkinManager activates OsuArgonSkinTransformer and the
/// upstream Argon drawable implementations.
/// </summary>
public partial class ReplayViewerGame : OsuGameBase
{
    private readonly ViewerContract contract;
    private readonly BeatmapAnalysis analysis;
    private readonly PreparedReplayAnalysis? preparedAnalysis;
    private OsuScreenStack? screenStack;
    private KumoriWorkingBeatmap? workingBeatmap;
    private OsuRuleset? ruleset;
    private KumoriReplayPlayer? currentPlayer;
    private AdvancedAnalyzerOverlay? advancedAnalyzerOverlay;
    private AdvancedAnalyzerViewModel? advancedAnalyzerViewModel;
    private KumoriComparisonOverlay? comparisonOverlay;
    private GameHost? gameHost;
    private ComparisonContract? activeComparison;
    private ComparisonContract? importedComparison;
    private readonly Bindable<string> comparisonImportStatus = new(string.Empty);
    private readonly List<Action> replayScreenUnbindActions = [];
    private bool osrPickerOpen;

    internal ReplayViewerGame(ViewerContract contract, BeatmapAnalysis analysis, PreparedReplayAnalysis? preparedAnalysis = null)
    {
        this.contract = contract;
        this.analysis = analysis;
        this.preparedAnalysis = preparedAnalysis;
    }

    /// <summary>
    /// SkinManager constructs persisted skins while OsuGameBase is loading its
    /// dependencies, before this viewer creates an <see cref="OsuRuleset"/>.
    /// Preload the ruleset assembly so Newtonsoft can resolve ruleset-owned HUD
    /// component types found in lazer skin layout JSON during that earlier pass.
    /// </summary>
    internal static void EnsureSkinLayoutDependenciesLoaded()
    {
        // Load by simple name. Kumori's release version is intentionally
        // applied to bundled assemblies, while lazer layout JSON persists the
        // assembly version it was exported with. Once the matching simple-name
        // assembly is loaded, System.Type conversion can resolve those layout
        // entries across that harmless version difference.
        Assembly rulesetAssembly = Assembly.Load("osu.Game.Rulesets.Osu");
        const string requiredType = "osu.Game.Rulesets.Osu.HUD.AimErrorMeter";
        if (rulesetAssembly.GetType(requiredType, throwOnError: false) is null)
        {
            throw new TypeLoadException(
                $"The bundled osu! ruleset does not provide the skin HUD component '{requiredType}'.");
        }

        Logger.Log(
            $"Kumori: preloaded skin layout dependency {rulesetAssembly.GetName().FullName}.");
    }

    public override void SetHost(GameHost host)
    {
        base.SetHost(host);

        if (host.Window != null)
        {
            using Stream? icon = typeof(ReplayViewerGame).Assembly
                .GetManifestResourceStream("Kumori.ReplayViewer.replay.ico");
            if (icon != null)
                host.Window.SetIconFromStream(icon);

            host.Window.CursorState |= CursorState.Hidden;
        }
    }

    protected override void LoadComplete()
    {
        try
        {
            base.LoadComplete();

            // OsuGameBase registers these services during its own dependency
            // loading. Building the player here (rather than in a derived
            // BackgroundDependencyLoader) guarantees they are available.
            var audio = Dependencies.Get<AudioManager>();
            var host = gameHost = Dependencies.Get<GameHost>();
            var frameworkConfig = Dependencies.Get<FrameworkConfigManager>();

            configureWindow(frameworkConfig);

            Window.Title = $"Kumori — {analysis.Artist} — {analysis.Title} [{analysis.Difficulty}]";

            ruleset = new OsuRuleset();
            workingBeatmap = new KumoriWorkingBeatmap(contract.BeatmapPath, contract.MediaDirectory, contract.MediaPaths, audio, host);
            Beatmap.Value = workingBeatmap;
            Ruleset.Value = ruleset.RulesetInfo;

            // Analysis playback: the hit-lighting flash (skin lighting.png)
            // obscures exactly the moments being reviewed. This host has its own
            // config store, so this never touches the player's real osu! setting.
            LocalConfig.SetValue(OsuSetting.HitLighting, false);

            viewerConfig = new KumoriViewerConfig(host.Storage);
            seedViewerSettingsFromContract();
            selectSkin();

            screenStack = new OsuScreenStack
            {
                RelativeSizeAxes = Axes.Both,
            };
            Add(screenStack);

            loadReplayScreen();
            NativeViewerLog.Write("Replay player screen added");
        }
        catch (Exception ex)
        {
            NativeViewerLog.Error(ex, "Replay viewer failed during LoadComplete");
            showLoadFailure(ex);
        }
    }

    private KumoriViewerConfig? viewerConfig;

    private void loadReplayScreen()
    {
        if (screenStack == null || ruleset == null || workingBeatmap == null || viewerConfig == null)
            return;

        ViewerContract sessionContract = contract with { Comparison = activeComparison };
        IReadOnlyList<ComparisonContract> comparisonOptions = importedComparison == null
            ? contract.ComparisonOptions
            : [importedComparison, .. contract.ComparisonOptions];
        Score score = ReplayScoreFactory.Create(
            contract,
            ruleset,
            workingBeatmap,
            viewerConfig.GetBindable<bool>(KumoriViewerSetting.DisableHidden).Value);
        (double firstHitTime, double lastHitTime) = workingBeatmap.Beatmap.CalculatePlayableBounds();
        double? analysisCoverageEnd = contract.ResolveAnalysisCoverageEnd(lastHitTime);
        double? playbackEndTime = contract.ResolveReplayPlaybackEnd(lastHitTime);
        SelectedMods.Value = score.ScoreInfo.Mods;
        var player = new KumoriReplayPlayer(score)
        {
            Anchor = Anchor.Centre,
            Origin = Anchor.Centre,
            ViewerConfig = viewerConfig,
            RequestReload = reloadReplayScreen,
            RequestWindowClose = () => gameHost?.Exit(),
            PlaybackEndTime = playbackEndTime,
            PlaybackRestartTime = firstHitTime,
            RecordedAccuracyOverride = usesAuthoritativeStableJudgements() ? contract.Attempt.Accuracy : null,
            Comparison = activeComparison,
            PrimaryAttempt = contract.Attempt,
            PrimaryHits = contract.FinalHits,
        };
        currentPlayer = player;

        // The seek bar reaches into the player through guarded delegates; the
        // player attaches it at positive depth so it renders underneath the
        // gameplay/HUD layers. Marker visibility binds to persisted settings
        // that the in-player "Kumori" settings group also edits.
        if (playbackEndTime is { } playbackEnd)
            lastHitTime = Math.Clamp(playbackEnd, firstHitTime + 1, lastHitTime);
        var seekBar = new KumoriSeekBar(
            firstHitTime,
            lastHitTime,
            () => player.IsLoaded ? player.GameplayTime : firstHitTime,
            time =>
            {
                if (player.IsLoaded)
                    player.Seek(time);
            });
        var timelinePopup = new AdvancedAnalyzerTimelinePopup();
        Add(timelinePopup);
        seekBar.SetAnalysisPopup(timelinePopup.Show, timelinePopup.HideCard);

        bindMarkerToggle(seekBar.ShowMisses, KumoriViewerSetting.ShowMissMarkers);
        bindMarkerToggle(seekBar.ShowMehs, KumoriViewerSetting.ShowMehMarkers);
        bindMarkerToggle(seekBar.ShowOks, KumoriViewerSetting.ShowOkMarkers);
        bindMarkerToggle(seekBar.ShowSliderBreaks, KumoriViewerSetting.ShowSliderBreakMarkers);

        seekBar.SetFinalHits(contract.FinalHits);
        seekBar.SetActualAccuracy(contract.Attempt.Accuracy);
        seekBar.SetCaptureEnd(playbackEndTime);
        seekBar.AddMarkers(KumoriTimelineMarkers.FromContract(contract.JudgementEvents));
        Logger.Log("Kumori: prefilled seek bar from captured judgement events; runtime replay judgements will merge in as playback runs.");

        player.SeekBar = seekBar;

        bool authoritativeStableJudgements = usesAuthoritativeStableJudgements();
        OsuReplayFrame[] analysisFrames = score.Replay.Frames.OfType<OsuReplayFrame>().ToArray();
        MissAnalysisModel capturedModel = MissAnalysisBuilder.Build(contract, analysis, analysisFrames);
        MissAnalysisModel initialModel = capturedModel;
        if (preparedAnalysis != null && !authoritativeStableJudgements)
        {
            MissAnalysisModel simulatedModel = MissAnalysisBuilder.BuildFromPrepared(
                analysis,
                analysisFrames,
                preparedAnalysis.Judgements,
                preparedAnalysis.Frames,
                analysisCoverageEnd);
            initialModel = MissAnalysisBuilder.MergeAuthoritative(
                capturedModel,
                simulatedModel,
                MissAnalysisBuilder.AuthoritativeCoreCounts(contract));
        }
        if (preparedAnalysis != null && !authoritativeStableJudgements)
        {
            seekBar.SetMarkers(initialModel.Markers);
            int restored = initialModel.Entries.Count(entry => entry.Source == AnalysisDataSource.Inferred);
            Logger.Log(restored == 0
                ? $"Kumori: loaded {initialModel.Entries.Count} exact judgements from prepared replay analysis."
                : $"Kumori: prepared replay analysis omitted {restored} captured judgements; exact lazer placements were retained and missing events were restored.",
                level: restored == 0 ? LogLevel.Verbose : LogLevel.Important);
        }
        advancedAnalyzerViewModel = new AdvancedAnalyzerViewModel(initialModel, viewerConfig, sessionContract);
        var analyzerRuntime = new AdvancedAnalyzerRuntime(() => currentPlayer);
        advancedAnalyzerOverlay = new AdvancedAnalyzerOverlay(advancedAnalyzerViewModel, analyzerRuntime);
        Add(advancedAnalyzerOverlay);
        comparisonOverlay = new KumoriComparisonOverlay(
            viewerConfig,
            comparisonOptions,
            activeComparison?.AttemptId,
            () => currentPlayer?.EnterComparisonMode(),
            () => currentPlayer?.ExitComparisonMode(),
            selectComparison,
            chooseOsrComparison,
            comparisonImportStatus,
            stopComparison,
            () => currentPlayer);
        Add(comparisonOverlay);
        seekBar.BindAnalyzer(advancedAnalyzerViewModel, entry =>
        {
            advancedAnalyzerOverlay.Open();
            advancedAnalyzerViewModel.Select(entry);
        });
        player.OpenMissAnalyzer = advancedAnalyzerOverlay.Open;
        player.OpenComparisonMenu = comparisonOverlay.Open;
        player.ComparisonSessionReady = () =>
        {
            if (activeComparison is not null)
                comparisonOverlay.ActivateCollapsed();
        };
        if (preparedAnalysis == null && !authoritativeStableJudgements)
            player.AnalysisJudgementsReady = snapshots => Schedule(() =>
        {
            if (!ReferenceEquals(currentPlayer, player))
            {
                return;
            }

            MissAnalysisModel simulatedModel = MissAnalysisBuilder.BuildFromJudgements(
                workingBeatmap.Beatmap.HitObjects,
                analysisFrames,
                snapshots,
                analysisCoverageEnd);
            MissAnalysisModel mergedModel = MissAnalysisBuilder.MergeAuthoritative(
                capturedModel,
                simulatedModel,
                MissAnalysisBuilder.AuthoritativeCoreCounts(contract));
            advancedAnalyzerViewModel.ReplaceModel(mergedModel);
            seekBar.SetMarkers(mergedModel.Markers);
            int restored = mergedModel.Entries.Count(entry => entry.Source == AnalysisDataSource.Inferred);
            Logger.Log(restored == 0
                ? $"Kumori: analyzer and seek bar now use {mergedModel.Entries.Count} exact lazer results."
                : $"Kumori: runtime lazer pass omitted {restored} captured judgements; retained exact placements and restored missing events.",
                level: restored == 0 ? LogLevel.Verbose : LogLevel.Important);
        });

        screenStack.Push(player);
    }

    private bool usesAuthoritativeStableJudgements()
        => contract.Attempt.MovementSource.Equals("stable_memory", StringComparison.OrdinalIgnoreCase)
           || contract.Attempt.MovementSource.Equals("stable_live", StringComparison.OrdinalIgnoreCase)
           || contract.Attempt.MovementSource.Equals("stable_replay", StringComparison.OrdinalIgnoreCase);

    private void reloadReplayScreen()
    {
        Schedule(() =>
        {
            advancedAnalyzerOverlay?.Close();
            var previousViewModel = advancedAnalyzerViewModel;
            advancedAnalyzerOverlay = null;
            advancedAnalyzerViewModel = null;
            comparisonOverlay = null;
            unbindReplayScreenSettings();
            previousViewModel?.Dispose();
            Clear();
            screenStack = new OsuScreenStack
            {
                RelativeSizeAxes = Axes.Both,
            };
            Add(screenStack);
            loadReplayScreen();
            Logger.Log("Kumori: replay player reloaded after a viewer setting changed.");
        });
    }

    private void selectComparison(ComparisonContract? comparison)
    {
        if (activeComparison?.AttemptId == comparison?.AttemptId)
            return;

        if (comparison is not { Ephemeral: true })
        {
            importedComparison = null;
            comparisonImportStatus.Value = string.Empty;
        }
        activeComparison = comparison;
        reloadReplayScreen();
    }

    private void stopComparison()
    {
        if (activeComparison is null)
            return;

        bool wasImported = activeComparison.Ephemeral;
        activeComparison = null;
        if (wasImported)
        {
            importedComparison = null;
            comparisonImportStatus.Value = string.Empty;
        }
        reloadReplayScreen();
    }

    private void chooseOsrComparison()
    {
        if (osrPickerOpen)
            return;

        osrPickerOpen = true;
        comparisonImportStatus.Value = "Opening replay picker...";
        Task.Run(WindowsReplayFilePicker.SelectOsr)
            .ContinueWith(task => Schedule(() =>
            {
                osrPickerOpen = false;

                if (task.IsFaulted)
                {
                    Exception error = task.Exception?.GetBaseException()
                                      ?? new InvalidOperationException("The replay picker could not be opened.");
                    comparisonImportStatus.Value = error.Message;
                    NativeViewerLog.Error(error, "Replay comparison picker failed");
                    return;
                }

                string? path = task.Result;
                if (string.IsNullOrWhiteSpace(path))
                {
                    comparisonImportStatus.Value = string.Empty;
                    return;
                }

                osrSelected(new System.IO.FileInfo(path));
            }), TaskScheduler.Default);
    }

    private void osrSelected(System.IO.FileInfo file)
    {
        comparisonImportStatus.Value = $"Validating {file.Name}...";
        Task.Run(() => OsrComparisonImporter.Import(file.FullName, contract.BeatmapPath, contract.Attempt))
            .ContinueWith(task => Schedule(() =>
            {
                if (task.IsFaulted)
                {
                    Exception error = task.Exception?.GetBaseException()
                                      ?? new InvalidDataException("The replay could not be loaded.");
                    comparisonImportStatus.Value = error.Message;
                    NativeViewerLog.Error(error, $"Rejected temporary comparison replay {file.Name}");
                    return;
                }

                importedComparison = task.Result;
                activeComparison = importedComparison;
                comparisonImportStatus.Value = $"Loaded {file.Name} for this viewer session only.";
                reloadReplayScreen();
            }), TaskScheduler.Default);
    }

    private void showLoadFailure(Exception ex)
    {
        Clear();
        Add(new Container
        {
            RelativeSizeAxes = Axes.Both,
            Child = new SpriteText
            {
                Anchor = Anchor.Centre,
                Origin = Anchor.Centre,
                Width = 760,
                Text = $"Replay Analyzer could not load this play.\n{ex.GetType().Name}: {ex.Message}\n\nLog: {NativeViewerLog.LogPath}",
                Font = FontUsage.Default.With(size: 24),
                Colour = Colour4.White,
            },
        });
    }

    private void configureWindow(FrameworkConfigManager frameworkConfig)
    {
        Rectangle bounds = Window.CurrentDisplayBindable.Value?.UsableBounds
                           ?? Window.PrimaryDisplay?.UsableBounds
                           ?? new Rectangle(0, 0, 1920, 1080);

        Size size = viewerWindowSize(bounds);
        Point position = new(
            bounds.Left + Math.Max(0, (bounds.Width - size.Width) / 2),
            bounds.Top + Math.Max(0, (bounds.Height - size.Height) / 2));

        frameworkConfig.SetValue(FrameworkSetting.WindowMode, WindowMode.Windowed);
        frameworkConfig.SetValue(FrameworkSetting.WindowedSize, size);
        frameworkConfig.SetValue(FrameworkSetting.WindowedPositionX, (double)position.X);
        frameworkConfig.SetValue(FrameworkSetting.WindowedPositionY, (double)position.Y);

        Window.MinSize = new Size(
            Math.Min(960, Math.Max(640, bounds.Width - 80)),
            Math.Min(540, Math.Max(480, bounds.Height - 80)));
    }

    private static Size viewerWindowSize(Rectangle bounds)
    {
        int maxWidth = Math.Min(1600, (int)Math.Round(bounds.Width * 0.72));
        int maxHeight = Math.Min(900, (int)Math.Round(bounds.Height * 0.76));
        int width = Math.Max(960, maxWidth);
        int height = (int)Math.Round(width / (16.0 / 9.0));

        if (height > maxHeight)
        {
            height = Math.Max(540, maxHeight);
            width = (int)Math.Round(height * (16.0 / 9.0));
        }

        width = Math.Min(width, Math.Max(640, bounds.Width - 80));
        height = Math.Min(height, Math.Max(480, bounds.Height - 80));
        return new Size(width, height);
    }

    /// <summary>
    /// Links a seek bar toggle to its persisted setting and writes the ini
    /// out on every change so toggles survive crashes and forced closes.
    /// </summary>
    private void bindMarkerToggle(osu.Framework.Bindables.BindableBool target, KumoriViewerSetting setting)
    {
        var persisted = viewerConfig!.GetBindable<bool>(setting);
        target.Value = persisted.Value;

        Action<ValueChangedEvent<bool>> persistedChanged = value =>
        {
            if (target.Value != value.NewValue)
                target.Value = value.NewValue;

            viewerConfig!.Save();
        };

        Action<ValueChangedEvent<bool>> targetChanged = value =>
        {
            if (persisted.Value != value.NewValue)
                persisted.Value = value.NewValue;

            viewerConfig!.Save();
        };
        persisted.ValueChanged += persistedChanged;
        target.ValueChanged += targetChanged;
        replayScreenUnbindActions.Add(() =>
        {
            persisted.ValueChanged -= persistedChanged;
            target.ValueChanged -= targetChanged;
        });
    }

    private void unbindReplayScreenSettings()
    {
        foreach (var unbind in replayScreenUnbindActions)
            unbind();
        replayScreenUnbindActions.Clear();
    }

    protected override void Dispose(bool isDisposing)
    {
        unbindReplayScreenSettings();
        advancedAnalyzerViewModel?.Dispose();
        advancedAnalyzerViewModel = null;
        base.Dispose(isDisposing);
    }

    private void seedViewerSettingsFromContract()
    {
        if (viewerConfig == null)
            return;

        var seeded = viewerConfig.GetBindable<bool>(KumoriViewerSetting.ContractSettingsSeeded);

        if (!seeded.Value)
        {
            bool contractDisablesHidden = getBoolSetting("osu_replay_disable_hidden");
            viewerConfig.SetValue(KumoriViewerSetting.DisableHidden, contractDisablesHidden);
            viewerConfig.SetValue(KumoriViewerSetting.ContractSettingsSeeded, true);
        }

        string skinPath = getStringSetting("osu_replay_skin_path")?.Trim() ?? string.Empty;
        string storedPath = viewerConfig.GetBindable<string>(KumoriViewerSetting.SkinPath).Value;

        // Empty is meaningful: it explicitly selects the protected built-in
        // Argon Pro skin and must clear a previously imported skin ID.
        if (!string.Equals(storedPath, skinPath, StringComparison.OrdinalIgnoreCase))
        {
            viewerConfig.SetValue(KumoriViewerSetting.SkinPath, skinPath);
            viewerConfig.SetValue(KumoriViewerSetting.SkinId, string.Empty);
        }

        viewerConfig.Save();
    }

    /// <summary>
    /// Selects the skin for this session. If the contract carries an
    /// "osu_replay_skin_path" setting pointing at a .osk archive, it is
    /// imported through SkinManager (lazer's own pipeline; content-hash
    /// deduplication makes repeated launches cheap) and selected. Otherwise
    /// — or on any failure — the built-in Argon Pro skin is used.
    /// </summary>
    private void selectSkin()
    {
        string? skinPath = viewerConfig?.GetBindable<string>(KumoriViewerSetting.SkinPath).Value;
        string? skinId = viewerConfig?.GetBindable<string>(KumoriViewerSetting.SkinId).Value;
        bool hasUsableCustomSource = !string.IsNullOrWhiteSpace(skinPath)
                                     && (File.Exists(skinPath) || Directory.Exists(skinPath));

        if (hasUsableCustomSource && Guid.TryParse(skinId, out Guid parsedSkinId))
        {
            var existing = SkinManager.GetAllUsableSkins().FirstOrDefault(s => s.ID == parsedSkinId);

            if (existing != null)
            {
                SkinManager.CurrentSkinInfo.Value = existing;
                Logger.Log($"Kumori: restored skin {existing} ({parsedSkinId}) from viewer settings.");
                return;
            }

            Logger.Log($"Kumori: saved skin {parsedSkinId} was not found; re-importing its source.", level: LogLevel.Important);
        }

        if (hasUsableCustomSource)
        {
            try
            {
                string importPath = PrepareSkinImportPath(skinPath!);

                try
                {
                    var imported = SkinManager.Import(new ImportTask(importPath)).GetResultSafely();

                    if (imported != null)
                    {
                        SkinManager.CurrentSkinInfo.Value = imported;
                        viewerConfig?.SetValue(KumoriViewerSetting.SkinPath, skinPath!);
                        viewerConfig?.SetValue(KumoriViewerSetting.SkinId, imported.ID.ToString());
                        viewerConfig?.Save();
                        Logger.Log($"Kumori: imported and remembered skin {imported} ({imported.ID}).");
                        return;
                    }

                    Logger.Log($"Custom skin import returned nothing for \"{skinPath}\"; falling back to Argon Pro.", level: LogLevel.Important);
                }
                finally
                {
                    try
                    {
                        if (File.Exists(importPath))
                            File.Delete(importPath);
                    }
                    catch (Exception e)
                    {
                        Logger.Error(e, $"Kumori: could not remove temporary skin archive \"{importPath}\".");
                    }
                }
            }
            catch (Exception e)
            {
                Logger.Error(e, $"Failed to import custom skin \"{skinPath}\"; falling back to Argon Pro.");
            }
        }
        else if (!string.IsNullOrWhiteSpace(skinPath))
            Logger.Log($"Custom skin \"{skinPath}\" does not exist; falling back to Argon Pro.", level: LogLevel.Important);

        // Select the protected built-in skin through SkinManager. This is the
        // same path used by lazer itself and avoids copied colours/assets.
        SkinManager.CurrentSkinInfo.Value = SkinManager.GetAllUsableSkins()
                                                       .Single(s => s.ID == ArgonProSkin.CreateInfo().ID);
        viewerConfig?.SetValue(KumoriViewerSetting.SkinPath, string.Empty);
        viewerConfig?.SetValue(KumoriViewerSetting.SkinId, string.Empty);
        viewerConfig?.Save();
    }

    /// <summary>
    /// Whether the contract carries a truthy boolean for <paramref name="key"/>.
    /// </summary>
    private bool getBoolSetting(string key)
        => contract.Settings.TryGetValue(key, out JsonElement element)
           && element.ValueKind == JsonValueKind.True;

    private string? getStringSetting(string key)
        => contract.Settings.TryGetValue(key, out JsonElement element)
           && element.ValueKind == JsonValueKind.String
            ? element.GetString()
            : null;

    internal static string PrepareSkinImportPath(string skinPath)
    {
        string archivePath = Path.Combine(Path.GetTempPath(), $"kumori-skin-{Guid.NewGuid():N}.osk");
        if (File.Exists(skinPath))
        {
            // SkinManager's importer may consume its input archive. Always give
            // it a disposable copy so Kumori's library remains persistent.
            File.Copy(skinPath, archivePath);
            return archivePath;
        }

        ZipFile.CreateFromDirectory(skinPath, archivePath);
        return archivePath;
    }

}

internal static class ReplayScoreFactory
{
    public static Score Create(ViewerContract contract, OsuRuleset ruleset, WorkingBeatmap workingBeatmap, bool disableHidden)
    {
        var replay = LazerReplayAdapter.CreateReplay(contract);
        if (LazerReplayAdapter.DecodedScore is Score decoded)
        {
            Mod[] decodedPlaybackMods = filterMods(
                LazerReplayAdapter.ResolveMods(contract.Attempt, decoded.ScoreInfo.Mods, workingBeatmap.Beatmap),
                disableHidden);
            return new Score
            {
                Replay = decoded.Replay,
                ScoreInfo = decoded.ScoreInfo.DeepClone()
            }.WithFilteredMods(decodedPlaybackMods);
        }

        (double firstHitTime, double lastHitTime) = workingBeatmap.Beatmap.CalculatePlayableBounds();
        LazerReplayAdapter.FitCapturedReplay(
            replay,
            firstHitTime,
            lastHitTime,
            contract.Attempt.ClockRate,
            contract.Attempt.MovementSource);

        Mod[] mods = filterMods(
            LazerReplayAdapter.ResolveMods(contract.Attempt, beatmap: workingBeatmap.Beatmap),
            disableHidden);
        return new Score
        {
            Replay = replay,
            ScoreInfo = new ScoreInfo
            {
                Ruleset = ruleset.RulesetInfo,
                BeatmapInfo = workingBeatmap.BeatmapInfo,
                User = new APIUser
                {
                    Username = string.IsNullOrWhiteSpace(contract.Attempt.PlayerName)
                        ? "Kumori capture"
                        : contract.Attempt.PlayerName,
                },
                Date = DateTimeOffset.Now,
                Mods = mods,
            },
        };
    }

    private static Mod[] filterMods(IEnumerable<Mod> mods, bool disableHidden)
        => disableHidden ? mods.Where(m => m is not OsuModHidden).ToArray() : mods.ToArray();
}

internal static class KumoriScoreExtensions
{
    public static Score WithFilteredMods(this Score score, Mod[] mods)
    {
        score.ScoreInfo.Mods = mods;
        return score;
    }
}

internal sealed class KumoriWorkingBeatmap : WorkingBeatmap
{
    private readonly IBeatmap beatmap;
    private readonly NativeStorage? mediaStorage;
    private readonly IResourceStore<byte[]> resources;
    private readonly ITrackStore trackStore;
    private readonly LargeTextureStore textureStore;

    public KumoriWorkingBeatmap(string beatmapPath, string? mediaDirectory, IReadOnlyDictionary<string, string>? mediaPaths, AudioManager audio, GameHost host)
        : this(decode(beatmapPath), audio, host, resolveMediaDirectory(beatmapPath, mediaDirectory), mediaPaths)
    {
    }

    private KumoriWorkingBeatmap(
        IBeatmap beatmap, AudioManager audio, GameHost host, string mediaDirectory, IReadOnlyDictionary<string, string>? mediaPaths)
        : base(beatmap.BeatmapInfo, audio)
    {
        this.beatmap = beatmap;
        mediaStorage = mediaPaths is { Count: > 0 } ? null : new NativeStorage(mediaDirectory);
        resources = mediaPaths is { Count: > 0 }
            ? new MappedResourceStore(mediaPaths)
            : new StorageBackedResourceStore(mediaStorage);
        trackStore = audio.GetTrackStore(resources);
        textureStore = new LargeTextureStore(
            host.Renderer, host.CreateTextureLoaderStore(resources));
    }

    public override bool BeatmapLoaded => true;
    protected override IBeatmap GetBeatmap() => beatmap;
    public override Texture? GetBackground()
        => string.IsNullOrWhiteSpace(Metadata.BackgroundFile)
            ? null
            : textureStore.Get(Metadata.BackgroundFile);
    protected override Track? GetBeatmapTrack()
        => string.IsNullOrWhiteSpace(Metadata.AudioFile) || !resourceExists(Metadata.AudioFile)
            ? null
            : trackStore.Get(Metadata.AudioFile);
    protected override ISkin? GetSkin() => null;
    public override Stream? GetStream(string storagePath) => resources.GetStream(storagePath);

    private bool resourceExists(string name) => resources.GetStream(name) is Stream stream && dispose(stream);
    private static bool dispose(Stream stream) { stream.Dispose(); return true; }

    private static Beatmap decode(string path)
    {
        using var stream = File.OpenRead(path);
        using var reader = new LineBufferedReader(stream);
        return Decoder.GetDecoder<Beatmap>(reader).Decode(reader);
    }

    private static string resolveMediaDirectory(string beatmapPath, string? mediaDirectory)
        => !string.IsNullOrWhiteSpace(mediaDirectory) && Directory.Exists(mediaDirectory)
            ? mediaDirectory
            : Path.GetDirectoryName(beatmapPath)!;
}

internal sealed class MappedResourceStore : IResourceStore<byte[]>
{
    private readonly IReadOnlyDictionary<string, string> paths;

    public MappedResourceStore(IReadOnlyDictionary<string, string> paths) => this.paths = paths;

    public byte[] Get(string name) => resolve(name) is { } path ? File.ReadAllBytes(path) : null!;
    public Task<byte[]> GetAsync(string name, CancellationToken cancellationToken = default) => Task.FromResult(Get(name));
    public Stream? GetStream(string name) => resolve(name) is { } path ? File.OpenRead(path) : null;
    public Task<Stream?> GetStreamAsync(string name, CancellationToken cancellationToken = default) => Task.FromResult(GetStream(name));
    public IEnumerable<string> GetAvailableResources() => paths.Keys;
    public void Dispose() { }

    private string? resolve(string name)
    {
        if (paths.TryGetValue(name, out var exact) && File.Exists(exact))
            return exact;

        var safeName = Path.GetFileName(name.Replace('\\', '/'));
        return paths.TryGetValue(safeName, out var flattened) && File.Exists(flattened) ? flattened : null;
    }
}
