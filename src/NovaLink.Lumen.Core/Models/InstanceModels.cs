using System.Text.Json;
using System.Text.Json.Serialization;

namespace NovaLink.Lumen.Core.Models;

public sealed class InstanceMeta
{
    public string? Name { get; set; }
    public string? Icon { get; set; }
    public bool? Isolated { get; set; }
    public string? GameDir { get; set; }
    public long LastPlayed { get; set; }
    public long PlaytimeMs { get; set; }
    public string? InstalledAt { get; set; }
    public string? ForgeVersion { get; set; }
    public string? NeoForgeVersion { get; set; }
    public string? FabricVersion { get; set; }
    public string? QuiltVersion { get; set; }
    public string? OptifineVersion { get; set; }
    public int? Memory { get; set; }

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? Extra { get; set; }
}

public sealed class GameRootHistory
{
    public string Path { get; set; } = "";
    public long LastUsed { get; set; }
}

public sealed record InstanceSummary(
    string Id,
    string Name,
    string Kernel,
    string Loader,
    string LoaderVersion,
    string VersionName,
    string GameDir,
    string Icon,
    int Mods,
    int Worlds,
    long LastPlayed,
    bool Isolated,
    long ReleaseTime = 0);
