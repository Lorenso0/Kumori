using System.Buffers.Binary;
using System.IO.Pipes;
using System.Security.Principal;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Kumori.Skins;

public enum SkinStudioPreviewScene
{
    Showcase,
    Circles,
    Sliders,
    Hud,
    Cursor,
    Spinner,
    Judgements,
    Followpoints,
}

public static class SkinStudioPreviewScenes
{
    public static double TimeMilliseconds(SkinStudioPreviewScene scene) => scene switch
    {
        SkinStudioPreviewScene.Circles => 900,
        SkinStudioPreviewScene.Sliders => 2_550,
        SkinStudioPreviewScene.Hud => 2_900,
        SkinStudioPreviewScene.Cursor => 5_100,
        SkinStudioPreviewScene.Spinner => 7_900,
        SkinStudioPreviewScene.Judgements => 11_050,
        SkinStudioPreviewScene.Followpoints => 6_500,
        _ => 5_100,
    };

    public static int ComboColourCount(SkinIniDocument? skinIni)
    {
        if (skinIni is null)
            return 4;

        var count = Enumerable.Range(1, 8)
            .Count(index => !string.IsNullOrWhiteSpace(
                skinIni.GetValue("Colours", $"Combo{index}")));
        return count == 0 ? 4 : count;
    }
}

public static class SkinStudioExtrasPreview
{
    public static SkinStudioPreviewScene SceneFor(
        string familyId,
        IEnumerable<string> components)
    {
        ArgumentNullException.ThrowIfNull(components);
        if (familyId.Equals("osu.followpoints", StringComparison.OrdinalIgnoreCase))
            return SkinStudioPreviewScene.Followpoints;
        var firstComponent = components.FirstOrDefault(component =>
            !string.IsNullOrWhiteSpace(component));
        if (firstComponent is not null)
        {
            var semantic = SkinStudioSemanticPreviewCatalog.Resolve(
                firstComponent,
                familyId);
            if (!semantic.IsRaw)
                return semantic.Scene;
        }
        var catalogScene = components
            .Select(component => SkinStudioElementCatalog.Find(component)?.PreviewScene)
            .FirstOrDefault(scene => scene is not null);
        if (catalogScene is { } scene)
            return scene;

        return familyId.ToLowerInvariant() switch
        {
            "osu.cursor" or "osu.star-particles" => SkinStudioPreviewScene.Cursor,
            "osu.hitcircles" or "osu.combo-colours" or "interface.countdown" =>
                SkinStudioPreviewScene.Circles,
            "osu.slider" or "osu.slider-colours" =>
                SkinStudioPreviewScene.Sliders,
            "osu.hitbursts" or "osu.result-judgements" or "osu.comboburst" =>
                SkinStudioPreviewScene.Judgements,
            "osu.spinner" => SkinStudioPreviewScene.Spinner,
            _ when familyId.StartsWith("interface.", StringComparison.OrdinalIgnoreCase) =>
                SkinStudioPreviewScene.Hud,
            _ => SkinStudioPreviewScene.Showcase,
        };
    }
}

public sealed record SkinStudioRendererLaunchContract
{
    public const int CurrentVersion = 2;

    [JsonPropertyName("contract_version")]
    public int ContractVersion { get; init; } = CurrentVersion;

    [JsonPropertyName("workspace_path")]
    public required string WorkspacePath { get; init; }

    [JsonPropertyName("draft_id")]
    public required Guid DraftId { get; init; }

    [JsonPropertyName("draft_revision")]
    public required long DraftRevision { get; init; }

    [JsonPropertyName("theme_id")]
    public string ThemeId { get; init; } = "dark";

    [JsonPropertyName("custom_theme")]
    public IReadOnlyDictionary<string, string> CustomTheme { get; init; } =
        new Dictionary<string, string>();

    [JsonPropertyName("session_id")]
    public required Guid SessionId { get; init; }

    [JsonPropertyName("command_pipe_name")]
    public required string CommandPipeName { get; init; }

    public SkinStudioRendererLaunchContract Normalize()
    {
        if (ContractVersion != CurrentVersion)
            throw new InvalidDataException(
                $"Unsupported renderer contract version {ContractVersion}; expected {CurrentVersion}.");
        if (string.IsNullOrWhiteSpace(WorkspacePath))
            throw new InvalidDataException("The renderer workspace path is required.");
        if (DraftId == Guid.Empty)
            throw new InvalidDataException("The renderer draft identifier is required.");
        if (DraftRevision < 0)
            throw new InvalidDataException("The renderer draft revision cannot be negative.");
        ValidatePipeName(CommandPipeName);
        if (SessionId == Guid.Empty)
            throw new InvalidDataException("The renderer session identifier is required.");

        return this with
        {
            WorkspacePath = Path.GetFullPath(WorkspacePath),
            ThemeId = string.IsNullOrWhiteSpace(ThemeId) ? "dark" : ThemeId.Trim(),
            CustomTheme = new Dictionary<string, string>(
                CustomTheme,
                StringComparer.OrdinalIgnoreCase),
            CommandPipeName = CommandPipeName.Trim(),
        };
    }

    public void Save(string path)
    {
        var normalized = Normalize();
        var fullPath = Path.GetFullPath(path);
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        var temporary = fullPath + ".new";
        try
        {
            File.WriteAllText(
                temporary,
                JsonSerializer.Serialize(normalized, SkinStudioLaunchContract.JsonOptions));
            File.Move(temporary, fullPath, overwrite: true);
        }
        finally
        {
            try { if (File.Exists(temporary)) File.Delete(temporary); } catch { }
        }
    }

    public static SkinStudioRendererLaunchContract Load(string path)
    {
        using var stream = File.OpenRead(Path.GetFullPath(path));
        return (JsonSerializer.Deserialize<SkinStudioRendererLaunchContract>(
                    stream,
                    SkinStudioLaunchContract.JsonOptions)
                ?? throw new InvalidDataException("The renderer contract was empty."))
            .Normalize();
    }

    public static void ValidatePipeName(string? pipeName)
    {
        if (string.IsNullOrWhiteSpace(pipeName)
            || pipeName.Length > 128
            || !pipeName.StartsWith("kumori-skin-renderer-", StringComparison.Ordinal)
            || pipeName.Any(character =>
                !char.IsAsciiLetterOrDigit(character) && character != '-'))
        {
            throw new InvalidDataException("The renderer command pipe name is invalid.");
        }
    }
}

public enum SkinStudioRendererCommandKind
{
    LoadDraft,
    Seek,
    SelectPreviewTarget,
    Play,
    Pause,
    Restart,
    AuditionSample,
    StopAudio,
    SetActive,
    SetAutoMotion,
    SetPreviewColour,
    SetPreviewScale,
    SetMenuCursorVisible,
    PollEvent,
    SetSmoothTrail,
}

public enum SkinStudioRendererColourTarget
{
    Combo1,
    Combo2,
    Combo3,
    Combo4,
    Combo5,
    Combo6,
    Combo7,
    Combo8,
    SliderInner,
    SliderOuter,
    ElementTint,
}

public sealed record SkinStudioRendererRequest
{
    [JsonPropertyName("request_id")]
    public Guid RequestId { get; init; } = Guid.NewGuid();

    [JsonPropertyName("command")]
    public required SkinStudioRendererCommandKind Command { get; init; }

    [JsonPropertyName("draft_id")]
    public Guid? DraftId { get; init; }

    [JsonPropertyName("draft_revision")]
    public long? DraftRevision { get; init; }

    [JsonPropertyName("scene")]
    public SkinStudioPreviewScene? Scene { get; init; }

    [JsonPropertyName("component")]
    public string? Component { get; init; }

    [JsonPropertyName("preview_target_id")]
    public string? PreviewTargetId { get; init; }

    [JsonPropertyName("family_id")]
    public string? FamilyId { get; init; }

    [JsonPropertyName("ruleset")]
    public SkinStudioRuleset? Ruleset { get; init; }

    [JsonPropertyName("mania_key_count")]
    public int? ManiaKeyCount { get; init; }

    [JsonPropertyName("components")]
    public IReadOnlyList<string>? Components { get; init; }

    [JsonPropertyName("active")]
    public bool? Active { get; init; }

    [JsonPropertyName("colour_target")]
    public SkinStudioRendererColourTarget? ColourTarget { get; init; }

    [JsonPropertyName("colour_red")]
    public byte? ColourRed { get; init; }

    [JsonPropertyName("colour_green")]
    public byte? ColourGreen { get; init; }

    [JsonPropertyName("colour_blue")]
    public byte? ColourBlue { get; init; }

    [JsonPropertyName("cursor_scale")]
    public double? CursorScale { get; init; }

    [JsonPropertyName("object_scale")]
    public double? ObjectScale { get; init; }
}

public sealed record SkinStudioRendererResponse
{
    [JsonPropertyName("request_id")]
    public required Guid RequestId { get; init; }

    [JsonPropertyName("accepted")]
    public required bool Accepted { get; init; }

    [JsonPropertyName("message")]
    public required string Message { get; init; }

    [JsonPropertyName("loaded_draft_id")]
    public Guid? LoadedDraftId { get; init; }

    [JsonPropertyName("loaded_revision")]
    public long? LoadedRevision { get; init; }

    [JsonPropertyName("playing")]
    public bool Playing { get; init; }

    [JsonPropertyName("scene")]
    public SkinStudioPreviewScene Scene { get; init; } = SkinStudioPreviewScene.Showcase;

    [JsonPropertyName("event")]
    public SkinStudioRendererEventKind Event { get; init; } = SkinStudioRendererEventKind.Ready;

    [JsonPropertyName("audio_playing")]
    public bool AudioPlaying { get; init; }

    [JsonPropertyName("preview_target_id")]
    public string? PreviewTargetId { get; init; }

    [JsonPropertyName("family_id")]
    public string? FamilyId { get; init; }

    [JsonPropertyName("component")]
    public string? Component { get; init; }

    [JsonPropertyName("ruleset")]
    public SkinStudioRuleset? Ruleset { get; init; }

    [JsonPropertyName("preview_kind")]
    public SkinStudioSemanticPreviewKind? PreviewKind { get; init; }

    [JsonPropertyName("compatibility")]
    public SkinExtraCompatibility? Compatibility { get; init; }

    [JsonPropertyName("asset_provenance")]
    public SkinStudioAssetProvenance? AssetProvenance { get; init; }

    [JsonPropertyName("colour_target")]
    public SkinStudioRendererColourTarget? ColourTarget { get; init; }

    [JsonPropertyName("colour_red")]
    public byte? ColourRed { get; init; }

    [JsonPropertyName("colour_green")]
    public byte? ColourGreen { get; init; }

    [JsonPropertyName("colour_blue")]
    public byte? ColourBlue { get; init; }

    [JsonPropertyName("anchor_x")]
    public double? AnchorX { get; init; }

    [JsonPropertyName("anchor_y")]
    public double? AnchorY { get; init; }

    [JsonPropertyName("avoid_left")]
    public double? AvoidLeft { get; init; }

    [JsonPropertyName("avoid_top")]
    public double? AvoidTop { get; init; }

    [JsonPropertyName("avoid_right")]
    public double? AvoidRight { get; init; }

    [JsonPropertyName("avoid_bottom")]
    public double? AvoidBottom { get; init; }
}

public enum SkinStudioRendererEventKind
{
    Ready,
    RevisionLoaded,
    PlaybackState,
    AudioState,
    ColourEditRequested,
    RecoverableError,
}

public sealed class SkinStudioRendererPipeClient
{
    private const int connect_timeout_milliseconds = 5_000;
    private readonly string pipeName;

    public SkinStudioRendererPipeClient(string pipeName)
    {
        SkinStudioRendererLaunchContract.ValidatePipeName(pipeName);
        this.pipeName = pipeName.Trim();
    }

    public async Task<SkinStudioRendererResponse> SendAsync(
        SkinStudioRendererRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        using var pipe = new NamedPipeClientStream(
            ".",
            pipeName,
            PipeDirection.InOut,
            PipeOptions.Asynchronous,
            TokenImpersonationLevel.Identification);
        await pipe.ConnectAsync(connect_timeout_milliseconds, cancellationToken)
            .ConfigureAwait(false);
        await SkinStudioRendererPipeProtocol.WriteAsync(
            pipe,
            request,
            cancellationToken).ConfigureAwait(false);
        var response = await SkinStudioRendererPipeProtocol
            .ReadAsync<SkinStudioRendererResponse>(pipe, cancellationToken)
            .ConfigureAwait(false);
        if (response.RequestId != request.RequestId)
            throw new InvalidDataException("The renderer response correlation identifier did not match.");
        return response;
    }
}

public static class SkinStudioRendererPipeProtocol
{
    public const int MaximumMessageBytes = 64 * 1024;

    public static async Task WriteAsync<T>(
        Stream stream,
        T value,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(stream);
        var payload = JsonSerializer.SerializeToUtf8Bytes(
            value,
            SkinStudioLaunchContract.JsonOptions);
        if (payload.Length is <= 0 or > MaximumMessageBytes)
            throw new InvalidDataException("The renderer message has an invalid size.");
        var header = new byte[sizeof(int)];
        BinaryPrimitives.WriteInt32LittleEndian(header, payload.Length);
        await stream.WriteAsync(header, cancellationToken).ConfigureAwait(false);
        await stream.WriteAsync(payload, cancellationToken).ConfigureAwait(false);
        await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    public static async Task<T> ReadAsync<T>(
        Stream stream,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(stream);
        var header = new byte[sizeof(int)];
        await stream.ReadExactlyAsync(header, cancellationToken).ConfigureAwait(false);
        var length = BinaryPrimitives.ReadInt32LittleEndian(header);
        if (length is <= 0 or > MaximumMessageBytes)
            throw new InvalidDataException("The renderer message has an invalid size.");
        var payload = new byte[length];
        await stream.ReadExactlyAsync(payload, cancellationToken).ConfigureAwait(false);
        return JsonSerializer.Deserialize<T>(
                   payload,
                   SkinStudioLaunchContract.JsonOptions)
               ?? throw new InvalidDataException("The renderer message was empty.");
    }
}
