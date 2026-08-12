using System.Reflection;
using System.Text.Json;
using Kumori.Skins;
using Kumori.Tracking;
using osu.Framework;
using osu.Framework.Platform;
using osu.Game.Rulesets.Osu;

namespace Kumori.SkinStudio;

public static class Program
{
    public const string LazerRevision = "2026.726.0-lazer";
    public const string IpcPipeName = "kumori-skin-studio-v1";

    [STAThread]
    public static int Main(string[] args)
    {
        try
        {
            var options = StudioArguments.Parse(args);
            if (options.InspectLazerCatalogRoot is not null)
            {
                var catalog = new LazerSkinRealmService().LoadCatalog(
                    options.InspectLazerCatalogRoot);
                Console.WriteLine(JsonSerializer.Serialize(
                    catalog,
                    SkinStudioLaunchContract.JsonOptions));
                return 0;
            }
            if (options.AuditLivePreviewDraftPath is not null)
            {
                var result = new RealLivePreviewAuditService().Verify(
                    options.AuditLivePreviewDraftPath);
                Console.WriteLine(JsonSerializer.Serialize(
                    result,
                    SkinStudioLaunchContract.JsonOptions));
                return 0;
            }
            if (options.Probe)
            {
                Console.WriteLine(JsonSerializer.Serialize(new
                {
                    status = "ok",
                    contract_version = SkinStudioLaunchContract.CurrentVersion,
                    lazer_revision = LazerRevision,
                    ruleset = new OsuRuleset().RulesetInfo.ShortName,
                    ruleset_available = true,
                    graphics = "desktop-host-required",
                    embedded_host = "child-hwnd-v1",
                    default_workspace = "all-elements-workbench",
                    gameplay_workspace = "inline-real-gameplay",
                    renderer_contract_version = SkinStudioRendererLaunchContract.CurrentVersion,
                    renderer_only = true,
                    isolated_workspace = SkinStudioPaths.DefaultWorkspace,
                    assembly = Assembly.GetExecutingAssembly().GetName().Version?.ToString(),
                    release_stage = "release",
                    default_eligible = true,
                }));
                return 0;
            }

            var rendererContract = options.RendererContractPath is null
                ? null
                : SkinStudioRendererLaunchContract.Load(options.RendererContractPath);
            var contract = rendererContract is not null
                ? new SkinStudioLaunchContract
                {
                    WorkspacePath = rendererContract.WorkspacePath,
                    DraftId = rendererContract.DraftId,
                    ThemeId = rendererContract.ThemeId,
                    CustomTheme = rendererContract.CustomTheme,
                }.Normalize()
                : options.ContractPath is null
                ? new SkinStudioLaunchContract
                {
                    WorkspacePath = SkinStudioPaths.DefaultWorkspace,
                }.Normalize()
                : SkinStudioLaunchContract.Load(options.ContractPath);
            var beatmapPath = options.BeatmapPath
                              ?? Path.Combine(
                                  AppContext.BaseDirectory,
                                  "Fixtures",
                                  "kumori-skin-preview.osu");
            if (!File.Exists(beatmapPath))
                throw new FileNotFoundException("Skin Studio preview beatmap was not found.", beatmapPath);

            var hostOptions = new HostOptions
            {
                IPCPipeName = options.AcceptanceOutputPath is not null
                              || options.CommandAcceptanceOutputPath is not null
                              || options.PublishAcceptanceOutputPath is not null
                    ? $"{IpcPipeName}-acceptance-{Guid.NewGuid():N}"
                    : options.EmbeddedSession is null
                    ? IpcPipeName
                    : $"{IpcPipeName}-{options.EmbeddedSession}",
                FriendlyGameName = "Kumori Skin Studio",
            };
            using DesktopGameHost host = Host.GetSuitableDesktopHost(
                "Kumori.SkinStudio",
                hostOptions);
            if (!host.IsPrimaryInstance)
            {
                var client = new StudioActivationChannel(host);
                client.ActivateAsync(new StudioActivationMessage
                {
                    ContractPath = options.ContractPath,
                }).Wait(TimeSpan.FromSeconds(5));
                Console.Error.WriteLine(JsonSerializer.Serialize(new
                {
                    status = "activated_existing",
                }));
                return 0;
            }
            var game = new KumoriSkinStudioGame(
                contract,
                Path.GetFullPath(beatmapPath),
                options.Embedded,
                options.AcceptanceOutputPath,
                options.CommandAcceptanceOutputPath,
                options.PublishAcceptanceOutputPath,
                options.EmbeddedSession,
                rendererOnly: rendererContract is not null,
                rendererPipeName: rendererContract?.CommandPipeName,
                rendererInitialRevision: rendererContract?.DraftRevision);
            var activation = new StudioActivationChannel(host, game.HandleActivation);
            host.Run(game);
            GC.KeepAlive(activation);
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(JsonSerializer.Serialize(new
            {
                status = "error",
                type = ex.GetType().FullName,
                message = ex.Message,
            }));
            return 1;
        }
    }
}

internal sealed record StudioArguments(
    bool Probe,
    string? ContractPath,
    string? RendererContractPath,
    string? BeatmapPath,
    bool Embedded,
    string? EmbeddedSession,
    string? AcceptanceOutputPath,
    string? CommandAcceptanceOutputPath,
    string? PublishAcceptanceOutputPath,
    string? InspectLazerCatalogRoot,
    string? AuditLivePreviewDraftPath)
{
    public static StudioArguments Parse(string[] args)
    {
        var embedded = args.Contains("--embedded", StringComparer.OrdinalIgnoreCase);
        var session = ValueAfter(args, "--embedded-session");
        var rendererContract = ValueAfter(args, "--renderer-contract");
        if (session is not null
            && (!embedded
                || !Guid.TryParseExact(session, "N", out _)))
        {
            throw new InvalidDataException(
                "The embedded Studio session identifier is invalid.");
        }
        if (rendererContract is not null && !embedded)
            throw new InvalidDataException("A renderer contract requires embedded mode.");
        if (rendererContract is not null
            && ValueAfter(args, "--contract") is not null)
        {
            throw new InvalidDataException(
                "Studio and renderer contracts cannot be used together.");
        }
        var acceptanceOutput = ValueAfter(args, "--acceptance-output");
        var commandAcceptanceOutput =
            ValueAfter(args, "--command-acceptance-output");
        var publishAcceptanceOutput =
            ValueAfter(args, "--publish-acceptance-output");
        var inspectLazerCatalog =
            ValueAfter(args, "--inspect-lazer-catalog");
        var auditLivePreview = ValueAfter(args, "--audit-live-preview");
        if ((acceptanceOutput is not null
             || commandAcceptanceOutput is not null
             || publishAcceptanceOutput is not null)
            && embedded)
            throw new InvalidDataException(
                "Acceptance capture cannot run inside an embedded Studio.");
        if (new[]
            {
                acceptanceOutput,
                commandAcceptanceOutput,
                publishAcceptanceOutput,
            }.Count(value => value is not null) > 1)
        {
            throw new InvalidDataException(
                "Visual, command, and publish acceptance captures must run separately.");
        }
        if (auditLivePreview is not null
            && (embedded
                || acceptanceOutput is not null
                || commandAcceptanceOutput is not null
                || publishAcceptanceOutput is not null
                || args.Contains("--probe", StringComparer.OrdinalIgnoreCase)))
        {
            throw new InvalidDataException(
                "Live-preview audit must run as a standalone read-only command.");
        }
        if (inspectLazerCatalog is not null
            && (embedded
                || acceptanceOutput is not null
                || commandAcceptanceOutput is not null
                || publishAcceptanceOutput is not null
                || auditLivePreview is not null
                || args.Contains("--probe", StringComparer.OrdinalIgnoreCase)))
        {
            throw new InvalidDataException(
                "Lazer catalog inspection must run as a standalone read-only command.");
        }
        return new StudioArguments(
            args.Contains("--probe", StringComparer.OrdinalIgnoreCase),
            ValueAfter(args, "--contract"),
            rendererContract,
            ValueAfter(args, "--beatmap"),
            embedded,
            session,
            acceptanceOutput,
            commandAcceptanceOutput,
            publishAcceptanceOutput,
            inspectLazerCatalog,
            auditLivePreview);
    }

    private static string? ValueAfter(string[] args, string key)
    {
        var index = Array.FindIndex(
            args,
            value => value.Equals(key, StringComparison.OrdinalIgnoreCase));
        if (index < 0 || index + 1 >= args.Length)
            return null;
        var value = args[index + 1];
        return key.Equals("--embedded-session", StringComparison.OrdinalIgnoreCase)
            ? value
            : Path.GetFullPath(value);
    }
}
