using System.Text.Json;
using System.Text.Json.Serialization;

namespace NovaLink.Lumen.Core.Models;

public sealed class VersionJson
{
    public string Id { get; set; } = "";
    public string? Name { get; set; }
    public string? Type { get; set; }
    public string? InheritsFrom { get; set; }
    public string? Jar { get; set; }
    public string? ClientVersion { get; set; }
    public string? MainClass { get; set; }
    public string? Assets { get; set; }
    public AssetIndex? AssetIndex { get; set; }
    public string? MinecraftArguments { get; set; }
    public Dictionary<string, DownloadInfo>? Downloads { get; set; }
    public List<Library> Libraries { get; set; } = [];
    public VersionArguments? Arguments { get; set; }
    public JavaVersion? JavaVersion { get; set; }

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? Extra { get; set; }
}

public sealed class AssetIndex
{
    public string? Id { get; set; }
    public string? Sha1 { get; set; }
    public long Size { get; set; }
    public long TotalSize { get; set; }
    public string? Url { get; set; }
}

public sealed class DownloadInfo
{
    public string? Path { get; set; }
    public string? Url { get; set; }
    public string? Sha1 { get; set; }
    public long Size { get; set; }
}

public sealed class Library
{
    public string Name { get; set; } = "";
    public DownloadInfo? Downloads { get; set; }
    public Dictionary<string, DownloadInfo>? Classifiers { get; set; }
    public Dictionary<string, string>? Natives { get; set; }
    public List<Rule>? Rules { get; set; }
    public Extract? Extract { get; set; }
}

public sealed class Rule
{
    public string Action { get; set; } = "allow";
    public RuleOs? Os { get; set; }
    public Dictionary<string, bool>? Features { get; set; }
}

public sealed class RuleOs
{
    public string? Name { get; set; }
    public string? Version { get; set; }
    public string? Arch { get; set; }
}

public sealed class Extract
{
    public List<string>? Exclude { get; set; }
}

public sealed class VersionArguments
{
    public List<JsonElement>? Game { get; set; }
    public List<JsonElement>? Jvm { get; set; }
}

public sealed class JavaVersion
{
    public int MajorVersion { get; set; }
    public string? Component { get; set; }
}
