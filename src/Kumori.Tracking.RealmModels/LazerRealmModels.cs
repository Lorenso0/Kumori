using Realms;

namespace Kumori.Tracking;

[MapTo("BeatmapSet")]
internal partial class LazerBeatmapSet : RealmObject
{
    [PrimaryKey]
    [MapTo("ID")]
    public Guid Id { get; set; }

    [Indexed]
    [MapTo("OnlineID")]
    public int OnlineId { get; set; }

    [MapTo("DeletePending")]
    public bool DeletePending { get; set; }

    [MapTo("Files")]
    public IList<LazerNamedFileUsage> Files { get; } = null!;
}

[MapTo("RealmNamedFileUsage")]
internal partial class LazerNamedFileUsage : EmbeddedObject
{
    [MapTo("File")]
    public LazerRealmFile File { get; set; } = null!;

    [MapTo("Filename")]
    public string Filename { get; set; } = string.Empty;
}

[MapTo("File")]
internal partial class LazerRealmFile : RealmObject
{
    [PrimaryKey]
    [MapTo("Hash")]
    public string Hash { get; set; } = string.Empty;
}

[MapTo("Score")]
internal partial class LazerScore : RealmObject
{
    [PrimaryKey]
    [MapTo("ID")]
    public Guid Id { get; set; }

    [Indexed]
    public string BeatmapHash { get; set; } = string.Empty;

    public DateTimeOffset Date { get; set; }

    public bool DeletePending { get; set; }

    public IList<LazerNamedFileUsage> Files { get; } = null!;
}

[MapTo("Skin")]
internal partial class LazerSkin : RealmObject
{
    [PrimaryKey]
    [MapTo("ID")]
    public Guid Id { get; set; }

    [MapTo("Name")]
    public string Name { get; set; } = "";

    [MapTo("Creator")]
    public string Creator { get; set; } = "";

    [MapTo("DeletePending")]
    public bool DeletePending { get; set; }

    [MapTo("Files")]
    public IList<LazerNamedFileUsage> Files { get; } = null!;
}

[MapTo("KeyBinding")]
internal partial class LazerKeyBinding : RealmObject
{
    [PrimaryKey]
    [MapTo("ID")]
    public Guid Id { get; set; }

    [MapTo("RulesetName")]
    public string? RulesetName { get; set; }

    [MapTo("Variant")]
    public int? Variant { get; set; }

    [MapTo("Action")]
    public int Action { get; set; }

    [MapTo("KeyCombination")]
    public string KeyCombination { get; set; } = "";
}
