using System.Text.Json;
using System.Drawing;
using System.IO.Compression;
using osu.Framework.Allocation;
using osu.Framework.Audio;
using osu.Framework.Audio.Track;
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
    private GameHost? gameHost;

    internal ReplayViewerGame(ViewerContract contract, BeatmapAnalysis analysis, PreparedReplayAnalysis? preparedAnalysis = null)
    {
        this.contract = contract;
        this.analysis = analysis;
        this.preparedAnalysis = preparedAnalysis;
    }

    public override void SetHost(GameHost host)
    {
        base.SetHost(host);

        if (host.Window != null)
            host.Window.CursorState |= CursorState.Hidden;
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
        workingBeatmap = new KumoriWorkingBeatmap(contract.BeatmapPath, contract.MediaDirectory, audio, host);
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

        Score score = ReplayScoreFactory.Create(
            contract,
            ruleset,
            workingBeatmap,
            viewerConfig.GetBindable<bool>(KumoriViewerSetting.DisableHidden).Value);
        SelectedMods.Value = score.ScoreInfo.Mods;
        var player = new KumoriReplayPlayer(score)
        {
            Anchor = Anchor.Centre,
            Origin = Anchor.Centre,
            ViewerConfig = viewerConfig,
            RequestReload = reloadReplayScreen,
            RequestWindowClose = () => gameHost?.Exit(),
        };
        currentPlayer = player;

        // The seek bar reaches into the player through guarded delegates; the
        // player attaches it at positive depth so it renders underneath the
        // gameplay/HUD layers. Marker visibility binds to persisted settings
        // that the in-player "Kumori" settings group also edits.
        (double firstHitTime, double lastHitTime) = workingBeatmap.Beatmap.CalculatePlayableBounds();
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
        seekBar.AddMarkers(KumoriTimelineMarkers.FromContract(contract.JudgementEvents));
        Logger.Log("Kumori: prefilled seek bar from captured judgement events; runtime replay judgements will merge in as playback runs.");

        player.SeekBar = seekBar;

        MissAnalysisModel initialModel = preparedAnalysis != null
            ? MissAnalysisBuilder.BuildFromPrepared(
                analysis,
                score.Replay.Frames.OfType<OsuReplayFrame>(),
                preparedAnalysis.Judgements,
                preparedAnalysis.Frames)
            : MissAnalysisBuilder.Build(
                contract,
                analysis,
                score.Replay.Frames.OfType<OsuReplayFrame>());
        if (preparedAnalysis != null)
        {
            seekBar.SetMarkers(initialModel.Markers);
            Logger.Log($"Kumori: loaded {initialModel.Entries.Count} exact judgements from prepared replay analysis.");
        }
        advancedAnalyzerViewModel = new AdvancedAnalyzerViewModel(initialModel, viewerConfig);
        var analyzerRuntime = new AdvancedAnalyzerRuntime(() => currentPlayer);
        advancedAnalyzerOverlay = new AdvancedAnalyzerOverlay(advancedAnalyzerViewModel, analyzerRuntime);
        Add(advancedAnalyzerOverlay);
        seekBar.BindAnalyzer(advancedAnalyzerViewModel, entry =>
        {
            advancedAnalyzerOverlay.Open();
            advancedAnalyzerViewModel.Select(entry);
        });
        player.OpenMissAnalyzer = advancedAnalyzerOverlay.Open;
        if (preparedAnalysis == null)
            player.AnalysisJudgementsReady = snapshots => Schedule(() =>
        {
            int expectedEvents = contract.JudgementEvents
                .Where(e => KumoriTimelineMarkers.KindFromContract(e.Kind) != null)
                .Sum(e => Math.Max(1, e.Delta));
            int minimumCompletePass = Math.Max(1, (int)Math.Ceiling(expectedEvents * 0.8));

            if (snapshots.Count < minimumCompletePass || !ReferenceEquals(currentPlayer, player))
            {
                Logger.Log($"Kumori: lazer analysis pass was incomplete ({snapshots.Count}/{expectedEvents}); using captured events with playable beatmap geometry.", level: LogLevel.Important);
                return;
            }

            MissAnalysisModel exactModel = MissAnalysisBuilder.BuildFromJudgements(
                workingBeatmap.Beatmap.HitObjects,
                score.Replay.Frames.OfType<OsuReplayFrame>(),
                snapshots);
            advancedAnalyzerViewModel.ReplaceModel(exactModel);
            seekBar.SetMarkers(exactModel.Markers);
            Logger.Log($"Kumori: analyzer and seek bar now use {exactModel.Entries.Count} exact lazer results.");
        });

        screenStack.Push(player);
    }

    private void reloadReplayScreen()
    {
        Schedule(() =>
        {
            advancedAnalyzerOverlay?.Close();
            advancedAnalyzerOverlay = null;
            advancedAnalyzerViewModel = null;
            Clear();
            screenStack = new OsuScreenStack
            {
                RelativeSizeAxes = Axes.Both,
            };
            Add(screenStack);
            loadReplayScreen();
            Logger.Log("Kumori: replay player reloaded after Hidden mod setting changed.");
        });
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

        persisted.ValueChanged += value =>
        {
            if (target.Value != value.NewValue)
                target.Value = value.NewValue;

            viewerConfig!.Save();
        };

        target.ValueChanged += value =>
        {
            if (persisted.Value != value.NewValue)
                persisted.Value = value.NewValue;

            viewerConfig!.Save();
        };
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

        if (getStringSetting("osu_replay_skin_path") is { Length: > 0 } skinPath)
        {
            string storedPath = viewerConfig.GetBindable<string>(KumoriViewerSetting.SkinPath).Value;

            if (!string.Equals(storedPath, skinPath, StringComparison.OrdinalIgnoreCase))
            {
                viewerConfig.SetValue(KumoriViewerSetting.SkinPath, skinPath);
                viewerConfig.SetValue(KumoriViewerSetting.SkinId, string.Empty);
            }
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

        if (Guid.TryParse(skinId, out Guid parsedSkinId))
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

        if (!string.IsNullOrWhiteSpace(skinPath))
        {
            if (File.Exists(skinPath) || Directory.Exists(skinPath))
            {
                try
                {
                    string importPath = prepareSkinImportPath(skinPath);

                    try
                    {
                        var imported = SkinManager.Import(new ImportTask(importPath)).GetResultSafely();

                        if (imported != null)
                        {
                            SkinManager.CurrentSkinInfo.Value = imported;
                            viewerConfig?.SetValue(KumoriViewerSetting.SkinPath, skinPath);
                            viewerConfig?.SetValue(KumoriViewerSetting.SkinId, imported.ID.ToString());
                            viewerConfig?.Save();
                            Logger.Log($"Kumori: imported and remembered skin {imported} ({imported.ID}).");
                            return;
                        }

                        Logger.Log($"Custom skin import returned nothing for \"{skinPath}\"; falling back to Argon Pro.", level: LogLevel.Important);
                    }
                    finally
                    {
                        if (!string.Equals(importPath, skinPath, StringComparison.OrdinalIgnoreCase))
                        {
                            try
                            {
                                File.Delete(importPath);
                            }
                            catch (Exception e)
                            {
                                Logger.Error(e, $"Kumori: could not remove temporary skin archive \"{importPath}\".");
                            }
                        }
                    }
                }
                catch (Exception e)
                {
                    Logger.Error(e, $"Failed to import custom skin \"{skinPath}\"; falling back to Argon Pro.");
                }
            }
            else
                Logger.Log($"Custom skin \"{skinPath}\" does not exist; falling back to Argon Pro.", level: LogLevel.Important);
        }

        // Select the protected built-in skin through SkinManager. This is the
        // same path used by lazer itself and avoids copied colours/assets.
        SkinManager.CurrentSkinInfo.Value = SkinManager.GetAllUsableSkins()
                                                       .Single(s => s.ID == ArgonProSkin.CreateInfo().ID);
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

    private static string prepareSkinImportPath(string skinPath)
    {
        if (File.Exists(skinPath))
            return skinPath;

        string archivePath = Path.Combine(Path.GetTempPath(), $"kumori-skin-{Guid.NewGuid():N}.osk");
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
            return new Score
            {
                Replay = decoded.Replay,
                ScoreInfo = decoded.ScoreInfo.DeepClone()
            }.WithFilteredMods(filterMods(decoded.ScoreInfo.Mods, disableHidden));
        }

        (double firstHitTime, double lastHitTime) = workingBeatmap.Beatmap.CalculatePlayableBounds();
        LazerReplayAdapter.FitCapturedReplay(replay, firstHitTime, lastHitTime, contract.Attempt.ClockRate);

        Mod[] mods = filterMods(LazerReplayAdapter.CreateCapturedMods(contract.Attempt), disableHidden);
        return new Score
        {
            Replay = replay,
            ScoreInfo = new ScoreInfo
            {
                Ruleset = ruleset.RulesetInfo,
                BeatmapInfo = workingBeatmap.BeatmapInfo,
                User = new APIUser { Username = "Kumori capture" },
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
    private readonly NativeStorage mediaStorage;
    private readonly ITrackStore trackStore;
    private readonly LargeTextureStore textureStore;

    public KumoriWorkingBeatmap(string beatmapPath, string? mediaDirectory, AudioManager audio, GameHost host)
        : this(decode(beatmapPath), audio, host, resolveMediaDirectory(beatmapPath, mediaDirectory))
    {
    }

    private KumoriWorkingBeatmap(
        IBeatmap beatmap, AudioManager audio, GameHost host, string mediaDirectory)
        : base(beatmap.BeatmapInfo, audio)
    {
        this.beatmap = beatmap;
        mediaStorage = new NativeStorage(mediaDirectory);
        var resources = new StorageBackedResourceStore(mediaStorage);
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
        => string.IsNullOrWhiteSpace(Metadata.AudioFile) || !mediaStorage.Exists(Metadata.AudioFile)
            ? null
            : trackStore.Get(Metadata.AudioFile);
    protected override ISkin? GetSkin() => null;
    public override Stream? GetStream(string storagePath) => mediaStorage.GetStream(storagePath);

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
