using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using NovaLink.Lumen.Core.Models;
using NovaLink.Lumen.Core;

namespace NovaLink.Lumen.Services.Minecraft;

/// <summary>
/// 实例内容库：扫描模组/资源包/光影/世界，读取模组内嵌元数据与图标，
/// 并支持批量启用、删除、导入与全实例统计。
/// </summary>
public sealed class LibraryService
{
    private sealed record Meta(
        string? DisplayName,
        string? Version,
        string? Loader,
        string? Description,
        string? Authors,
        string? IconPath,
        string? Sha1,
        int PackFormat);

    private readonly AppPaths _paths;
    private readonly ConcurrentDictionary<string, (long Ticks, long Length, Meta Meta)> _cache = new(StringComparer.OrdinalIgnoreCase);

    public LibraryService(AppPaths paths) => _paths = paths;

    public LibrarySnapshot Scan(InstanceSummary instance)
    {
        var root = instance.GameDir;
        var mods = Files(Path.Combine(root, "mods"), instance, "mod", "*.jar", "*.jar.disabled")
            .GroupBy(x => x.Path, StringComparer.OrdinalIgnoreCase).Select(g => g.First()).OrderBy(x => x.Title, StringComparer.CurrentCulture).ToList();
        var worlds = Directories(Path.Combine(root, "saves"))
            .Where(item => File.Exists(Path.Combine(item.Path, "level.dat"))).ToList();
        var resourcePacks = Files(Path.Combine(root, "resourcepacks"), instance, "resourcepack", "*.zip", "*.zip.disabled").ToList();
        var shaderPacks = Files(Path.Combine(root, "shaderpacks"), instance, "shader", "*.zip", "*.zip.disabled").ToList();
        return new LibrarySnapshot(mods, worlds, resourcePacks, shaderPacks);
    }

    public LibraryOverview Overview(IEnumerable<InstanceSummary> instances)
    {
        var list = instances.ToList();
        var mods = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var worlds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var packs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var shaders = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var sized = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
        foreach (var instance in list)
        {
            var snapshot = Scan(instance);
            foreach (var item in snapshot.Mods)
            {
                mods.Add(item.Path);
                sized[item.Path] = item.Size;
            }
            foreach (var item in snapshot.Worlds)
            {
                worlds.Add(item.Path);
                sized[item.Path] = item.Size;
            }
            foreach (var item in snapshot.ResourcePacks)
            {
                packs.Add(item.Path);
                sized[item.Path] = item.Size;
            }
            foreach (var item in snapshot.ShaderPacks)
            {
                shaders.Add(item.Path);
                sized[item.Path] = item.Size;
            }
        }
        return new LibraryOverview(list.Count, mods.Count, worlds.Count, packs.Count, shaders.Count, sized.Values.Sum());
    }

    public bool ToggleMod(LibraryItem item) => SetEnabled(item, !item.Enabled);

    public bool SetEnabled(LibraryItem item, bool enabled)
    {
        if (item.Kind != "mod" || !File.Exists(item.Path)) return false;
        var isDisabled = item.Path.EndsWith(".disabled", StringComparison.OrdinalIgnoreCase);
        if (enabled && isDisabled)
        {
            var target = item.Path[..^".disabled".Length];
            if (File.Exists(target)) return false;
            File.Move(item.Path, target);
            return true;
        }
        if (!enabled && !isDisabled)
        {
            var target = item.Path + ".disabled";
            if (File.Exists(target)) return false;
            File.Move(item.Path, target);
            return true;
        }
        return false;
    }

    public int SetEnabled(IEnumerable<LibraryItem> items, bool enabled)
    {
        var changed = 0;
        foreach (var item in items)
        {
            try { if (SetEnabled(item, enabled)) changed++; }
            catch { }
        }
        return changed;
    }

    public void Delete(LibraryItem item)
    {
        if (item.IsDirectory)
        {
            if (Directory.Exists(item.Path)) Directory.Delete(item.Path, true);
        }
        else if (File.Exists(item.Path)) File.Delete(item.Path);
    }

    public int DeleteMany(IEnumerable<LibraryItem> items)
    {
        var deleted = 0;
        foreach (var item in items)
        {
            try { Delete(item); deleted++; }
            catch { }
        }
        return deleted;
    }

    /// <summary>把外部文件复制进实例目录，重名时追加 (2)、(3)。</summary>
    public string Import(string sourcePath, InstanceSummary instance, string kind)
    {
        var folder = FolderFor(instance, kind);
        Directory.CreateDirectory(folder);
        var name = Path.GetFileName(sourcePath);
        var target = UniqueTarget(folder, name);
        File.Copy(sourcePath, target, false);
        return target;
    }

    public static string FolderFor(InstanceSummary instance, string kind) => kind switch
    {
        "world" => Path.Combine(instance.GameDir, "saves"),
        "resourcepack" => Path.Combine(instance.GameDir, "resourcepacks"),
        "shader" => Path.Combine(instance.GameDir, "shaderpacks"),
        _ => Path.Combine(instance.GameDir, "mods")
    };

    private static string UniqueTarget(string folder, string name)
    {
        var target = Path.Combine(folder, name);
        if (!File.Exists(target)) return target;
        var ext = Path.GetExtension(name);
        var stem = Path.GetFileNameWithoutExtension(name);
        for (var n = 2; n < 999; n++)
        {
            target = Path.Combine(folder, stem + " (" + n + ")" + ext);
            if (!File.Exists(target)) return target;
        }
        return Path.Combine(folder, stem + "-" + Guid.NewGuid().ToString("N")[..6] + ext);
    }

    private IEnumerable<LibraryItem> Files(string dir, InstanceSummary instance, string kind, string pattern, string? extraPattern)
    {
        if (!Directory.Exists(dir)) yield break;
        foreach (var path in Directory.EnumerateFiles(dir, pattern))
            yield return Build(path, instance, kind, false);
        if (extraPattern is not null)
            foreach (var path in Directory.EnumerateFiles(dir, extraPattern))
                yield return Build(path, instance, kind, false);
    }

    private IEnumerable<LibraryItem> Directories(string dir)
    {
        if (!Directory.Exists(dir)) yield break;
        foreach (var path in Directory.EnumerateDirectories(dir))
            yield return Build(path, null, "world", true);
    }

    private LibraryItem Build(string path, InstanceSummary? instance, string kind, bool directory)
    {
        long size = 0;
        try
        {
            if (directory)
                size = Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories)
                    .Select(f => { try { return new FileInfo(f).Length; } catch { return 0L; } }).Sum();
            else size = new FileInfo(path).Length;
        }
        catch { }
        var modified = 0L;
        try
        {
            var ticks = directory ? new DirectoryInfo(path).LastWriteTimeUtc.Ticks : new FileInfo(path).LastWriteTimeUtc.Ticks;
            modified = new DateTimeOffset(new DateTime(ticks, DateTimeKind.Utc)).ToUnixTimeMilliseconds();
        }
        catch { }
        var name = Path.GetFileName(path);
        var enabled = !name.EndsWith(".disabled", StringComparison.OrdinalIgnoreCase);
        if (name.EndsWith(".disabled", StringComparison.OrdinalIgnoreCase))
            name = name[..^".disabled".Length];

        Meta meta = new(null, null, null, null, null, null, null, 0);
        if (!directory)
        {
            try { meta = ReadMeta(path, instance, kind); }
            catch { }
        }
        var displayName = directory ? WorldName(path) : meta.DisplayName;
        var loader = meta.Loader;
        if (string.IsNullOrWhiteSpace(loader) && kind == "mod" && instance is not null) loader = instance.Loader;
        return new LibraryItem(name, path, kind, size, modified, enabled, directory,
            displayName, meta.Version, loader, meta.Description, meta.Authors, meta.IconPath, meta.Sha1, meta.PackFormat);
    }

    private Meta ReadMeta(string path, InstanceSummary? instance, string kind)
    {
        var info = new FileInfo(path);
        if (_cache.TryGetValue(path, out var cached) && cached.Ticks == info.LastWriteTimeUtc.Ticks && cached.Length == info.Length)
            return cached.Meta;

        string? sha1 = null;
        try { sha1 = Convert.ToHexString(SHA1.HashData(File.ReadAllBytes(path))).ToLowerInvariant(); }
        catch { }

        Meta meta = kind switch
        {
            "mod" => ReadMod(path, instance, sha1),
            "resourcepack" => ReadResourcePack(path, sha1),
            "shader" => ReadShaderPack(path, sha1),
            _ => new Meta(null, null, null, null, null, null, sha1, 0)
        };
        _cache[path] = (info.LastWriteTimeUtc.Ticks, info.Length, meta);
        return meta;
    }

    private Meta ReadMod(string path, InstanceSummary? instance, string? sha1)
    {
        using var zip = ZipFile.OpenRead(path);
        var fabric = zip.GetEntry("fabric.mod.json");
        if (fabric is not null)
        {
            using var doc = JsonDocument.Parse(ReadEntry(fabric));
            var root = doc.RootElement;
            var icon = Text(root, "icon");
            var authors = AuthorList(root, "authors");
            return new Meta(
                Text(root, "name") ?? Text(root, "id"),
                Text(root, "version"),
                "Fabric",
                Text(root, "description"),
                authors,
                ExtractIcon(zip, icon, path, sha1),
                sha1, 0);
        }

        var tomlEntry = zip.GetEntry("META-INF/neoforge.mods.toml") ?? zip.GetEntry("META-INF/mods.toml");
        if (tomlEntry is not null)
        {
            var loader = tomlEntry.FullName.Contains("neoforge", StringComparison.OrdinalIgnoreCase) ? "NeoForge" : "Forge";
            var text = ReadEntry(tomlEntry);
            var fields = ParseToml(text);
            var logo = fields.TryGetValue("logoFile", out var value) ? value : null;
            return new Meta(
                fields.GetValueOrDefault("displayName") ?? fields.GetValueOrDefault("modId"),
                fields.GetValueOrDefault("version"),
                loader,
                fields.GetValueOrDefault("description"),
                fields.GetValueOrDefault("authors"),
                ExtractIcon(zip, logo, path, sha1),
                sha1, 0);
        }

        var mcmod = zip.GetEntry("mcmod.info");
        if (mcmod is not null)
        {
            using var doc = JsonDocument.Parse(ReadEntry(mcmod));
            var root = doc.RootElement.ValueKind == JsonValueKind.Array ? doc.RootElement[0] : doc.RootElement;
            var logo = Text(root, "logoFile");
            return new Meta(
                Text(root, "name") ?? Text(root, "modid"),
                Text(root, "version"),
                "Forge",
                Text(root, "description"),
                AuthorList(root, "authorList"),
                ExtractIcon(zip, logo, path, sha1),
                sha1, 0);
        }

        return new Meta(null, null, instance?.Loader, null, null, null, sha1, 0);
    }

    private Meta ReadResourcePack(string path, string? sha1)
    {
        using var zip = ZipFile.OpenRead(path);
        var mcmeta = zip.GetEntry("pack.mcmeta");
        string? description = null;
        var format = 0;
        if (mcmeta is not null)
        {
            try
            {
                using var doc = JsonDocument.Parse(ReadEntry(mcmeta));
                if (doc.RootElement.TryGetProperty("pack", out var pack))
                {
                    description = Text(pack, "description");
                    if (pack.TryGetProperty("pack_format", out var f) && f.TryGetInt32(out var value)) format = value;
                }
            }
            catch { }
        }
        return new Meta(null, null, "Resource Pack", description, null, ExtractIcon(zip, "pack.png", path, sha1), sha1, format);
    }

    private Meta ReadShaderPack(string path, string? sha1)
    {
        using var zip = ZipFile.OpenRead(path);
        var props = zip.GetEntry("shaders/shaders.properties");
        string? description = null;
        if (props is not null)
        {
            try
            {
                var profiles = ReadEntry(props).Split('\n').Count(line => line.TrimStart().StartsWith("profile.", StringComparison.OrdinalIgnoreCase));
                description = profiles > 0 ? "Iris / OptiFine 光影 · " + profiles + " 个画质预设" : "Iris / OptiFine 光影包";
            }
            catch { }
        }
        return new Meta(null, null, "Iris / OptiFine", description, null, ExtractIcon(zip, "shaders/icon.png", path, sha1), sha1, 0);
    }

    private string? ExtractIcon(ZipArchive zip, string? entryName, string jarPath, string? sha1)
    {
        try
        {
            var candidates = new List<string>();
            if (!string.IsNullOrWhiteSpace(entryName)) candidates.Add(entryName!.Replace('\\', '/').TrimStart('/'));
            candidates.AddRange(new[] { "icon.png", "pack.png", "logo.png", "assets/icon.png", "assets/logo.png" });
            foreach (var candidate in candidates.Distinct(StringComparer.OrdinalIgnoreCase))
            {
                var entry = zip.GetEntry(candidate);
                if (entry is null || entry.Length <= 0 || entry.Length > 2 * 1024 * 1024) continue;
                var ext = Path.GetExtension(candidate).ToLowerInvariant();
                if (ext is not (".png" or ".jpg" or ".jpeg")) continue;
                var bytes = ReadBytes(entry);
                var hash = Convert.ToHexString(SHA1.HashData(bytes)).ToLowerInvariant()[..16];
                var folder = Path.Combine(_paths.Cache, "content-icons");
                Directory.CreateDirectory(folder);
                var target = Path.Combine(folder, hash + ext);
                if (!File.Exists(target)) File.WriteAllBytes(target, bytes);
                return target;
            }
        }
        catch { }
        return null;
    }

    private static string WorldName(string path)
    {
        try
        {
            var levelName = Path.Combine(path, "levelname.txt");
            if (File.Exists(levelName)) return File.ReadAllText(levelName).Trim();
        }
        catch { }
        return Path.GetFileName(path);
    }

    private static string ReadEntry(ZipArchiveEntry entry) => Encoding.UTF8.GetString(ReadBytes(entry));

    private static byte[] ReadBytes(ZipArchiveEntry entry)
    {
        using var stream = entry.Open();
        using var memory = new MemoryStream();
        stream.CopyTo(memory);
        return memory.ToArray();
    }

    private static string? Text(JsonElement element, string name)
    {
        if (!element.TryGetProperty(name, out var value)) return null;
        return value.ValueKind switch
        {
            JsonValueKind.String => value.GetString(),
            JsonValueKind.Number => value.ToString(),
            JsonValueKind.Object => value.TryGetProperty("text", out var t) ? t.GetString() : value.ToString(),
            JsonValueKind.Array => string.Join(" ", value.EnumerateArray().Select(x => x.ValueKind == JsonValueKind.String ? x.GetString() : x.ToString())),
            _ => null
        };
    }

    private static string? AuthorList(JsonElement element, string name)
    {
        if (!element.TryGetProperty(name, out var value) || value.ValueKind != JsonValueKind.Array) return null;
        var names = new List<string>();
        foreach (var author in value.EnumerateArray())
        {
            if (author.ValueKind == JsonValueKind.String) names.Add(author.GetString() ?? "");
            else if (author.ValueKind == JsonValueKind.Object) names.Add(Text(author, "name") ?? Text(author, "id") ?? "");
        }
        names.RemoveAll(string.IsNullOrWhiteSpace);
        return names.Count == 0 ? null : string.Join(", ", names);
    }

    private static Dictionary<string, string> ParseToml(string text)
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var raw in text.Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0 || line.StartsWith('#')) continue;
            if (line.StartsWith('[')) continue;
            var index = line.IndexOf('=');
            if (index <= 0) continue;
            var key = line[..index].Trim();
            if (!map.ContainsKey(key)) map[key] = Unquote(line[(index + 1)..].Trim());
        }
        return map;
    }

    private static string Unquote(string value)
    {
        if (value.Length >= 2 && ((value[0] == '"' && value[^1] == '"') || (value[0] == '\'' && value[^1] == '\'')))
            return value[1..^1];
        return value;
    }
}