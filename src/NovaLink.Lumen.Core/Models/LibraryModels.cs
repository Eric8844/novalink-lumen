using System.Collections.Generic;

namespace NovaLink.Lumen.Core.Models;

public sealed record LibraryItem(
    string Name,
    string Path,
    string Kind,
    long Size,
    long Modified,
    bool Enabled,
    bool IsDirectory,
    string? DisplayName = null,
    string? Version = null,
    string? Loader = null,
    string? Description = null,
    string? Authors = null,
    string? IconPath = null,
    string? Sha1 = null,
    int PackFormat = 0)
{
    public string Title => string.IsNullOrWhiteSpace(DisplayName) ? Name : DisplayName!;

    public string Subtitle
    {
        get
        {
            var parts = new List<string>();
            if (!string.IsNullOrWhiteSpace(Version)) parts.Add("v" + Version);
            if (!string.IsNullOrWhiteSpace(Loader)) parts.Add(Loader!);
            if (PackFormat > 0) parts.Add("格式 " + PackFormat);
            if (!Enabled) parts.Add("已禁用");
            return string.Join("  ·  ", parts);
        }
    }
}

public sealed record LibrarySnapshot(
    IReadOnlyList<LibraryItem> Mods,
    IReadOnlyList<LibraryItem> Worlds,
    IReadOnlyList<LibraryItem> ResourcePacks,
    IReadOnlyList<LibraryItem> ShaderPacks);

public sealed record LibraryOverview(
    int Instances,
    int Mods,
    int Worlds,
    int ResourcePacks,
    int ShaderPacks,
    long TotalBytes);
