using System.IO.Compression;
using System.Drawing;
using System.Collections.Concurrent;
using System.Text.Json;
using Kumori.Core;
using Kumori.Skins;
using Kumori.Tracking;
using osu.Framework.Allocation;
using osu.Framework.Audio;
using osu.Framework.Audio.Track;
using osu.Framework.Configuration;
using osu.Framework.Extensions;
using osu.Framework.Graphics;
using osu.Framework.Graphics.Containers;
using osu.Framework.Graphics.Cursor;
using osu.Framework.Graphics.Shapes;
using osu.Framework.Graphics.Sprites;
using osu.Framework.Graphics.Textures;
using osu.Framework.Input;
using osu.Framework.Input.Events;
using osu.Framework.IO.Stores;
using osu.Framework.Localisation;
using osu.Framework.Logging;
using osu.Framework.Platform;
using osu.Framework.Screens;
using osu.Framework.Testing;
using osu.Framework.Timing;
using osu.Game;
using osu.Game.Audio;
using osu.Game.Beatmaps;
using osu.Game.Beatmaps.Formats;
using osu.Game.Configuration;
using osu.Game.Database;
using osu.Game.Graphics.Containers;
using osu.Game.Graphics.Cursor;
using osu.Game.Graphics.UserInterface;
using osu.Game.IO;
using osu.Game.Rulesets;
using osu.Game.Rulesets.Mods;
using osu.Game.Rulesets.Objects;
using osu.Game.Rulesets.Objects.Drawables;
using osu.Game.Rulesets.Scoring;
using osu.Game.Rulesets.UI;
using osu.Game.Rulesets.Osu;
using osu.Game.Rulesets.Osu.Objects.Drawables;
using osu.Game.Rulesets.Osu.Objects.Drawables.Connections;
using osu.Game.Rulesets.Osu.Skinning.Legacy;
using osu.Game.Rulesets.Osu.UI;
using osu.Game.Rulesets.Osu.UI.Cursor;
using osu.Game.Scoring;
using osu.Game.Screens;
using osu.Game.Screens.Play;
using osu.Game.Screens.Play.HUD;
using osu.Game.Screens.Play.Leaderboards;
using osu.Game.Skinning;
using osu.Game.Users;
using osuTK;
using osuTK.Graphics;

namespace Kumori.SkinStudio;

public partial class KumoriSkinStudioGame : OsuGameBase
{
    private const float left_width = 250;
    private const float right_width = 320;
    private const float top_height = 78;
    private const float bottom_height = 38;

    private readonly SkinStudioLaunchContract contract;
    private readonly string beatmapPath;
    private readonly bool embedded;
    private readonly string? embeddedSession;
    private readonly bool rendererOnly;
    private readonly string? rendererPipeName;
    private readonly long? rendererInitialRevision;
    private readonly string? acceptanceOutputPath;
    private readonly string? commandAcceptanceOutputPath;
    private readonly string? publishAcceptanceOutputPath;
    private readonly string extrasRoot;
    private readonly SkinDraftWorkspaceService drafts;
    private readonly SkinDraftAssetService assets;
    private readonly SkinDraftBackupService backups;
    private readonly SkinStudioPreferencesService preferenceStore;
    private readonly StudioNativeSkinCache nativeSkinCache;
    private SkinStudioPreferences studioPreferences;
    private SkinDraftManifest? draft;
    private OsuScreenStack? screenStack;
    private StudioScenePlayer? player;
    private StudioSkinCursorContainer? rendererInteractiveCursor;
    private StudioRendererInteractionLayer? rendererInteractionLayer;
    private StudioExtrasInspectionOverlay? rendererSemanticOverlay;
    private Container? workbenchContainer;
    private Container? gameplayContainer;
    private StudioSkinWorkbench? workbench;
    private StudioElementNavigator? elementNavigator;
    private StudioActionButton? workbenchButton;
    private StudioActionButton? mockupButton;
    private StudioActionButton? gameplayButton;
    private StudioActionButton? deleteDraftButton;
    private StudioActionButton? replaceAssetButton;
    private StudioActionButton? deleteAssetButton;
    private StudioActionButton? resetAssetButton;
    private StudioActionButton? copyAssetButton;
    private StudioActionButton? pasteAssetButton;
    private StudioActionButton? transformAssetButton;
    private StudioActionButton? normalizeAudioButton;
    private StudioActionButton? audioTransportButton;
    private StudioActionButton? deleteAnimationFrameButton;
    private StudioActionButton? insertAnimationFrameButton;
    private StudioActionButton? moveAnimationFrameButton;
    private StudioActionButton? exportAssetButton;
    private StudioActionButton? externalEditAssetButton;
    private StudioActionButton? applyExternalAssetButton;
    private StudioActionButton? addSelectedToExtrasButton;
    private StudioActionButton? addCategoryToExtrasButton;
    private StudioActionButton? applyExternalSkinIniButton;
    private StudioActionButton? undoButton;
    private StudioActionButton? redoButton;
    private StudioActionButton? discardSelectedButton;
    private StudioActionButton? discardAllButton;
    private StudioActionButton? resetCategoryButton;
    private StudioActionButton? reviewChangesButton;
    private StudioActionButton? restoreDeletedDraftButton;
    private StudioActionButton? restoreDraftBackupButton;
    private StudioActionButton? recoverInterruptedDraftButton;
    private StudioActionButton? automaticBackupButton;
    private StudioIdentityOverlay? identityOverlay;
    private StudioSkinIniOverlay? skinIniOverlay;
    private StudioRawSkinIniOverlay? rawSkinIniOverlay;
    private StudioImageTransformOverlay? imageTransformOverlay;
    private StudioAudioTransportOverlay? audioTransportOverlay;
    private StudioExtrasOverlay? extrasOverlay;
    private StudioExtrasAudioBrowserOverlay? extrasAudioBrowserOverlay;
    private StudioExtrasCatalogAcceptanceController?
        extrasCatalogAcceptanceController;
    private StudioExtrasExtractionOverlay? extrasExtractionOverlay;
    private StudioExtrasCompositionOverlay? extrasCompositionOverlay;
    private StudioDraftBrowserOverlay? draftBrowserOverlay;
    private StudioChangeReviewOverlay? changeReviewOverlay;
    private StudioTextPromptOverlay? pathPromptOverlay;
    private StudioInstalledSkinBrowserOverlay? installedSkinBrowserOverlay;
    private StudioOpeningSkinOverlay? openingSkinOverlay;
    private StudioToolsOverlay? advancedToolsOverlay;
    private OsuTextBox? quickColour;
    private StudioActionButton? quickColorizeButton;
    private StudioActionButton? quickTintButton;
    private SpriteText? previewBadgeText;
    private SpriteText? statusText;
    private SpriteText? changesText;
    private SpriteText? skinText;
    private SpriteText? selectedAssetText;
    private GameHost? gameHost;
    private ISystemFileSelector? assetSelector;
    private ISystemFileSelector? skinSelector;
    private ISystemFileSelector? beatmapSelector;
    private ISystemFileSelector? extrasPackageSelector;
    private ISystemFileSelector? extrasSkinSelector;
    private string? externalSkinIniPath;
    private string? externalAssetPath;
    private string? externalAssetFilename;
    private string? externalAssetExpectedHash;
    private string? externalAssetOpenedCopyHash;
    private string? externalAssetRejectedHash;
    private FileSystemWatcher? externalAssetWatcher;
    private bool externalAssetChanged;
    private AudioManager? audioManager;
    private OsuRuleset? osuRuleset;
    private string? pendingAssetTarget;
    private string? selectedAssetComponent;
    private SkinDraftAssetFamilySnapshot? assetClipboard;
    private LazerSkinCatalog? installedSkinCatalog;
    private string? lastPublishArchivePath;
    private VerifiedLazerCatalogBackup? lastPublishBackup;
    private LazerSkinPublishVerificationResult? lastPublishVerification;
    private Exception? lastPublishFailure;
    private bool publishFinished;
    private EmbeddedWindowActivationMonitor? embeddedWindowActivation;
    private CancellationTokenSource? skinLoadCancellation;
    private StudioSkinFileSnapshot effectiveSkinFiles =
        StudioSkinFileSnapshot.Empty;
    private Task<StudioSkinFileSnapshot>? effectiveSkinFilesTask;
    private Guid? effectiveSkinFilesDraftId;
    private long effectiveSkinFilesRevision = -1;
    private long skinLoadGeneration;
    private bool gameplayMode;
    private bool mockupMode;
    private double deleteDraftArmedUntil;
    private ManualFramedClock? acceptanceWorkbenchClock;
    private SkinStudioRendererPipeServer? rendererPipeServer;
    private TaskCompletionSource<(Guid DraftId, long Revision)>?
        rendererLoadCompletion;
    private SkinnableSound? rendererAuditionSound;
    private SkinStudioSemanticPreviewDescriptor? rendererPreviewTarget;
    private SkinStudioAssetProvenance rendererAssetProvenance =
        SkinStudioAssetProvenance.Unknown;
    private string[] rendererAudioSequence = [];
    private int rendererAudioSequenceIndex;
    private double rendererNextAudioTime;
    private double rendererAudioInterval = 500;
    private bool rendererAutomaticAudio;
    private bool rendererLayeredHitSounds = true;
    private SkinStudioPreviewScene rendererScene = SkinStudioPreviewScene.Showcase;
    private string? rendererInspectionFamily;
    private readonly HashSet<string> rendererInspectionComponents =
        new(StringComparer.OrdinalIgnoreCase);
    private Colour4 rendererInspectionTint = Colour4.White;
    private readonly Dictionary<string, Colour4> rendererElementTints =
        new(StringComparer.OrdinalIgnoreCase);
    private bool rendererPlaying;
    private bool rendererAutoMotion;
    private bool rendererAudioPlaying;
    private bool rendererIsActive = true;
    private bool rendererMenuCursorVisible = true;
    private float rendererCursorScale = 1;
    private float rendererObjectScale = 1;
    private bool rendererResumeAfterActivation;
    private readonly ConcurrentQueue<RendererColourEditRequest>
        rendererColourEditRequests = new();
    private Guid? rendererLoadedDraftId;
    private long? rendererLoadedRevision;
    private Guid? rendererLoadingDraftId;
    private long? rendererLoadingRevision;
    private int previewComboColourCount = 4;

    public KumoriSkinStudioGame(
        SkinStudioLaunchContract contract,
        string beatmapPath,
        bool embedded = false,
        string? acceptanceOutputPath = null,
        string? commandAcceptanceOutputPath = null,
        string? publishAcceptanceOutputPath = null,
        string? embeddedSession = null,
        bool rendererOnly = false,
        string? rendererPipeName = null,
        long? rendererInitialRevision = null)
    {
        this.contract = contract.Normalize();
        this.beatmapPath = beatmapPath;
        this.embedded = embedded;
        this.embeddedSession = embeddedSession;
        this.rendererOnly = rendererOnly;
        this.rendererPipeName = rendererPipeName;
        this.rendererInitialRevision = rendererInitialRevision;
        if (rendererOnly)
        {
            if (!embedded)
                throw new InvalidDataException("Renderer-only mode must be embedded.");
            SkinStudioRendererLaunchContract.ValidatePipeName(rendererPipeName);
        }
        this.acceptanceOutputPath = string.IsNullOrWhiteSpace(acceptanceOutputPath)
            ? null
            : Path.GetFullPath(acceptanceOutputPath);
        this.commandAcceptanceOutputPath =
            string.IsNullOrWhiteSpace(commandAcceptanceOutputPath)
                ? null
                : Path.GetFullPath(commandAcceptanceOutputPath);
        this.publishAcceptanceOutputPath =
            string.IsNullOrWhiteSpace(publishAcceptanceOutputPath)
                ? null
                : Path.GetFullPath(publishAcceptanceOutputPath);
        extrasRoot = this.commandAcceptanceOutputPath is null
            ? AppPaths.SkinExtrasDir
            : Path.Combine(
                this.contract.WorkspacePath,
                "extras-library");
        if (this.acceptanceOutputPath is not null
            && !SkinStudioWriteBoundary.IsNormalWriteAllowed(
                this.contract.PlayerRoot,
                this.acceptanceOutputPath))
        {
            throw new InvalidDataException(
                "Visual acceptance output cannot overlap the osu!lazer player root.");
        }
        if (this.commandAcceptanceOutputPath is not null
            && !SkinStudioWriteBoundary.IsNormalWriteAllowed(
                this.contract.PlayerRoot,
                this.commandAcceptanceOutputPath))
        {
            throw new InvalidDataException(
                "Command acceptance output cannot overlap the osu!lazer player root.");
        }
        if (this.publishAcceptanceOutputPath is not null
            && !SkinStudioWriteBoundary.IsNormalWriteAllowed(
                this.contract.PlayerRoot,
                this.publishAcceptanceOutputPath))
        {
            throw new InvalidDataException(
                "Publish acceptance output cannot overlap the osu!lazer player root.");
        }
        SkinStudioWriteBoundary.AssertNormalRootsAreIsolated(
            this.contract.PlayerRoot,
            this.contract.WorkspacePath,
            extrasRoot);
        drafts = new SkinDraftWorkspaceService(this.contract.WorkspacePath);
        assets = new SkinDraftAssetService(drafts);
        backups = new SkinDraftBackupService(drafts);
        preferenceStore =
            new SkinStudioPreferencesService(this.contract.WorkspacePath);
        nativeSkinCache =
            new StudioNativeSkinCache(this.contract.WorkspacePath);
        try
        {
            studioPreferences = preferenceStore.Load();
        }
        catch
        {
            studioPreferences = new SkinStudioPreferences();
        }
    }

    public override void SetHost(GameHost host)
    {
        base.SetHost(host);
        gameHost = host;
        if (host.Window is not null)
        {
            host.Window.Title = "Kumori Skin Studio";
            host.Window.CursorState |= CursorState.Hidden;
            if (embedded)
                host.Window.Hide();
        }
    }

    internal void HandleActivation(StudioActivationMessage message)
    {
        Schedule(() =>
        {
            try
            {
                if (!embedded)
                {
                    gameHost?.Window?.Show();
                    gameHost?.Window?.Raise();
                }
                if (string.IsNullOrWhiteSpace(message.ContractPath))
                {
                    updateDraftPresentation("Studio activated.");
                    return;
                }

                var activationContract = SkinStudioLaunchContract.Load(message.ContractPath);
                if (!Path.GetFullPath(activationContract.WorkspacePath).Equals(
                        Path.GetFullPath(contract.WorkspacePath),
                        StringComparison.OrdinalIgnoreCase))
                {
                    updateDraftPresentation("Activation rejected: workspace does not match this Studio instance.");
                    return;
                }

                if (activationContract.DraftId is { } requested
                    && draft?.DraftId != requested)
                {
                    draft = drafts.Load(requested);
                    clearAssetSelection();
                    selectDraftSkin();
                    restartNativePreviewIfOpen();
                    updateDraftPresentation($"Opened draft “{draft.Name}”.");
                    return;
                }

                updateDraftPresentation("Studio activated.");
            }
            catch (Exception ex)
            {
                Logger.Error(ex, "Could not process Skin Studio activation.");
                updateDraftPresentation($"Activation failed: {ex.Message}");
            }
        });
    }

    protected override void LoadComplete()
    {
        try
        {
            base.LoadComplete();
            gameHost = Dependencies.Get<GameHost>();
            if (embedded)
                embeddedWindowActivation =
                    new EmbeddedWindowActivationMonitor();
            if (!rendererOnly)
            {
                assetSelector = gameHost.CreateSystemFileSelector(
                    [".png", ".jpg", ".jpeg", ".wav", ".mp3", ".ogg"]);
                if (assetSelector is not null)
                    assetSelector.Selected += file => Schedule(() => importAsset(file.FullName));
                skinSelector = gameHost.CreateSystemFileSelector([".osk", ".zip"]);
                if (skinSelector is not null)
                    skinSelector.Selected += file => Schedule(() => importSkin(file.FullName));
                beatmapSelector = gameHost.CreateSystemFileSelector([".osu"]);
                if (beatmapSelector is not null)
                    beatmapSelector.Selected += file => Schedule(() => importBeatmap(file.FullName));
                extrasPackageSelector = gameHost.CreateSystemFileSelector([".zip", ".kse"]);
                if (extrasPackageSelector is not null)
                    extrasPackageSelector.Selected += file => Schedule(() => importExtrasPackage(file.FullName));
                extrasSkinSelector = gameHost.CreateSystemFileSelector([".osk", ".zip"]);
                if (extrasSkinSelector is not null)
                    extrasSkinSelector.Selected += file => Schedule(() => extractSkinPackageToExtras(file.FullName));
            }
            audioManager = Dependencies.Get<AudioManager>();
            var frameworkConfig = Dependencies.Get<FrameworkConfigManager>();
            configureWindow(frameworkConfig);
            prepareDraft();
            previewComboColourCount = comboColourCountForDraft(draft);
            if (rendererOnly && draft is { } initialDraft
                && rendererInitialRevision is { } expectedRevision
                && initialDraft.History[initialDraft.HistoryIndex].Revision != expectedRevision)
            {
                throw new InvalidDataException(
                    "The renderer draft changed before startup. Kumori will restart it with the latest revision.");
            }

            osuRuleset = new OsuRuleset();
            var workingBeatmap = new StudioWorkingBeatmap(
                beatmapPath,
                audioManager,
                gameHost,
                previewComboColourCount);
            Beatmap.Value = workingBeatmap;
            Ruleset.Value = osuRuleset.RulesetInfo;
            LocalConfig.SetValue(OsuSetting.BeatmapSkins, false);
            LocalConfig.SetValue(OsuSetting.HUDVisibilityMode, HUDVisibilityMode.Always);
            LocalConfig.SetValue(OsuSetting.ShowStoryboard, false);

            if (rendererOnly)
            {
                if (draft is null)
                    throw new InvalidDataException("Renderer-only mode requires an existing draft.");
                buildRendererSurface();
                rendererLoadCompletion = new TaskCompletionSource<(Guid, long)>(
                    TaskCreationOptions.RunContinuationsAsynchronously);
                selectDraftSkin(announceCompletion: false);
                if (!ensureGameplayPlayer())
                    throw new InvalidOperationException("The native lazer preview could not start.");
                gameplayMode = false;
                mockupMode = true;
                rendererScene = SkinStudioPreviewScene.Showcase;
                seekNativeMockupWhenReady();
                rendererPipeServer = new SkinStudioRendererPipeServer(
                    rendererPipeName!,
                    handleRendererRequestAsync);
                signalEmbeddedReady();
                return;
            }

            selectDraftSkin();
            buildShell();
            SkinManager.CurrentSkin.BindValueChanged(
                _ => Scheduler.AddDelayed(refreshWorkbench, 50),
                true);
            showWorkbench();
            if (shouldPromptForSkin())
            {
                presentOpeningSkinChooser(required: true);
                updateDraftPresentation(
                    "Choose a skin to begin. Kumori will open it as an isolated draft.");
            }
            else
            {
                updateDraftPresentation(
                    "Skin loaded. Choose a category on the left, an element "
                    + "in the middle, and edit it on the right.");
            }
            if (acceptanceOutputPath is not null)
                Scheduler.AddDelayed(startVisualAcceptanceCapture, 750);
            else if (commandAcceptanceOutputPath is not null)
                Scheduler.AddDelayed(startCommandAcceptanceCapture, 750);
            else if (publishAcceptanceOutputPath is not null)
                Scheduler.AddDelayed(startPublishAcceptance, 750);
            signalEmbeddedReady();
        }
        catch (Exception ex)
        {
            Logger.Error(ex, "Kumori Skin Studio failed to load.");
            showFailure(ex);
        }
    }

    private void buildRendererSurface()
    {
        screenStack = new OsuScreenStack { RelativeSizeAxes = Axes.Both };
        gameplayContainer = new Container
        {
            RelativeSizeAxes = Axes.Both,
            Alpha = 0,
            // The renderer is intentionally hidden until its skin and scene
            // are ready, but its subtree must continue loading and updating or
            // the readiness handshake can never complete.
            AlwaysPresent = true,
            Child = screenStack,
        };
        Add(gameplayContainer);
        rendererSemanticOverlay = new StudioExtrasInspectionOverlay();
        Add(rendererSemanticOverlay);

        // Studio owns cursor motion. The cursor itself still comes from
        // lazer's ruleset skin lookup, including trails and particles.
        rendererInteractiveCursor = new StudioSkinCursorContainer
        {
            RelativeSizeAxes = Axes.Both,
            PreviewScale = rendererCursorScale,
        };
        rendererInteractiveCursor.Show();
        Add(new RulesetSkinProvidingContainer(
            osuRuleset ?? throw new InvalidOperationException(
                "The osu! ruleset must be ready before the renderer surface is built."),
            Beatmap.Value.Beatmap,
            Beatmap.Value.Skin)
        {
            RelativeSizeAxes = Axes.Both,
            Depth = float.MinValue,
            Child = rendererInteractiveCursor,
        });
        rendererInteractionLayer = new StudioRendererInteractionLayer(
            screenSpacePosition =>
                player?.TryRequestColourEdit(screenSpacePosition) == true,
            GlobalCursorDisplay.MenuCursor)
        {
            RelativeSizeAxes = Axes.Both,
            Depth = float.MinValue,
        };
        Add(rendererInteractionLayer);
    }

    private async Task<SkinStudioRendererResponse> handleRendererRequestAsync(
        SkinStudioRendererRequest request)
    {
        try
        {
            switch (request.Command)
            {
                case SkinStudioRendererCommandKind.LoadDraft:
                    if (request.DraftId is not { } requestedDraft
                        || request.DraftRevision is not { } requestedRevision)
                    {
                        throw new InvalidDataException(
                            "LoadDraft requires a draft identifier and revision.");
                    }
                    await runOnUpdateThread(() =>
                    {
                        stopRendererAudio();
                        if (!string.IsNullOrWhiteSpace(request.Component))
                        {
                            rendererInspectionFamily = request.Component.Trim();
                            rendererInspectionComponents.Clear();
                            foreach (var component in request.Components ?? [])
                            {
                                if (!string.IsNullOrWhiteSpace(component))
                                    rendererInspectionComponents.Add(component.Trim());
                            }
                            rendererPreviewTarget = SkinStudioSemanticPreviewCatalog.Resolve(
                                rendererInspectionComponents.FirstOrDefault(),
                                rendererInspectionFamily,
                                request.ManiaKeyCount);
                            rendererScene = request.Scene
                                            ?? rendererPreviewTarget.Scene;
                            rendererInspectionTint = Colour4.White;
                            rendererElementTints.Clear();
                            rendererAutoMotion = isAnimatedInspection();
                            rendererPlaying = rendererAutoMotion;
                        }
                        applyRendererCursorScale();
                        applyRendererInspectionTint();
                    }).ConfigureAwait(false);
                    await loadRendererDraftAsync(requestedDraft, requestedRevision)
                        .WaitAsync(TimeSpan.FromSeconds(30)).ConfigureAwait(false);
                    await runOnUpdateThread(() =>
                    {
                        updateRendererAssetProvenance();
                        if (rendererPreviewTarget is not null)
                            startSemanticAudio(rendererPreviewTarget);
                    }).ConfigureAwait(false);
                    break;

                case SkinStudioRendererCommandKind.Seek:
                    await runOnUpdateThread(() =>
                    {
                        stopRendererAudio();
                        rendererPreviewTarget = null;
                        rendererAssetProvenance = SkinStudioAssetProvenance.Unknown;
                        rendererScene = request.Scene ?? SkinStudioPreviewScene.Showcase;
                        rendererInspectionFamily = string.IsNullOrWhiteSpace(request.Component)
                            ? null
                            : request.Component.Trim();
                        rendererInspectionComponents.Clear();
                        foreach (var component in request.Components ?? [])
                        {
                            if (!string.IsNullOrWhiteSpace(component))
                                rendererInspectionComponents.Add(component.Trim());
                        }
                        rendererAutoMotion = rendererInspectionFamily is null
                            ? rendererScene is SkinStudioPreviewScene.Showcase
                                or SkinStudioPreviewScene.Spinner
                            : isAnimatedInspection();
                        rendererPlaying = rendererAutoMotion;
                        applyRendererCursorScale();
                        applyRendererInspectionTint();
                        configureRendererScene();
                    }).ConfigureAwait(false);
                    break;

                case SkinStudioRendererCommandKind.SelectPreviewTarget:
                    var target = SkinStudioSemanticPreviewCatalog.ResolveTarget(
                        request.PreviewTargetId,
                        request.FamilyId,
                        request.Component,
                        request.Ruleset,
                        request.ManiaKeyCount);
                    if (target.IsRaw)
                    {
                        throw new InvalidDataException(
                            $"'{target.ComponentName}' is not a recognised semantic skin element.");
                    }
                    await runOnUpdateThread(() =>
                    {
                        stopRendererAudio();
                        rendererPreviewTarget = target;
                        rendererScene = target.Scene;
                        rendererInspectionFamily = target.FamilyId;
                        rendererInspectionComponents.Clear();
                        rendererInspectionComponents.Add(target.ComponentName);
                        rendererInspectionTint = Colour4.White;
                        rendererElementTints.Clear();
                        rendererAutoMotion = target.Animation is
                            SkinStudioAnimationPolicy.Native or
                            SkinStudioAnimationPolicy.ScriptedLoop;
                        rendererPlaying = rendererAutoMotion || target.IsAudio;
                        updateRendererAssetProvenance();
                        applyRendererCursorScale();
                        applyRendererInspectionTint();
                        configureRendererScene();
                        startSemanticAudio(target);
                    }).ConfigureAwait(false);
                    break;

                case SkinStudioRendererCommandKind.Play:
                    await runOnUpdateThread(() =>
                    {
                        rendererPlaying = true;
                        rendererAutoMotion = rendererInspectionFamily is null
                            ? rendererScene is SkinStudioPreviewScene.Showcase
                                or SkinStudioPreviewScene.Cursor
                                or SkinStudioPreviewScene.Spinner
                            : isAnimatedInspection();
                        configureRendererScene();
                        if (rendererPreviewTarget is not null)
                            startSemanticAudio(rendererPreviewTarget);
                    }).ConfigureAwait(false);
                    break;

                case SkinStudioRendererCommandKind.Pause:
                    await runOnUpdateThread(() =>
                    {
                        rendererPlaying = false;
                        rendererAutoMotion = false;
                        stopRendererAudio();
                        player?.ConfigureScene(
                            rendererScene,
                            false,
                            rendererInspectionFamily,
                            rendererInspectionComponents,
                            rendererPreviewTarget?.ManiaKeyCount);
                        player?.PauseForAcceptance();
                    }).ConfigureAwait(false);
                    break;

                case SkinStudioRendererCommandKind.Restart:
                    await runOnUpdateThread(() =>
                    {
                        stopRendererAudio();
                        player?.Restart();
                        configureRendererScene();
                        if (rendererPreviewTarget is not null)
                            startSemanticAudio(rendererPreviewTarget);
                    }).ConfigureAwait(false);
                    break;

                case SkinStudioRendererCommandKind.AuditionSample:
                    if (string.IsNullOrWhiteSpace(request.Component))
                        throw new InvalidDataException("AuditionSample requires a component name.");
                    await runOnUpdateThread(() => auditionRendererSample(request.Component))
                        .ConfigureAwait(false);
                    break;

                case SkinStudioRendererCommandKind.StopAudio:
                    await runOnUpdateThread(stopRendererAudio).ConfigureAwait(false);
                    break;

                case SkinStudioRendererCommandKind.SetActive:
                    await runOnUpdateThread(() =>
                    {
                        rendererIsActive = request.Active != false;
                        if (rendererIsActive)
                        {
                            gameplayContainer!.AlwaysPresent = true;
                            if (rendererLoadedDraftId is not null)
                                gameplayContainer.Show();
                            if (rendererResumeAfterActivation)
                            {
                                rendererAutoMotion = rendererInspectionFamily is null
                                    ? rendererScene is SkinStudioPreviewScene.Showcase
                                        or SkinStudioPreviewScene.Cursor
                                        or SkinStudioPreviewScene.Spinner
                                    : isAnimatedInspection();
                                rendererPlaying = true;
                                configureRendererScene();
                                if (rendererPreviewTarget is not null)
                                    startSemanticAudio(rendererPreviewTarget);
                            }
                            rendererResumeAfterActivation = false;
                            return;
                        }
                        rendererResumeAfterActivation |= rendererPlaying
                            || rendererAutoMotion;
                        rendererPlaying = false;
                        rendererAutoMotion = false;
                        player?.ConfigureScene(
                            rendererScene,
                            false,
                            rendererInspectionFamily,
                            rendererInspectionComponents,
                            rendererPreviewTarget?.ManiaKeyCount);
                        player?.PauseForAcceptance();
                        stopRendererAudio();
                        if (rendererLoadedDraftId is not null && gameplayContainer is not null)
                        {
                            gameplayContainer.Hide();
                            gameplayContainer.AlwaysPresent = false;
                        }
                    }).ConfigureAwait(false);
                    break;

                case SkinStudioRendererCommandKind.SetAutoMotion:
                    await runOnUpdateThread(() =>
                    {
                        rendererAutoMotion = request.Active == true;
                        rendererPlaying = rendererAutoMotion;
                        configureRendererScene();
                    }).ConfigureAwait(false);
                    break;

                case SkinStudioRendererCommandKind.SetSmoothTrail:
                    await runOnUpdateThread(() =>
                        setRendererSmoothTrail(request.Active == true))
                        .ConfigureAwait(false);
                    break;

                case SkinStudioRendererCommandKind.SetPreviewColour:
                    if (request.ColourTarget is not { } colourTarget
                        || request.ColourRed is not { } colourRed
                        || request.ColourGreen is not { } colourGreen
                        || request.ColourBlue is not { } colourBlue)
                    {
                        throw new InvalidDataException(
                            "SetPreviewColour requires a target and RGB value.");
                    }
                    await runOnUpdateThread(() =>
                    {
                        var colour = new Colour4(
                            colourRed,
                            colourGreen,
                            colourBlue,
                            byte.MaxValue);
                        if (colourTarget == SkinStudioRendererColourTarget.ElementTint)
                        {
                            if (string.IsNullOrWhiteSpace(request.Component))
                                rendererInspectionTint = colour;
                            else
                                rendererElementTints[request.Component.Trim()] = colour;
                            applyRendererInspectionTint();
                            return;
                        }
                        player?.SetPreviewColour(colourTarget, colour);
                    }).ConfigureAwait(false);
                    break;

                case SkinStudioRendererCommandKind.SetPreviewScale:
                    if (request.CursorScale is not { } cursorScale
                        || request.ObjectScale is not { } objectScale
                        || cursorScale is < 0.5 or > 2
                        || objectScale is < 0.6 or > 1.5)
                    {
                        throw new InvalidDataException(
                            "SetPreviewScale requires cursor and object scales within the supported range.");
                    }
                    await runOnUpdateThread(() =>
                    {
                        rendererCursorScale = (float)cursorScale;
                        rendererObjectScale = (float)objectScale;
                        applyRendererCursorScale();
                        player?.SetObjectPreviewScale(rendererObjectScale);
                    }).ConfigureAwait(false);
                    break;

                case SkinStudioRendererCommandKind.SetMenuCursorVisible:
                    await runOnUpdateThread(() =>
                        rendererMenuCursorVisible = request.Active != false)
                        .ConfigureAwait(false);
                    break;

                case SkinStudioRendererCommandKind.PollEvent:
                    if (rendererColourEditRequests.TryDequeue(out var colourEdit))
                        return rendererColourEditResponse(request, colourEdit);
                    break;

                default:
                    throw new InvalidDataException("Unknown renderer command.");
            }

            return rendererResponse(request, true, "Renderer command completed.");
        }
        catch (Exception ex)
        {
            Logger.Error(ex, $"Renderer command {request.Command} failed.");
            return rendererResponse(request, false, ex.Message);
        }
    }

    private void configureRendererScene()
    {
        if (player is null)
            return;
        var advanceGameplay = StudioScenePlayer.AdvancesGameplay(
            rendererScene,
            rendererAutoMotion);
        player.ConfigureScene(
            rendererScene,
            advanceGameplay,
            rendererInspectionFamily,
            rendererInspectionComponents,
            rendererPreviewTarget?.ManiaKeyCount);
        rendererSemanticOverlay?.Configure(
            rendererInspectionFamily,
            rendererInspectionComponents,
            rendererPreviewTarget?.ManiaKeyCount);
        var seekTime = advanceGameplay ? rendererScene switch
        {
            SkinStudioPreviewScene.Sliders => 2_400,
            SkinStudioPreviewScene.Cursor => 4_300,
            SkinStudioPreviewScene.Spinner => 7_500,
            _ => SkinStudioPreviewScenes.TimeMilliseconds(rendererScene),
        } : SkinStudioPreviewScenes.TimeMilliseconds(rendererScene);
        player.SeekAndPauseForAcceptance(
            seekTime);
        if (advanceGameplay)
            player.Play();
    }

    private bool isCursorInspection() =>
        rendererInspectionFamily?.Equals(
            "osu.cursor",
            StringComparison.OrdinalIgnoreCase) == true
        || rendererInspectionFamily?.Equals(
            "osu.star-particles",
            StringComparison.OrdinalIgnoreCase) == true;

    private bool isAnimatedInspection() =>
        isCursorInspection()
        || rendererInspectionFamily?.Equals(
            "osu.slider",
            StringComparison.OrdinalIgnoreCase) == true
        || rendererInspectionFamily?.Equals(
            "osu.spinner",
            StringComparison.OrdinalIgnoreCase) == true;

    private void applyRendererCursorScale()
    {
        if (rendererInteractiveCursor is not null)
        {
            rendererInteractiveCursor.PreviewScale = rendererCursorScale
                * (isCursorInspection() ? 2.5f : 1f);
        }
    }

    private void applyRendererInspectionTint()
    {
        var colour = rendererInspectionFamily is null
            ? Colour4.White
            : rendererInspectionTint;
        if (rendererInteractiveCursor is not null)
            rendererInteractiveCursor.SetLayerTints(
                colour,
                rendererElementTints);
        player?.SetInspectionTints(colour, rendererElementTints);
        rendererSemanticOverlay?.SetElementTints(rendererElementTints);
    }

    private void setRendererSmoothTrail(bool smoothTrail)
    {
        if (rendererInteractiveCursor is null)
            return;
        foreach (var trail in rendererInteractiveCursor
                     .ChildrenOfType<LegacyCursorTrail>())
        {
            trail.SetDisjointTrailForPreview(!smoothTrail);
        }
    }

    private async Task loadRendererDraftAsync(Guid draftId, long revision)
    {
        TaskCompletionSource<(Guid, long)> completion = new(
            TaskCreationOptions.RunContinuationsAsynchronously);
        await runOnUpdateThread(() =>
        {
            var selected = drafts.Load(draftId);
            var actualRevision = selected.History[selected.HistoryIndex].Revision;
            if (actualRevision != revision)
            {
                throw new InvalidDataException(
                    $"Draft {draftId:N} is at revision {actualRevision}, not requested revision {revision}.");
            }
            if (rendererLoadedDraftId == draftId
                && rendererLoadedRevision == revision)
            {
                completion.TrySetResult((draftId, revision));
                return;
            }
            if (rendererLoadingDraftId == draftId
                && rendererLoadingRevision == revision
                && rendererLoadCompletion is { Task.IsCompleted: false } pending)
            {
                completion = pending;
                return;
            }
            rendererLoadCompletion?.TrySetCanceled();
            rendererLoadCompletion = completion;
            draft = selected;
            selectDraftSkin(announceCompletion: false);
        }).ConfigureAwait(false);
        await completion.Task.ConfigureAwait(false);
    }

    private SkinStudioRendererResponse rendererResponse(
        SkinStudioRendererRequest request,
        bool accepted,
        string message) => new()
        {
            RequestId = request.RequestId,
            Accepted = accepted,
            Message = message,
            LoadedDraftId = rendererLoadedDraftId,
            LoadedRevision = rendererLoadedRevision,
            Playing = rendererPlaying,
            Scene = rendererScene,
            Event = accepted
            ? request.Command switch
            {
                SkinStudioRendererCommandKind.LoadDraft => SkinStudioRendererEventKind.RevisionLoaded,
                SkinStudioRendererCommandKind.AuditionSample or SkinStudioRendererCommandKind.StopAudio =>
                    SkinStudioRendererEventKind.AudioState,
                SkinStudioRendererCommandKind.Seek or SkinStudioRendererCommandKind.SelectPreviewTarget
                    or SkinStudioRendererCommandKind.Play
                    or SkinStudioRendererCommandKind.Pause or SkinStudioRendererCommandKind.Restart
                    or SkinStudioRendererCommandKind.SetActive
                    or SkinStudioRendererCommandKind.SetAutoMotion => SkinStudioRendererEventKind.PlaybackState,
                _ => SkinStudioRendererEventKind.Ready,
            }
            : SkinStudioRendererEventKind.RecoverableError,
            AudioPlaying = rendererAudioPlaying,
            PreviewTargetId = rendererPreviewTarget?.Id,
            FamilyId = rendererPreviewTarget?.FamilyId,
            Component = rendererPreviewTarget?.ComponentName,
            Ruleset = rendererPreviewTarget?.Ruleset,
            PreviewKind = rendererPreviewTarget?.Kind,
            Compatibility = rendererPreviewTarget?.Compatibility,
            AssetProvenance = rendererPreviewTarget is null
            ? null
            : rendererAssetProvenance,
        };

    private SkinStudioRendererResponse rendererColourEditResponse(
        SkinStudioRendererRequest request,
        RendererColourEditRequest edit) => new()
        {
            RequestId = request.RequestId,
            Accepted = true,
            Message = "Open the Kumori colour editor.",
            LoadedDraftId = rendererLoadedDraftId,
            LoadedRevision = rendererLoadedRevision,
            Playing = rendererPlaying,
            Scene = rendererScene,
            Event = SkinStudioRendererEventKind.ColourEditRequested,
            AudioPlaying = rendererAudioPlaying,
            PreviewTargetId = rendererPreviewTarget?.Id,
            FamilyId = rendererPreviewTarget?.FamilyId,
            Component = rendererPreviewTarget?.ComponentName,
            Ruleset = rendererPreviewTarget?.Ruleset,
            PreviewKind = rendererPreviewTarget?.Kind,
            Compatibility = rendererPreviewTarget?.Compatibility,
            AssetProvenance = rendererPreviewTarget is null
            ? null
            : rendererAssetProvenance,
            ColourTarget = edit.Target,
            ColourRed = edit.Red,
            ColourGreen = edit.Green,
            ColourBlue = edit.Blue,
            AnchorX = edit.AnchorX,
            AnchorY = edit.AnchorY,
            AvoidLeft = edit.AvoidLeft,
            AvoidTop = edit.AvoidTop,
            AvoidRight = edit.AvoidRight,
            AvoidBottom = edit.AvoidBottom,
        };

    private void queueRendererColourEdit(
        SkinStudioRendererColourTarget target,
        Colour4 colour,
        Vector2 screenSpacePosition,
        Vector2 avoidTopLeft,
        Vector2 avoidBottomRight)
    {
        var surface = rendererInteractionLayer;
        var localPosition = surface?.ToLocalSpace(screenSpacePosition)
                            ?? Vector2.Zero;
        var localAvoidTopLeft = surface?.ToLocalSpace(avoidTopLeft)
                                ?? Vector2.Zero;
        var localAvoidBottomRight = surface?.ToLocalSpace(avoidBottomRight)
                                    ?? Vector2.Zero;
        var width = Math.Max(1, surface?.DrawWidth ?? 1);
        var height = Math.Max(1, surface?.DrawHeight ?? 1);
        rendererColourEditRequests.Enqueue(new RendererColourEditRequest(
            target,
            colourByte(colour.R),
            colourByte(colour.G),
            colourByte(colour.B),
            Math.Clamp(localPosition.X / width, 0, 1),
            Math.Clamp(localPosition.Y / height, 0, 1),
            Math.Clamp(Math.Min(localAvoidTopLeft.X, localAvoidBottomRight.X) / width, 0, 1),
            Math.Clamp(Math.Min(localAvoidTopLeft.Y, localAvoidBottomRight.Y) / height, 0, 1),
            Math.Clamp(Math.Max(localAvoidTopLeft.X, localAvoidBottomRight.X) / width, 0, 1),
            Math.Clamp(Math.Max(localAvoidTopLeft.Y, localAvoidBottomRight.Y) / height, 0, 1)));
    }

    private static byte colourByte(float value) =>
        (byte)Math.Round(Math.Clamp(value, 0, 1) * 255);

    private void auditionRendererSample(string component)
    {
        stopRendererAudio();
        var sample = rendererSample(component)
                     ?? throw new InvalidDataException(
                         $"The sample '{component}' is not supported by the renderer.");
        rendererAuditionSound = new SkinnableSound(sample)
        {
            Looping = component.Equals("pause-loop", StringComparison.OrdinalIgnoreCase),
        };
        Add(rendererAuditionSound);
        rendererAuditionSound.Play();
        rendererAudioPlaying = true;
    }

    private void startSemanticAudio(SkinStudioSemanticPreviewDescriptor target)
    {
        rendererAutomaticAudio = false;
        rendererAudioSequence = [];
        rendererAudioSequenceIndex = 0;
        rendererAudioInterval = 500;
        if (!target.IsAudio || !rendererIsActive)
            return;

        rendererLayeredHitSounds = layeredHitSoundsForDraft();
        var plan = SkinStudioSemanticAudioPlan.Build(target);
        rendererAudioSequence = plan.Components.ToArray();
        rendererAudioInterval = plan.IntervalMilliseconds;

        if (rendererAudioSequence.Length == 0)
            return;
        rendererAutomaticAudio = true;
        rendererNextAudioTime = Time.Current;
        rendererAudioPlaying = true;
        updateSemanticAudio(force: true);
    }

    private void updateSemanticAudio(bool force = false)
    {
        if (!rendererAutomaticAudio
            || !rendererIsActive
            || rendererAudioSequence.Length == 0
            || (!force && Time.Current < rendererNextAudioTime))
        {
            return;
        }

        var pulseIndex = rendererAudioSequenceIndex % rendererAudioSequence.Length;
        var component = rendererAudioSequence[
            rendererAudioSequenceIndex++ % rendererAudioSequence.Length];
        var samples = semanticSamples(component);
        if (samples.Length == 0)
            return;
        var looping = double.IsPositiveInfinity(rendererAudioInterval)
                      || component.Equals("pause-loop", StringComparison.OrdinalIgnoreCase)
                      || component.Contains("sliderslide", StringComparison.OrdinalIgnoreCase)
                      || component.Contains("sliderwhistle", StringComparison.OrdinalIgnoreCase)
                      || component.Equals("spinnerspin", StringComparison.OrdinalIgnoreCase);
        if (rendererAuditionSound is null)
        {
            rendererAuditionSound = new SkinnableSound(samples);
            Add(rendererAuditionSound);
        }
        else
        {
            rendererAuditionSound.Stop();
            rendererAuditionSound.Samples = samples;
        }
        rendererAuditionSound.Looping = looping;
        rendererAuditionSound.Play();
        if (rendererPreviewTarget?.Kind == SkinStudioSemanticPreviewKind.HitSoundLoop)
            player?.PulseHitSoundCircle(pulseIndex);
        rendererAudioPlaying = true;
        rendererNextAudioTime = double.IsPositiveInfinity(rendererAudioInterval)
            ? double.PositiveInfinity
            : Time.Current + rendererAudioInterval;
    }

    private ISampleInfo[] semanticSamples(string component)
    {
        if (component.StartsWith("taiko-", StringComparison.OrdinalIgnoreCase))
            return [new SampleInfo($"Gameplay/{component}")];
        return SkinStudioSemanticAudioPlan
            .LayeredComponents(component, rendererLayeredHitSounds)
            .Select(rendererSample)
            .Where(sample => sample is not null)
            .Cast<ISampleInfo>()
            .ToArray();
    }

    private bool layeredHitSoundsForDraft()
    {
        if (draft is null)
            return true;
        try
        {
            var files = new SkinPackageService(drafts).Materialize(draft.DraftId);
            if (!files.TryGetValue("skin.ini", out var bytes))
                return true;
            var value = SkinIniDocument.Parse(bytes)
                .GetValue("General", "LayeredHitSounds");
            return !string.Equals(value?.Trim(), "0", StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return true;
        }
    }

    private void updateRendererAssetProvenance()
    {
        if (rendererPreviewTarget is null || draft is null)
        {
            rendererAssetProvenance = SkinStudioAssetProvenance.Unknown;
            return;
        }
        var supplied = assets.List(draft.DraftId).Any(asset =>
            asset.ComponentName.Equals(
                rendererPreviewTarget.ComponentName,
                StringComparison.OrdinalIgnoreCase));
        rendererAssetProvenance = supplied
            ? SkinStudioAssetProvenance.Skin
            : rendererPreviewTarget.Compatibility == SkinExtraCompatibility.StableOnly
                ? SkinStudioAssetProvenance.Missing
                : SkinStudioAssetProvenance.LazerFallback;
    }

    private void stopRendererAudio()
    {
        rendererAutomaticAudio = false;
        rendererAudioSequence = [];
        rendererAudioSequenceIndex = 0;
        rendererNextAudioTime = double.PositiveInfinity;
        if (rendererAuditionSound is not null)
        {
            rendererAuditionSound.Stop();
            rendererAuditionSound.Expire();
            rendererAuditionSound = null;
        }
        rendererAudioPlaying = false;
    }

    private static ISampleInfo? rendererSample(string component)
    {
        var parts = component.Split('-', 2);
        if (parts.Length == 2
            && parts[0] is "normal" or "soft" or "drum"
            && parts[1] is "hitnormal" or "hitwhistle" or "hitfinish" or "hitclap"
                or "slidertick" or "sliderslide" or "sliderwhistle")
        {
            return new HitSampleInfo(parts[1], parts[0]);
        }
        return component.ToLowerInvariant() switch
        {
            "combobreak" => new SampleInfo("Gameplay/combobreak"),
            "failsound" => new SampleInfo("Gameplay/failsound"),
            "pause-loop" => new SampleInfo("Gameplay/pause-loop"),
            "spinnerspin" => new SampleInfo("Gameplay/spinnerspin"),
            "spinnerbonus" => new SampleInfo("Gameplay/spinnerbonus"),
            "spinnerbonus-max" => new SampleInfo("Gameplay/spinnerbonus-max"),
            "count1s" => new SampleInfo("Gameplay/count1s"),
            "count2s" => new SampleInfo("Gameplay/count2s"),
            "count3s" => new SampleInfo("Gameplay/count3s"),
            "readys" => new SampleInfo("Gameplay/readys"),
            "gos" => new SampleInfo("Gameplay/gos"),
            "sectionpass" => new SampleInfo("Gameplay/sectionpass"),
            "sectionfail" => new SampleInfo("Gameplay/sectionfail"),
            "nightcore-kick" => new SampleInfo("Gameplay/nightcore-kick"),
            "nightcore-clap" => new SampleInfo("Gameplay/nightcore-clap"),
            "nightcore-hat" => new SampleInfo("Gameplay/nightcore-hat"),
            "nightcore-finish" => new SampleInfo("Gameplay/nightcore-finish"),
            "applause" => new SampleInfo("Results/applause"),
            "applause-xh" => new SampleInfo("Results/applause-s"),
            "applause-x" => new SampleInfo("Results/applause-s"),
            "applause-sh" => new SampleInfo("Results/applause-s"),
            "applause-s" => new SampleInfo("Results/applause-s"),
            "applause-a" => new SampleInfo("Results/applause-a"),
            "applause-b" => new SampleInfo("Results/applause-b"),
            "applause-c" => new SampleInfo("Results/applause-c"),
            "applause-d" => new SampleInfo("Results/applause-d"),
            "seeya" => new SampleInfo("Outro/seeya"),
            "welcome" => new SampleInfo("Intro/Welcome/welcome"),
            "menuhit" => new SampleInfo("UI/menuhit"),
            "menuback" => new SampleInfo("UI/menuback"),
            "menu-play-click" => new SampleInfo("UI/menu-play-click"),
            "menu-back-click" => new SampleInfo("UI/menu-back-click"),
            "key-confirm" => new SampleInfo("UI/key-confirm"),
            "key-delete" => new SampleInfo("UI/key-delete"),
            "key-movement" => new SampleInfo("UI/key-movement"),
            "rank-up" => new SampleInfo("Gameplay/rank-up"),
            "rank-down" => new SampleInfo("Gameplay/rank-down"),
            _ => null,
        };
    }

    private void signalEmbeddedReady()
    {
        if (!embedded || string.IsNullOrWhiteSpace(embeddedSession))
            return;
        Console.Out.WriteLine(JsonSerializer.Serialize(new
        {
            status = "embedded_ready",
            session = embeddedSession,
        }));
        Console.Out.Flush();
    }

    private void configureWindow(FrameworkConfigManager config)
    {
        config.SetValue(FrameworkSetting.WindowMode, WindowMode.Windowed);
        config.SetValue(FrameworkSetting.WindowedSize, new Size(1500, 930));
        config.SetValue(FrameworkSetting.FrameSync, FrameSync.Limit2x);
    }

    private void prepareDraft()
    {
        Directory.CreateDirectory(contract.WorkspacePath);
        if (contract.DraftId is { } requested)
        {
            draft = drafts.Load(requested);
            return;
        }

        if (!string.IsNullOrWhiteSpace(contract.SourceSkinPath))
        {
            var source = Path.GetFullPath(contract.SourceSkinPath);
            var fingerprint = SkinPackageService.Fingerprint(source);
            draft = drafts.List().FirstOrDefault(
                candidate => candidate.SourceFingerprint == fingerprint);
            draft ??= drafts.Create(
                Path.GetFileNameWithoutExtension(source),
                "Kumori",
                source,
                fingerprint);
            return;
        }

        draft = drafts.List().FirstOrDefault();
    }

    private void selectDraftSkin(bool announceCompletion = true)
    {
        var selectedDraft = draft;
        if (selectedDraft is null)
            return;

        skinLoadCancellation?.Cancel();
        skinLoadCancellation?.Dispose();
        var cancellation = new CancellationTokenSource();
        skinLoadCancellation = cancellation;
        var generation = ++skinLoadGeneration;

        try
        {
            long revision = selectedDraft.History[selectedDraft.HistoryIndex].Revision;
            rendererLoadingDraftId = selectedDraft.DraftId;
            rendererLoadingRevision = revision;
            var snapshotTask = prepareEffectiveSkinFiles(
                selectedDraft,
                revision,
                generation,
                cancellation.Token);
            if (nativeSkinCache.TryGet(
                    selectedDraft.DraftId,
                    revision,
                    out Guid cachedSkinId))
            {
                var cached = SkinManager.Query(info => info.ID == cachedSkinId);
                if (cached is not null)
                {
                    SkinManager.CurrentSkinInfo.Value = cached;
                    refreshGameplayPlayerForSkin(comboColourCountForDraft(selectedDraft));
                    Scheduler.AddDelayed(
                        () => completeRendererLoadWhenReady(
                            selectedDraft.DraftId,
                            revision,
                            generation),
                        75);
                    Logger.Log(
                        $"Reused native Studio skin cache for draft "
                        + $"{selectedDraft.DraftId:N} revision {revision}.");
                    Scheduler.AddDelayed(refreshWorkbench, 100);
                    return;
                }
                nativeSkinCache.Remove(selectedDraft.DraftId);
            }

            if (statusText is not null)
                updateDraftPresentation($"Loading native preview for '{selectedDraft.Name}'...");

            _ = loadDraftSkinAsync(
                selectedDraft,
                revision,
                generation,
                snapshotTask,
                announceCompletion,
                cancellation.Token);
        }
        catch (Exception ex)
        {
            cancellation.Dispose();
            Logger.Error(ex, $"Could not prepare draft skin '{selectedDraft.SourcePath}'.");
        }
    }

    private async Task loadDraftSkinAsync(
        SkinDraftManifest selectedDraft,
        long revision,
        long generation,
        Task<StudioSkinFileSnapshot> snapshotTask,
        bool announceCompletion,
        CancellationToken cancellationToken)
    {
        string? importPath = null;
        try
        {
            var snapshot = await snapshotTask.ConfigureAwait(false);
            var comboColourCount = comboColourCountForFiles(snapshot.Files);
            importPath = await Task.Run(
                () =>
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    Directory.CreateDirectory(Path.GetTempPath());
                    return new SkinPackageService(drafts).Export(
                        snapshot.Files,
                        Path.Combine(
                            Path.GetTempPath(),
                            $"kumori-renderer-{selectedDraft.DraftId:N}-r{revision}-{Guid.NewGuid():N}.osk"),
                        CompressionLevel.NoCompression);
                },
                cancellationToken).ConfigureAwait(false);

            var imported = await SkinManager.Import(
                    new ImportTask(importPath),
                    cancellationToken: cancellationToken)
                .ConfigureAwait(false);
            if (imported is null)
            {
                Schedule(() => rendererLoadCompletion?.TrySetException(
                    new InvalidDataException("The native skin import returned no skin.")));
                return;
            }

            Schedule(() =>
            {
                if (generation != skinLoadGeneration
                    || cancellationToken.IsCancellationRequested)
                {
                    return;
                }

                SkinManager.CurrentSkinInfo.Value = imported;
                nativeSkinCache.Set(selectedDraft.DraftId, revision, imported.ID);
                refreshGameplayPlayerForSkin(comboColourCount);
                Scheduler.AddDelayed(
                    () => completeRendererLoadWhenReady(
                        selectedDraft.DraftId,
                        revision,
                        generation),
                    75);
                Scheduler.AddDelayed(refreshWorkbench, 100);
                if (announceCompletion)
                    updateDraftPresentation($"Skin loaded: {selectedDraft.Name}.");
            });
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            Logger.Error(ex, $"Could not import draft skin '{selectedDraft.SourcePath}'.");
            Schedule(() =>
            {
                if (generation == skinLoadGeneration)
                {
                    rendererLoadCompletion?.TrySetException(ex);
                    updateDraftPresentation($"Skin preview failed: {ex.Message}");
                }
            });
        }
        finally
        {
            try
            {
                if (importPath is not null)
                    File.Delete(importPath);
            }
            catch
            {
            }
        }
    }

    private int comboColourCountForDraft(SkinDraftManifest? selectedDraft)
    {
        if (selectedDraft is null)
            return 4;
        try
        {
            return comboColourCountForFiles(
                new SkinPackageService(drafts).Materialize(selectedDraft.DraftId));
        }
        catch
        {
            return 4;
        }
    }

    private static int comboColourCountForFiles(
        IReadOnlyDictionary<string, byte[]> files)
    {
        if (!files.TryGetValue("skin.ini", out var bytes))
            return 4;
        try
        {
            return SkinStudioPreviewScenes.ComboColourCount(
                SkinIniDocument.Parse(bytes));
        }
        catch
        {
            return 4;
        }
    }

    private void refreshGameplayPlayerForSkin(int comboColourCount)
    {
        comboColourCount = Math.Clamp(comboColourCount, 1, 8);
        if (audioManager is null || gameHost is null)
            return;

        var paletteChanged = comboColourCount != previewComboColourCount;
        previewComboColourCount = comboColourCount;

        // The cursor is hosted by a skin-providing container outside the
        // gameplay player and updates directly when SkinManager switches.
        // Keep the existing player alive so changing cursor packs is nearly
        // instantaneous and the trail never flashes through a reload.
        if (rendererOnly
            && rendererInspectionFamily?.Equals(
                "osu.cursor",
                StringComparison.OrdinalIgnoreCase) == true
            && player is not null)
        {
            Scheduler.AddDelayed(() =>
            {
                applyRendererCursorScale();
                applyRendererInspectionTint();
                configureRendererScene();
            }, 1);
            return;
        }

        // A skin switch updates SkinManager's bindables, but gameplay drawables
        // may still hold texture-backed children from the prior skin. Recreate
        // the renderer-only player after every accepted revision so an edited
        // element is guaranteed to be visible as soon as the import completes.
        player?.Exit();
        player = null;
        if (paletteChanged)
        {
            Beatmap.Value = new StudioWorkingBeatmap(
                beatmapPath,
                audioManager,
                gameHost,
                previewComboColourCount);
        }
        Scheduler.AddDelayed(() =>
        {
            // Player construction completes asynchronously. Scene configuration
            // is performed by completeRendererLoadWhenReady() once the drawable
            // ruleset and gameplay clock are both available.
            ensureGameplayPlayer();
        }, 1);
    }

    private void completeRendererLoadWhenReady(
        Guid draftId,
        long revision,
        long generation,
        int attempt = 0,
        bool sceneConfigured = false)
    {
        if (generation != skinLoadGeneration)
            return;
        if (player?.IsRendererSceneReady == true && !sceneConfigured)
        {
            configureRendererScene();
            Scheduler.AddDelayed(
                () => completeRendererLoadWhenReady(
                    draftId,
                    revision,
                    generation,
                    attempt + 1,
                    sceneConfigured: true),
                50);
            return;
        }
        if (player?.IsRendererSceneReady == true)
        {
            rendererLoadedDraftId = draftId;
            rendererLoadedRevision = revision;
            if (rendererOnly && gameplayContainer is not null)
            {
                gameplayContainer.AlwaysPresent = rendererIsActive;
                if (rendererIsActive)
                    gameplayContainer.Show();
                else
                    gameplayContainer.Hide();
            }
            rendererLoadCompletion?.TrySetResult((draftId, revision));
            return;
        }
        if (attempt >= 600)
        {
            rendererLoadCompletion?.TrySetException(
                new TimeoutException("The lazer gameplay surface did not finish initializing."));
            return;
        }
        Scheduler.AddDelayed(
            () => completeRendererLoadWhenReady(
                draftId,
                revision,
                generation,
                attempt + 1,
                sceneConfigured),
            25);
    }

    private Task<StudioSkinFileSnapshot> prepareEffectiveSkinFiles(
        SkinDraftManifest selectedDraft,
        long revision,
        long generation,
        CancellationToken cancellationToken)
    {
        if (effectiveSkinFilesTask is not null
            && effectiveSkinFilesDraftId == selectedDraft.DraftId
            && effectiveSkinFilesRevision == revision
            && effectiveSkinFilesTask.Status == TaskStatus.RanToCompletion)
        {
            effectiveSkinFiles = effectiveSkinFilesTask.Result;
            return effectiveSkinFilesTask;
        }

        effectiveSkinFiles = StudioSkinFileSnapshot.Empty;
        effectiveSkinFilesDraftId = selectedDraft.DraftId;
        effectiveSkinFilesRevision = revision;
        var task = Task.Run(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            var files = new SkinPackageService(drafts).Materialize(
                selectedDraft.DraftId);
            cancellationToken.ThrowIfCancellationRequested();
            var supplied =
                StudioSkinWorkbench.VisibleSuppliedComponents(files);
            return new StudioSkinFileSnapshot(files, supplied);
        }, cancellationToken);
        effectiveSkinFilesTask = task;
        _ = task.ContinueWith(completed =>
        {
            if (completed.Status != TaskStatus.RanToCompletion)
                return;
            Schedule(() =>
            {
                if (generation != skinLoadGeneration
                    || effectiveSkinFilesDraftId != selectedDraft.DraftId
                    || effectiveSkinFilesRevision != revision)
                {
                    return;
                }
                effectiveSkinFiles = completed.Result;
                refreshWorkbench();
            });
        }, CancellationToken.None, TaskContinuationOptions.None, TaskScheduler.Default);
        return task;
    }

    private IReadOnlyDictionary<string, byte[]> currentEffectiveSkinFiles() =>
        effectiveSnapshotMatchesCurrentDraft()
            ? effectiveSkinFiles.Files
            : StudioSkinFileSnapshot.Empty.Files;

    private IReadOnlySet<string> currentVisibleSuppliedComponents() =>
        effectiveSnapshotMatchesCurrentDraft()
            ? effectiveSkinFiles.VisibleSuppliedComponents
            : StudioSkinFileSnapshot.Empty.VisibleSuppliedComponents;

    private bool effectiveSnapshotMatchesCurrentDraft()
    {
        var current = draft;
        return current is not null
               && current.History.Count > 0
               && effectiveSkinFilesDraftId == current.DraftId
               && effectiveSkinFilesRevision
               == current.History[current.HistoryIndex].Revision;
    }

    internal static float CursorAlpha(bool studioIsActive) =>
        studioIsActive ? 1 : 0;

    internal static bool UsesInteractiveRendererCursor(bool rendererOnly) =>
        rendererOnly;

    private void buildShell()
    {
        var acceptanceCatalogSync =
            commandAcceptanceOutputPath is null
                ? null
                : (extrasCatalogAcceptanceController =
                    new StudioExtrasCatalogAcceptanceController(
                        contract.WorkspacePath,
                        extrasRoot)).Service;
        screenStack = new OsuScreenStack { RelativeSizeAxes = Axes.Both };
        workbench = new StudioSkinWorkbench(
            SkinManager,
            selectAsset,
            updateDraftPresentation,
            updateCommandStates,
            currentEffectiveSkinFiles,
            currentVisibleSuppliedComponents);
        if (acceptanceOutputPath is not null)
        {
            acceptanceWorkbenchClock = new ManualFramedClock
            {
                CurrentTime = 0,
            };
            workbench.Clock = acceptanceWorkbenchClock;
        }
        workbenchContainer = new Container
        {
            RelativeSizeAxes = Axes.Both,
            Child = workbench,
        };
        gameplayContainer = new Container
        {
            RelativeSizeAxes = Axes.Both,
            Child = screenStack,
        };
        rendererSemanticOverlay = new StudioExtrasInspectionOverlay();
        gameplayContainer.Hide();
        Add(new Container
        {
            RelativeSizeAxes = Axes.Both,
            Padding = new MarginPadding
            {
                Left = left_width,
                Right = right_width,
                Top = top_height,
                Bottom = bottom_height,
            },
            Masking = true,
            Children =
            [
                workbenchContainer,
                gameplayContainer,
            ],
        });
        Add(new Container
        {
            RelativeSizeAxes = Axes.Both,
            Depth = -1,
            Padding = new MarginPadding
            {
                Left = left_width,
                Right = right_width,
                Top = top_height,
                Bottom = bottom_height,
            },
            Child = rendererSemanticOverlay,
        });

        AddRange(
        [
            new Box
            {
                RelativeSizeAxes = Axes.Both,
                Colour = Colour4.Black.Opacity(0.08f),
                Depth = 5,
            },
            panel(Anchor.TopLeft, left_width, 1, Axes.Y),
            panel(Anchor.TopRight, right_width, 1, Axes.Y),
            new Container
            {
                RelativeSizeAxes = Axes.X,
                Height = top_height,
                Depth = -10,
                Children =
                [
                    new Box
                    {
                        RelativeSizeAxes = Axes.Both,
                        Colour = Colour4.FromHex("#151421"),
                    },
                    topToolbarContent(),
                ],
            },
            new Container
            {
                RelativeSizeAxes = Axes.X,
                Height = bottom_height,
                Anchor = Anchor.BottomLeft,
                Origin = Anchor.BottomLeft,
                Depth = -10,
                Children =
                [
                    new Box
                    {
                        RelativeSizeAxes = Axes.Both,
                        Colour = Colour4.FromHex("#151421"),
                    },
                    statusText = new SpriteText
                    {
                        Text = "Loading native preview…",
                        Anchor = Anchor.CentreLeft,
                        Origin = Anchor.CentreLeft,
                        Margin = new MarginPadding { Left = 18 },
                        Font = FontUsage.Default.With(size: 12),
                        Colour = Colour4.FromHex("#C6A8BA"),
                    },
                ],
            },
            elementPanelContent(),
            rightPanelContent(),
            advancedToolsOverlay = advancedToolsContent(),
            skinIniOverlay = new StudioSkinIniOverlay(),
            rawSkinIniOverlay = new StudioRawSkinIniOverlay(),
            extrasOverlay = new StudioExtrasOverlay(
                extrasRoot,
                applyExtrasPack,
                compareExtrasPack,
                exportExtrasPack,
                deleteExtrasPack,
                restoreLatestExtrasPack,
                pack => extrasAudioBrowserOverlay?.Present(pack),
                updateDraftPresentation,
                acceptanceCatalogSync),
            extrasExtractionOverlay = new StudioExtrasExtractionOverlay(
                extrasRoot,
                () => extrasOverlay?.RefreshLibrary(),
                updateDraftPresentation),
            extrasCompositionOverlay = new StudioExtrasCompositionOverlay(
                () => extrasOverlay?.Present()),
            draftBrowserOverlay = new StudioDraftBrowserOverlay(openDraft),
            changeReviewOverlay = new StudioChangeReviewOverlay(discardSingleChange),
            pathPromptOverlay = new StudioTextPromptOverlay(),
            installedSkinBrowserOverlay =
                new StudioInstalledSkinBrowserOverlay(importInstalledSkin),
            openingSkinOverlay = new StudioOpeningSkinOverlay(
                openDraft,
                importInstalledSkin,
                () => presentFileSelector(
                    skinSelector,
                    "skin package",
                    [".osk", ".zip"],
                    importSkin),
                createBlankDraft,
                createDraftFromExtras),
            imageTransformOverlay = new StudioImageTransformOverlay(
                contract.WorkspacePath),
            audioTransportOverlay = new StudioAudioTransportOverlay(
                audioManager!,
                contract.WorkspacePath,
                updateDraftPresentation),
            extrasAudioBrowserOverlay =
                new StudioExtrasAudioBrowserOverlay(openExtrasAudio),
            identityOverlay = new StudioIdentityOverlay(),
        ]);
        if (quickColour is not null)
            quickColour.Current.Value = "#FFB7D5";
        automaticBackupButton?.SetSelected(
            studioPreferences.AutomaticEditBackups);
    }

    private Drawable elementPanelContent() =>
        elementNavigator = new StudioElementNavigator(
            SkinManager,
            left_width,
            top_height,
            bottom_height,
            category =>
            {
                showWorkbench();
                workbench?.SetCategory(category);
                elementNavigator?.SetSelectedCategory(category);
                updateDraftPresentation(
                    $"Showing {category}. Choose an element in the middle "
                    + "to inspect and edit it on the right.");
            },
            currentEffectiveSkinFiles,
            currentVisibleSuppliedComponents);

    private Drawable topToolbarContent() => new Container
    {
        RelativeSizeAxes = Axes.Both,
        Children =
        [
            new SpriteText
            {
                Text = "KUMORI  /  SKIN STUDIO",
                Position = new Vector2(18, 15),
                Font = FontUsage.Default.With(size: 19, weight: "Bold"),
                Colour = Colour4.FromHex("#F5D7E7"),
            },
            new SpriteText
            {
                Text = "ACTIVE SKIN",
                Position = new Vector2(250, 10),
                Font = FontUsage.Default.With(size: 9, weight: "Bold"),
                Colour = Colour4.FromHex("#A991A2"),
            },
            skinText = new SpriteText
            {
                Text = draft?.Name ?? "Choose a skin",
                Position = new Vector2(250, 28),
                Width = 310,
                Truncate = true,
                Font = FontUsage.Default.With(size: 14, weight: "SemiBold"),
                Colour = Colour4.White,
            },
            new SpriteText
            {
                Text = $"LAZER {Program.LazerRevision}",
                Position = new Vector2(18, 48),
                Font = FontUsage.Default.With(size: 9, weight: "SemiBold"),
                Colour = Colour4.FromHex("#8F7D8A"),
            },
            new FillFlowContainer
            {
                Width = 742,
                Height = 38,
                Anchor = Anchor.CentreRight,
                Origin = Anchor.CentreRight,
                Margin = new MarginPadding { Right = 14 },
                Direction = FillDirection.Horizontal,
                Spacing = new Vector2(7, 0),
                Children =
                [
                    compactToolbarButton(
                        "Skin",
                        () => presentOpeningSkinChooser(required: false),
                        86),
                    workbenchButton = compactToolbarButton(
                        "Elements",
                        showWorkbench,
                        96),
                    mockupButton = compactToolbarButton(
                        "Mockup",
                        showMockup,
                        92),
                    gameplayButton = compactToolbarButton(
                        "Gameplay",
                        showGameplay,
                        100),
                    compactToolbarButton(
                        "skin.ini",
                        editSkinIniStructured,
                        88),
                    reviewChangesButton = compactToolbarButton(
                        "Changes",
                        reviewChanges,
                        94,
                        enabled: false),
                    compactToolbarButton(
                        "Publish",
                        publishDraft,
                        100,
                        accent: true),
                ],
            },
        ],
    };

    private static StudioActionButton compactToolbarButton(
        string text,
        Action action,
        float width,
        bool accent = false,
        bool enabled = true) =>
        new(text, action, accent, enabled)
        {
            RelativeSizeAxes = Axes.None,
            Width = width,
            Height = 38,
        };

    private Drawable leftPanelContent() => new OsuScrollContainer(Direction.Vertical)
    {
        Width = left_width,
        RelativeSizeAxes = Axes.Y,
        ScrollbarVisible = true,
        Depth = -20,
        Child = new FillFlowContainer
        {
            Width = left_width,
            AutoSizeAxes = Axes.Y,
            Padding = new MarginPadding
            {
                Top = top_height + 20,
                Left = 18,
                Right = 18,
                Bottom = bottom_height + 16,
            },
            Direction = FillDirection.Vertical,
            Spacing = new Vector2(0, 12),
            Children =
        [
            heading("DRAFT"),
            skinText = body(draft?.Name ?? "No draft", 16, Colour4.White),
            body(
                draft?.SourcePath is null
                    ? "Isolated blank workspace"
                    : Path.GetFileName(draft.SourcePath),
                12,
                Colour4.FromHex("#A991A2")),
            new StudioActionButton(
                "Import .osk skin",
                () => presentFileSelector(
                    skinSelector,
                    "skin package",
                    [".osk", ".zip"],
                    importSkin)),
            new StudioActionButton("Create blank draft", createBlankDraft),
            new StudioActionButton("Create draft from Extras", createDraftFromExtras),
            new StudioActionButton(
                "Extras composition readiness",
                showExtrasCompositionReadiness),
            new StudioActionButton("Browse drafts", browseDrafts),
            new StudioActionButton("Next draft", selectNextDraft),
            new StudioActionButton(
                "Open installed lazer skin",
                browseInstalledSkins,
                enabled: !string.IsNullOrWhiteSpace(contract.PlayerRoot)),
            new StudioActionButton("Duplicate draft", duplicateDraft),
            new StudioActionButton("Rename skin / author", renameDraft),
            deleteDraftButton = new StudioActionButton("Move draft to trash", deleteDraftRecoverably),
            restoreDeletedDraftButton = new StudioActionButton(
                "Restore last deleted",
                restoreLastDeletedDraft,
                enabled: false),
            recoverInterruptedDraftButton = new StudioActionButton(
                "Recover interrupted draft save",
                recoverInterruptedDraft,
                enabled: false),
            new StudioActionButton("Create draft backup", createDraftBackup),
            automaticBackupButton = new StudioActionButton(
                automaticBackupLabel(),
                toggleAutomaticEditBackups),
            new StudioActionButton(
                "Set backup retention",
                promptBackupRetention),
            restoreDraftBackupButton = new StudioActionButton(
                "Restore latest backup",
                restoreLatestBackup,
                enabled: false),
            divider(),
            heading("WORKSPACES"),
            workbenchButton = new StudioActionButton("All-elements workbench", showWorkbench),
            mockupButton = new StudioActionButton("Gameplay Mockup", showMockup),
            gameplayButton = new StudioActionButton("Real gameplay", showGameplay),
            new StudioActionButton(
                "Import / replace asset",
                importAnyAsset),
            new StudioActionButton(
                "Import asset folder (multi-file)",
                promptAssetFolder),
            new StudioActionButton("Structured skin.ini", editSkinIniStructured),
            new StudioActionButton("Raw skin.ini editor", editSkinIniRaw),
            new StudioActionButton("Edit raw skin.ini externally", editSkinIniExternally),
            applyExternalSkinIniButton = new StudioActionButton(
                "Apply raw skin.ini edit",
                applyExternalSkinIni,
                enabled: false),
            new StudioActionButton("Browse Extras library", () => extrasOverlay?.Present()),
            new StudioActionButton("Extract draft to Extras", extractDraftToExtras),
            new StudioActionButton(
                "Extract .osk to Extras",
                () => presentFileSelector(
                    extrasSkinSelector,
                    "skin package",
                    [".osk", ".zip"],
                    extractSkinPackageToExtras)),
            new StudioActionButton(
                "Extract skin folder to Extras",
                promptExtrasFolder),
            new StudioActionButton(
                "Import Extras package",
                () => presentFileSelector(
                    extrasPackageSelector,
                    "Extras package",
                    [".zip", ".kse"],
                    importExtrasPackage)),
            divider(),
            heading("PREVIEW MAP"),
            new StudioActionButton(
                "Import custom .osu",
                () => presentFileSelector(
                    beatmapSelector,
                    "beatmap",
                    [".osu"],
                    importBeatmap)),
            body(
                "Kumori Skin Coverage\nCircles • Slider • Spinner • HUD • Cursor",
                12,
                Colour4.FromHex("#C6A8BA")),
        ],
        },
    };

    private Drawable rightPanelContent() => new OsuScrollContainer(Direction.Vertical)
    {
        Width = right_width,
        RelativeSizeAxes = Axes.Y,
        Anchor = Anchor.TopRight,
        Origin = Anchor.TopRight,
        ScrollbarVisible = true,
        Depth = -20,
        Child = new FillFlowContainer
        {
            Width = right_width,
            AutoSizeAxes = Axes.Y,
            Padding = new MarginPadding
            {
                Top = top_height + 16,
                Left = 18,
                Right = 18,
                Bottom = bottom_height + 14,
            },
            Direction = FillDirection.Vertical,
            Spacing = new Vector2(0, 10),
            Children =
            [
                heading("ELEMENT PROPERTIES"),
                selectedAssetText = body(
                    "Choose an element from the preview.",
                    13,
                    Colour4.White),
                divider(),
                heading("QUICK RECOLOUR"),
                quickColour = new OsuTextBox
                {
                    RelativeSizeAxes = Axes.X,
                    Height = 38,
                    PlaceholderText = "#FFB7D5",
                },
                quickColorizeButton = new StudioActionButton(
                    "Apply solid colour",
                    () => applyQuickRecolour(
                        SkinImageTransformMode.Colorize),
                    enabled: false),
                quickTintButton = new StudioActionButton(
                    "Tint, keep shading",
                    () => applyQuickRecolour(
                        SkinImageTransformMode.Tint),
                    enabled: false),
                divider(),
                heading("ASSET"),
                replaceAssetButton = new StudioActionButton(
                    "Replace element",
                    replaceSelectedAsset,
                    accent: true,
                    enabled: false),
                transformAssetButton = new StudioActionButton(
                    "Edit / transform",
                    transformSelectedImage,
                    enabled: false),
                resetAssetButton = new StudioActionButton(
                    "Reset element",
                    resetSelectedAsset,
                    enabled: false),
                exportAssetButton = new StudioActionButton(
                    "Export element",
                    exportSelectedAsset,
                    enabled: false),
                externalEditAssetButton = new StudioActionButton(
                    "Open externally",
                    editSelectedAssetExternally,
                    enabled: false),
                deleteAssetButton = new StudioActionButton(
                    "Delete element...",
                    deleteSelectedAsset,
                    enabled: false),
                divider(),
                heading("CHANGES"),
                changesText = body(
                    "No staged changes",
                    12,
                    Colour4.FromHex("#D8C2CF")),
                undoButton = new StudioActionButton(
                    "Undo",
                    undo,
                    enabled: false),
                redoButton = new StudioActionButton(
                    "Redo",
                    redo,
                    enabled: false),
                new StudioActionButton(
                    "Export .osk",
                    exportDraft),
                new StudioActionButton(
                    "More tools...",
                    () => advancedToolsOverlay?.Present()),
            ],
        },
    };

    private StudioToolsOverlay advancedToolsContent() => new(
    [
        heading("ASSET TOOLS"),
        copyAssetButton = new StudioActionButton(
            "Copy selected family",
            copySelectedAsset,
            enabled: false),
        pasteAssetButton = new StudioActionButton(
            "Paste into selected",
            pasteAssetIntoSelected,
            enabled: false),
        normalizeAudioButton = new StudioActionButton(
            "Normalize selected audio",
            normalizeSelectedAudio,
            enabled: false),
        audioTransportButton = new StudioActionButton(
            "Audio transport / waveform",
            openSelectedAudioTransport,
            enabled: false),
        deleteAnimationFrameButton = new StudioActionButton(
            "Delete animation frame",
            promptDeleteAnimationFrame,
            enabled: false),
        insertAnimationFrameButton = new StudioActionButton(
            "Insert / duplicate frame",
            promptInsertAnimationFrame,
            enabled: false),
        moveAnimationFrameButton = new StudioActionButton(
            "Move animation frame",
            promptMoveAnimationFrame,
            enabled: false),
        new StudioActionButton(
            "Set skin animation FPS",
            promptAnimationFrameRate),
        applyExternalAssetButton = new StudioActionButton(
            "Apply external asset edit",
            applyExternalAssetEdit,
            enabled: false),
        addSelectedToExtrasButton = new StudioActionButton(
            "Add selected family to Extras",
            extractSelectedAssetToExtras,
            enabled: false),
        divider(),
        heading("CHANGE TOOLS"),
        new StudioActionButton(
            "Check source conflict",
            checkSourceConflict),
        discardSelectedButton = new StudioActionButton(
            "Discard selected change",
            resetSelectedAsset,
            enabled: false),
        resetCategoryButton = new StudioActionButton(
            "Reset current category",
            resetFilteredCategory,
            enabled: false),
        addCategoryToExtrasButton = new StudioActionButton(
            "Add current category to Extras",
            extractFilteredCategoryToExtras,
            enabled: false),
        discardAllButton = new StudioActionButton(
            "Discard all changes",
            discardAllChanges,
            enabled: false),
        new StudioActionButton(
            "Live sync now",
            syncLivePreview,
            enabled: contract.LiveSyncEnabled
                     && !string.IsNullOrWhiteSpace(contract.PlayerRoot)),
        new StudioActionButton(
            "Restart preview",
            restartPreview),
        divider(),
        heading("SKIN & DRAFT"),
        new StudioActionButton(
            "Import .osk / .zip",
            () => presentFileSelector(
                skinSelector,
                "skin package",
                [".osk", ".zip"],
                importSkin)),
        new StudioActionButton(
            "Browse all Extras",
            () => extrasOverlay?.Present()),
        new StudioActionButton(
            "Create blank draft",
            createBlankDraft),
        new StudioActionButton(
            "Create draft from Extras",
            createDraftFromExtras),
        new StudioActionButton(
            "Browse drafts",
            browseDrafts),
        new StudioActionButton(
            "Open installed lazer skin",
            browseInstalledSkins,
            enabled: !string.IsNullOrWhiteSpace(contract.PlayerRoot)),
        new StudioActionButton(
            "Duplicate draft",
            duplicateDraft),
        new StudioActionButton(
            "Rename skin / author",
            renameDraft),
        new StudioActionButton(
            "Edit raw skin.ini",
            editSkinIniRaw),
        new StudioActionButton(
            "Edit skin.ini externally",
            editSkinIniExternally),
        applyExternalSkinIniButton = new StudioActionButton(
            "Apply external skin.ini edit",
            applyExternalSkinIni,
            enabled: false),
        new StudioActionButton(
            "Import asset folder",
            promptAssetFolder),
        new StudioActionButton(
            "Extract draft to Extras",
            extractDraftToExtras),
        new StudioActionButton(
            "Extract .osk to Extras",
            () => presentFileSelector(
                extrasSkinSelector,
                "skin package",
                [".osk", ".zip"],
                extractSkinPackageToExtras)),
        new StudioActionButton(
            "Import Extras package",
            () => presentFileSelector(
                extrasPackageSelector,
                "Extras package",
                [".zip", ".kse"],
                importExtrasPackage)),
        new StudioActionButton(
            "Import custom preview map",
            () => presentFileSelector(
                beatmapSelector,
                "beatmap",
                [".osu"],
                importBeatmap)),
        new StudioActionButton(
            "Create draft backup",
            createDraftBackup),
        automaticBackupButton = new StudioActionButton(
            automaticBackupLabel(),
            toggleAutomaticEditBackups),
        restoreDraftBackupButton = new StudioActionButton(
            "Restore latest backup",
            restoreLatestBackup,
            enabled: false),
        recoverInterruptedDraftButton = new StudioActionButton(
            "Recover interrupted save",
            recoverInterruptedDraft,
            enabled: false),
        deleteDraftButton = new StudioActionButton(
            "Move draft to trash",
            deleteDraftRecoverably),
        restoreDeletedDraftButton = new StudioActionButton(
            "Restore last deleted draft",
            restoreLastDeletedDraft,
            enabled: false),
    ]);

    private Drawable legacyRightPanelContent() => new OsuScrollContainer(Direction.Vertical)
    {
        Width = right_width,
        RelativeSizeAxes = Axes.Y,
        Anchor = Anchor.TopRight,
        Origin = Anchor.TopRight,
        ScrollbarVisible = true,
        Depth = -20,
        Child = new FillFlowContainer
        {
            Width = right_width,
            AutoSizeAxes = Axes.Y,
            Padding = new MarginPadding
            {
                Top = top_height + 20,
                Left = 20,
                Right = 20,
                Bottom = bottom_height + 16,
            },
            Direction = FillDirection.Vertical,
            Spacing = new Vector2(0, 12),
            Children =
        [
            heading("SKIN"),
            skinText = body(draft?.Name ?? "Choose a skin", 16, Colour4.White),
            new StudioActionButton(
                "Choose or change skin",
                () => presentOpeningSkinChooser(required: false),
                accent: true),
            new StudioActionButton(
                "Import .osk / .zip",
                () => presentFileSelector(
                    skinSelector,
                    "skin package",
                    [".osk", ".zip"],
                    importSkin)),
            new StudioActionButton(
                "Browse all Extras",
                () => extrasOverlay?.Present()),
            workbenchButton = new StudioActionButton(
                "Element preview",
                showWorkbench),
            mockupButton = new StudioActionButton(
                "Gameplay Mockup",
                showMockup),
            gameplayButton = new StudioActionButton(
                "Real gameplay preview",
                showGameplay),
            new StudioActionButton("Edit skin.ini", editSkinIniStructured),
            new StudioActionButton("Edit raw skin.ini", editSkinIniRaw),
            divider(),
            heading("INSPECTOR"),
            body(
                "Choose a category on the left, then an element in the middle. Its editing tools appear here.",
                13,
                Colour4.FromHex("#D8C2CF")),
            divider(),
            heading("SELECTED ASSET"),
            selectedAssetText = body("Click an element tile", 13, Colour4.White),
            heading("RECOLOUR"),
            quickColour = new OsuTextBox
            {
                RelativeSizeAxes = Axes.X,
                Height = 38,
                PlaceholderText = "#FFB7D5",
            },
            quickColorizeButton = new StudioActionButton(
                "Apply solid colour",
                () => applyQuickRecolour(SkinImageTransformMode.Colorize),
                enabled: false),
            quickTintButton = new StudioActionButton(
                "Tint while keeping shading",
                () => applyQuickRecolour(SkinImageTransformMode.Tint),
                enabled: false),
            replaceAssetButton = new StudioActionButton(
                "Replace selected",
                replaceSelectedAsset,
                enabled: false),
            deleteAssetButton = new StudioActionButton(
                "Delete selected family",
                deleteSelectedAsset,
                enabled: false),
            resetAssetButton = new StudioActionButton(
                "Reset selected family",
                resetSelectedAsset,
                enabled: false),
            copyAssetButton = new StudioActionButton(
                "Copy selected family",
                copySelectedAsset,
                enabled: false),
            pasteAssetButton = new StudioActionButton(
                "Paste into selected",
                pasteAssetIntoSelected,
                enabled: false),
            transformAssetButton = new StudioActionButton(
                "More recolour / transform options",
                transformSelectedImage,
                enabled: false),
            normalizeAudioButton = new StudioActionButton(
                "Normalize selected audio",
                normalizeSelectedAudio,
                enabled: false),
            audioTransportButton = new StudioActionButton(
                "Audio transport / waveform",
                openSelectedAudioTransport,
                enabled: false),
            deleteAnimationFrameButton = new StudioActionButton(
                "Delete animation frame",
                promptDeleteAnimationFrame,
                enabled: false),
            insertAnimationFrameButton = new StudioActionButton(
                "Insert / duplicate frame",
                promptInsertAnimationFrame,
                enabled: false),
            moveAnimationFrameButton = new StudioActionButton(
                "Move animation frame",
                promptMoveAnimationFrame,
                enabled: false),
            new StudioActionButton(
                "Set skin animation FPS",
                promptAnimationFrameRate),
            exportAssetButton = new StudioActionButton(
                "Export selected family",
                exportSelectedAsset,
                enabled: false),
            externalEditAssetButton = new StudioActionButton(
                "Edit selected externally",
                editSelectedAssetExternally,
                enabled: false),
            applyExternalAssetButton = new StudioActionButton(
                "Apply external asset edit",
                applyExternalAssetEdit,
                enabled: false),
            addSelectedToExtrasButton = new StudioActionButton(
                "Add selected family to Extras",
                extractSelectedAssetToExtras,
                enabled: false),
            divider(),
            heading("CHANGES"),
            changesText = body("No staged changes", 13, Colour4.White),
            reviewChangesButton = new StudioActionButton(
                "Review all changes",
                reviewChanges,
                enabled: false),
            undoButton = new StudioActionButton("Undo", undo, enabled: false),
            redoButton = new StudioActionButton("Redo", redo, enabled: false),
            new StudioActionButton("Check source conflict", checkSourceConflict),
            discardSelectedButton = new StudioActionButton(
                "Discard selected change",
                resetSelectedAsset,
                enabled: false),
            resetCategoryButton = new StudioActionButton(
                "Reset filtered category",
                resetFilteredCategory,
                enabled: false),
            addCategoryToExtrasButton = new StudioActionButton(
                "Add filtered category to Extras",
                extractFilteredCategoryToExtras,
                enabled: false),
            discardAllButton = new StudioActionButton(
                "Discard all changes",
                discardAllChanges,
                enabled: false),
            divider(),
            new StudioActionButton("Export .osk", exportDraft, accent: true),
            new StudioActionButton("Publish to osu!lazer", publishDraft),
            new StudioActionButton(
                "Live sync now",
                syncLivePreview,
                enabled: contract.LiveSyncEnabled
                         && !string.IsNullOrWhiteSpace(contract.PlayerRoot)),
            new StudioActionButton("Restart preview", restartPreview),
            divider(),
            heading("DRAFT & EXTRAS TOOLS"),
            new StudioActionButton("Create blank draft", createBlankDraft),
            new StudioActionButton("Create draft from Extras", createDraftFromExtras),
            new StudioActionButton("Browse drafts", browseDrafts),
            new StudioActionButton(
                "Open installed lazer skin",
                browseInstalledSkins,
                enabled: !string.IsNullOrWhiteSpace(contract.PlayerRoot)),
            new StudioActionButton("Duplicate draft", duplicateDraft),
            new StudioActionButton("Rename skin / author", renameDraft),
            new StudioActionButton(
                "Import asset folder",
                promptAssetFolder),
            new StudioActionButton("Edit skin.ini externally", editSkinIniExternally),
            applyExternalSkinIniButton = new StudioActionButton(
                "Apply external skin.ini edit",
                applyExternalSkinIni,
                enabled: false),
            new StudioActionButton("Extract draft to Extras", extractDraftToExtras),
            new StudioActionButton(
                "Extract .osk to Extras",
                () => presentFileSelector(
                    extrasSkinSelector,
                    "skin package",
                    [".osk", ".zip"],
                    extractSkinPackageToExtras)),
            new StudioActionButton(
                "Import Extras package",
                () => presentFileSelector(
                    extrasPackageSelector,
                    "Extras package",
                    [".zip", ".kse"],
                    importExtrasPackage)),
            new StudioActionButton(
                "Import custom preview map",
                () => presentFileSelector(
                    beatmapSelector,
                    "beatmap",
                    [".osu"],
                    importBeatmap)),
            new StudioActionButton("Create draft backup", createDraftBackup),
            automaticBackupButton = new StudioActionButton(
                automaticBackupLabel(),
                toggleAutomaticEditBackups),
            restoreDraftBackupButton = new StudioActionButton(
                "Restore latest backup",
                restoreLatestBackup,
                enabled: false),
            recoverInterruptedDraftButton = new StudioActionButton(
                "Recover interrupted save",
                recoverInterruptedDraft,
                enabled: false),
            deleteDraftButton = new StudioActionButton(
                "Move draft to trash",
                deleteDraftRecoverably),
            restoreDeletedDraftButton = new StudioActionButton(
                "Restore last deleted draft",
                restoreLastDeletedDraft,
                enabled: false),
            new StudioActionButton(
                embedded ? "Close via Kumori navigation" : "Close Studio",
                () => gameHost?.Exit(),
                enabled: !embedded),
            divider(),
            body(
                contract.LiveSyncEnabled
                && !string.IsNullOrWhiteSpace(contract.PlayerRoot)
                    ? "Guarded live sync permitted for this launch. Originals are never edited."
                    : "Live sync is visibly disabled until a player root is detected and launch-scoped permission is granted.",
                11,
                Colour4.FromHex("#A991A2")),
        ],
        },
    };

    private Drawable authoritativeBadge() => new Container
    {
        AutoSizeAxes = Axes.Both,
        Anchor = Anchor.TopCentre,
        Origin = Anchor.TopCentre,
        Y = 14,
        Depth = -30,
        Masking = true,
        CornerRadius = 8,
        Children =
        [
            new Box
            {
                RelativeSizeAxes = Axes.Both,
                Colour = Colour4.FromHex("#251C2C").Opacity(0.94f),
            },
            previewBadgeText = new SpriteText
            {
                Text = "●  LAZER SKIN WORKBENCH",
                Margin = new MarginPadding { Horizontal = 14, Vertical = 7 },
                Font = FontUsage.Default.With(size: 11, weight: "Bold"),
                Colour = Colour4.FromHex("#FFB7D5"),
            },
        ],
    };

    private void showWorkbench()
    {
        gameplayMode = false;
        mockupMode = false;
        if (player is not null)
            player.PauseForAcceptance();
        gameplayContainer?.Hide();
        rendererSemanticOverlay?.Hide();
        workbenchContainer?.Show();
        workbenchButton?.SetSelected(true);
        mockupButton?.SetSelected(false);
        gameplayButton?.SetSelected(false);
        if (previewBadgeText is not null)
            previewBadgeText.Text = "●  LAZER SKIN WORKBENCH";
        refreshWorkbench();
        updateDraftPresentation(
            "Choose a category from the Asset Library on the left, then click "
            + "an element in the middle to inspect and edit its full file family.");
    }

    private void showMockup()
    {
        if (!ensureGameplayPlayer())
        {
            updateDraftPresentation("Native lazer mockup is still loading.");
            return;
        }

        gameplayMode = false;
        mockupMode = true;
        workbenchContainer?.Hide();
        rendererSemanticOverlay?.Hide();
        gameplayContainer?.Show();
        workbenchButton?.SetSelected(false);
        mockupButton?.SetSelected(true);
        gameplayButton?.SetSelected(false);
        if (previewBadgeText is not null)
            previewBadgeText.Text = "●  NATIVE LAZER MOCKUP · PAUSED";
        seekNativeMockupWhenReady();
        updateDraftPresentation(
            "Gameplay Mockup uses the real lazer DrawableRuleset, paused where "
            + "its native slider, circles, cursor, follow circle, and HUD are "
            + "visible. Choose a category on the left and an element in the "
            + "middle to edit it.");
    }

    private void showGameplay()
    {
        if (!ensureGameplayPlayer())
        {
            updateDraftPresentation("Real gameplay is still loading.");
            return;
        }

        gameplayMode = true;
        mockupMode = false;
        workbenchContainer?.Hide();
        gameplayContainer?.Show();
        rendererSemanticOverlay?.Show();
        workbenchButton?.SetSelected(false);
        mockupButton?.SetSelected(false);
        gameplayButton?.SetSelected(true);
        if (previewBadgeText is not null)
            previewBadgeText.Text = "●  AUTHORITATIVE LAZER GAMEPLAY";

        player!.Restart();
        updateDraftPresentation(
            "Real gameplay visuals are active through lazer's drawable ruleset scene surface.");
    }

    private bool ensureGameplayPlayer()
    {
        if (osuRuleset is null || screenStack is null)
            return false;
        if (player is not null)
            return true;

        // This is intentionally not an autoplay/replay score. Studio drives
        // only time and cursor presentation; lazer owns the actual drawable
        // ruleset and skin resolution.
        SelectedMods.Value = [];
        player = new StudioScenePlayer();
        player.SetObjectPreviewScale(rendererObjectScale);
        player.ColourEditRequested += queueRendererColourEdit;
        screenStack.Push(player);
        return true;
    }

    private void seekNativeMockupWhenReady(int attempt = 0)
    {
        if (!mockupMode || player is null)
            return;
        if (player.CanSeekForAcceptance)
        {
            player.SeekAndPauseForAcceptance(
                StudioVisualAcceptancePlan.NativeMockupTime);
            return;
        }
        if (attempt >= 300)
        {
            updateDraftPresentation(
                "The native lazer mockup could not finish loading.");
            return;
        }
        Scheduler.AddDelayed(
            () => seekNativeMockupWhenReady(attempt + 1),
            50);
    }

    private void restartNativePreviewIfOpen()
    {
        if (player is null)
            return;

        player.Restart();
        if (mockupMode)
            seekNativeMockupWhenReady();
    }

    private void refreshWorkbench()
    {
        if (workbench?.IsLoaded == true)
            workbench.Rebuild();
        if (elementNavigator?.IsLoaded == true)
            elementNavigator.Rebuild();
    }

    private void selectAsset(string componentName)
    {
        if (!string.Equals(
                selectedAssetComponent,
                componentName,
                StringComparison.OrdinalIgnoreCase))
        {
            externalAssetWatcher?.Dispose();
            externalAssetWatcher = null;
            externalAssetPath = null;
            externalAssetFilename = null;
            externalAssetExpectedHash = null;
            externalAssetOpenedCopyHash = null;
            externalAssetRejectedHash = null;
            externalAssetChanged = false;
        }
        selectedAssetComponent = componentName;
        var family = draft is null
            ? []
            : assets.Family(draft.DraftId, componentName);
        var isAudio = family.Any(asset => asset.IsAudio)
                      || StudioSkinWorkbench.IsAudioComponent(componentName);
        elementNavigator?.SetSelectedComponent(componentName);
        var semanticPreviewShown = !rendererOnly
                                   && showStandaloneSemanticPreview(componentName, isAudio);
        if (!semanticPreviewShown && !gameplayMode && !mockupMode && !isAudio)
            workbench?.FocusComponent(componentName);
        setAssetActionsEnabled(true);
        if (selectedAssetText is not null)
        {
            selectedAssetText.Text =
                $"{componentName}\n{SkinDraftAssetService.VariantSummary(family)}";
            queueAudioAnalysis(componentName, family);
        }
        updateDraftPresentation(semanticPreviewShown
            ? $"Selected {componentName}. The middle canvas is showing its logical lazer context."
            : $"Selected {componentName}. Preview it in the middle and edit it with the controls on the right.");
    }

    private bool showStandaloneSemanticPreview(string componentName, bool isAudio)
    {
        var target = SkinStudioSemanticPreviewCatalog.Resolve(componentName);
        if (target.IsRaw || !ensureGameplayPlayer())
            return false;

        stopRendererAudio();
        if (isAudio)
            workbench?.StopAudioPreviews();
        rendererPreviewTarget = target;
        rendererScene = target.Scene;
        rendererInspectionFamily = target.FamilyId;
        rendererInspectionComponents.Clear();
        rendererInspectionComponents.Add(target.ComponentName);
        rendererInspectionTint = Colour4.White;
        rendererElementTints.Clear();
        rendererAutoMotion = target.Animation is
            SkinStudioAnimationPolicy.Native or
            SkinStudioAnimationPolicy.ScriptedLoop;
        rendererPlaying = rendererAutoMotion || target.IsAudio;
        updateRendererAssetProvenance();
        gameplayMode = false;
        mockupMode = true;
        workbenchContainer?.Hide();
        gameplayContainer?.Show();
        workbenchButton?.SetSelected(false);
        mockupButton?.SetSelected(true);
        gameplayButton?.SetSelected(false);
        if (previewBadgeText is not null)
        {
            previewBadgeText.Text =
                $"â—  SEMANTIC PREVIEW · {target.FamilyId.ToUpperInvariant()}";
        }
        configureRendererScene();
        if (target.IsAudio)
            startSemanticAudio(target);
        return true;
    }

    private void importAnyAsset()
    {
        pendingAssetTarget = null;
        presentFileSelector(
            assetSelector,
            "skin asset",
            [".png", ".jpg", ".jpeg", ".wav", ".mp3", ".ogg"],
            importAsset);
    }

    private void promptAssetFolder()
    {
        pathPromptOverlay?.Present(
            "Import asset folder",
            "Enter a folder containing PNG, JPG, WAV, MP3, or OGG files. Supported top-level files import in one undoable revision.",
            contract.WorkspacePath,
            importAssetFolder);
    }

    private bool importAssetFolder(string directory)
    {
        if (draft is null)
            return false;
        try
        {
            directory = Path.GetFullPath(directory);
            if (!Directory.Exists(directory))
                throw new DirectoryNotFoundException($"Asset folder was not found: {directory}");
            var supported = new HashSet<string>(
                [".png", ".jpg", ".jpeg", ".wav", ".mp3", ".ogg"],
                StringComparer.OrdinalIgnoreCase);
            var files = Directory.EnumerateFiles(directory)
                .Where(path => supported.Contains(Path.GetExtension(path)))
                .OrderBy(path => Path.GetFileName(path), StringComparer.OrdinalIgnoreCase)
                .ToArray();
            if (files.Length == 0)
                throw new InvalidDataException("The folder has no supported top-level skin assets.");
            createAutomaticEditBackup("multi-file asset import");
            draft = assets.ImportFiles(draft.DraftId, files);
            reloadActivePreview();
            updateDraftPresentation(
                $"Imported {files.Length} asset file(s) from {directory} in one revision.");
            return true;
        }
        catch (Exception ex)
        {
            Logger.Error(ex, "Multi-file skin asset import failed.");
            updateDraftPresentation($"Asset folder import failed: {ex.Message}");
            return false;
        }
    }

    private void presentFileSelector(
        ISystemFileSelector? selector,
        string purpose,
        IReadOnlyCollection<string> allowedExtensions,
        Action<string> selected)
    {
        ArgumentNullException.ThrowIfNull(allowedExtensions);
        ArgumentNullException.ThrowIfNull(selected);
        if (selector is not null)
        {
            selector.Present();
            return;
        }

        pathPromptOverlay?.Present(
            $"Choose {purpose}",
            $"The operating-system picker is unavailable in this embedded host. Enter a complete path ({string.Join(", ", allowedExtensions)}).",
            contract.WorkspacePath,
            path =>
            {
                path = Path.GetFullPath(path);
                if (!File.Exists(path))
                    throw new FileNotFoundException($"{purpose} was not found.", path);
                if (!allowedExtensions.Contains(
                        Path.GetExtension(path),
                        StringComparer.OrdinalIgnoreCase))
                {
                    throw new InvalidDataException(
                        $"{purpose} must use one of: {string.Join(", ", allowedExtensions)}.");
                }
                selected(path);
                return true;
            });
    }

    private void replaceSelectedAsset()
    {
        if (string.IsNullOrWhiteSpace(selectedAssetComponent))
        {
            updateDraftPresentation("Select an asset tile first.");
            return;
        }
        pendingAssetTarget = selectedAssetComponent;
        presentFileSelector(
            assetSelector,
            "replacement asset",
            [".png", ".jpg", ".jpeg", ".wav", ".mp3", ".ogg"],
            importAsset);
    }

    private void normalizeSelectedAudio()
    {
        if (draft is null || string.IsNullOrWhiteSpace(selectedAssetComponent))
            return;
        try
        {
            var before = assets.Family(draft.DraftId, selectedAssetComponent)
                .Count(asset => asset.IsAudio);
            createAutomaticEditBackup(
                $"normalizing {selectedAssetComponent} audio");
            draft = assets.NormalizeAudioFamily(
                draft.DraftId,
                selectedAssetComponent);
            reloadActivePreview();
            selectAsset(selectedAssetComponent);
            updateDraftPresentation(
                $"Normalized {before} audio variant(s) in {selectedAssetComponent} to bounded 16-bit PCM WAV. Undo is available.");
        }
        catch (Exception ex)
        {
            Logger.Error(ex, "Could not normalize selected skin audio.");
            updateDraftPresentation($"Audio normalization failed: {ex.Message}");
        }
    }

    private void openSelectedAudioTransport()
    {
        if (draft is null || string.IsNullOrWhiteSpace(selectedAssetComponent))
            return;
        try
        {
            var audio = assets.Family(draft.DraftId, selectedAssetComponent)
                .FirstOrDefault(asset => asset.IsAudio)
                        ?? throw new InvalidOperationException(
                            "The selected family has no draft-supplied audio.");
            var bytes = new SkinPackageService(drafts)
                .Materialize(draft.DraftId)[audio.Filename];
            var analysis = new SkinAudioTransformService().Analyze(bytes);
            audioTransportOverlay?.Present(audio.Filename, bytes, analysis);
        }
        catch (Exception ex)
        {
            Logger.Error(ex, "Could not open selected audio transport.");
            updateDraftPresentation($"Audio transport failed: {ex.Message}");
        }
    }

    private void openExtrasAudio(
        SkinExtraPackDescriptor pack,
        string targetFilename)
    {
        try
        {
            ArgumentNullException.ThrowIfNull(pack);
            var normalized =
                SkinDraftWorkspaceService.NormalizeSkinFilename(
                    targetFilename);
            if (!pack.Manifest.Files.Any(file =>
                    file.TargetFilename.Equals(
                        normalized,
                        StringComparison.OrdinalIgnoreCase))
                || !SkinMediaTypes.IsAudio(normalized))
            {
                throw new InvalidDataException(
                    "The requested audio file is not declared by this Extras pack.");
            }
            var root = Path.TrimEndingDirectorySeparator(
                           Path.GetFullPath(pack.DirectoryPath))
                       + Path.DirectorySeparatorChar;
            var path = Path.GetFullPath(Path.Combine(
                pack.DirectoryPath,
                normalized.Replace('/', Path.DirectorySeparatorChar)));
            if (!path.StartsWith(root, StringComparison.OrdinalIgnoreCase)
                || !File.Exists(path))
            {
                throw new InvalidDataException(
                    "The Extras audio path is missing or escapes its pack.");
            }
            var bytes = File.ReadAllBytes(path);
            var analysis = new SkinAudioTransformService().Analyze(bytes);
            extrasAudioBrowserOverlay?.Hide();
            audioTransportOverlay?.Present(normalized, bytes, analysis);
            updateDraftPresentation(
                $"Opened Extras audio {normalized} from {pack.Manifest.DisplayName}.");
        }
        catch (Exception ex)
        {
            Logger.Error(ex, "Could not open Extras audio transport.");
            updateDraftPresentation(
                $"Extras audio transport failed: {ex.Message}");
        }
    }

    private void queueAudioAnalysis(
        string componentName,
        IReadOnlyList<SkinDraftAsset> family)
    {
        if (draft is null)
            return;
        var audio = family.FirstOrDefault(asset => asset.IsAudio);
        if (audio is null)
            return;
        var draftId = draft.DraftId;
        _ = Task.Run(() =>
        {
            var files = new SkinPackageService(drafts).Materialize(draftId);
            var analysis = new SkinAudioTransformService().Analyze(files[audio.Filename]);
            return FormatAudioAnalysis(analysis);
        }).ContinueWith(task => Schedule(() =>
        {
            if (selectedAssetText is null
                || !string.Equals(
                    selectedAssetComponent,
                    componentName,
                    StringComparison.OrdinalIgnoreCase))
            {
                return;
            }
            if (task.IsCompletedSuccessfully)
                selectedAssetText.Text += $"\n{task.Result}";
            else
                selectedAssetText.Text += "\nAudio waveform unavailable";
        }), TaskScheduler.Default);
    }

    internal static string FormatAudioAnalysis(SkinAudioAnalysis analysis)
    {
        ArgumentNullException.ThrowIfNull(analysis);
        const string levels = "▁▂▃▄▅▆▇█";
        var peak = Math.Max(analysis.Peak, 0.0001f);
        var waveform = new string(analysis.Waveform.Select(value =>
        {
            var index = (int)Math.Round(
                Math.Clamp(value / peak, 0, 1) * (levels.Length - 1));
            return levels[index];
        }).ToArray());
        var duration = TimeSpan.FromMilliseconds(analysis.DurationMilliseconds);
        return FormattableString.Invariant(
            $"{analysis.SampleRate:N0} Hz · {analysis.Channels} ch · {duration:mm\\:ss\\.fff} · peak {analysis.Peak:0.000}\n{waveform}");
    }

    private void deleteSelectedAsset()
    {
        if (draft is null || string.IsNullOrWhiteSpace(selectedAssetComponent))
        {
            updateDraftPresentation("Select an asset tile first.");
            return;
        }
        try
        {
            var family = assets.Family(draft.DraftId, selectedAssetComponent);
            if (family.Count == 0)
            {
                updateDraftPresentation(
                    $"{selectedAssetComponent} is currently supplied by lazer fallback; there are no draft files to delete.");
                return;
            }
            createAutomaticEditBackup(
                $"deleting {selectedAssetComponent} family");
            draft = assets.DeleteFamily(draft.DraftId, selectedAssetComponent);
            reloadActivePreview();
            updateDraftPresentation(
                $"Deleted {family.Count} file(s) in {selectedAssetComponent}; lazer fallback is now visible. Undo is available.");
        }
        catch (Exception ex)
        {
            Logger.Error(ex, "Could not delete selected skin asset family.");
            updateDraftPresentation($"Delete failed: {ex.Message}");
        }
    }

    private void promptDeleteAnimationFrame()
    {
        if (draft is null || string.IsNullOrWhiteSpace(selectedAssetComponent))
            return;
        var frames = assets.Family(draft.DraftId, selectedAssetComponent)
            .Where(asset => asset.AnimationFrame is not null)
            .Select(asset => asset.AnimationFrame!.Value)
            .Distinct()
            .Order()
            .ToArray();
        if (frames.Length == 0)
        {
            updateDraftPresentation(
                $"{selectedAssetComponent} has no explicit animation frames.");
            return;
        }
        pathPromptOverlay?.Present(
            "Delete animation frame",
            $"Enter a frame number ({string.Join(", ", frames)}). Its 1× and @2x files are deleted together and Undo remains available.",
            frames[0].ToString(),
            deleteAnimationFrame);
    }

    private bool deleteAnimationFrame(string value)
    {
        if (draft is null || string.IsNullOrWhiteSpace(selectedAssetComponent))
            return false;
        if (!int.TryParse(value, out var frame) || frame < 0)
            throw new InvalidDataException("Enter a non-negative animation frame number.");
        try
        {
            var before = assets.Family(draft.DraftId, selectedAssetComponent)
                .Count(asset => asset.AnimationFrame == frame);
            createAutomaticEditBackup(
                $"deleting {selectedAssetComponent} frame {frame}");
            draft = assets.DeleteAnimationFrame(
                draft.DraftId,
                selectedAssetComponent,
                frame);
            reloadActivePreview();
            selectAsset(selectedAssetComponent);
            updateDraftPresentation(
                $"Deleted frame {frame} ({before} resolution file(s)) from {selectedAssetComponent}. Undo is available.");
            return true;
        }
        catch (Exception ex)
        {
            Logger.Error(ex, "Could not delete animation frame.");
            updateDraftPresentation($"Animation frame deletion failed: {ex.Message}");
            return false;
        }
    }

    private void promptInsertAnimationFrame()
    {
        if (!tryGetSelectedAnimationFrames(out var frames))
            return;
        pathPromptOverlay?.Present(
            "Insert / duplicate animation frame",
            $"Enter source,insert position. Existing frames at or after the position shift up. Available frames: {string.Join(", ", frames)}.",
            $"{frames[0]},{frames[^1] + 1}",
            insertAnimationFrame);
    }

    private bool insertAnimationFrame(string value)
    {
        if (draft is null || string.IsNullOrWhiteSpace(selectedAssetComponent))
            return false;
        var (source, target) = parseFramePair(value, "source,insert position");
        try
        {
            createAutomaticEditBackup(
                $"inserting {selectedAssetComponent} frame {target}");
            draft = assets.InsertAnimationFrame(
                draft.DraftId,
                selectedAssetComponent,
                source,
                target);
            reloadActivePreview();
            selectAsset(selectedAssetComponent);
            updateDraftPresentation(
                $"Inserted frame {target} from frame {source} in {selectedAssetComponent}. Later frames shifted together; Undo is available.");
            return true;
        }
        catch (Exception ex)
        {
            Logger.Error(ex, "Could not insert animation frame.");
            updateDraftPresentation($"Animation frame insertion failed: {ex.Message}");
            return false;
        }
    }

    private void promptMoveAnimationFrame()
    {
        if (!tryGetSelectedAnimationFrames(out var frames))
            return;
        pathPromptOverlay?.Present(
            "Move animation frame",
            $"Enter source,target. Intervening frames shift as one transaction. Available frames: {string.Join(", ", frames)}.",
            $"{frames[0]},{frames[^1]}",
            moveAnimationFrame);
    }

    private bool moveAnimationFrame(string value)
    {
        if (draft is null || string.IsNullOrWhiteSpace(selectedAssetComponent))
            return false;
        var (source, target) = parseFramePair(value, "source,target");
        try
        {
            createAutomaticEditBackup(
                $"moving {selectedAssetComponent} frame {source} to {target}");
            draft = assets.MoveAnimationFrame(
                draft.DraftId,
                selectedAssetComponent,
                source,
                target);
            reloadActivePreview();
            selectAsset(selectedAssetComponent);
            updateDraftPresentation(
                $"Moved frame {source} to {target} in {selectedAssetComponent}; Undo is available.");
            return true;
        }
        catch (Exception ex)
        {
            Logger.Error(ex, "Could not move animation frame.");
            updateDraftPresentation($"Animation frame move failed: {ex.Message}");
            return false;
        }
    }

    private bool tryGetSelectedAnimationFrames(out int[] frames)
    {
        frames = [];
        if (draft is null || string.IsNullOrWhiteSpace(selectedAssetComponent))
            return false;
        frames = assets.Family(draft.DraftId, selectedAssetComponent)
            .Where(asset => asset.AnimationFrame is not null)
            .Select(asset => asset.AnimationFrame!.Value)
            .Distinct()
            .Order()
            .ToArray();
        if (frames.Length > 0)
            return true;
        updateDraftPresentation(
            $"{selectedAssetComponent} has no explicit animation frames.");
        return false;
    }

    private static (int Source, int Target) parseFramePair(
        string value,
        string expected)
    {
        var parts = value.Split(',', StringSplitOptions.TrimEntries);
        if (parts.Length != 2
            || !int.TryParse(parts[0], out var source)
            || !int.TryParse(parts[1], out var target)
            || source < 0
            || target < 0)
        {
            throw new InvalidDataException(
                $"Enter two non-negative frame numbers as {expected}.");
        }
        return (source, target);
    }

    private void promptAnimationFrameRate()
    {
        if (draft is null)
            return;
        try
        {
            var files = new SkinPackageService(drafts).Materialize(draft.DraftId);
            var document = SkinIniDocument.Parse(files["skin.ini"]);
            pathPromptOverlay?.Present(
                "Skin animation FPS",
                "Enter a whole-number frame rate. Use -1 for lazer/legacy automatic timing.",
                document.GetValue("General", "AnimationFramerate") ?? "-1",
                setAnimationFrameRate);
        }
        catch (Exception ex)
        {
            Logger.Error(ex, "Could not read skin animation frame rate.");
            updateDraftPresentation($"Animation timing could not be opened: {ex.Message}");
        }
    }

    private bool setAnimationFrameRate(string value)
    {
        if (draft is null)
            return false;
        if (!int.TryParse(value, out var frameRate)
            || frameRate is < -1 or > 1000)
        {
            throw new InvalidDataException(
                "Animation frame rate must be -1 or a whole number from 0 through 1000.");
        }
        var files = new SkinPackageService(drafts).Materialize(draft.DraftId);
        var document = SkinIniDocument.Parse(files["skin.ini"]);
        document.SetValue(
            "General",
            "AnimationFramerate",
            frameRate.ToString(System.Globalization.CultureInfo.InvariantCulture));
        var committed = commitSkinIni(document.ToBytes(), structured: true);
        if (committed)
        {
            updateDraftPresentation(
                frameRate == -1
                    ? "Skin animation timing now uses lazer's automatic legacy behaviour."
                    : $"Skin animation timing is now {frameRate} FPS.");
        }
        return committed;
    }

    private void resetSelectedAsset()
    {
        if (draft is null || string.IsNullOrWhiteSpace(selectedAssetComponent))
        {
            updateDraftPresentation("Select an asset tile first.");
            return;
        }
        try
        {
            var before = draft.Changes.Count;
            createAutomaticEditBackup(
                $"resetting {selectedAssetComponent} family");
            draft = assets.ResetFamily(draft.DraftId, selectedAssetComponent);
            if (draft.Changes.Count == before)
            {
                updateDraftPresentation(
                    $"{selectedAssetComponent} has no staged changes to reset.");
                return;
            }
            reloadActivePreview();
            updateDraftPresentation(
                $"Reset all staged changes for {selectedAssetComponent}. Undo is available.");
        }
        catch (Exception ex)
        {
            Logger.Error(ex, "Could not reset selected skin asset family.");
            updateDraftPresentation($"Reset failed: {ex.Message}");
        }
    }

    private void copySelectedAsset()
    {
        if (draft is null || string.IsNullOrWhiteSpace(selectedAssetComponent))
        {
            updateDraftPresentation("Select a draft-supplied asset tile first.");
            return;
        }
        try
        {
            assetClipboard = assets.CopyFamily(
                draft.DraftId,
                selectedAssetComponent);
            pasteAssetButton?.SetEnabled(true);
            updateDraftPresentation(
                $"Copied {assetClipboard.Files.Count} file(s) from {assetClipboard.SourceComponentName}. Select a destination tile and paste.");
        }
        catch (Exception ex)
        {
            Logger.Error(ex, "Could not copy selected skin asset family.");
            updateDraftPresentation($"Copy failed: {ex.Message}");
        }
    }

    private void pasteAssetIntoSelected()
    {
        if (draft is null
            || assetClipboard is null
            || string.IsNullOrWhiteSpace(selectedAssetComponent))
        {
            updateDraftPresentation(
                "Copy a draft-supplied family, then select a destination tile.");
            return;
        }
        try
        {
            var source = assetClipboard.SourceComponentName;
            createAutomaticEditBackup(
                $"pasting {source} into {selectedAssetComponent}");
            draft = assets.PasteFamily(
                draft.DraftId,
                selectedAssetComponent,
                assetClipboard);
            reloadActivePreview();
            updateDraftPresentation(
                $"Pasted the complete {source} family into {selectedAssetComponent} in one undoable revision.");
        }
        catch (Exception ex)
        {
            Logger.Error(ex, "Could not paste copied skin asset family.");
            updateDraftPresentation($"Paste failed: {ex.Message}");
        }
    }

    private void resetFilteredCategory()
    {
        if (draft is null || workbench is null)
            return;
        var category = workbench.ActiveCategoryTitle;
        var components = workbench.ActiveCategoryComponents();
        if (category is null || components.Count == 0)
        {
            updateDraftPresentation(
                "Choose a specific category with the workbench Category control first.");
            return;
        }
        try
        {
            var historyCount = draft.History.Count;
            createAutomaticEditBackup($"resetting {category} category");
            draft = assets.ResetFamilies(
                draft.DraftId,
                components,
                $"Reset {category} category");
            if (draft.History.Count == historyCount)
            {
                updateDraftPresentation(
                    $"{category} has no staged changes to reset.");
                return;
            }
            reloadActivePreview();
            updateDraftPresentation(
                $"Reset all staged changes in {category} in one undoable revision.");
        }
        catch (Exception ex)
        {
            Logger.Error(ex, "Could not reset the selected workbench category.");
            updateDraftPresentation($"Category reset failed: {ex.Message}");
        }
    }

    private void exportSelectedAsset()
    {
        if (draft is null || string.IsNullOrWhiteSpace(selectedAssetComponent))
        {
            updateDraftPresentation("Select an asset tile first.");
            return;
        }
        var defaultDestination = Path.Combine(
            contract.WorkspacePath,
            "element-exports",
            $"{sanitizeFilename(selectedAssetComponent)}-{DateTimeOffset.UtcNow:yyyyMMdd-HHmmss}");
        pathPromptOverlay?.Present(
            "Export selected family",
            "Enter a destination folder. The detected osu!lazer player root is always blocked.",
            defaultDestination,
            exportSelectedAssetTo);
    }

    private bool exportSelectedAssetTo(string destination)
    {
        if (draft is null || string.IsNullOrWhiteSpace(selectedAssetComponent))
            return false;
        try
        {
            destination = Path.GetFullPath(destination);
            if (!SkinStudioWriteBoundary.IsNormalWriteAllowed(
                    contract.PlayerRoot,
                    destination))
            {
                throw new InvalidDataException(
                    "Export is blocked because the destination overlaps the detected osu!lazer player root.");
            }
            var written = assets.ExportFamily(
                draft.DraftId,
                selectedAssetComponent,
                destination);
            updateDraftPresentation(
                written.Count == 0
                    ? $"{selectedAssetComponent} is fallback-only; no draft file was exported."
                    : $"Exported {written.Count} file(s) to {destination}.");
            return true;
        }
        catch (Exception ex)
        {
            Logger.Error(ex, "Could not export selected skin asset family.");
            updateDraftPresentation($"Asset export failed: {ex.Message}");
            return false;
        }
    }

    private void transformSelectedImage()
    {
        if (draft is null || string.IsNullOrWhiteSpace(selectedAssetComponent))
        {
            updateDraftPresentation("Select a draft-supplied image tile first.");
            return;
        }
        if (!assets.Family(draft.DraftId, selectedAssetComponent).Any(asset => asset.IsImage))
        {
            updateDraftPresentation(
                $"{selectedAssetComponent} has no draft-supplied image files to transform.");
            return;
        }
        var animationFrames = assets.Family(draft.DraftId, selectedAssetComponent)
            .Where(asset => asset.IsImage && asset.AnimationFrame is not null)
            .Select(asset => asset.AnimationFrame!.Value)
            .Distinct()
            .Order()
            .ToArray();
        imageTransformOverlay?.Present(
            selectedAssetComponent,
            animationFrames,
            applySelectedImageTransform);
    }

    private void applyQuickRecolour(SkinImageTransformMode mode)
    {
        if (quickColour is null
            || !StudioImageTransformOverlay.TryParseHexColour(
                quickColour.Current.Value,
                out var colour))
        {
            updateDraftPresentation(
                "Enter a recolour value as #RRGGBB, for example #FFB7D5.");
            return;
        }
        applySelectedImageTransform(
            new SkinImageTransform(mode, colour, 0, 1, 1),
            SkinImageTransformScope.FullFamily,
            null);
    }

    private bool applySelectedImageTransform(
        SkinImageTransform transform,
        SkinImageTransformScope scope,
        int? animationFrame)
    {
        if (draft is null || string.IsNullOrWhiteSpace(selectedAssetComponent))
            return false;
        try
        {
            createAutomaticEditBackup(
                $"transforming {selectedAssetComponent}");
            draft = assets.TransformImageFamily(
                draft.DraftId,
                selectedAssetComponent,
                transform,
                scope,
                animationFrame);
            reloadActivePreview();
            updateDraftPresentation(
                $"Applied {transform.Mode} to {selectedAssetComponent} ({scope}"
                + (animationFrame is int frame ? $", frame {frame}" : "")
                + "). Undo is available.");
            return true;
        }
        catch (Exception ex)
        {
            Logger.Error(ex, "Could not transform selected skin image family.");
            updateDraftPresentation($"Image transform failed: {ex.Message}");
            return false;
        }
    }

    private void extractDraftToExtras()
    {
        if (draft is null || extrasExtractionOverlay is null)
            return;
        try
        {
            var files = new SkinPackageService(drafts)
                .Materialize(draft.DraftId)
                .Select(pair => new SkinExtractionFile(pair.Key, pair.Value))
                .ToArray();
            var source = new SkinExtrasExtractionService().BuildSource(
                draft.Name,
                $"Kumori draft {draft.DraftId:N}",
                files);
            extrasExtractionOverlay.Present(source);
            updateDraftPresentation(
                $"Choose which families from “{draft.Name}” to add to the Extras library.");
        }
        catch (Exception ex)
        {
            Logger.Error(ex, "Could not prepare draft Extras extraction.");
            updateDraftPresentation($"Extras extraction could not start: {ex.Message}");
        }
    }

    private void extractSelectedAssetToExtras()
    {
        if (string.IsNullOrWhiteSpace(selectedAssetComponent))
            return;
        extractComponentsToExtras(
            [selectedAssetComponent],
            $"selected family {selectedAssetComponent}");
    }

    private void extractFilteredCategoryToExtras()
    {
        var title = workbench?.ActiveCategoryTitle;
        var components = workbench?.ActiveCategoryComponents() ?? [];
        if (string.IsNullOrWhiteSpace(title) || components.Count == 0)
            return;
        extractComponentsToExtras(
            components,
            $"filtered category {title}");
    }

    private void extractComponentsToExtras(
        IReadOnlyCollection<string> components,
        string scope)
    {
        if (draft is null || extrasExtractionOverlay is null)
            return;
        try
        {
            var files = new SkinPackageService(drafts)
                .Materialize(draft.DraftId)
                .Select(pair => new SkinExtractionFile(pair.Key, pair.Value))
                .ToArray();
            var extraction = new SkinExtrasExtractionService();
            var source = extraction.BuildSource(
                draft.Name,
                $"Kumori draft {draft.DraftId:N}",
                files);
            var families = extraction.Analyze(source);
            var selectedIds =
                StudioExtrasExtractionOverlay.SelectionIdsForComponents(
                    families,
                    components);
            if (selectedIds.Count == 0)
            {
                updateDraftPresentation(
                    $"No complete Extras family is available for {scope}.");
                return;
            }
            extrasExtractionOverlay.Present(source, selectedIds);
            updateDraftPresentation(
                $"Prepared {selectedIds.Count} Extras family/families for {scope}; review and extract.");
        }
        catch (Exception ex)
        {
            Logger.Error(ex, "Could not prepare selected Extras extraction.");
            updateDraftPresentation(
                $"Selected Extras extraction could not start: {ex.Message}");
        }
    }

    private void extractSkinPackageToExtras(string path)
    {
        try
        {
            var source = new SkinExtrasExtractionService().ReadOsk(path);
            extrasExtractionOverlay?.Present(source);
            updateDraftPresentation(
                $"Choose which families from “{source.DisplayName}” to add to Extras.");
        }
        catch (Exception ex)
        {
            Logger.Error(ex, "Could not analyze skin package for Extras extraction.");
            updateDraftPresentation($"Skin extraction failed: {ex.Message}");
        }
    }

    private void promptExtrasFolder()
    {
        pathPromptOverlay?.Present(
            "Extract skin folder",
            "Enter the full path to a legacy skin folder. Kumori reads only root-level skin assets.",
            "",
            path =>
            {
                var source = new SkinExtrasExtractionService().ReadFolder(path);
                extrasExtractionOverlay?.Present(source);
                updateDraftPresentation(
                    $"Choose which families from “{source.DisplayName}” to add to Extras.");
                return true;
            });
    }

    private void applyExtrasPack(
        SkinExtraPackDescriptor pack,
        SkinDraftExtrasSelection selection)
    {
        if (draft is null)
            return;
        try
        {
            var backup = backups.Create(
                draft.DraftId,
                $"Automatic backup before Extras/{pack.Manifest.DisplayName}",
                studioPreferences.BackupRetention);
            backups.Verify(backup);
            draft = new SkinDraftExtrasService(drafts).StageSelection(
                draft.DraftId,
                pack,
                selection);
            SkinExtrasLibraryStateStore.Update(
                extrasRoot,
                pack.Manifest.Fingerprint,
                state => state.LastUsedUtc = DateTimeOffset.UtcNow);
            extrasOverlay?.Hide();
            reloadActivePreview();
            updateDraftPresentation(
                $"Applied {selection.TargetFilenames.Count} file(s) and {selection.IniPatch.Count} setting(s) "
                + $"from Extras pack “{pack.Manifest.DisplayName}” in one revision after backup {backup.BackupId}.");
        }
        catch (Exception ex)
        {
            Logger.Error(ex, "Could not apply Extras pack to Skin Studio draft.");
            updateDraftPresentation($"Extras apply stopped: {ex.Message}");
        }
    }

    private string compareExtrasPack(
        SkinExtraPackDescriptor pack,
        SkinDraftExtrasSelection selection)
    {
        if (draft is null)
            return "Open a draft before comparing Extras.";
        var comparison = new SkinDraftExtrasComparisonService(drafts).Compare(
            draft.DraftId,
            pack,
            selection);
        return $"{pack.Manifest.DisplayName}: {comparison.Summary}";
    }

    private void importExtrasPackage(string path)
    {
        try
        {
            var result = SkinExtraPortablePackage.Import(path, extrasRoot);
            extrasOverlay?.RefreshLibrary();
            updateDraftPresentation(result.WasDuplicate
                ? result.Message
                : $"Imported Extras package “{result.Pack.Manifest.DisplayName}” and verified its contents.");
        }
        catch (Exception ex)
        {
            Logger.Error(ex, "Could not import Extras package.");
            updateDraftPresentation($"Extras import failed: {ex.Message}");
        }
    }

    private void exportExtrasPack(SkinExtraPackDescriptor pack)
    {
        try
        {
            var health = SkinExtraPackValidator.Validate(pack);
            if (!health.IsHealthy)
            {
                throw new InvalidDataException(
                    $"Pack has {health.Errors} validation error(s). Repair it before export.");
            }
            var directory = Path.Combine(contract.WorkspacePath, "extras-exports");
            Directory.CreateDirectory(directory);
            var destination = Path.Combine(
                directory,
                $"{sanitizeFilename(pack.Manifest.DisplayName)}-{DateTimeOffset.UtcNow:yyyyMMdd-HHmmss}.zip");
            SkinExtraPortablePackage.Export(pack, destination);
            updateDraftPresentation(
                $"Exported verified Extras package to {destination}.");
        }
        catch (Exception ex)
        {
            Logger.Error(ex, "Could not export Extras package.");
            updateDraftPresentation($"Extras export failed: {ex.Message}");
        }
    }

    private void deleteExtrasPack(SkinExtraPackDescriptor pack)
    {
        try
        {
            var deleted = new SkinExtraPackTrashService().DeleteRecoverably(
                extrasRoot,
                pack);
            updateDraftPresentation(
                $"Moved Extras pack “{deleted.DisplayName}” to recoverable library trash.");
        }
        catch (Exception ex)
        {
            Logger.Error(ex, "Could not move Extras pack to trash.");
            updateDraftPresentation($"Extras delete stopped: {ex.Message}");
        }
    }

    private void restoreLatestExtrasPack()
    {
        try
        {
            var service = new SkinExtraPackTrashService();
            var deleted = service.List(extrasRoot).FirstOrDefault();
            if (deleted is null)
            {
                updateDraftPresentation("Extras library trash is empty.");
                return;
            }
            service.Restore(extrasRoot, deleted.TrashId);
            extrasOverlay?.RefreshLibrary();
            updateDraftPresentation(
                $"Restored Extras pack “{deleted.DisplayName}” to its original library location.");
        }
        catch (Exception ex)
        {
            Logger.Error(ex, "Could not restore Extras pack.");
            updateDraftPresentation($"Extras restore failed: {ex.Message}");
        }
    }

    private void editSelectedAssetExternally() =>
        prepareSelectedAssetExternalEdit(openExternal: true);

    private void prepareSelectedAssetExternalEdit(bool openExternal)
    {
        if (draft is null
            || (openExternal && gameHost is null)
            || string.IsNullOrWhiteSpace(selectedAssetComponent))
        {
            updateDraftPresentation("Select a draft-supplied asset tile first.");
            return;
        }
        try
        {
            var family = assets.Family(draft.DraftId, selectedAssetComponent);
            var primary = family
                              .OrderBy(asset => asset.IsTwoX)
                              .ThenBy(asset => asset.AnimationFrame.HasValue)
                              .ThenBy(asset => asset.Filename, StringComparer.OrdinalIgnoreCase)
                              .FirstOrDefault()
                          ?? throw new InvalidOperationException(
                              "This element is supplied only by lazer fallback.");
            var files = new SkinPackageService(drafts).Materialize(draft.DraftId);
            var directory = Path.Combine(
                contract.WorkspacePath,
                "external-edit",
                draft.DraftId.ToString("N"),
                "assets");
            externalAssetPath = Path.Combine(
                directory,
                primary.Filename.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(externalAssetPath)!);
            File.WriteAllBytes(externalAssetPath, files[primary.Filename]);
            externalAssetFilename = primary.Filename;
            externalAssetExpectedHash = primary.ContentHash;
            externalAssetOpenedCopyHash =
                SkinDraftWorkspaceService.Hash(files[primary.Filename]);
            externalAssetRejectedHash = null;
            externalAssetChanged = false;
            watchExternalAsset(externalAssetPath);
            applyExternalAssetButton?.SetEnabled(false);
            if (openExternal)
                gameHost!.OpenFileExternally(externalAssetPath);
            updateDraftPresentation(
                $"Opened isolated {primary.Filename}. Kumori is watching the copy and will validate it before apply.");
        }
        catch (Exception ex)
        {
            Logger.Error(ex, "Could not open selected skin asset externally.");
            updateDraftPresentation($"External edit failed: {ex.Message}");
        }
    }

    private void applyExternalAssetEdit()
    {
        if (draft is null
            || string.IsNullOrWhiteSpace(externalAssetPath)
            || string.IsNullOrWhiteSpace(externalAssetFilename)
            || !File.Exists(externalAssetPath))
        {
            updateDraftPresentation("Open a selected asset externally first.");
            return;
        }
        try
        {
            var materialized = new SkinPackageService(drafts).Materialize(draft.DraftId);
            if (!materialized.TryGetValue(externalAssetFilename, out var current)
                || !SkinDraftWorkspaceService.Hash(current).Equals(
                    externalAssetExpectedHash,
                    StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException(
                    "The draft asset changed after external editing began. Reopen it to avoid overwriting newer work.");
            }
            var bytes = File.ReadAllBytes(externalAssetPath);
            if (SkinMediaTypes.IsImage(externalAssetFilename))
            {
                var validated = SkinMediaValidationService.ValidateImage(
                    externalAssetFilename,
                    bytes);
                if (!validated.HasVisiblePixels)
                {
                    updateDraftPresentation(
                        $"Warning: {externalAssetFilename} is a valid "
                        + $"{validated.Width}x{validated.Height} image but is fully transparent.");
                }
            }
            createAutomaticEditBackup(
                $"applying external edit to {externalAssetFilename}");
            draft = drafts.StageFile(
                draft.DraftId,
                externalAssetFilename,
                bytes,
                externalAssetExpectedHash,
                $"Apply external edit to {externalAssetFilename}");
            externalAssetExpectedHash = SkinDraftWorkspaceService.Hash(bytes);
            externalAssetOpenedCopyHash = externalAssetExpectedHash;
            externalAssetRejectedHash = null;
            externalAssetChanged = false;
            reloadActivePreview();
            updateDraftPresentation(
                $"Applied, validated, and journalled external edit to {externalAssetFilename}.");
        }
        catch (Exception ex)
        {
            Logger.Error(ex, "Could not apply external skin asset edit.");
            try
            {
                externalAssetRejectedHash =
                    string.IsNullOrWhiteSpace(externalAssetPath)
                    || !File.Exists(externalAssetPath)
                        ? null
                        : SkinDraftWorkspaceService.Hash(
                            File.ReadAllBytes(externalAssetPath));
            }
            catch
            {
                externalAssetRejectedHash = null;
            }
            updateDraftPresentation($"External edit stopped: {ex.Message}");
        }
    }

    private void watchExternalAsset(string path)
    {
        externalAssetWatcher?.Dispose();
        externalAssetWatcher = new FileSystemWatcher(
            Path.GetDirectoryName(path)!,
            Path.GetFileName(path))
        {
            NotifyFilter = NotifyFilters.FileName
                           | NotifyFilters.LastWrite
                           | NotifyFilters.Size
                           | NotifyFilters.CreationTime,
            EnableRaisingEvents = true,
        };
        externalAssetWatcher.Changed += externalAssetWatcherChanged;
        externalAssetWatcher.Created += externalAssetWatcherChanged;
        externalAssetWatcher.Renamed += externalAssetWatcherChanged;
    }

    private void externalAssetWatcherChanged(
        object sender,
        FileSystemEventArgs args)
    {
        try
        {
            Scheduler.AddDelayed(refreshExternalAssetWatchState, 175);
        }
        catch (ObjectDisposedException)
        {
        }
    }

    private void refreshExternalAssetWatchState()
    {
        if (string.IsNullOrWhiteSpace(externalAssetPath)
            || !File.Exists(externalAssetPath))
        {
            externalAssetChanged = false;
            applyExternalAssetButton?.SetEnabled(false);
            updateDraftPresentation(
                "The isolated external-edit copy was removed. Reopen the selected asset to continue.");
            return;
        }
        try
        {
            var hash = SkinDraftWorkspaceService.Hash(
                File.ReadAllBytes(externalAssetPath));
            externalAssetChanged = !hash.Equals(
                externalAssetOpenedCopyHash,
                StringComparison.OrdinalIgnoreCase);
            applyExternalAssetButton?.SetEnabled(externalAssetChanged);
            if (externalAssetChanged)
            {
                if (hash.Equals(
                        externalAssetRejectedHash,
                        StringComparison.OrdinalIgnoreCase))
                {
                    return;
                }
                externalAssetRejectedHash = null;
                updateDraftPresentation(
                    $"External changes detected for {externalAssetFilename}. Apply when the editor has finished saving.");
            }
        }
        catch (IOException)
        {
            Scheduler.AddDelayed(refreshExternalAssetWatchState, 250);
        }
        catch (UnauthorizedAccessException)
        {
            Scheduler.AddDelayed(refreshExternalAssetWatchState, 250);
        }
    }

    private void reloadActivePreview()
    {
        selectDraftSkin(announceCompletion: false);
        if (gameplayMode || mockupMode)
            restartNativePreviewIfOpen();
        else
            refreshWorkbench();
    }

    private void setAssetActionsEnabled(bool enabled)
    {
        if (enabled)
        {
            updateAssetActionStates();
            return;
        }
        replaceAssetButton?.SetEnabled(false);
        deleteAssetButton?.SetEnabled(false);
        resetAssetButton?.SetEnabled(false);
        copyAssetButton?.SetEnabled(false);
        pasteAssetButton?.SetEnabled(false);
        transformAssetButton?.SetEnabled(false);
        quickColorizeButton?.SetEnabled(false);
        quickTintButton?.SetEnabled(false);
        normalizeAudioButton?.SetEnabled(false);
        audioTransportButton?.SetEnabled(false);
        deleteAnimationFrameButton?.SetEnabled(false);
        insertAnimationFrameButton?.SetEnabled(false);
        moveAnimationFrameButton?.SetEnabled(false);
        exportAssetButton?.SetEnabled(false);
        externalEditAssetButton?.SetEnabled(false);
        applyExternalAssetButton?.SetEnabled(false);
        addSelectedToExtrasButton?.SetEnabled(false);
        discardSelectedButton?.SetEnabled(false);
    }

    private void updateAssetActionStates()
    {
        var hasSelection = draft is not null
                           && !string.IsNullOrWhiteSpace(selectedAssetComponent);
        if (!hasSelection)
        {
            setAssetActionsEnabled(false);
            return;
        }
        try
        {
            var family = assets.Family(
                draft!.DraftId,
                selectedAssetComponent!);
            var hasFamily = family.Count > 0;
            var hasStagedChange = draft.Changes.Any(change =>
                SkinDraftAssetService.ComponentName(change.Filename).Equals(
                    selectedAssetComponent,
                    StringComparison.OrdinalIgnoreCase));
            replaceAssetButton?.SetEnabled(assetSelector is not null);
            deleteAssetButton?.SetEnabled(hasFamily);
            resetAssetButton?.SetEnabled(hasStagedChange);
            discardSelectedButton?.SetEnabled(hasStagedChange);
            copyAssetButton?.SetEnabled(hasFamily);
            pasteAssetButton?.SetEnabled(assetClipboard is not null);
            var hasImage = family.Any(asset => asset.IsImage);
            transformAssetButton?.SetEnabled(hasImage);
            quickColorizeButton?.SetEnabled(hasImage);
            quickTintButton?.SetEnabled(hasImage);
            normalizeAudioButton?.SetEnabled(family.Any(asset => asset.IsAudio));
            audioTransportButton?.SetEnabled(family.Any(asset => asset.IsAudio));
            deleteAnimationFrameButton?.SetEnabled(
                family.Any(asset => asset.IsImage && asset.AnimationFrame is not null));
            insertAnimationFrameButton?.SetEnabled(
                family.Any(asset => asset.IsImage && asset.AnimationFrame is not null));
            moveAnimationFrameButton?.SetEnabled(
                family.Where(asset => asset.IsImage)
                    .Select(asset => asset.AnimationFrame)
                    .Where(frame => frame is not null)
                    .Distinct()
                    .Count() > 1);
            exportAssetButton?.SetEnabled(hasFamily);
            externalEditAssetButton?.SetEnabled(hasFamily && gameHost is not null);
            addSelectedToExtrasButton?.SetEnabled(hasFamily);
            applyExternalAssetButton?.SetEnabled(
                !string.IsNullOrWhiteSpace(externalAssetPath)
                && !string.IsNullOrWhiteSpace(externalAssetFilename)
                && File.Exists(externalAssetPath)
                && externalAssetChanged);
        }
        catch
        {
            setAssetActionsEnabled(false);
        }
    }

    private void clearAssetSelection()
    {
        externalAssetWatcher?.Dispose();
        externalAssetWatcher = null;
        selectedAssetComponent = null;
        pendingAssetTarget = null;
        externalAssetPath = null;
        externalAssetFilename = null;
        externalAssetExpectedHash = null;
        externalAssetOpenedCopyHash = null;
        externalAssetRejectedHash = null;
        externalAssetChanged = false;
        externalSkinIniPath = null;
        elementNavigator?.SetSelectedComponent(null);
        setAssetActionsEnabled(false);
        if (selectedAssetText is not null)
            selectedAssetText.Text = "Click an element tile";
    }

    private void duplicateDraft()
    {
        if (draft is null)
            return;
        try
        {
            draft = drafts.Duplicate(draft.DraftId);
            clearAssetSelection();
            selectDraftSkin();
            showWorkbench();
            updateDraftPresentation($"Created independent draft “{draft.Name}”.");
        }
        catch (Exception ex)
        {
            Logger.Error(ex, "Could not duplicate Skin Studio draft.");
            updateDraftPresentation($"Duplicate failed: {ex.Message}");
        }
    }

    private void createBlankDraft()
    {
        identityOverlay?.Present(
            "Create blank draft",
            "New Kumori Skin",
            "Kumori",
            (name, creator) =>
            {
                try
                {
                    draft = drafts.Create(name, creator);
                    clearAssetSelection();
                    selectDraftSkin();
                    showWorkbench();
                    updateDraftPresentation($"Created isolated blank draft “{draft.Name}”.");
                    return true;
                }
                catch (Exception ex)
                {
                    Logger.Error(ex, "Could not create a blank Skin Studio draft.");
                    updateDraftPresentation($"Create failed: {ex.Message}");
                    return false;
                }
            });
    }

    private void createDraftFromExtras()
    {
        identityOverlay?.Present(
            "Create draft from Extras",
            "New Kumori Skin",
            "Kumori",
            (name, creator) =>
            {
                try
                {
                    draft = drafts.Create(name, creator);
                    clearAssetSelection();
                    selectDraftSkin();
                    showWorkbench();
                    extrasOverlay?.Present();
                    updateDraftPresentation(
                        $"Created isolated draft “{draft.Name}”. Choose its first Extras family.");
                    return true;
                }
                catch (Exception ex)
                {
                    Logger.Error(ex, "Could not create draft from Extras.");
                    updateDraftPresentation($"Create from Extras failed: {ex.Message}");
                    return false;
                }
            });
    }

    private void showExtrasCompositionReadiness()
    {
        if (draft is null)
            return;
        try
        {
            var files = new SkinPackageService(drafts).Materialize(
                draft.DraftId);
            extrasCompositionOverlay?.Present(draft, files);
            var summary = StudioExtrasCompositionSummary.Build(draft, files);
            updateDraftPresentation(
                summary.ExportReady
                    ? $"Extras composition is export-ready with "
                      + $"{summary.FamilyCount} complete family/families."
                    : "Extras composition needs at least one complete visual family before publishing.");
        }
        catch (Exception ex)
        {
            Logger.Error(ex, "Could not build Extras composition summary.");
            updateDraftPresentation(
                $"Extras readiness summary failed: {ex.Message}");
        }
    }

    private void renameDraft()
    {
        if (draft is null)
            return;
        identityOverlay?.Present(
            "Rename skin / author",
            draft.Name,
            draft.Creator,
            (name, creator) =>
            {
                try
                {
                    createAutomaticEditBackup("renaming skin and author");
                    draft = drafts.UpdateIdentity(draft.DraftId, name, creator);
                    reloadActivePreview();
                    updateDraftPresentation(
                        $"Updated identity to “{draft.Name}” by “{draft.Creator}” and journalled skin.ini.");
                    return true;
                }
                catch (Exception ex)
                {
                    Logger.Error(ex, "Could not update Skin Studio draft identity.");
                    updateDraftPresentation($"Identity update failed: {ex.Message}");
                    return false;
                }
            });
    }

    private void deleteDraftRecoverably()
    {
        if (draft is null || deleteDraftButton is null)
            return;
        if (Time.Current < deleteDraftArmedUntil)
        {
            try
            {
                var safetyBackup = backups.Create(
                    draft.DraftId,
                    "Automatic backup before recoverable draft deletion",
                    studioPreferences.BackupRetention);
                backups.Verify(safetyBackup);
                var deleted = drafts.DeleteRecoverably(draft.DraftId);
                draft = drafts.List().FirstOrDefault()
                        ?? drafts.Create("New Kumori Skin", "Kumori");
                clearAssetSelection();
                deleteDraftArmedUntil = 0;
                deleteDraftButton.SetText("Move draft to trash");
                selectDraftSkin();
                showWorkbench();
                updateDraftPresentation(
                    $"Moved “{deleted.Name}” to recoverable trash. Safety backup: {safetyBackup.BackupId}.");
            }
            catch (Exception ex)
            {
                Logger.Error(ex, "Could not move Skin Studio draft to trash.");
                updateDraftPresentation($"Delete stopped: {ex.Message}");
            }
            return;
        }

        deleteDraftArmedUntil = Time.Current + 5000;
        deleteDraftButton.SetText("Confirm move to trash");
        updateDraftPresentation(
            $"Click again within five seconds to move “{draft.Name}” to recoverable trash.");
        Scheduler.AddDelayed(() =>
        {
            if (Time.Current < deleteDraftArmedUntil)
                return;
            deleteDraftButton?.SetText("Move draft to trash");
        }, 5100);
    }

    private void restoreLastDeletedDraft()
    {
        try
        {
            var deleted = drafts.ListDeleted().FirstOrDefault();
            if (deleted is null)
            {
                updateDraftPresentation("Recoverable draft trash is empty.");
                return;
            }
            draft = drafts.RestoreDeleted(deleted.TrashName);
            clearAssetSelection();
            selectDraftSkin();
            showWorkbench();
            updateDraftPresentation($"Restored draft “{draft.Name}”.");
        }
        catch (Exception ex)
        {
            Logger.Error(ex, "Could not restore deleted Skin Studio draft.");
            updateDraftPresentation($"Restore failed: {ex.Message}");
        }
    }

    private void recoverInterruptedDraft()
    {
        try
        {
            var candidate = drafts.ListRecoveryCandidates()
                .FirstOrDefault(item => item.PendingManifestValid);
            if (candidate is null)
            {
                updateDraftPresentation(
                    "No valid interrupted-save manifest is available. The Studio did not modify any damaged draft.");
                return;
            }
            draft = drafts.RecoverPendingManifest(candidate.DirectoryName);
            clearAssetSelection();
            selectDraftSkin();
            showWorkbench();
            updateDraftPresentation(
                $"Recovered interrupted save for “{draft.Name}”. The previous committed manifest was retained in that draft's recovery-backups folder.");
        }
        catch (Exception ex)
        {
            Logger.Error(ex, "Could not recover interrupted Skin Studio draft save.");
            updateDraftPresentation($"Interrupted-save recovery failed closed: {ex.Message}");
        }
    }

    private void reviewChanges()
    {
        if (draft is not null)
            changeReviewOverlay?.Present(draft);
    }

    private bool discardSingleChange(string filename)
    {
        if (draft is null)
            return false;
        try
        {
            var before = draft.Changes.Count;
            createAutomaticEditBackup($"discarding {filename}");
            draft = drafts.Unstage(draft.DraftId, filename);
            if (draft.Changes.Count == before)
            {
                updateDraftPresentation($"{filename} is no longer staged.");
                return false;
            }
            reloadActivePreview();
            updateDraftPresentation(
                $"Discarded only {filename}. Undo is available.");
            return true;
        }
        catch (Exception ex)
        {
            Logger.Error(ex, "Could not discard reviewed Skin Studio change.");
            updateDraftPresentation($"Discard failed: {ex.Message}");
            return false;
        }
    }

    private void discardAllChanges()
    {
        if (draft is null)
            return;
        if (draft.Changes.Count == 0)
        {
            updateDraftPresentation("There are no staged changes to discard.");
            return;
        }
        try
        {
            var backup = backups.Create(
                draft.DraftId,
                "Automatic backup before discarding all staged changes",
                studioPreferences.BackupRetention);
            backups.Verify(backup);
            draft = drafts.DiscardAll(draft.DraftId);
            reloadActivePreview();
            updateDraftPresentation(
                $"Discarded all staged changes after verified backup {backup.BackupId}. Undo remains available.");
        }
        catch (Exception ex)
        {
            Logger.Error(ex, "Could not safely discard Skin Studio draft changes.");
            updateDraftPresentation($"Discard stopped before mutation: {ex.Message}");
        }
    }

    private void createDraftBackup()
    {
        if (draft is null)
            return;
        try
        {
            var backup = backups.Create(
                draft.DraftId,
                "Manual Skin Studio backup",
                studioPreferences.BackupRetention);
            backups.Verify(backup);
            updateDraftPresentation(
                $"Created and verified draft backup {backup.BackupId} ({backup.ArchiveSize:N0} bytes).");
        }
        catch (Exception ex)
        {
            Logger.Error(ex, "Could not create Skin Studio draft backup.");
            updateDraftPresentation($"Backup failed: {ex.Message}");
        }
    }

    private string automaticBackupLabel() =>
        $"Automatic edit backups: "
        + (studioPreferences.AutomaticEditBackups ? "on" : "off")
        + $" | keep {studioPreferences.BackupRetention}";

    private void toggleAutomaticEditBackups()
    {
        try
        {
            studioPreferences = preferenceStore.Save(
                studioPreferences with
                {
                    AutomaticEditBackups =
                        !studioPreferences.AutomaticEditBackups,
                });
            automaticBackupButton?.SetText(automaticBackupLabel());
            automaticBackupButton?.SetSelected(
                studioPreferences.AutomaticEditBackups);
            updateDraftPresentation(
                studioPreferences.AutomaticEditBackups
                    ? $"Automatic verified backups are enabled before edits; "
                      + $"the newest {studioPreferences.BackupRetention} are retained."
                    : "Automatic edit backups are disabled. Mandatory safety backups before destructive draft operations remain enabled.");
        }
        catch (Exception ex)
        {
            Logger.Error(ex, "Could not update automatic backup preference.");
            updateDraftPresentation(
                $"Backup preference update failed: {ex.Message}");
        }
    }

    private void promptBackupRetention()
    {
        pathPromptOverlay?.Present(
            "Automatic backup retention",
            $"Enter how many verified draft backups to retain "
            + $"({SkinStudioPreferencesService.MinimumRetention} to "
            + $"{SkinStudioPreferencesService.MaximumRetention}).",
            studioPreferences.BackupRetention.ToString(),
            setBackupRetention);
    }

    private bool setBackupRetention(string value)
    {
        if (!int.TryParse(value, out var retention)
            || retention is < SkinStudioPreferencesService.MinimumRetention
                or > SkinStudioPreferencesService.MaximumRetention)
        {
            throw new InvalidDataException(
                $"Backup retention must be between "
                + $"{SkinStudioPreferencesService.MinimumRetention} and "
                + $"{SkinStudioPreferencesService.MaximumRetention}.");
        }
        studioPreferences = preferenceStore.Save(
            studioPreferences with { BackupRetention = retention });
        automaticBackupButton?.SetText(automaticBackupLabel());
        updateDraftPresentation(
            $"Automatic backup retention is now {retention}. New backups prune older entries only after verification.");
        return true;
    }

    private SkinDraftBackup? createAutomaticEditBackup(string operation)
    {
        if (draft is null || !studioPreferences.AutomaticEditBackups)
            return null;
        var backup = backups.Create(
            draft.DraftId,
            $"Automatic backup before {operation}",
            studioPreferences.BackupRetention);
        backups.Verify(backup);
        return backup;
    }

    private void restoreLatestBackup()
    {
        try
        {
            var backup = backups.List().FirstOrDefault();
            if (backup is null)
            {
                updateDraftPresentation("No draft backup is available.");
                return;
            }
            draft = backups.RestoreAsNewDraft(backup);
            clearAssetSelection();
            selectDraftSkin();
            showWorkbench();
            updateDraftPresentation(
                $"Verified and restored {backup.BackupId} as independent draft “{draft.Name}”.");
        }
        catch (Exception ex)
        {
            Logger.Error(ex, "Could not restore Skin Studio draft backup.");
            updateDraftPresentation($"Backup restore failed: {ex.Message}");
        }
    }

    private void undo()
    {
        if (draft is null)
            return;
        var previousIndex = draft.HistoryIndex;
        draft = drafts.Undo(draft.DraftId);
        selectDraftSkin();
        restartNativePreviewIfOpen();
        updateDraftPresentation(
            draft.HistoryIndex < previousIndex
                ? "Undid the latest draft revision."
                : "Nothing to undo.");
    }

    private void checkSourceConflict()
    {
        if (draft is null)
            return;
        try
        {
            var check = drafts.CheckSource(draft.DraftId);
            updateDraftPresentation(check.State switch
            {
                SkinDraftSourceState.None =>
                    "This draft is independent and has no external source to conflict with.",
                SkinDraftSourceState.Unchanged =>
                    "The original source fingerprint is unchanged. Studio edits remain isolated.",
                SkinDraftSourceState.Missing =>
                    "The original source is missing. The immutable draft snapshot is still recoverable.",
                SkinDraftSourceState.Changed =>
                    "Conflict detected: the original source changed after this isolated draft was created.",
                _ => "Source state is unknown.",
            });
        }
        catch (Exception ex)
        {
            Logger.Error(ex, "Could not check Skin Studio draft source.");
            updateDraftPresentation($"Source check failed: {ex.Message}");
        }
    }

    private void redo()
    {
        if (draft is null)
            return;
        var previousIndex = draft.HistoryIndex;
        draft = drafts.Redo(draft.DraftId);
        selectDraftSkin();
        restartNativePreviewIfOpen();
        updateDraftPresentation(
            draft.HistoryIndex > previousIndex
                ? "Redid the draft revision."
                : "Nothing to redo.");
    }

    private void exportDraft()
    {
        if (draft is null)
            return;
        var defaultPath = Path.Combine(
            contract.WorkspacePath,
            "exports",
            sanitizeFilename(draft.Name) + ".osk");
        pathPromptOverlay?.Present(
            "Export .osk",
            "Enter the complete .osk path. The detected osu!lazer player root is always blocked.",
            defaultPath,
            exportDraftTo);
    }

    private bool exportDraftTo(string destination)
    {
        if (draft is null)
            return false;
        try
        {
            destination = Path.GetFullPath(destination);
            if (!destination.EndsWith(".osk", StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("The export filename must end in .osk.");
            if (!SkinStudioWriteBoundary.IsNormalWriteAllowed(
                    contract.PlayerRoot,
                    destination))
            {
                throw new InvalidDataException(
                    "Export is blocked because the destination overlaps the detected osu!lazer player root.");
            }
            var path = new SkinPackageService(drafts).Export(
                draft.DraftId,
                destination);
            updateDraftPresentation($"Exported {path}.");
            return true;
        }
        catch (Exception ex)
        {
            Logger.Error(ex, "Skin export failed.");
            updateDraftPresentation($"Export failed: {ex.Message}");
            return false;
        }
    }

    private async void publishDraft()
    {
        if (draft is null || gameHost is null)
            return;
        publishFinished = false;
        lastPublishArchivePath = null;
        lastPublishBackup = null;
        lastPublishVerification = null;
        lastPublishFailure = null;
        string? archivePath = null;
        string? backupPath = null;
        string? importStagingPath = null;
        try
        {
            if (string.IsNullOrWhiteSpace(contract.PlayerRoot))
                throw new InvalidOperationException(
                    "Publish is blocked because no osu!lazer root was detected.");
            var idle = new ClosedLazerIdleProbe().Probe(contract.PlayerRoot);
            if (!idle.IsProvenIdle)
            {
                throw new InvalidOperationException(
                    $"Publish is blocked until osu!lazer is closed: {idle.Detail}");
            }

            var publishingDraft = draft;
            var expectedFiles = new SkinPackageService(drafts).Materialize(
                publishingDraft.DraftId);
            var queue = Path.Combine(contract.WorkspacePath, "publish-queue");
            archivePath = new SkinPackageService(drafts).Export(
                publishingDraft.DraftId,
                Path.Combine(
                    queue,
                    $"{sanitizeFilename(publishingDraft.Name)}-{DateTimeOffset.UtcNow:yyyyMMdd-HHmmss}.osk"));
            lastPublishArchivePath = archivePath;
            importStagingPath = Path.Combine(
                queue,
                "import-staging",
                $"{Guid.NewGuid():N}.osk");
            Directory.CreateDirectory(
                Path.GetDirectoryName(importStagingPath)!);
            File.Copy(archivePath, importStagingPath, overwrite: false);
            updateDraftPresentation(
                "Export validated. Creating and verifying a complete lazer catalog backup before import...");

            var preparation = await Task.Run(() =>
            {
                var realm = new LazerSkinRealmService();
                var before = realm.LoadCatalog(contract.PlayerRoot);
                var backup = new LazerCatalogBackupService()
                    .CreateVerified(
                        contract.PlayerRoot,
                        Path.Combine(
                            contract.WorkspacePath,
                            "real-lazer-backups"),
                        "before-publish");
                return (Realm: realm, Before: before, Backup: backup);
            });
            backupPath = preparation.Backup.DirectoryPath;
            lastPublishBackup = preparation.Backup;

            await runOnUpdateThread(() =>
            {
                updateDraftPresentation(
                    $"Verified pre-publish backup of {preparation.Backup.SkinCount} skin(s) and "
                    + $"{preparation.Backup.ReferencedBlobCount} blob(s). Opening retained .osk through lazer...");
                gameHost.OpenFileExternally(importStagingPath);
            });

            var imported =
                await new LazerSkinPublishVerificationService(
                        preparation.Realm)
                    .WaitForImportAsync(
                        contract.PlayerRoot,
                        preparation.Before.Skins
                            .Select(skin => skin.Id)
                            .ToHashSet(),
                        publishingDraft.Name,
                        publishingDraft.Creator,
                        expectedFiles,
                        TimeSpan.FromSeconds(120));
            lastPublishVerification = imported;
            try
            {
                if (File.Exists(importStagingPath))
                    File.Delete(importStagingPath);
            }
            catch
            {
            }
            await runOnUpdateThread(() =>
                updateDraftPresentation(
                    $"Publish verified in osu!lazer as {imported.Name} "
                    + $"({imported.FileCount} files, {imported.SkinId}). "
                    + $"Archive retained: {archivePath}. Backup: {backupPath}."));
        }
        catch (Exception ex)
        {
            lastPublishFailure = ex;
            Logger.Error(ex, "Skin publish failed.");
            try
            {
                await runOnUpdateThread(() =>
                    updateDraftPresentation(
                        $"Publish failed verification: {ex.Message} "
                        + $"Archive retained: {archivePath ?? "not created"}. "
                        + $"Backup: {backupPath ?? "not completed"}."));
            }
            catch
            {
            }
        }
        finally
        {
            publishFinished = true;
        }
    }

    private void syncLivePreview()
    {
        if (draft is null)
            return;
        if (!contract.LiveSyncEnabled)
        {
            updateDraftPresentation(
                "Live sync is disabled. Close Studio and opt in from the Kumori launcher.");
            return;
        }
        if (string.IsNullOrWhiteSpace(contract.PlayerRoot))
        {
            updateDraftPresentation(
                "Live sync is blocked because no osu!lazer root was detected.");
            return;
        }

        try
        {
            updateDraftPresentation(
                "Verifying the mandatory backup before the live-edit transaction…");
            var sync = new LivePreviewSyncService(
                drafts,
                new LazerLivePreviewStore(),
                new ClosedLazerIdleProbe(),
                Path.Combine(contract.WorkspacePath, "real-lazer-backups"));
            var result = sync.Sync(
                draft.DraftId,
                contract.PlayerRoot,
                liveSyncPermission: true,
                allowWhilePlayerRunning: true);
            draft = drafts.Load(draft.DraftId);
            var reload = SkinStudioReloadPipeClient.Queue(
                contract.ReloadPipeName,
                result.SkinId);
            updateDraftPresentation(
                $"{(result.Created ? "Created" : "Updated")} disposable preview copy "
                + $"with {result.ChangedFiles} change(s). Backup: {Path.GetFileName(result.BackupPath)}. "
                + reload.Message);
        }
        catch (Exception ex)
        {
            Logger.Error(ex, "Guarded live preview sync failed.");
            updateDraftPresentation($"Live sync stopped without unsafe fallback: {ex.Message}");
        }
    }

    private void importAsset(string path)
    {
        if (draft is null)
            return;
        try
        {
            var filename = SkinDraftWorkspaceService.NormalizeSkinFilename(
                pendingAssetTarget is null
                    ? Path.GetFileName(path)
                    : assets.ResolveReplacementFilename(
                        draft.DraftId,
                        pendingAssetTarget,
                        Path.GetFileName(path)));
            var materialized = new SkinPackageService(drafts).Materialize(draft.DraftId);
            var expectedHash = materialized.TryGetValue(filename, out var current)
                ? SkinDraftWorkspaceService.Hash(current)
                : null;
            createAutomaticEditBackup($"importing {filename}");
            draft = drafts.StageFile(
                draft.DraftId,
                filename,
                File.ReadAllBytes(path),
                expectedHash,
                $"Import {filename}");
            selectDraftSkin();
            restartNativePreviewIfOpen();
            refreshWorkbench();
            updateDraftPresentation(
                $"Imported {filename}; "
                + $"{(gameplayMode
                    ? "real gameplay"
                    : mockupMode
                        ? "Gameplay Mockup"
                        : "workbench")} reloaded.");
        }
        catch (Exception ex)
        {
            Logger.Error(ex, "Asset import failed.");
            updateDraftPresentation($"Asset import failed: {ex.Message}");
        }
        finally
        {
            pendingAssetTarget = null;
        }
    }

    private void importSkin(string path)
    {
        try
        {
            SkinPackageService.ValidatePackage(path);
            var fingerprint = SkinPackageService.Fingerprint(path);
            draft = drafts.List().FirstOrDefault(
                candidate => candidate.SourceFingerprint == fingerprint);
            draft ??= drafts.Create(
                Path.GetFileNameWithoutExtension(path),
                "Kumori",
                path,
                fingerprint);
            clearAssetSelection();
            selectDraftSkin();
            restartNativePreviewIfOpen();
            updateDraftPresentation($"Opened isolated draft “{draft.Name}”.");
        }
        catch (Exception ex)
        {
            Logger.Error(ex, "Skin import failed.");
            updateDraftPresentation($"Skin import failed: {ex.Message}");
        }
    }

    private void selectNextDraft()
    {
        var available = drafts.List();
        if (available.Count == 0)
            return;
        var current = draft is null
            ? -1
            : available.ToList().FindIndex(candidate => candidate.DraftId == draft.DraftId);
        draft = available[(current + 1 + available.Count) % available.Count];
        clearAssetSelection();
        selectDraftSkin();
        restartNativePreviewIfOpen();
        updateDraftPresentation($"Opened draft “{draft.Name}”.");
    }

    private void browseDrafts()
    {
        draftBrowserOverlay?.Present(
            drafts.List(),
            draft?.DraftId);
    }

    private bool shouldPromptForSkin() =>
        acceptanceOutputPath is null
        && commandAcceptanceOutputPath is null
        && publishAcceptanceOutputPath is null
        && contract.DraftId is null
        && string.IsNullOrWhiteSpace(contract.SourceSkinPath);

    private async void presentOpeningSkinChooser(bool required)
    {
        var playerRootAvailable =
            !string.IsNullOrWhiteSpace(contract.PlayerRoot);
        openingSkinOverlay?.Present(
            drafts.List(),
            installedSkinCatalog?.Skins,
            installedLoading: playerRootAvailable,
            installedError: playerRootAvailable
                ? null
                : "No osu!lazer player root was detected. You can still import an .osk or open a Kumori draft.",
            required);
        if (!playerRootAvailable)
            return;

        try
        {
            var catalog = await Task.Run(() =>
                new LazerSkinRealmService().LoadCatalog(contract.PlayerRoot!));
            Schedule(() =>
            {
                installedSkinCatalog = catalog;
                openingSkinOverlay?.UpdateInstalled(catalog.Skins);
                updateDraftPresentation(
                    $"Choose from {drafts.List().Count} draft(s), {catalog.Skins.Count} installed skin(s), or import an .osk.");
            });
        }
        catch (Exception ex)
        {
            Logger.Error(ex, "Could not populate the opening skin chooser.");
            Schedule(() => openingSkinOverlay?.UpdateInstalled(
                [],
                $"Could not read installed lazer skins: {ex.Message}"));
        }
    }

    private async void browseInstalledSkins()
    {
        if (string.IsNullOrWhiteSpace(contract.PlayerRoot))
            return;
        try
        {
            updateDraftPresentation(
                "Reading the installed lazer skin catalog in read-only mode…");
            var catalog = await Task.Run(() =>
                new LazerSkinRealmService().LoadCatalog(contract.PlayerRoot));
            Schedule(() =>
            {
                installedSkinCatalog = catalog;
                installedSkinBrowserOverlay?.Present(catalog.Skins);
                updateDraftPresentation(
                    $"Loaded {catalog.Skins.Count} installed lazer skin(s) without writing to the player root.");
            });
        }
        catch (Exception ex)
        {
            Logger.Error(ex, "Could not read installed lazer skins.");
            Schedule(() =>
                updateDraftPresentation(
                    $"Installed skin catalog failed: {ex.Message}"));
        }
    }

    private async void importInstalledSkin(Guid skinId)
    {
        var catalog = installedSkinCatalog;
        if (catalog is null
            || string.IsNullOrWhiteSpace(contract.PlayerRoot))
        {
            updateDraftPresentation("Reload the installed skin catalog first.");
            return;
        }
        var skin = catalog.Skins.FirstOrDefault(
            candidate => candidate.Id == skinId);
        if (skin is null)
        {
            updateDraftPresentation("The selected installed skin is no longer in the catalog.");
            return;
        }
        string? snapshot = null;
        try
        {
            updateDraftPresentation(
                $"Creating a hash-verified read-only snapshot of “{skin.DisplayName}”…");
            var directory = Path.Combine(
                contract.WorkspacePath,
                "installed-snapshots");
            snapshot = Path.Combine(
                directory,
                $"{skin.Id:N}-{DateTimeOffset.UtcNow:yyyyMMdd-HHmmss}.osk");
            var realm = new LazerSkinRealmService();
            await Task.Run(() =>
                LazerInstalledSkinSnapshotService.CreateVerifiedOsk(
                    skin,
                    hash => realm.ReadFile(catalog.RootPath, hash),
                    snapshot));
            var completedSnapshot = snapshot;
            snapshot = null;
            Schedule(() => finalizeInstalledSkinSnapshot(
                skin,
                completedSnapshot));
        }
        catch (Exception ex)
        {
            Logger.Error(ex, "Could not snapshot installed lazer skin.");
            Schedule(() =>
                updateDraftPresentation(
                    $"Installed skin import failed: {ex.Message}"));
        }
        finally
        {
            try
            {
                if (snapshot is not null)
                    File.Delete(snapshot);
            }
            catch
            {
            }
        }
    }

    private void finalizeInstalledSkinSnapshot(
        LazerSkinInfo skin,
        string snapshot)
    {
        try
        {
            var fingerprint = SkinPackageService.Fingerprint(snapshot);
            draft = drafts.List().FirstOrDefault(candidate =>
                candidate.SourceFingerprint == fingerprint);
            draft ??= drafts.Create(
                skin.Name,
                skin.Creator,
                snapshot,
                fingerprint,
                trackOrigin: false);
            clearAssetSelection();
            selectDraftSkin();
            showWorkbench();
            updateDraftPresentation(
                $"Opened isolated snapshot of installed skin “{skin.DisplayName}”; the player root remains read-only.");
        }
        catch (Exception ex)
        {
            Logger.Error(ex, "Could not finalize installed lazer skin snapshot.");
            updateDraftPresentation($"Installed skin import failed: {ex.Message}");
        }
        finally
        {
            try { File.Delete(snapshot); } catch { }
        }
    }

    private void openDraft(Guid draftId)
    {
        try
        {
            draft = drafts.Load(draftId);
            clearAssetSelection();
            selectDraftSkin();
            if (gameplayMode)
                restartNativePreviewIfOpen();
            else if (mockupMode)
                showMockup();
            else
                showWorkbench();
            updateDraftPresentation($"Opened draft “{draft.Name}”.");
        }
        catch (Exception ex)
        {
            Logger.Error(ex, "Could not open selected Skin Studio draft.");
            updateDraftPresentation($"Open draft failed: {ex.Message}");
        }
    }

    private void importBeatmap(string path)
    {
        if (gameHost is null || audioManager is null || osuRuleset is null || screenStack is null)
            return;
        try
        {
            var maps = Path.Combine(contract.WorkspacePath, "beatmaps");
            var imported = StudioBeatmapImportService.Prepare(path, maps);
            Beatmap.Value = new StudioWorkingBeatmap(
                imported.BeatmapPath,
                audioManager,
                gameHost);
            player?.Exit();
            player = null;
            Scheduler.AddDelayed(() =>
            {
                showGameplay();
                var media = imported.CopiedMedia.Count == 0
                    ? "no referenced media copied"
                    : $"{imported.CopiedMedia.Count} media file(s) copied";
                var missing = imported.MissingMedia.Count == 0
                    ? string.Empty
                    : $" Missing: {string.Join(", ", imported.MissingMedia)}.";
                updateDraftPresentation(
                    $"Custom osu!standard map “{Path.GetFileName(path)}” loaded "
                    + $"({imported.HitObjectCount} objects, {media}, "
                    + $"{imported.Hash[..12]}).{missing}");
            }, 250);
        }
        catch (Exception ex)
        {
            Logger.Error(ex, "Custom beatmap import failed.");
            updateDraftPresentation($"Custom beatmap rejected: {ex.Message}");
        }
    }

    private void editSkinIniStructured()
    {
        if (draft is null)
            return;
        try
        {
            var files = new SkinPackageService(drafts).Materialize(draft.DraftId);
            presentStructuredSkinIni(files["skin.ini"]);
        }
        catch (Exception ex)
        {
            Logger.Error(ex, "Could not open structured skin.ini editor.");
            updateDraftPresentation($"Could not open skin.ini: {ex.Message}");
        }
    }

    private void editSkinIniRaw()
    {
        if (draft is null)
            return;
        try
        {
            var files = new SkinPackageService(drafts).Materialize(draft.DraftId);
            presentRawSkinIni(files["skin.ini"]);
        }
        catch (Exception ex)
        {
            Logger.Error(ex, "Could not open raw skin.ini editor.");
            updateDraftPresentation($"Could not open raw skin.ini: {ex.Message}");
        }
    }

    private void presentStructuredSkinIni(byte[] bytes)
    {
        skinIniOverlay?.Present(
            bytes,
            updated => commitSkinIni(updated, structured: true),
            presentRawSkinIni,
            focusSkinIniContext);
    }

    private void presentRawSkinIni(byte[] bytes)
    {
        rawSkinIniOverlay?.Present(
            bytes,
            updated => commitSkinIni(updated, structured: false),
            presentStructuredSkinIni);
    }

    private bool commitSkinIni(byte[] bytes, bool structured)
    {
        if (draft is null)
            return false;
        var mode = structured ? "Structured" : "Raw";
        try
        {
            SkinIniDocument.Parse(bytes);
            var currentFiles = new SkinPackageService(drafts).Materialize(
                draft.DraftId);
            var expected = currentFiles.TryGetValue("skin.ini", out var current)
                ? SkinDraftWorkspaceService.Hash(current)
                : null;
            createAutomaticEditBackup(
                $"editing skin.ini in {mode.ToLowerInvariant()} mode");
            draft = drafts.StageFile(
                draft.DraftId,
                "skin.ini",
                bytes,
                expected,
                $"Edit {mode.ToLowerInvariant()} skin.ini");
            reloadActivePreview();
            updateDraftPresentation(
                $"{mode} skin.ini changes were validated, journalled, and reloaded.");
            return true;
        }
        catch (Exception ex)
        {
            Logger.Error(ex, $"Could not apply {mode.ToLowerInvariant()} skin.ini edit.");
            updateDraftPresentation(
                $"{mode} skin.ini edit failed: {ex.Message}");
            return false;
        }
    }

    private void focusSkinIniContext(string componentName)
    {
        showWorkbench();
        workbench?.FocusComponent(componentName);
        updateDraftPresentation(
            $"Focused workbench context for {componentName}.");
    }

    private void editSkinIniExternally()
    {
        if (draft is null || gameHost is null)
            return;
        try
        {
            var files = new SkinPackageService(drafts).Materialize(draft.DraftId);
            var directory = Path.Combine(
                contract.WorkspacePath,
                "external-edit",
                draft.DraftId.ToString("N"));
            Directory.CreateDirectory(directory);
            externalSkinIniPath = Path.Combine(directory, "skin.ini");
            File.WriteAllBytes(externalSkinIniPath, files["skin.ini"]);
            gameHost.OpenFileExternally(externalSkinIniPath);
            updateDraftPresentation(
                "Opened an isolated skin.ini copy. Use “Apply skin.ini edit” after saving.");
        }
        catch (Exception ex)
        {
            Logger.Error(ex, "Could not open external skin.ini editor.");
            updateDraftPresentation($"Could not open skin.ini: {ex.Message}");
        }
    }

    private void applyExternalSkinIni()
    {
        if (draft is null
            || string.IsNullOrWhiteSpace(externalSkinIniPath)
            || !File.Exists(externalSkinIniPath))
        {
            updateDraftPresentation("Open skin.ini for editing first.");
            return;
        }
        try
        {
            var bytes = File.ReadAllBytes(externalSkinIniPath);
            // Parse before staging so malformed encoding/structure never reaches
            // the working skin. Unknown keys, comments, and line endings remain.
            SkinIniDocument.Parse(bytes);
            var files = new SkinPackageService(drafts).Materialize(draft.DraftId);
            var expected = files.TryGetValue("skin.ini", out var current)
                ? SkinDraftWorkspaceService.Hash(current)
                : null;
            createAutomaticEditBackup("applying external skin.ini edit");
            draft = drafts.StageFile(
                draft.DraftId,
                "skin.ini",
                bytes,
                expected,
                "Apply external skin.ini edit");
            selectDraftSkin();
            restartNativePreviewIfOpen();
            updateDraftPresentation(
                "skin.ini was journalled and the authoritative preview reloaded.");
        }
        catch (Exception ex)
        {
            Logger.Error(ex, "Could not apply external skin.ini edit.");
            updateDraftPresentation($"skin.ini edit rejected: {ex.Message}");
        }
    }

    private void restartPreview()
    {
        if (gameplayMode && player?.IsLoaded == true)
        {
            player.Restart();
            updateDraftPresentation("Native gameplay preview restarted.");
            return;
        }
        if (mockupMode)
        {
            seekNativeMockupWhenReady();
            updateDraftPresentation(
                "Native lazer Gameplay Mockup refreshed from the active draft.");
            return;
        }
        refreshWorkbench();
        updateDraftPresentation("All-elements workbench refreshed from the active draft.");
    }

    private void updateDraftPresentation(string status)
    {
        if (draft is not null)
        {
            draft = drafts.Load(draft.DraftId);
            if (skinText is not null)
                skinText.Text = draft.Name;
            if (changesText is not null)
            {
                if (draft.Changes.Count == 0)
                {
                    changesText.Text = "No staged changes";
                }
                else
                {
                    var visible = draft.Changes.Take(5)
                        .Select(change =>
                            $"{(change.Kind == SkinDraftChangeKind.Delete ? "−" : "+")} {change.Filename}");
                    changesText.Text = string.Join('\n', visible)
                                       + (draft.Changes.Count > 5
                                           ? $"\n+ {draft.Changes.Count - 5} more"
                                           : "");
                }
            }
        }
        updateCommandStates();
        if (statusText is not null)
            statusText.Text = status;
    }

    private void updateCommandStates()
    {
        var hasDraft = draft is not null;
        undoButton?.SetEnabled(hasDraft && draft!.CanUndo);
        redoButton?.SetEnabled(hasDraft && draft!.CanRedo);
        discardAllButton?.SetEnabled(hasDraft && draft!.Changes.Count > 0);
        reviewChangesButton?.SetEnabled(hasDraft && draft!.Changes.Count > 0);
        var categoryComponents = workbench?.ActiveCategoryComponents()
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        resetCategoryButton?.SetEnabled(
            hasDraft
            && categoryComponents is { Count: > 0 }
            && draft!.Changes.Any(change => categoryComponents.Contains(
                SkinDraftAssetService.ComponentName(change.Filename))));
        addCategoryToExtrasButton?.SetEnabled(
            hasDraft
            && workbench?.ActiveCategoryTitle is not null
            && categoryComponents is { Count: > 0 });
        applyExternalSkinIniButton?.SetEnabled(
            hasDraft
            && !string.IsNullOrWhiteSpace(externalSkinIniPath)
            && File.Exists(externalSkinIniPath));
        restoreDeletedDraftButton?.SetEnabled(drafts.ListDeleted().Count > 0);
        restoreDraftBackupButton?.SetEnabled(backups.List().Count > 0);
        recoverInterruptedDraftButton?.SetEnabled(
            drafts.ListRecoveryCandidates().Any(candidate =>
                candidate.PendingManifestValid));
        updateAssetActionStates();
    }

    private void showFailure(Exception exception)
    {
        ClearInternal();
        Add(new FillFlowContainer
        {
            RelativeSizeAxes = Axes.Both,
            Anchor = Anchor.Centre,
            Origin = Anchor.Centre,
            Direction = FillDirection.Vertical,
            Spacing = new Vector2(0, 12),
            Padding = new MarginPadding(80),
            Children =
            [
                heading("Kumori Skin Studio could not start"),
                body(exception.Message, 15, Colour4.FromHex("#F5D7E7")),
            ],
        });
    }

    private static Container panel(
        Anchor anchor,
        float width,
        float height,
        Axes relativeAxes) => new()
        {
            Anchor = anchor,
            Origin = anchor,
            Width = width,
            Height = height,
            RelativeSizeAxes = relativeAxes,
            Depth = -10,
            Child = new Box
            {
                RelativeSizeAxes = Axes.Both,
                Colour = Colour4.FromHex("#1B1925"),
            },
        };

    private static SpriteText heading(string text) => new()
    {
        Text = text,
        Font = FontUsage.Default.With(size: 12, weight: "Bold"),
        Colour = Colour4.FromHex("#FFB7D5"),
    };

    private static SpriteText body(string text, float size, Colour4 colour) => new()
    {
        Text = text,
        Font = FontUsage.Default.With(size: size),
        Colour = colour,
    };

    private static Drawable divider() => new Box
    {
        RelativeSizeAxes = Axes.X,
        Height = 1,
        Colour = Colour4.FromHex("#3A3040"),
        Margin = new MarginPadding { Vertical = 4 },
    };

    private static string sanitizeFilename(string value)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var result = new string(value.Select(character =>
            invalid.Contains(character) ? '_' : character).ToArray()).Trim();
        return string.IsNullOrWhiteSpace(result) ? "Kumori-skin" : result;
    }

    internal static string PrepareSkinImportPath(string sourcePath)
    {
        var temporary = Path.Combine(
            Path.GetTempPath(),
            $"kumori-studio-skin-{Guid.NewGuid():N}.osk");
        if (File.Exists(sourcePath))
        {
            File.Copy(sourcePath, temporary);
            return temporary;
        }
        ZipFile.CreateFromDirectory(sourcePath, temporary);
        return temporary;
    }

    protected override void Dispose(bool isDisposing)
    {
        rendererPipeServer?.Dispose();
        rendererPipeServer = null;
        stopRendererAudio();
        externalAssetWatcher?.Dispose();
        externalAssetWatcher = null;
        skinLoadCancellation?.Cancel();
        skinLoadCancellation?.Dispose();
        skinLoadCancellation = null;
        embeddedWindowActivation?.Dispose();
        extrasCatalogAcceptanceController?.Dispose();
        extrasCatalogAcceptanceController = null;
        base.Dispose(isDisposing);
        assetSelector?.Dispose();
        skinSelector?.Dispose();
        beatmapSelector?.Dispose();
        extrasPackageSelector?.Dispose();
    }

    protected override void Update()
    {
        // Re-parenting SDL under WPF can reset the native cursor state after
        // startup. Keep the operating-system pointer hidden so the only user
        // pointer in the renderer is lazer's built-in menu cursor.
        if (embedded && gameHost?.Window is { } embeddedWindow)
            embeddedWindow.CursorState |= CursorState.Hidden;
        embeddedWindowActivation?.Poll();
        // An SDL child embedded into WPF does not reliably update Game.IsActive.
        // Query its GUI thread's actual focus window so the cursor stays visible
        // inside Studio but disappears once focus leaves the native surface.
        var cursorHasFocus = embedded
            ? embeddedWindowActivation?.HasKeyboardFocus ?? true
            : IsActive.Value;
        if (rendererInteractiveCursor is not null)
        {
            // The skin cursor is the asset being previewed. Keep lazer's
            // normal menu cursor as a separate user-controlled pointer for
            // colour chips and future renderer-only affordances.
            GlobalCursorDisplay.MenuCursor.Alpha = rendererMenuCursorVisible
                ? CursorAlpha(cursorHasFocus)
                : 0;
            var sceneUsesCursor = rendererScene is SkinStudioPreviewScene.Showcase
                or SkinStudioPreviewScene.Cursor;
            var centredExtrasCursor = isCursorInspection()
                                      && rendererScene == SkinStudioPreviewScene.Cursor
                                      && !rendererAutoMotion;
            var studioDrivesCursor = sceneUsesCursor
                                     && (rendererAutoMotion || centredExtrasCursor);
            rendererInteractiveCursor.ManualMovement = studioDrivesCursor;
            if (centredExtrasCursor)
            {
                rendererInteractiveCursor.ManualPosition =
                    rendererInteractiveCursor.DrawSize / 2;
            }
            else if (studioDrivesCursor && player is { CanSeekForAcceptance: true })
            {
                var gameplayPosition = StudioSceneCursorPath.PositionAt(
                    rendererScene,
                    rendererScene == SkinStudioPreviewScene.Showcase
                        ? StudioScenePlayer.ShowcaseCursorCycleStart
                          + Time.Current % StudioScenePlayer.ShowcaseCursorCycleDuration
                        : player.CurrentTime);
                rendererInteractiveCursor.ManualPosition =
                    rendererInteractiveCursor.ToLocalSpace(
                        player.GameplayPositionToScreenSpace(gameplayPosition));
            }
            rendererInteractiveCursor.Alpha = !sceneUsesCursor
                ? 0
                : centredExtrasCursor
                    ? 1
                : studioDrivesCursor
                    ? 1
                    : CursorAlpha(cursorHasFocus);
        }
        else
        {
            GlobalCursorDisplay.MenuCursor.Alpha = rendererMenuCursorVisible
                ? CursorAlpha(cursorHasFocus)
                : 0;
        }
        updateSemanticAudio();
        base.Update();
    }
}

internal sealed record RendererColourEditRequest(
    SkinStudioRendererColourTarget Target,
    byte Red,
    byte Green,
    byte Blue,
    double AnchorX,
    double AnchorY,
    double AvoidLeft,
    double AvoidTop,
    double AvoidRight,
    double AvoidBottom);

internal partial class StudioScenePlayer : Player
{
    [Cached(typeof(IGameplayLeaderboardProvider))]
    private readonly EmptyGameplayLeaderboardProvider leaderboardProvider = new();

    internal const double StationarySliderStartTime = 2_250;
    internal const double ShowcaseMidSliderStartTime = 4_300;
    internal const double ShowcasePaletteStartTime = 5_450;
    internal const double ShowcasePaletteEndTime = 5_457;
    internal const double ShowcaseCursorCycleStart = 0;
    internal const double ShowcaseCursorCycleEnd = 6_000;
    internal const double ShowcaseCursorCycleDuration =
        ShowcaseCursorCycleEnd - ShowcaseCursorCycleStart;
    internal const double ShowcaseWaitingSliderStartTime = 5_460;
    private SkinStudioPreviewScene scene = SkinStudioPreviewScene.Showcase;
    private bool autoMotion;
    private string? inspectionFamily;
    private int? inspectionManiaKeyCount;
    private Colour4 inspectionTint = Colour4.White;
    private readonly Dictionary<string, Colour4> inspectionElementTints =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly List<DrawableHitCircle> assetPreviewCircles = [];
    private readonly List<DrawableHitCircle> numberPreviewCircles = [];
    private readonly List<DrawableSlider> assetPreviewSliders = [];
    private DrawableSpinner? assetPreviewSpinner;
    private readonly Dictionary<DrawableSlider, DrawableHitCircle> assetPreviewSliderHeads = [];
    private readonly Dictionary<DrawableHitCircle, float> circleBaseScales = [];
    private readonly Dictionary<DrawableSlider, float> sliderBaseScales = [];
    private readonly Dictionary<DrawableSlider, double> assetPreviewSliderSourceTimes = [];
    private StudioPreviewColourLegend? assetPreviewColourLegend;
    private readonly HashSet<string> inspectionComponents =
        new(StringComparer.OrdinalIgnoreCase);
    private float objectPreviewScale = 1;

    internal event Action<
        SkinStudioRendererColourTarget,
        Colour4,
        Vector2,
        Vector2,
        Vector2>?
        ColourEditRequested;

    internal const bool UsesReplayPipeline = false;

    internal static bool AdvancesGameplay(
        SkinStudioPreviewScene scene,
        bool motionRequested) =>
        motionRequested
        && scene is SkinStudioPreviewScene.Sliders
            or SkinStudioPreviewScene.Cursor
            or SkinStudioPreviewScene.Spinner;

    public StudioScenePlayer()
        : base(new PlayerConfiguration
        {
            ShowResults = false,
            AutomaticallySkipIntro = true,
            AllowPause = false,
            AllowRestart = false,
            AllowSkipping = false,
            // The renderer has no gameplay/replay actions, but its
            // presentation furniture (linked colour chips) must receive
            // pointer input so Kumori can open the owned WPF editor.
            AllowUserInteraction = true,
            ShowFailingOverlay = false,
            ShowLeaderboard = false,
        })
    {
    }

    protected override UserActivity? InitialActivity => null;
    public override bool DisallowExternalBeatmapRulesetChanges => true;
    public override bool? AllowGlobalTrackControl => false;
    protected override bool PauseOnFocusLost => false;

    protected override void PrepareReplay()
    {
        // Player normally starts recording a local play here. Studio is a
        // scene renderer: it neither records nor consumes replay frames.
    }

    protected override void PrepareDrawableRuleset(DrawableRuleset drawableRuleset)
    {
        base.PrepareDrawableRuleset(drawableRuleset);
        drawableRuleset.Playfield.HitObjectContainer
            .EnableAssetPreviewLifetimeExtension();
        var previewBeatmap = ((osu.Game.Rulesets.Osu.UI.DrawableOsuRuleset)
            drawableRuleset).Beatmap;

        var paletteCircles = drawableRuleset.Objects
                     .OfType<osu.Game.Rulesets.Osu.Objects.HitCircle>()
                     .Where(circle => circle.StartTime
                         is >= ShowcasePaletteStartTime
                            and <= ShowcasePaletteEndTime)
                     .ToArray();
        for (var paletteIndex = 0; paletteIndex < paletteCircles.Length;
             paletteIndex++)
        {
            var hitCircle = paletteCircles[paletteIndex];
            var compact = paletteCircles.Length > 4;
            var column = paletteIndex % 4;
            var row = paletteIndex / 4;
            var preview = new osu.Game.Rulesets.Osu.Objects.HitCircle
            {
                StartTime = 5_000 + paletteIndex,
                Position = compact
                    ? new Vector2(82 + column * 116, 55 + row * 95)
                    : new Vector2(
                        512f * (paletteIndex + 1) / (paletteCircles.Length + 1),
                        72),
            };
            preview.ApplyDefaults(previewBeatmap.ControlPointInfo,
                previewBeatmap.Difficulty);
            copyPreviewPresentation(hitCircle, preview);
            preview.TimePreempt = 10_000;
            var drawable = new DrawableHitCircle(preview);
            assetPreviewCircles.Add(drawable);
            circleBaseScales[drawable] = preview.Scale;
            drawableRuleset.Playfield.AddAssetPreviewDrawable(drawable);
        }

        // Hit-circle number fonts are only meaningful when composed inside the
        // actual hitcircle stack. Keep ten real lazer hitcircles alive so the
        // editor can show the complete 1-10 range, including the two-glyph 10.
        for (var index = 0;
             index < SkinStudioSemanticPreviewCatalog.HitCircleNumberPreviewCount;
             index++)
        {
            var preview = new osu.Game.Rulesets.Osu.Objects.HitCircle
            {
                StartTime = 5_100 + index,
                Position = Vector2.Zero,
                IndexInCurrentCombo = index,
                ComboIndex = index / 4,
                ComboIndexWithOffsets = index / 4,
                NewCombo = index == 0,
            };
            preview.ApplyDefaults(previewBeatmap.ControlPointInfo,
                previewBeatmap.Difficulty);
            preview.TimePreempt = 10_000;
            var drawable = new DrawableHitCircle(preview);
            numberPreviewCircles.Add(drawable);
            circleBaseScales[drawable] = preview.Scale;
            drawableRuleset.Playfield.AddAssetPreviewDrawable(drawable);
        }

        foreach (var slider in drawableRuleset.Objects
                     .OfType<osu.Game.Rulesets.Osu.Objects.Slider>()
                     .Where(slider => Math.Abs(slider.StartTime
                                               - StationarySliderStartTime) < 1
                                      || Math.Abs(slider.StartTime - 2_500) < 1
                                      || Math.Abs(slider.StartTime
                                                  - ShowcaseMidSliderStartTime) < 1
                                      || Math.Abs(slider.StartTime
                                                  - ShowcaseWaitingSliderStartTime) < 1))
        {
            var preview = new osu.Game.Rulesets.Osu.Objects.Slider
            {
                StartTime = slider.StartTime,
                Position = slider.Position,
                Path = slider.Path,
                RepeatCount = slider.RepeatCount,
                ClassicSliderBehaviour = slider.ClassicSliderBehaviour,
                GenerateTicks = slider.GenerateTicks,
                TickDistanceMultiplier = slider.TickDistanceMultiplier,
                SliderVelocityMultiplier = slider.SliderVelocityMultiplier,
                Samples = slider.Samples.ToArray(),
                NodeSamples = slider.NodeSamples
                    .Select(samples => (IList<osu.Game.Audio.HitSampleInfo>)
                        samples.ToArray())
                    .ToArray(),
            };
            preview.ApplyDefaults(previewBeatmap.ControlPointInfo,
                previewBeatmap.Difficulty);
            copyPreviewPresentation(slider, preview);
            preview.TimePreempt = 10_000;
            var drawable = new DrawableSlider(preview);
            assetPreviewSliders.Add(drawable);
            sliderBaseScales[drawable] = preview.Scale;
            assetPreviewSliderSourceTimes[drawable] = slider.StartTime;
            drawableRuleset.Playfield.AddAssetPreviewDrawable(drawable);

            // Slider heads normally expire as gameplay advances. The studio is
            // a stationary presentation surface, so keep a separate real lazer
            // hit-circle drawable at the slider start. This preserves the
            // current skin's circle and approach-circle rendering without
            // relying on replay timing or synthesised input.
            var headPreview = new osu.Game.Rulesets.Osu.Objects.HitCircle
            {
                StartTime = preview.StartTime,
                Position = preview.Position,
            };
            headPreview.ApplyDefaults(previewBeatmap.ControlPointInfo,
                previewBeatmap.Difficulty);
            copyPreviewPresentation(preview.HeadCircle, headPreview);
            headPreview.TimePreempt = 10_000;
            var headDrawable = new DrawableHitCircle(headPreview);
            assetPreviewSliderHeads[drawable] = headDrawable;
            circleBaseScales[headDrawable] = headPreview.Scale;
            drawableRuleset.Playfield.AddAssetPreviewDrawable(headDrawable);
        }

        var spinnerSource = drawableRuleset.Objects
            .OfType<osu.Game.Rulesets.Osu.Objects.Spinner>()
            .FirstOrDefault();
        if (spinnerSource is not null)
        {
            var preview = new osu.Game.Rulesets.Osu.Objects.Spinner
            {
                StartTime = spinnerSource.StartTime,
                EndTime = spinnerSource.EndTime,
                Position = spinnerSource.Position,
            };
            preview.ApplyDefaults(
                previewBeatmap.ControlPointInfo,
                previewBeatmap.Difficulty);
            copyPreviewPresentation(spinnerSource, preview);
            assetPreviewSpinner = new DrawableSpinner(preview);
            drawableRuleset.Playfield.AddAssetPreviewDrawable(
                assetPreviewSpinner);
        }

        assetPreviewColourLegend = new StudioPreviewColourLegend(
            assetPreviewCircles,
            assetPreviewSliders,
            assetPreviewSliderSourceTimes,
            (target, colour, anchor, avoidTopLeft, avoidBottomRight) =>
                ColourEditRequested?.Invoke(
                    target,
                    colour,
                    anchor,
                    avoidTopLeft,
                    avoidBottomRight));
        drawableRuleset.Playfield.AddAssetPreviewOverlay(assetPreviewColourLegend);
        applyObjectPreviewScale();
    }

    private static void copyPreviewPresentation(
        osu.Game.Rulesets.Osu.Objects.OsuHitObject source,
        osu.Game.Rulesets.Osu.Objects.OsuHitObject target)
    {
        target.ComboIndex = source.ComboIndex;
        target.ComboIndexWithOffsets = source.ComboIndexWithOffsets;
        target.IndexInCurrentCombo = source.IndexInCurrentCombo;
        target.NewCombo = source.NewCombo;
        target.ComboOffset = source.ComboOffset;
        target.LastInCombo = source.LastInCombo;
        target.StackHeight = source.StackHeight;
        target.Scale = source.Scale;
    }

    protected override Task ImportScore(Score score) => Task.CompletedTask;

    protected override osu.Game.Screens.Ranking.ResultsScreen CreateResults(
        ScoreInfo score) => throw new InvalidOperationException(
        "Studio scenes never create a results screen.");

    protected override bool CheckModsAllowFailure() => false;

    protected override void PerformFail()
    {
    }

    protected override void LoadComplete()
    {
        base.LoadComplete();

        if (!LoadedBeatmapSuccessfully)
            return;
        DrawableRuleset.Playfield.HitObjectContainer
            .EnableAssetPreviewLifetimeExtension();
        ConfigureScene(scene, autoMotion);
    }

    public override void OnEntering(ScreenTransitionEvent e)
    {
        base.OnEntering(e);
        ApplyToBackground(background =>
        {
            background.IgnoreUserSettings.Value = true;
            background.DimWhenUserSettingsIgnored.Value = 1;
            background.BlurAmount.Value = 0;
            background.FadeColour(Colour4.White, 0);
        });
    }

    internal void ConfigureScene(SkinStudioPreviewScene nextScene, bool motion) =>
        ConfigureScene(nextScene, motion, inspectionFamily);

    internal void ConfigureScene(
        SkinStudioPreviewScene nextScene,
        bool motion,
        string? nextInspectionFamily,
        IReadOnlyCollection<string>? nextInspectionComponents = null,
        int? maniaKeyCount = null)
    {
        scene = nextScene;
        inspectionFamily = string.IsNullOrWhiteSpace(nextInspectionFamily)
            ? null
            : nextInspectionFamily.Trim();
        inspectionManiaKeyCount = maniaKeyCount is >= 1 and <= 18
            ? maniaKeyCount
            : null;
        if (nextInspectionComponents is not null)
        {
            inspectionComponents.Clear();
            foreach (var component in nextInspectionComponents)
            {
                if (!string.IsNullOrWhiteSpace(component))
                    inspectionComponents.Add(component.Trim());
            }
        }
        autoMotion = AdvancesGameplay(nextScene, motion);
        if (!CanSeekForAcceptance)
            return;

        // Inspection scenes are real lazer drawables, isolated from unrelated
        // gameplay furniture. Showcase deliberately retains both gameplay and
        // HUD so it remains the combined overview.
        DrawableRuleset.Alpha = inspectionFamily is not null
            ? 1
            : scene == SkinStudioPreviewScene.Hud ? 0 : 1;
        DrawableRuleset.Playfield.HitObjectContainer.Alpha =
            scene == SkinStudioPreviewScene.Cursor ? 0 : 1;
        DrawableRuleset.Overlays.Alpha =
            scene == SkinStudioPreviewScene.Cursor ? 0 : 1;
        HUDOverlay.Alpha = inspectionFamily is null
                           && scene == SkinStudioPreviewScene.Hud ? 1 : 0;
        DrawableRuleset.Colour = inspectionFamily is null
            ? Colour4.White
            : inspectionTint;
        HUDOverlay.Colour = inspectionFamily is null
            ? Colour4.White
            : inspectionTint;
        if (DrawableRuleset.Cursor is not null)
            DrawableRuleset.Cursor.Alpha = 0;
        assetPreviewColourLegend?.ConfigureScene(
            scene,
            inspectionFamily is not null);
        configureNativeJudgements();
        // Family inspection uses native components in its isolated overlay;
        // keep gameplay connections exclusive to non-inspection scenes.
        ((OsuPlayfield)DrawableRuleset.Playfield).FollowPoints.Alpha =
            inspectionFamily is null ? 1 : 0;
        applyObjectPreviewScale();
        configureSceneObjects();
        applyInspectionElementTints();
        configureStationarySliderReference();
        enforceExclusiveInspectionScene();
    }

    private void enforceExclusiveInspectionScene()
    {
        if (inspectionFamily?.Equals(
                "osu.followpoints",
                StringComparison.OrdinalIgnoreCase) != true)
        {
            return;
        }

        foreach (var hitObject in DrawableRuleset.Playfield.HitObjectContainer.Objects)
            hitObject.Alpha = 0;
        foreach (var slider in assetPreviewSliders)
            slider.Alpha = 0;
        foreach (var head in assetPreviewSliderHeads.Values)
            head.Alpha = 0;
    }

    private void configureNativeJudgements()
    {
        var playfield = (OsuPlayfield)DrawableRuleset.Playfield;
        if (inspectionFamily?.Equals(
                "osu.hitbursts",
                StringComparison.OrdinalIgnoreCase) != true)
        {
            playfield.ClearAssetPreviewJudgements();
            return;
        }

        var results = inspectionComponents
            .Select(component => component.ToLowerInvariant() switch
            {
                "hit0" => HitResult.Miss,
                "hit50" or "particle50" => HitResult.Meh,
                "hit100" or "particle100" => HitResult.Ok,
                "hit300" or "particle300" => HitResult.Great,
                _ => HitResult.None,
            })
            .Where(result => result != HitResult.None)
            .Distinct()
            .ToArray();
        playfield.ShowAssetPreviewJudgements(
            results.Length == 0
                ? [HitResult.Miss, HitResult.Meh, HitResult.Ok, HitResult.Great]
                : results,
            SkinStudioPreviewScenes.TimeMilliseconds(
                SkinStudioPreviewScene.Judgements) - 50);
    }

    private void configureSceneObjects()
    {
        var objects = DrawableRuleset.Playfield.HitObjectContainer.Objects
            .ToArray();
        foreach (var hitObject in objects)
        {
            hitObject.LifetimeStart = -60_000;
            hitObject.LifetimeEnd = 60_000;
            hitObject.Alpha = 0;
        }
        foreach (var circle in assetPreviewCircles)
            circle.Alpha = 0;
        foreach (var circle in numberPreviewCircles)
            circle.Alpha = 0;
        foreach (var slider in assetPreviewSliders)
            slider.Alpha = 0;
        foreach (var head in assetPreviewSliderHeads.Values)
            head.Alpha = 0;

        if (inspectionFamily is not null
            && configureExtrasInspection(objects, inspectionFamily))
        {
            return;
        }

        switch (scene)
        {
            case SkinStudioPreviewScene.Showcase:
                foreach (var circle in assetPreviewCircles)
                    stabilizeHitCircle(circle, showApproachCircle: true);
                foreach (var slider in assetPreviewSliders.Where(slider =>
                             Math.Abs(assetPreviewSliderSourceTimes[slider]
                                      - ShowcaseMidSliderStartTime) < 1
                             || Math.Abs(assetPreviewSliderSourceTimes[slider]
                                         - ShowcaseWaitingSliderStartTime) < 1))
                {
                    var waiting = Math.Abs(assetPreviewSliderSourceTimes[slider]
                                           - ShowcaseWaitingSliderStartTime) < 1;
                    stabilizeSlider(
                        slider,
                        waiting ? 0 : 0.5,
                        assetPreviewSliderHeads[slider],
                        showApproachCircle: true,
                        showBall: !waiting,
                        showFollowCircle: !waiting);
                }
                break;

            case SkinStudioPreviewScene.Circles:
                foreach (var circle in assetPreviewCircles)
                    stabilizeHitCircle(circle, showApproachCircle: true);
                break;

            case SkinStudioPreviewScene.Sliders:
                foreach (var slider in assetPreviewSliders.Where(slider =>
                             Math.Abs(assetPreviewSliderSourceTimes[slider] - 2_250) < 1
                             || Math.Abs(assetPreviewSliderSourceTimes[slider] - 2_500) < 1))
                {
                    stabilizeSlider(
                        slider,
                        Math.Abs(assetPreviewSliderSourceTimes[slider] - 2_250) < 1
                            ? 0
                            : 0.5,
                        assetPreviewSliderHeads[slider],
                        showApproachCircle: true,
                        showBall: Math.Abs(assetPreviewSliderSourceTimes[slider]
                                                   - 2_500) < 1,
                        showFollowCircle: Math.Abs(assetPreviewSliderSourceTimes[slider]
                                                         - 2_500) < 1);
                }
                break;

            case SkinStudioPreviewScene.Spinner:
                foreach (var spinner in objects.OfType<DrawableSpinner>())
                    spinner.Alpha = 1;
                break;

            case SkinStudioPreviewScene.Judgements:
                foreach (var hitObject in objects.Where(hitObject =>
                             hitObject.HitObject.StartTime is >= 10_500 and <= 11_000))
                    hitObject.Alpha = 1;
                break;
        }
    }

    private bool configureExtrasInspection(
        IReadOnlyList<DrawableHitObject> objects,
        string familyId)
    {
        switch (familyId.ToLowerInvariant())
        {
            case "osu.hitcircles":
                if (assetPreviewCircles.FirstOrDefault() is not { } circle)
                    return false;
                circle.Position = new Vector2(256, 192);
                stabilizeHitCircle(circle, showApproachCircle: true);
                return true;

            case "osu.combo-colours":
                if (assetPreviewCircles.Count == 0)
                    return false;
                for (var index = 0; index < assetPreviewCircles.Count; index++)
                {
                    var paletteCircle = assetPreviewCircles[index];
                    paletteCircle.Position = new Vector2(
                        256 + (index - (assetPreviewCircles.Count - 1) / 2f) * 92,
                        192);
                    paletteCircle.LifetimeStart = -60_000;
                    paletteCircle.LifetimeEnd = 60_000;
                    paletteCircle.Alpha = 1;
                    paletteCircle.Colour = Colour4.White;
                }
                return true;

            case "osu.followpoints":
                return true;

            case "osu.number-font":
                if (!inspectionComponents.Any(component =>
                        component.StartsWith("default-", StringComparison.OrdinalIgnoreCase)))
                {
                    return inspectionFamily is not null;
                }
                if (numberPreviewCircles.Count !=
                    SkinStudioSemanticPreviewCatalog.HitCircleNumberPreviewCount)
                    return false;
                for (var index = 0; index < numberPreviewCircles.Count; index++)
                {
                    var numberCircle = numberPreviewCircles[index];
                    numberCircle.Position = new Vector2(
                        82 + index % 5 * 87,
                        120 + index / 5 * 142);
                    stabilizeHitCircle(numberCircle, showApproachCircle: false);
                }
                return true;

            case "osu.slider":
            case "osu.slider-colours":
                var slider = assetPreviewSliders.FirstOrDefault(candidate =>
                                 Math.Abs(assetPreviewSliderSourceTimes[candidate] - 2_500) < 1)
                             ?? assetPreviewSliders.FirstOrDefault();
                if (slider is null)
                    return false;
                slider.Position = new Vector2(72, 192);
                var head = assetPreviewSliderHeads[slider];
                head.Position = new Vector2(72, 192);
                head.Alpha = 0;
                configureNativeSliderInspection(
                    slider,
                    familyId.Equals(
                        "osu.slider-colours",
                        StringComparison.OrdinalIgnoreCase));
                return true;

            case "osu.spinner":
                if (assetPreviewSpinner is null)
                    return false;
                assetPreviewSpinner.LifetimeStart = -60_000;
                assetPreviewSpinner.LifetimeEnd = 60_000;
                assetPreviewSpinner.Alpha = 1;
                assetPreviewSpinner.Colour = Colour4.White;
                return true;

            case "osu.hitbursts":
            case "osu.result-judgements":
            case "osu.comboburst":
                return true;

            case "osu.cursor":
            case "osu.star-particles":
                return true;

            case var _ when familyId.StartsWith(
                "audio.hitsounds.",
                StringComparison.OrdinalIgnoreCase):
                if (inspectionComponents.Any(component =>
                        component.Contains("slider", StringComparison.OrdinalIgnoreCase)))
                {
                    var audioSlider = assetPreviewSliders.FirstOrDefault(candidate =>
                                          Math.Abs(assetPreviewSliderSourceTimes[candidate] - 2_500) < 1)
                                      ?? assetPreviewSliders.FirstOrDefault();
                    if (audioSlider is null)
                        return false;
                    stabilizeSlider(
                        audioSlider,
                        0.5,
                        assetPreviewSliderHeads[audioSlider],
                        showApproachCircle: false,
                        showBall: true,
                        showFollowCircle: true);
                    return true;
                }
                var hitSoundCircles = assetPreviewCircles.Take(4).ToArray();
                for (var index = 0; index < hitSoundCircles.Length; index++)
                {
                    hitSoundCircles[index].Position = new Vector2(100 + index * 104, 192);
                    stabilizeHitCircle(hitSoundCircles[index], showApproachCircle: false);
                }
                return hitSoundCircles.Length > 0;

            case "audio.spinner":
                if (assetPreviewSpinner is null)
                    return false;
                assetPreviewSpinner.LifetimeStart = -60_000;
                assetPreviewSpinner.LifetimeEnd = 60_000;
                assetPreviewSpinner.Alpha = 1;
                return true;

            default:
                return inspectionFamily is not null;
        }
    }

    private void configureNativeSliderInspection(
        DrawableSlider slider,
        bool coloursOnly)
    {
        bool selected(string component) => inspectionComponents.Contains(component);

        slider.LifetimeStart = -60_000;
        slider.LifetimeEnd = 60_000;
        slider.Alpha = 1;
        slider.Colour = Colour4.White;

        var showBody = coloursOnly;
        slider.Body.Alpha = showBody ? 1 : 0;
        if (slider.Body.Drawable is { } body)
            body.Alpha = showBody ? 1 : 0;
        if (slider.SliderBody is not null)
        {
            slider.SliderBody.SnakingIn.Value = false;
            slider.SliderBody.SnakingOut.Value = false;
            slider.SliderBody.UpdateProgress(0);
            slider.SliderBody.Alpha = showBody ? 1 : 0;
        }

        slider.HeadCircle.Alpha = selected("sliderstartcircle")
                                  || selected("sliderstartcircleoverlay") ? 1 : 0;
        slider.TailCircle.Alpha = selected("sliderendcircle")
                                  || selected("sliderendcircleoverlay") ? 1 : 0;

        var showBall = selected("sliderb") || selected("sliderb0");
        var showFollowCircle = selected("sliderfollowcircle");
        slider.Ball.UpdateProgress(0.5);
        slider.Ball.SetPreviewPresentation(showBall, showFollowCircle);

        foreach (var nested in slider.NestedHitObjects)
        {
            switch (nested)
            {
                case DrawableSliderTick tick:
                    tick.ClearTransforms();
                    tick.Alpha = selected("sliderscorepoint") ? 1 : 0;
                    tick.Scale = Vector2.One;
                    break;

                case DrawableSliderRepeat repeat:
                    repeat.ClearTransforms();
                    repeat.Alpha = selected("reversearrow") ? 1 : 0;
                    repeat.Scale = Vector2.One;
                    repeat.CirclePiece.Alpha = 0;
                    repeat.Arrow.ClearTransforms();
                    repeat.Arrow.Alpha = selected("reversearrow") ? 1 : 0;
                    break;
            }
        }
    }

    private static void stabilizeHitCircle(
        DrawableHitCircle circle,
        bool showApproachCircle)
    {
        circle.SuppressHitAnimationsForPreview();
        circle.RestoreComboColourForPreview();
        circle.ClearTransforms();
        circle.LifetimeStart = -60_000;
        circle.LifetimeEnd = 60_000;
        circle.Alpha = 1;
        circle.Colour = Colour4.White;

        circle.CirclePiece.ClearTransforms();
        circle.CirclePiece.Alpha = 1;
        circle.CirclePiece.Colour = Colour4.White;
        if (circle.CirclePiece.Drawable is { } circleDrawable)
        {
            circleDrawable.ClearTransforms();
            circleDrawable.Alpha = 1;
            circleDrawable.Colour = Colour4.White;
        }

        circle.ApproachCircle.ClearTransforms();
        circle.ApproachCircle.LifetimeStart = -60_000;
        circle.ApproachCircle.LifetimeEnd = 60_000;
        circle.ApproachCircle.Alpha = showApproachCircle ? 1 : 0;
        circle.ApproachCircle.Scale = new Vector2(1.5f);
        if (circle.ApproachCircle.Drawable is { } approachDrawable)
        {
            approachDrawable.ClearTransforms();
            approachDrawable.Alpha = showApproachCircle ? 1 : 0;
        }
    }

    private static void stabilizeSlider(
        DrawableSlider slider,
        double ballProgress,
        DrawableHitCircle headPreview,
        bool showApproachCircle,
        bool showBall,
        bool showFollowCircle)
    {
        slider.SuppressHitAnimationsForPreview();
        slider.ClearTransforms();
        slider.LifetimeStart = -60_000;
        slider.LifetimeEnd = 60_000;
        slider.Alpha = 1;
        slider.Colour = Colour4.White;

        slider.Body.ClearTransforms();
        slider.Body.Alpha = 1;
        slider.Body.Colour = Colour4.White;
        if (slider.Body.Drawable is { } bodyDrawable)
        {
            bodyDrawable.ClearTransforms();
            bodyDrawable.Alpha = 1;
            bodyDrawable.Colour = Colour4.White;
        }
        if (slider.SliderBody is not null)
        {
            slider.SliderBody.SnakingIn.Value = false;
            slider.SliderBody.SnakingOut.Value = false;
            slider.SliderBody.UpdateProgress(0);
            slider.SliderBody.Alpha = 1;
            slider.SliderBody.Colour = Colour4.White;
        }

        // Keep the slider's own lazer head drawable so sliderstartcircle and
        // sliderstartcircleoverlay are resolved through their native lookup.
        // The separate generic circle remains only as a safe construction-time
        // fallback and must never cover the slider-specific artwork.
        headPreview.Alpha = 0;
        stabilizeHitCircle(slider.HeadCircle, showApproachCircle);
        slider.TailCircle.ClearTransforms();
        slider.TailCircle.LifetimeStart = -60_000;
        slider.TailCircle.LifetimeEnd = 60_000;
        slider.TailCircle.Alpha = 1;
        slider.TailCircle.Colour = Colour4.White;
        if (slider.TailCircle.CirclePiece is { } tailPiece)
        {
            tailPiece.ClearTransforms();
            tailPiece.Alpha = 1;
            tailPiece.Colour = Colour4.White;
            if (tailPiece.Drawable is { } tailDrawable)
            {
                tailDrawable.ClearTransforms();
                tailDrawable.Alpha = 1;
                tailDrawable.Colour = Colour4.White;
            }
        }
        slider.Ball.ClearTransforms();
        slider.Ball.Alpha = showBall || showFollowCircle ? 1 : 0;
        slider.Ball.UpdateProgress(ballProgress);
        slider.Ball.SetPreviewPresentation(showBall, showFollowCircle);
    }

    private void configureStationarySliderReference()
    {
        if (inspectionFamily is not null
            || scene != SkinStudioPreviewScene.Sliders)
            return;
        var reference = assetPreviewSliders.FirstOrDefault(slider =>
                            Math.Abs(assetPreviewSliderSourceTimes[slider]
                                     - StationarySliderStartTime) < 1)
                        ?? DrawableRuleset.Playfield.HitObjectContainer.Objects
                            .OfType<DrawableSlider>().FirstOrDefault(slider =>
                                Math.Abs(slider.HitObject.StartTime
                                         - StationarySliderStartTime) < 1);
        if (reference is null)
            return;

        // This is still lazer's real slider drawable and current skin. Its
        // path remains as a stable reference; its moving ball/follow circle is
        // the only part suppressed.
        reference.Alpha = 1;
        reference.Body.Alpha = 1;
        reference.HeadCircle.Alpha = 1;
        reference.TailCircle.Alpha = 1;
        reference.Ball.Alpha = 0;
    }

    public void Restart()
    {
        if (!LoadedBeatmapSuccessfully)
            return;

        GameplayClockContainer.Seek(0);
        GameplayClockContainer.Start();
    }

    public void Play()
    {
        if (LoadedBeatmapSuccessfully)
            GameplayClockContainer.Start();
    }

    internal void SeekAndPauseForAcceptance(double time)
    {
        if (!CanSeekForAcceptance)
            return;
        GameplayClockContainer.Seek(time);
        GameplayClockContainer.Stop();
    }

    internal bool CanSeekForAcceptance =>
        LoadedBeatmapSuccessfully
        && DrawableRuleset is { IsLoaded: true }
        && GameplayClockContainer is not null;

    internal double CurrentTime => GameplayClockContainer.CurrentTime;

    internal bool TryRequestColourEdit(Vector2 screenSpacePosition) =>
        assetPreviewColourLegend?.TryEditAtScreenPosition(screenSpacePosition)
        == true;

    internal void SetPreviewColour(
        SkinStudioRendererColourTarget target,
        Colour4 colour) =>
        assetPreviewColourLegend?.SetPreviewColour(target, colour);

    internal void SetInspectionTints(
        Colour4 colour,
        IReadOnlyDictionary<string, Colour4> elementTints)
    {
        inspectionTint = colour;
        inspectionElementTints.Clear();
        foreach (var (component, tint) in elementTints)
            inspectionElementTints[component] = tint;
        if (!CanSeekForAcceptance)
            return;
        DrawableRuleset.Colour = inspectionFamily is null
            ? Colour4.White
            : inspectionTint;
        HUDOverlay.Colour = inspectionFamily is null
            ? Colour4.White
            : inspectionTint;
        applyInspectionElementTints();
    }

    private void applyInspectionElementTints()
    {
        if (inspectionFamily is null || inspectionElementTints.Count == 0)
            return;

        Colour4 tintFor(Colour4 fallback, params string[] components)
        {
            foreach (var component in components)
            {
                if (inspectionElementTints.TryGetValue(component, out var tint))
                    return tint;
            }
            return fallback;
        }

        foreach (var circle in assetPreviewCircles.Where(circle => circle.Alpha > 0))
        {
            var bodyTint = tintFor(Colour4.White, "hitcircle");
            var overlayTint = tintFor(Colour4.White, "hitcircleoverlay");
            var numberTint = tintFor(Colour4.White, "default", "hitcircle-number");
            circle.AccentColour.Value = bodyTint;
            circle.CirclePiece.Colour = Colour4.White;
            if (circle.CirclePiece.Drawable is LegacyMainCirclePiece legacyPiece)
                legacyPiece.SetPreviewElementColours(bodyTint, overlayTint, numberTint);
            else if (circle.CirclePiece.Drawable is { } piece)
                piece.Colour = Colour4.White;
            var approachTint = tintFor(Colour4.White, "approachcircle");
            circle.ApproachCircle.Colour = approachTint;
            if (circle.ApproachCircle.Drawable is { } approach)
                approach.Colour = approachTint;
        }

        foreach (var slider in assetPreviewSliders.Where(slider => slider.Alpha > 0))
        {
            var bodyTint = tintFor(Colour4.White, "slider");
            slider.Body.Colour = bodyTint;
            if (slider.Body.Drawable is { } body)
                body.Colour = bodyTint;
            slider.Ball.Colour = Colour4.White;
            slider.Ball.SetPreviewElementColours(
                tintFor(Colour4.White, "sliderb"),
                tintFor(Colour4.White, "sliderfollowcircle"));
        }

        var familyTint = inspectionElementTints.Values.FirstOrDefault();
        if (familyTint != default)
        {
            foreach (var spinner in DrawableRuleset.Playfield.HitObjectContainer.Objects
                         .OfType<DrawableSpinner>()
                         .Where(spinner => spinner.Alpha > 0))
                spinner.Colour = familyTint;
            if (scene == SkinStudioPreviewScene.Judgements)
            {
                foreach (var hitObject in DrawableRuleset.Playfield.HitObjectContainer.Objects
                             .Where(hitObject => hitObject.Alpha > 0))
                    hitObject.Colour = familyTint;
            }
            if (scene == SkinStudioPreviewScene.Hud)
                HUDOverlay.Colour = familyTint;
        }
    }

    internal void SetObjectPreviewScale(float scale)
    {
        objectPreviewScale = Math.Clamp(scale, 0.6f, 1.5f);
        applyObjectPreviewScale();
    }

    internal void PulseHitSoundCircle(int index)
    {
        var visible = assetPreviewCircles.Where(circle => circle.Alpha > 0).Take(4).ToArray();
        if (visible.Length == 0)
            return;
        var circle = visible[Math.Abs(index) % visible.Length];
        circle.ClearTransforms();
        circle.Scale = Vector2.One;
        circle.ScaleTo(1.18f, 70, Easing.OutQuint)
            .Then()
            .ScaleTo(1, 210, Easing.OutQuint);
    }

    private void applyObjectPreviewScale()
    {
        var circleInspectionScale = inspectionFamily is not null
                                    && scene == SkinStudioPreviewScene.Circles
            ? 1.6f
            : 1f;
        const float sliderInspectionScale = 1f;
        foreach (var (circle, baseScale) in circleBaseScales)
            circle.HitObject.Scale = baseScale * objectPreviewScale
                                     * circleInspectionScale;
        foreach (var (slider, baseScale) in sliderBaseScales)
            slider.HitObject.Scale = baseScale * objectPreviewScale
                                     * sliderInspectionScale;
    }

    internal Vector2 GameplayPositionToScreenSpace(Vector2 position) =>
        DrawableRuleset.Playfield.ToScreenSpace(position);

    internal bool IsRendererSceneReady =>
        CanSeekForAcceptance
        && !DrawableRuleset.FrameStableClock.IsCatchingUp.Value;

    internal bool IsAcceptanceFrameReady(double time) =>
        CanSeekForAcceptance
        && Math.Abs(GameplayClockContainer.CurrentTime - time) <= 25
        && !DrawableRuleset.FrameStableClock.IsCatchingUp.Value;

    internal void SeekAndPlayForAudioAcceptance(double time)
    {
        GameplayClockContainer.Seek(time);
        GameplayClockContainer.Start();
    }

    internal void PauseForAcceptance() => GameplayClockContainer.Stop();

    protected override void Update()
    {
        base.Update();
        if (autoMotion && CanSeekForAcceptance)
        {
            var (start, end) = scene switch
            {
                SkinStudioPreviewScene.Sliders => (2_400d, 4_850d),
                SkinStudioPreviewScene.Cursor => (4_300d, 6_600d),
                SkinStudioPreviewScene.Spinner => (7_500d, 9_500d),
                _ => (double.NaN, double.NaN),
            };
            if (!double.IsNaN(start) && GameplayClockContainer.CurrentTime >= end)
            {
                GameplayClockContainer.Seek(start);
                GameplayClockContainer.Start();
            }
        }
        if (LoadedBeatmapSuccessfully && GameplayState.HasPassed)
            GameplayClockContainer.Seek(0);
    }

    protected override void UpdateAfterChildren()
    {
        base.UpdateAfterChildren();
        if (autoMotion
            && inspectionFamily?.Equals(
                "osu.slider",
                StringComparison.OrdinalIgnoreCase) == true)
        {
            var slider = assetPreviewSliders.FirstOrDefault(candidate =>
                Math.Abs(assetPreviewSliderSourceTimes[candidate] - 2_500) < 1);
            if (slider is not null && slider.HitObject.SpanDuration > 0)
            {
                var span = (CurrentTime - slider.HitObject.StartTime)
                           / slider.HitObject.SpanDuration;
                span %= 2;
                if (span < 0)
                    span += 2;
                slider.Ball.UpdateProgress(span <= 1 ? span : 2 - span);
            }
        }
        if (autoMotion
            && inspectionFamily?.Equals(
                "osu.spinner",
                StringComparison.OrdinalIgnoreCase) == true
            && assetPreviewSpinner is { Alpha: > 0 } spinner)
        {
            spinner.RotationTracker.AddRotation(
                Math.Clamp((float)(360 * Time.Elapsed / 1_000), 0, 45));
        }
    }
}

internal sealed partial class StudioExtrasInspectionOverlay : CompositeDrawable
{
    private Container content = null!;
    private ISkinSource skin = null!;
    private string configuredKey = "";
    private string? pendingFamilyId;
    private string[] pendingComponents = [];
    private int? pendingManiaKeyCount;
    private readonly Dictionary<string, List<Drawable>> componentDrawables =
        new(StringComparer.OrdinalIgnoreCase);

    internal bool HasVisibleContent { get; private set; }

    public StudioExtrasInspectionOverlay()
    {
        RelativeSizeAxes = Axes.Both;
        Alpha = 0;
    }

    [BackgroundDependencyLoader]
    private void load(ISkinSource skinSource)
    {
        skin = skinSource;
        InternalChild = content = new Container
        {
            Anchor = Anchor.Centre,
            Origin = Anchor.Centre,
            Size = new Vector2(512, 384),
        };
        if (pendingFamilyId is not null)
            Configure(pendingFamilyId, pendingComponents, pendingManiaKeyCount);
    }

    internal void Configure(
        string? familyId,
        IReadOnlyCollection<string> requestedComponents,
        int? maniaKeyCount = null)
    {
        pendingFamilyId = familyId;
        pendingComponents = requestedComponents.ToArray();
        pendingManiaKeyCount = maniaKeyCount;
        if (content is null)
            return;

        var components = requestedComponents
            .Where(component => !string.IsNullOrWhiteSpace(component))
            .Select(component => component.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(component => component, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var nextKey = $"{familyId}|{maniaKeyCount}|{string.Join('|', components)}";
        if (configuredKey.Equals(nextKey, StringComparison.Ordinal))
            return;
        configuredKey = nextKey;
        content.Clear(disposeChildren: true);
        componentDrawables.Clear();
        HasVisibleContent = false;
        Alpha = 0;
        if (string.IsNullOrWhiteSpace(familyId))
            return;

        switch (familyId.ToLowerInvariant())
        {
            case "osu.followpoints":
                buildFollowPoints(components);
                break;

            case "osu.slider":
                break;

            case "osu.hitbursts":
                break;

            case "osu.hitcircles":
            case "osu.slider-colours":
            case "osu.combo-colours":
            case "osu.cursor":
            case "osu.spinner":
                break;

            case "osu.number-font":
                buildNumberContext(components);
                break;

            case "interface.scorebar":
            case "interface.input-overlay":
            case "interface.countdown":
            case "interface.playfield":
            case "interface.pause":
            case "interface.background":
            case "interface.menu":
            case "interface.mod-icons":
            case "interface.song-select":
            case "interface.ranking":
            case "interface.leaderboard":
            case "metadata.previews":
                buildInterfaceContext(familyId, components);
                break;

            case "catch.fruits":
            case "catch.catcher":
            case "catch.comboburst":
                buildCatchContext(familyId, components);
                break;

            case "taiko.notes":
            case "taiko.rolls":
            case "taiko.playfield":
            case "taiko.pippidon":
                buildTaikoContext(familyId, components);
                break;

            case "mania.stage":
            case "mania.keys":
            case "mania.notes":
            case "mania.holds":
            case "mania.lighting":
            case "mania.hitbursts":
            case "mania.comboburst":
                buildManiaContext(familyId, components, maniaKeyCount ?? 4);
                break;

            case var _ when familyId.StartsWith("audio.", StringComparison.OrdinalIgnoreCase):
                buildAudioContext(familyId, components);
                break;

            default:
                buildAssetGrid(components);
                break;
        }

        Alpha = HasVisibleContent ? 1 : 0;
    }

    private void buildFollowPoints(IReadOnlyCollection<string> components)
    {
        if (!has(components, "followpoint"))
            return;

        var start = new Vector2(108, 250);
        var end = new Vector2(404, 134);
        var direction = end - start;
        var rotation = float.RadiansToDegrees(MathF.Atan2(direction.Y, direction.X));
        for (var index = 1; index <= 7; index++)
        {
            var progress = index / 8f;
            var point = new FollowPoint
            {
                Anchor = Anchor.TopLeft,
                Position = start + direction * progress,
                Rotation = rotation,
                Scale = new Vector2(0.72f),
                Alpha = 1,
                LifetimeStart = -60_000,
                LifetimeEnd = 60_000,
            };
            point.AnimationStartTime.Value = Time.Current - index * 32;
            add("followpoint", point);
        }
        HasVisibleContent = true;
    }

    private void buildNumberContext(IReadOnlyCollection<string> components)
    {
        var selected = components.FirstOrDefault() ?? "score-0";
        if (selected.StartsWith("default-", StringComparison.OrdinalIgnoreCase))
            return;
        var prefix = selected.Split('-', 2)[0].ToLowerInvariant();
        var value = prefix switch
        {
            "combo" => "1234x",
            "scoreentry" => "00421337",
            _ => "0098765.43%",
        };
        addContextPanel(prefix == "scoreentry" ? "LEADERBOARD  #1" :
            prefix == "combo" ? "COMBO" : "SCORE / ACCURACY");
        var glyphs = new List<(string Component, Drawable Drawable)>();
        foreach (var character in value)
        {
            var suffix = character switch
            {
                '.' => "dot",
                ',' => "comma",
                '%' => "percent",
                'x' or 'X' => "x",
                _ => character.ToString(),
            };
            var component = $"{prefix}-{suffix}";
            var drawable = skin.GetAnimation(
                component,
                animatable: true,
                looping: true,
                applyConfigFrameRate: true,
                startAtCurrentTime: false,
                maxSize: new Vector2(72));
            if (drawable is not null)
                glyphs.Add((component, drawable));
        }
        if (glyphs.Count == 0)
            return;
        var spacing = Math.Min(50, 410f / glyphs.Count);
        for (var index = 0; index < glyphs.Count; index++)
        {
            var entry = glyphs[index];
            entry.Drawable.Anchor = Anchor.Centre;
            entry.Drawable.Origin = Anchor.Centre;
            entry.Drawable.Position = new Vector2(
                (index - (glyphs.Count - 1) / 2f) * spacing,
                12);
            add(entry.Component, entry.Drawable);
        }
        HasVisibleContent = true;
    }

    private void buildInterfaceContext(
        string familyId,
        IReadOnlyCollection<string> requested)
    {
        addContextPanel(familyId switch
        {
            "interface.scorebar" => "GAMEPLAY HEALTH",
            "interface.input-overlay" => "LIVE INPUT",
            "interface.countdown" => "READY  ·  3  ·  2  ·  1  ·  GO",
            "interface.pause" => "PAUSED / FAILED",
            "interface.ranking" => "RESULTS",
            "interface.leaderboard" => "LEADERBOARD",
            "interface.song-select" => "SONG SELECT",
            _ => "INTERFACE CONTEXT",
        });
        var components = familyId switch
        {
            "interface.scorebar" =>
                new[] { "scorebar-bg", "scorebar-colour", "scorebar-marker", "scorebar-ki" },
            "interface.input-overlay" =>
                new[] { "inputoverlay-background", "inputoverlay-key" },
            "interface.countdown" => new[] { "ready", "count3", "count2", "count1", "go" },
            "interface.pause" =>
                new[] { "pause-overlay", "fail-background", "pause-continue", "pause-retry", "pause-back" },
            "interface.ranking" =>
                new[] { "ranking-panel", "ranking-graph", "ranking-accuracy", "ranking-maxcombo", "ranking-A" },
            "interface.leaderboard" =>
                new[] { "menu-button-background", "scoreentry-0", "scoreentry-1", "scoreentry-2" },
            _ => requested.ToArray(),
        };
        buildAssetRow(components, new Vector2(256, 204), familyId == "interface.scorebar" ? 126 : 92);
    }

    private void buildCatchContext(
        string familyId,
        IReadOnlyCollection<string> requested)
    {
        addPlayfieldFrame("CATCH PLAYFIELD");
        addCatchFallbackActors(familyId);
        var falling = familyId == "catch.fruits"
            ? new[] { "fruit-apple", "fruit-grapes", "fruit-orange", "fruit-pear", "fruit-drop", "fruit-bananas" }
            : requested.ToArray();
        for (var index = 0; index < falling.Length; index++)
        {
            tryAddAnimation(
                falling[index],
                new Vector2(96 + index % 4 * 105, 76 + index / 4 * 92),
                70);
        }
        var catcher = familyId == "catch.catcher"
            ? requested.FirstOrDefault() ?? "fruit-catcher-idle"
            : "fruit-catcher-idle";
        tryAddAnimation(catcher, new Vector2(256, 324), 150);
        if (familyId == "catch.comboburst")
            tryAddAnimation("comboburst-fruits", new Vector2(392, 170), 140);
        HasVisibleContent |= componentDrawables.Count > 0;
    }

    private void addCatchFallbackActors(string familyId)
    {
        if (familyId == "catch.fruits")
        {
            var colours = new[]
            {
                "#E95B64", "#AB72E9", "#F49B47", "#A4D457", "#6CC9EC",
            };
            for (var index = 0; index < colours.Length; index++)
            {
                content.Add(new CircularContainer
                {
                    Anchor = Anchor.TopLeft,
                    Origin = Anchor.Centre,
                    Position = new Vector2(92 + index * 82, 90 + index % 3 * 54),
                    Size = new Vector2(index == 4 ? 18 : 42),
                    Masking = true,
                    BorderThickness = 3,
                    BorderColour = Colour4.White,
                    Child = new Box
                    {
                        RelativeSizeAxes = Axes.Both,
                        Colour = Colour4.FromHex(colours[index]),
                    },
                });
            }
        }

        if (familyId is "catch.catcher" or "catch.comboburst")
        {
            content.Add(new CircularContainer
            {
                Anchor = Anchor.TopCentre,
                Origin = Anchor.Centre,
                Position = new Vector2(0, 318),
                Size = new Vector2(132, 38),
                Masking = true,
                BorderThickness = 4,
                BorderColour = Colour4.White,
                Child = new Box
                {
                    RelativeSizeAxes = Axes.Both,
                    Colour = Colour4.FromHex("#F06DA5"),
                },
            });
        }
    }

    private void buildTaikoContext(
        string familyId,
        IReadOnlyCollection<string> requested)
    {
        addPlayfieldFrame("TAIKO LANE");
        content.Add(new Box
        {
            Anchor = Anchor.Centre,
            Origin = Anchor.Centre,
            Size = new Vector2(450, 96),
            Colour = Colour4.FromHex("#241F2A"),
        });
        addTaikoFallbackActors(familyId);
        var components = familyId switch
        {
            "taiko.notes" => new[] { "taikohitcircle", "taikohitcircleoverlay", "taikobigcircle", "taiko-hit300" },
            "taiko.rolls" => new[] { "taiko-roll-middle", "taiko-roll-end", "sliderscorepoint" },
            "taiko.playfield" => new[] { "taiko-bar-left", "taiko-bar-right", "taiko-barline", "taiko-glow" },
            _ => requested.ToArray(),
        };
        for (var index = 0; index < components.Length; index++)
            tryAddAnimation(components[index], new Vector2(80 + index * 105, 192), 82);
        if (familyId == "taiko.pippidon")
            tryAddAnimation(requested.FirstOrDefault() ?? "pippidon-idle", new Vector2(420, 105), 130);
        HasVisibleContent |= componentDrawables.Count > 0;
    }

    private void addTaikoFallbackActors(string familyId)
    {
        if (familyId == "taiko.playfield")
        {
            content.Add(new Box
            {
                Anchor = Anchor.CentreLeft,
                Origin = Anchor.CentreLeft,
                Position = new Vector2(38, 0),
                Size = new Vector2(8, 120),
                Colour = Colour4.FromHex("#FFE49C"),
            });
            return;
        }

        var noteColours = familyId == "taiko.rolls"
            ? new[] { "#F6C65B", "#F6C65B", "#F6C65B" }
            : new[] { "#E6535F", "#54AEEB", "#E6535F", "#54AEEB" };
        for (var index = 0; index < noteColours.Length; index++)
        {
            content.Add(new CircularContainer
            {
                Anchor = Anchor.CentreLeft,
                Origin = Anchor.Centre,
                Position = new Vector2(108 + index * 102, 0),
                Size = new Vector2(familyId == "taiko.rolls" && index == 1 ? 82 : 48),
                Masking = true,
                BorderThickness = 4,
                BorderColour = Colour4.White,
                Child = new Box
                {
                    RelativeSizeAxes = Axes.Both,
                    Colour = Colour4.FromHex(noteColours[index]),
                },
            });
        }
    }

    private void buildManiaContext(
        string familyId,
        IReadOnlyCollection<string> requested,
        int maniaKeyCount)
    {
        maniaKeyCount = Math.Clamp(maniaKeyCount, 1, 18);
        addPlayfieldFrame($"MANIA  ·  {maniaKeyCount}K");
        var columnWidth = Math.Min(68, 320f / maniaKeyCount);
        for (var column = 0; column < maniaKeyCount; column++)
        {
            content.Add(new Box
            {
                Anchor = Anchor.TopCentre,
                Origin = Anchor.TopCentre,
                Position = new Vector2((column - (maniaKeyCount - 1) / 2f) * columnWidth, 54),
                Size = new Vector2(columnWidth - 4, 286),
                Colour = column % 2 == 0
                    ? Colour4.FromHex("#18151D")
                    : Colour4.FromHex("#211C27"),
            });
            var x = 256 + (column - (maniaKeyCount - 1) / 2f) * columnWidth;
            var isKey = familyId == "mania.keys";
            var isHold = familyId == "mania.holds";
            content.Add(new Box
            {
                Anchor = Anchor.TopLeft,
                Origin = Anchor.Centre,
                Position = new Vector2(x, isKey ? 318 : 108 + column % 4 * 45),
                Size = new Vector2(
                    Math.Max(10, columnWidth - 12),
                    isHold ? 112 : isKey ? 34 : 22),
                Colour = column % 2 == 0
                    ? Colour4.FromHex("#F1C9E0")
                    : Colour4.FromHex("#7EC8F2"),
            });
        }
        var selected = requested.FirstOrDefault();
        for (var column = 0; column < maniaKeyCount; column++)
        {
            var x = 256 + (column - (maniaKeyCount - 1) / 2f) * columnWidth;
            var component = selected ?? familyId switch
            {
                "mania.keys" => "mania-key",
                "mania.holds" => "mania-hold-body",
                "mania.lighting" => "lightingN",
                "mania.hitbursts" => "mania-hit300",
                "mania.stage" => "mania-stage-hint",
                _ => "mania-note",
            };
            var y = familyId == "mania.keys" ? 316 : 104 + column % 4 * 48;
            tryAddAnimation(component, new Vector2(x, y), familyId == "mania.holds" ? 96 : 62);
        }
        HasVisibleContent |= componentDrawables.Count > 0;
    }

    private void buildAudioContext(
        string familyId,
        IReadOnlyCollection<string> requested)
    {
        if (familyId.StartsWith("audio.hitsounds.", StringComparison.OrdinalIgnoreCase))
        {
            var slider = requested.Any(component =>
                component.Contains("slider", StringComparison.OrdinalIgnoreCase));
            content.Add(new SpriteText
            {
                Anchor = Anchor.BottomCentre,
                Origin = Anchor.BottomCentre,
                Y = -28,
                Text = slider
                    ? "SLIDER AUDIO  ·  SLIDE / WHISTLE / TICKS"
                    : "120 BPM  ·  NORMAL  ·  WHISTLE  ·  FINISH  ·  CLAP",
                Font = FontUsage.Default.With(size: 14, weight: "Bold"),
                Colour = Colour4.FromHex("#F3D6E8"),
            });
            HasVisibleContent = true;
            return;
        }
        if (familyId == "audio.spinner")
        {
            content.Add(new SpriteText
            {
                Anchor = Anchor.BottomCentre,
                Origin = Anchor.BottomCentre,
                Y = -28,
                Text = "SPINNER AUDIO  ·  SPIN / BONUS / MAX",
                Font = FontUsage.Default.With(size: 14, weight: "Bold"),
                Colour = Colour4.FromHex("#F3D6E8"),
            });
            HasVisibleContent = true;
            return;
        }
        addContextPanel(familyId switch
        {
            "audio.countdown" => "READY  ·  3  ·  2  ·  1  ·  GO",
            "audio.combobreak" => "COMBO 20  →  MISS",
            "audio.failsound" => "FAILED",
            "audio.sectionpass" => "SECTION PASSED",
            "audio.sectionfail" => "SECTION FAILED",
            "audio.applause" => "RESULTS  ·  GRADE",
            "audio.welcome" => "WELCOME",
            "audio.seeya" => "SEE YOU NEXT TIME",
            "audio.nightcore" => "NIGHTCORE  ·  120 BPM",
            _ => "INTERFACE SOUND EVENT",
        });
        var visual = familyId switch
        {
            "audio.countdown" => new[] { "ready", "count3", "count2", "count1", "go" },
            "audio.sectionpass" => new[] { "section-pass" },
            "audio.sectionfail" => new[] { "section-fail" },
            "audio.failsound" => new[] { "fail-background" },
            "audio.applause" => new[] { "ranking-panel", "ranking-A" },
            _ => Array.Empty<string>(),
        };
        buildAssetRow(visual, new Vector2(256, 210), 96);
        HasVisibleContent = true;
    }

    private void addContextPanel(string title)
    {
        content.Add(new Box
        {
            Anchor = Anchor.Centre,
            Origin = Anchor.Centre,
            Size = new Vector2(456, 250),
            Colour = Colour4.FromHex("#17131C"),
        });
        content.Add(new SpriteText
        {
            Anchor = Anchor.Centre,
            Origin = Anchor.Centre,
            Position = new Vector2(0, -88),
            Text = title,
            Font = FontUsage.Default.With(size: 18, weight: "Bold"),
            Colour = Colour4.FromHex("#F3D6E8"),
        });
        HasVisibleContent = true;
    }

    private void addPlayfieldFrame(string title)
    {
        content.Add(new Box
        {
            RelativeSizeAxes = Axes.Both,
            Colour = Colour4.FromHex("#100D14"),
        });
        content.Add(new SpriteText
        {
            Anchor = Anchor.TopCentre,
            Origin = Anchor.TopCentre,
            Y = 16,
            Text = title,
            Font = FontUsage.Default.With(size: 16, weight: "Bold"),
            Colour = Colour4.FromHex("#F3D6E8"),
        });
        HasVisibleContent = true;
    }

    private void buildAssetRow(
        IReadOnlyList<string> components,
        Vector2 centre,
        float cellSize)
    {
        for (var index = 0; index < components.Count; index++)
        {
            var x = centre.X + (index - (components.Count - 1) / 2f) * cellSize;
            tryAddAnimation(components[index], new Vector2(x, centre.Y), cellSize - 10);
        }
        HasVisibleContent |= componentDrawables.Count > 0;
    }

    private void tryAddAnimation(string component, Vector2 position, float maxSize)
    {
        // Do not ask legacy animation sources to synthesise a fallback for a
        // component they do not expose. Some ruleset-specific lookups defer
        // that failure until drawable load and would invalidate the complete
        // semantic host for an otherwise valid sparse skin.
        if (skin.GetTexture(component) is null
            && skin.GetTexture($"{component}-0") is null)
        {
            return;
        }
        var drawable = skin.GetAnimation(
            component,
            animatable: true,
            looping: true,
            applyConfigFrameRate: true,
            startAtCurrentTime: false,
            maxSize: new Vector2(maxSize));
        if (drawable is null)
            return;
        drawable.Anchor = Anchor.TopLeft;
        drawable.Origin = Anchor.Centre;
        drawable.Position = position;
        drawable.Alpha = 1;
        add(component, drawable);
    }
    private void buildAssetGrid(
        IReadOnlyCollection<string> components,
        Vector2? centre = null,
        float cellSize = 92,
        float scale = 1)
    {
        var drawables = components
            .Select(component => (Component: component, Drawable: skin.GetAnimation(
                component,
                animatable: true,
                looping: true,
                applyConfigFrameRate: true,
                startAtCurrentTime: false,
                maxSize: new Vector2(cellSize - 10))))
            .Where(entry => entry.Drawable is not null)
            .ToArray();
        for (var index = 0; index < drawables.Length; index++)
        {
            var component = drawables[index].Component;
            var drawable = drawables[index].Drawable!;
            drawable.Anchor = Anchor.Centre;
            drawable.Origin = Anchor.Centre;
            drawable.Position = gridPosition(
                index,
                drawables.Length,
                centre ?? new Vector2(256, 192),
                cellSize,
                cellSize);
            drawable.Scale *= scale;
            drawable.Alpha = 1;
            add(component, drawable);
        }
        HasVisibleContent |= drawables.Length > 0;
    }

    internal void SetElementTints(
        IReadOnlyDictionary<string, Colour4> elementTints)
    {
        foreach (var drawables in componentDrawables.Values)
        {
            foreach (var drawable in drawables)
                drawable.Colour = Colour4.White;
        }
        foreach (var (component, tint) in elementTints)
        {
            if (!componentDrawables.TryGetValue(component, out var drawables))
                continue;
            foreach (var drawable in drawables)
                drawable.Colour = tint;
        }
    }

    private void add(string component, Drawable drawable)
    {
        content.Add(drawable);
        if (!componentDrawables.TryGetValue(component, out var drawables))
            componentDrawables[component] = drawables = [];
        drawables.Add(drawable);
    }

    private static SkinnableDrawable skinnable(
        ISkinComponentLookup lookup,
        Vector2 position,
        float size,
        float scale = 1) => new(
        lookup,
        _ => Drawable.Empty(),
        ConfineMode.ScaleToFit)
        {
            RelativeSizeAxes = Axes.None,
            Size = new Vector2(size),
            Position = position,
            Anchor = Anchor.TopLeft,
            Origin = Anchor.Centre,
            Scale = new Vector2(scale),
            Alpha = 1,
        };

    private static Vector2 gridPosition(
        int index,
        int count,
        Vector2 centre,
        float cellWidth,
        float cellHeight)
    {
        var columns = Math.Min(4, Math.Max(1, (int)Math.Ceiling(Math.Sqrt(count))));
        var rows = (int)Math.Ceiling(count / (double)columns);
        var column = index % columns;
        var row = index / columns;
        return new Vector2(
            centre.X + (column - (columns - 1) / 2f) * cellWidth,
            centre.Y + (row - (rows - 1) / 2f) * cellHeight);
    }

    private static bool has(
        IEnumerable<string> components,
        string component) => components.Contains(
        component,
        StringComparer.OrdinalIgnoreCase);
}

internal sealed partial class StudioPreviewColourLegend : CompositeDrawable
{
    private readonly IReadOnlyList<DrawableHitCircle> circles;
    private readonly IReadOnlyList<DrawableSlider> sliders;
    private readonly IReadOnlyList<Drawable> circleDecorations;
    private readonly IReadOnlyList<Drawable> sliderDecorations;
    private readonly List<(Vector2 Position, Action<Vector2> Edit)> circleEditRegions = [];
    private readonly List<(Vector2 Position, Action<Vector2> Edit)> sliderEditRegions = [];
    private bool circlesVisible;
    private bool slidersVisible;

    public override bool HandlePositionalInput => true;

    public override bool ReceivePositionalInputAt(Vector2 screenSpacePos) =>
        IsPresent && base.ReceivePositionalInputAt(screenSpacePos);

    internal StudioPreviewColourLegend(
        IReadOnlyList<DrawableHitCircle> circles,
        IReadOnlyList<DrawableSlider> sliders,
        IReadOnlyDictionary<DrawableSlider, double> sliderSourceTimes,
        Action<
            SkinStudioRendererColourTarget,
            Colour4,
            Vector2,
            Vector2,
            Vector2> editColour)
    {
        this.circles = circles;
        this.sliders = sliders;
        Size = new Vector2(512, 384);
        Anchor = Anchor.Centre;
        Origin = Anchor.Centre;
        Depth = float.MinValue;
        AlwaysPresent = true;

        var circleItems = new List<Drawable>();
        for (var index = 0; index < circles.Count; index++)
        {
            var circle = circles[index];
            var y = Math.Max(6, circle.HitObject.Position.Y - 64);
            var chipPosition = new Vector2(circle.HitObject.Position.X, y);
            var targetPosition = circle.HitObject.Position + new Vector2(-18, -25);
            circleItems.Add(leaderLine(
                chipPosition + new Vector2(-12, 18),
                targetPosition));
            var target = (SkinStudioRendererColourTarget)(
                (int)SkinStudioRendererColourTarget.Combo1 + index);
            Action<Vector2> edit = anchor =>
            {
                var bounds = circle.ApproachCircle.ScreenSpaceDrawQuad.AABBFloat;
                editColour(
                    target,
                    circle.AccentColour.Value,
                    anchor,
                    bounds.TopLeft,
                    bounds.BottomRight);
            };
            circleEditRegions.Add((chipPosition, edit));
            circleItems.Add(new StudioColourChip(
                "RGB",
                () => circle.AccentColour.Value,
                () => edit(ToScreenSpace(chipPosition + new Vector2(0, 9))))
            {
                Position = chipPosition,
            });
        }

        var colourSource = sliders.FirstOrDefault(slider =>
                               sliderSourceTimes.TryGetValue(slider, out var source)
                               && Math.Abs(source - StudioScenePlayer.ShowcaseMidSliderStartTime) < 1)
                           ?? sliders.FirstOrDefault();
        var sliderItems = new List<Drawable>();
        if (colourSource is not null)
        {
            var innerPosition = new Vector2(132, 146);
            var outerPosition = new Vector2(380, 146);
            sliderItems.Add(leaderLine(
                innerPosition + new Vector2(0, 18),
                new Vector2(118, 205)));
            sliderItems.Add(leaderLine(
                outerPosition + new Vector2(0, 18),
                new Vector2(410, 191)));
            Action<Vector2> editInner = anchor =>
            {
                var bounds = colourSource.ScreenSpaceDrawQuad.AABBFloat;
                editColour(
                    SkinStudioRendererColourTarget.SliderInner,
                    colourSource.SliderBody?.AccentColour
                    ?? colourSource.AccentColour.Value,
                    anchor,
                    bounds.TopLeft,
                    bounds.BottomRight);
            };
            Action<Vector2> editOuter = anchor =>
            {
                var bounds = colourSource.ScreenSpaceDrawQuad.AABBFloat;
                editColour(
                    SkinStudioRendererColourTarget.SliderOuter,
                    colourSource.SliderBody?.BorderColour
                    ?? Colour4.White,
                    anchor,
                    bounds.TopLeft,
                    bounds.BottomRight);
            };
            sliderEditRegions.Add((innerPosition, editInner));
            sliderEditRegions.Add((outerPosition, editOuter));
            sliderItems.Add(new StudioColourChip(
                "INNER",
                () => colourSource.SliderBody?.AccentColour
                      ?? colourSource.AccentColour.Value,
                () => editInner(ToScreenSpace(innerPosition + new Vector2(0, 9))))
            {
                Position = innerPosition,
            });
            sliderItems.Add(new StudioColourChip(
                "OUTER",
                () => colourSource.SliderBody?.BorderColour
                      ?? Colour4.White,
                () => editOuter(ToScreenSpace(outerPosition + new Vector2(0, 9))))
            {
                Position = outerPosition,
            });
        }

        circleDecorations = circleItems;
        sliderDecorations = sliderItems;
        InternalChildren = circleItems
                                      .Concat(sliderItems)
                                      .ToArray();
    }

    internal void ConfigureScene(
        SkinStudioPreviewScene scene,
        bool inspection = false)
    {
        var showCircles = !inspection && scene is SkinStudioPreviewScene.Showcase
            or SkinStudioPreviewScene.Circles;
        var showSliders = !inspection && scene is SkinStudioPreviewScene.Showcase
            or SkinStudioPreviewScene.Sliders;
        circlesVisible = showCircles;
        slidersVisible = showSliders;
        foreach (var decoration in circleDecorations)
            decoration.Alpha = showCircles ? 1 : 0;
        foreach (var decoration in sliderDecorations)
            decoration.Alpha = showSliders ? 1 : 0;
    }

    protected override bool OnClick(ClickEvent e)
    {
        return tryEditAt(e.MousePosition, e.ScreenSpaceMousePosition);
    }

    internal bool TryEditAtScreenPosition(Vector2 screenSpacePosition)
    {
        return tryEditAt(ToLocalSpace(screenSpacePosition), screenSpacePosition);
    }

    internal void SetPreviewColour(
        SkinStudioRendererColourTarget target,
        Colour4 colour)
    {
        var comboIndex = (int)target
                         - (int)SkinStudioRendererColourTarget.Combo1;
        if (comboIndex >= 0 && comboIndex < circles.Count)
        {
            circles[comboIndex].AccentColour.Value = colour;
            return;
        }

        foreach (var slider in sliders)
        {
            var body = slider.SliderBody;
            if (body is null)
                continue;
            if (target == SkinStudioRendererColourTarget.SliderInner)
            {
                body.AccentColour = new Colour4(
                    colour.R,
                    colour.G,
                    colour.B,
                    body.AccentColour.A);
            }
            else if (target == SkinStudioRendererColourTarget.SliderOuter)
            {
                body.BorderColour = new Colour4(
                    colour.R,
                    colour.G,
                    colour.B,
                    body.BorderColour.A);
            }
        }
    }

    private bool tryEditAt(
        Vector2 localPosition,
        Vector2 screenSpacePosition)
    {
        IEnumerable<(Vector2 Position, Action<Vector2> Edit)> regions =
            circlesVisible && slidersVisible
            ? circleEditRegions.Concat(sliderEditRegions)
            : circlesVisible
                ? circleEditRegions
                : sliderEditRegions;
        foreach (var (position, edit) in regions)
        {
            if (Math.Abs(localPosition.X - position.X) <= 62
                && localPosition.Y >= position.Y
                && localPosition.Y <= position.Y + 18)
            {
                edit(screenSpacePosition);
                return true;
            }
        }
        return false;
    }

    private static Drawable leaderLine(Vector2 start, Vector2 end)
    {
        var difference = end - start;
        return new Box
        {
            Position = start,
            Origin = Anchor.CentreLeft,
            Size = new Vector2(difference.Length, 2),
            Rotation = MathF.Atan2(difference.Y, difference.X) * 180 / MathF.PI,
            Colour = Colour4.White.Opacity(0.9f),
            Depth = 1,
        };
    }
}

internal sealed partial class StudioRendererInteractionLayer
    : ClickableContainer, IProvideCursor
{
    private readonly Func<Vector2, bool> click;
    private readonly CursorContainer userCursor;

    internal StudioRendererInteractionLayer(
        Func<Vector2, bool> click,
        CursorContainer userCursor)
    {
        this.click = click;
        this.userCursor = userCursor;
    }

    CursorContainer IProvideCursor.Cursor => userCursor;

    public bool ProvidingUserCursor => true;

    // This layer intentionally has no visible child. Treat its full bounds as
    // interactive so the standard lazer menu cursor can click renderer-owned
    // affordances without requiring a fake transparent drawable.
    public override bool ReceivePositionalInputAt(Vector2 screenSpacePos) =>
        IsPresent;

    protected override bool OnHover(HoverEvent e) => true;

    protected override bool OnClick(ClickEvent e) =>
        click(e.ScreenSpaceMousePosition);
}

internal sealed partial class StudioColourChip : ClickableContainer
{
    private readonly string label;
    private readonly Func<Colour4> colourSource;
    private readonly Box swatch;
    private readonly SpriteText text;
    private Colour4 lastColour = new(float.NaN, float.NaN, float.NaN, float.NaN);

    internal StudioColourChip(
        string label,
        Func<Colour4> colourSource,
        Action editColour)
    {
        this.label = label;
        this.colourSource = colourSource;
        Size = new Vector2(124, 18);
        Origin = Anchor.TopCentre;
        Masking = true;
        CornerRadius = 4;
        BorderThickness = 1;
        BorderColour = Colour4.White.Opacity(0.25f);
        Action = editColour;
        InternalChildren =
        [
            new Box
            {
                RelativeSizeAxes = Axes.Both,
                Colour = Colour4.Black.Opacity(0.82f),
            },
            swatch = new Box
            {
                Position = new Vector2(6, 4),
                Size = new Vector2(10),
            },
            text = new SpriteText
            {
                Position = new Vector2(21, 3),
                Font = FontUsage.Default.With(size: 9, weight: "SemiBold"),
                Colour = Colour4.White,
            },
        ];
    }

    protected override void Update()
    {
        base.Update();
        var colour = colourSource();
        if (colour == lastColour)
            return;
        lastColour = colour;
        swatch.Colour = new Colour4(colour.R, colour.G, colour.B, 1);
        text.Text = $"{label} {toByte(colour.R)}, {toByte(colour.G)}, {toByte(colour.B)}";
    }

    private static int toByte(float value) =>
        (int)Math.Round(Math.Clamp(value, 0, 1) * 255);
}

internal static class StudioSceneCursorPath
{
    internal static Vector2 PositionAt(
        SkinStudioPreviewScene scene,
        double time)
    {
        var (start, end) = scene == SkinStudioPreviewScene.Showcase
            ? (StudioScenePlayer.ShowcaseCursorCycleStart,
                StudioScenePlayer.ShowcaseCursorCycleEnd)
            : (4_300d, 6_600d);
        var progress = (float)((time - start) / (end - start) % 1);
        if (progress < 0)
            progress += 1;
        var points = scene == SkinStudioPreviewScene.Showcase
            ? new[]
            {
                // Orbit the stationary inspection objects instead of crossing
                // them. This keeps the neutral slider visually unambiguous
                // while still exercising the real skin cursor and trail.
                new Vector2(40, 160),
                new Vector2(256, 150),
                new Vector2(472, 160),
                new Vector2(480, 260),
                new Vector2(472, 355),
                new Vector2(256, 350),
                new Vector2(40, 355),
                new Vector2(32, 260),
            }
            : new[]
            {
                new Vector2(96, 96),
                new Vector2(416, 96),
                new Vector2(416, 288),
                new Vector2(96, 288),
                new Vector2(256, 192),
            };
        var scaled = progress * points.Length;
        var segment = (int)MathF.Floor(scaled) % points.Length;
        var local = scaled - MathF.Floor(scaled);
        var p0 = points[(segment - 1 + points.Length) % points.Length];
        var p1 = points[segment];
        var p2 = points[(segment + 1) % points.Length];
        var p3 = points[(segment + 2) % points.Length];
        var local2 = local * local;
        var local3 = local2 * local;
        return 0.5f * ((2 * p1)
                       + (-p0 + p2) * local
                       + (2 * p0 - 5 * p1 + 4 * p2 - p3) * local2
                       + (-p0 + 3 * p1 - 3 * p2 + p3) * local3);
    }
}

internal partial class StudioSkinCursorContainer : OsuCursorContainer
{
    private float previewScale = 1;
    private Colour4 layerBaseTint = Colour4.White;
    private readonly Dictionary<string, Colour4> layerTints =
        new(StringComparer.OrdinalIgnoreCase);

    private bool manualMovement;
    private Vector2? lastManualScreenPosition;

    internal bool ManualMovement
    {
        get => manualMovement;
        set
        {
            if (manualMovement == value)
                return;
            manualMovement = value;
            lastManualScreenPosition = null;
            foreach (var trail in this.ChildrenOfType<CursorTrail>())
            {
                trail.ExternalPositionUpdatesOnly = value;
                trail.ResetTrail();
            }
        }
    }
    internal Vector2 ManualPosition { get; set; }

    // Scripted inspection motion is the only position source while active.
    // Letting real WPF/SDL mouse events reach CursorTrail as well makes lazer's
    // resampler bridge the two unrelated positions with long straight streaks.
    public override bool HandlePositionalInput => !ManualMovement;

    internal float PreviewScale
    {
        get => previewScale;
        set
        {
            previewScale = Math.Clamp(value, 0.5f, 4f);
            if (IsLoaded)
                ActiveCursor.ModScaleAdjust.Value = previewScale;
        }
    }

    internal void SetLayerTints(
        Colour4 baseTint,
        IReadOnlyDictionary<string, Colour4> elementTints)
    {
        layerBaseTint = baseTint;
        layerTints.Clear();
        foreach (var (component, tint) in elementTints)
            layerTints[component] = tint;
        applyLayerTints();
    }

    protected override void LoadComplete()
    {
        base.LoadComplete();
        ActiveCursor.ModScaleAdjust.Value = previewScale;
    }

    protected override void Update()
    {
        base.Update();
        applyLayerTints();
        if (ManualMovement)
        {
            ActiveCursor.Position = ManualPosition;
            var screenSpacePosition = ToScreenSpace(ManualPosition);
            var positionDiscontinuity = lastManualScreenPosition is null
                                        || Vector2.Distance(
                                            lastManualScreenPosition.Value,
                                            screenSpacePosition) > 80;
            foreach (var trail in this.ChildrenOfType<CursorTrail>())
            {
                trail.ExternalPositionUpdatesOnly = true;
                if (positionDiscontinuity)
                    trail.ResetTrail();
                trail.UpdateCursorPosition(screenSpacePosition);
            }
            lastManualScreenPosition = screenSpacePosition;
        }
        else
        {
            lastManualScreenPosition = null;
            foreach (var trail in this.ChildrenOfType<CursorTrail>())
                trail.ExternalPositionUpdatesOnly = false;
        }
    }

    private void applyLayerTints()
    {
        Colour = layerBaseTint;
        ActiveCursor.Colour = layerTints.TryGetValue("cursor", out var cursorTint)
            ? cursorTint
            : Colour4.White;
        var trailTint = layerTints.TryGetValue("cursortrail", out var selectedTrailTint)
            ? selectedTrailTint
            : Colour4.White;
        foreach (var trail in this.ChildrenOfType<CursorTrail>())
            trail.Colour = trailTint;
    }
}

internal sealed class StudioWorkingBeatmap : WorkingBeatmap
{
    private readonly IBeatmap beatmap;
    private readonly NativeStorage mediaStorage;
    private readonly IResourceStore<byte[]> resources;
    private readonly ITrackStore trackStore;
    private readonly LargeTextureStore textureStore;

    public StudioWorkingBeatmap(
        string beatmapPath,
        AudioManager audio,
        GameHost host,
        int comboColourCount = 8)
        : this(DecodePreview(beatmapPath, comboColourCount), beatmapPath, audio, host)
    {
    }

    private StudioWorkingBeatmap(
        Beatmap decoded,
        string beatmapPath,
        AudioManager audio,
        GameHost host)
        : base(decoded.BeatmapInfo, audio)
    {
        beatmap = decoded;
        mediaStorage = new NativeStorage(Path.GetDirectoryName(beatmapPath)!);
        resources = new StorageBackedResourceStore(mediaStorage);
        trackStore = audio.GetTrackStore(resources);
        textureStore = new LargeTextureStore(
            host.Renderer,
            host.CreateTextureLoaderStore(resources));
    }

    public override bool BeatmapLoaded => true;
    protected override IBeatmap GetBeatmap() => beatmap;

    public override Texture? GetBackground() =>
        string.IsNullOrWhiteSpace(Metadata.BackgroundFile)
            ? null
            : textureStore.Get(Metadata.BackgroundFile);

    protected override Track? GetBeatmapTrack() =>
        string.IsNullOrWhiteSpace(Metadata.AudioFile)
            ? null
            : trackStore.Get(Metadata.AudioFile);

    protected override ISkin? GetSkin() => null;
    public override Stream? GetStream(string storagePath) =>
        resources.GetStream(storagePath);

    internal static Beatmap Decode(string path)
    {
        using var stream = File.OpenRead(path);
        using var reader = new LineBufferedReader(stream);
        return Decoder.GetDecoder<Beatmap>(reader).Decode(reader);
    }

    internal static Beatmap DecodePreview(string path, int comboColourCount)
    {
        var decoded = Decode(path);
        var keep = Math.Clamp(comboColourCount, 1, 8);
        var paletteCircles = decoded.HitObjects
            .Where(hitObject => hitObject.StartTime
                                    is >= StudioScenePlayer.ShowcasePaletteStartTime
                                    and <= StudioScenePlayer.ShowcasePaletteEndTime
                                && hitObject.GetType().Name.Contains(
                                    "HitCircle",
                                    StringComparison.Ordinal))
            .ToArray();
        foreach (var extra in paletteCircles.Skip(keep))
            decoded.HitObjects.Remove(extra);
        return decoded;
    }
}

internal sealed partial class StudioToolsOverlay : CompositeDrawable
{
    public StudioToolsOverlay(IReadOnlyList<Drawable> tools)
    {
        RelativeSizeAxes = Axes.Both;
        Depth = -115;
        InternalChildren =
        [
            new StudioDismissLayer(Hide),
            new Container
            {
                Width = 430,
                RelativeSizeAxes = Axes.Y,
                Anchor = Anchor.TopRight,
                Origin = Anchor.TopRight,
                Padding = new MarginPadding
                {
                    Top = 18,
                    Right = 18,
                    Bottom = 18,
                },
                Child = new Container
                {
                    RelativeSizeAxes = Axes.Both,
                    Masking = true,
                    CornerRadius = 12,
                    BorderThickness = 1,
                    BorderColour = Colour4.FromHex("#6F3654"),
                    Children =
                    [
                        new Box
                        {
                            RelativeSizeAxes = Axes.Both,
                            Colour = Colour4.FromHex("#18151F"),
                        },
                        new OsuScrollContainer(Direction.Vertical)
                        {
                            RelativeSizeAxes = Axes.Both,
                            ScrollbarVisible = true,
                            Child = new FillFlowContainer
                            {
                                RelativeSizeAxes = Axes.X,
                                AutoSizeAxes = Axes.Y,
                                Direction = FillDirection.Vertical,
                                Spacing = new Vector2(0, 10),
                                Padding = new MarginPadding(20),
                                Children =
                                [
                                    new SpriteText
                                    {
                                        Text = "MORE TOOLS",
                                        Font = FontUsage.Default.With(
                                            size: 18,
                                            weight: "Bold"),
                                        Colour = Colour4.FromHex("#FFB7D5"),
                                    },
                                    new StudioActionButton(
                                        "Close",
                                        Hide,
                                        accent: true),
                                    .. tools,
                                ],
                            },
                        },
                    ],
                },
            },
        ];
        Hide();
    }

    public void Present() => Show();
}

internal sealed partial class StudioDismissLayer : ClickableContainer
{
    private readonly Action dismiss;

    public StudioDismissLayer(Action dismiss)
    {
        this.dismiss = dismiss;
        RelativeSizeAxes = Axes.Both;
        Child = new Box
        {
            RelativeSizeAxes = Axes.Both,
            Colour = Colour4.Black.Opacity(0.72f),
        };
    }

    protected override bool OnClick(ClickEvent e)
    {
        dismiss();
        return true;
    }
}

internal partial class StudioActionButton : ClickableContainer, IHasTooltip
{
    private const string default_disabled_reason =
        "This action is unavailable until its required selection or data is ready.";

    private readonly Action action;
    private readonly Box background;
    private readonly SpriteText label;
    private readonly bool accent;
    private bool enabled;
    private bool selected;
    private string disabledReason;

    public StudioActionButton(
        string text,
        Action action,
        bool accent = false,
        bool enabled = true,
        string? disabledReason = null)
    {
        this.action = action;
        this.accent = accent;
        this.enabled = enabled;
        this.disabledReason = string.IsNullOrWhiteSpace(disabledReason)
            ? default_disabled_reason
            : disabledReason;
        Alpha = enabled ? 1 : 0.42f;
        RelativeSizeAxes = Axes.X;
        Height = 38;
        Masking = true;
        CornerRadius = 7;
        Children =
        [
            background = new Box
            {
                RelativeSizeAxes = Axes.Both,
                Colour = accent
                    ? Colour4.FromHex("#8C3E66")
                    : Colour4.FromHex("#2B2634"),
            },
            label = new SpriteText
            {
                Text = text,
                Anchor = Anchor.CentreLeft,
                Origin = Anchor.CentreLeft,
                Margin = new MarginPadding { Left = 12 },
                Font = FontUsage.Default.With(size: 13, weight: "SemiBold"),
                Colour = Colour4.White,
            },
        ];
    }

    protected override bool OnClick(ClickEvent e)
    {
        if (enabled)
            action();
        return true;
    }

    protected override bool OnHover(HoverEvent e)
    {
        if (!enabled)
            return base.OnHover(e);
        background.FadeColour(Colour4.FromHex("#A84D78"), 120);
        return base.OnHover(e);
    }

    protected override void OnHoverLost(HoverLostEvent e)
    {
        background.FadeColour(baseColour(), 120);
        base.OnHoverLost(e);
    }

    public void SetSelected(bool value)
    {
        selected = value;
        background.FadeColour(baseColour(), 120);
    }

    public void SetText(string text) => label.Text = text;

    internal bool ActionEnabled => enabled;

    public LocalisableString TooltipText =>
        enabled ? default(LocalisableString) : disabledReason;

    public void SetEnabled(bool value, string? disabledReason = null)
    {
        enabled = value;
        if (!string.IsNullOrWhiteSpace(disabledReason))
            this.disabledReason = disabledReason;
        Alpha = value ? 1 : 0.42f;
        background.FadeColour(baseColour(), 120);
    }

    private Colour4 baseColour() =>
        selected
            ? Colour4.FromHex("#6F3654")
            : accent
                ? Colour4.FromHex("#8C3E66")
                : Colour4.FromHex("#2B2634");
}
