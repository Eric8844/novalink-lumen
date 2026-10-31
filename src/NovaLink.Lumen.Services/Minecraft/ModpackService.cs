using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using NovaLink.Lumen.Core;
using NovaLink.Lumen.Core.Models;
using NovaLink.Lumen.Services.Http;
using NovaLink.Lumen.Services.Storage;

namespace NovaLink.Lumen.Services.Minecraft;

/// <summary>安装 Modrinth .mrpack 整合包：准备基础版本与加载器、下载文件、解包 overrides。</summary>
public sealed class ModpackService
{
    private readonly AppPaths _paths;
    private readonly SettingsService _settings;
    private readonly HttpService _http;
    private readonly VersionManifestService _manifest;
    private readonly InstanceService _instances;
    private readonly LaunchService _launch;

    public ModpackService(AppPaths paths, SettingsService settings, HttpService http, VersionManifestService manifest, InstanceService instances, LaunchService launch)
    {
        _paths = paths;
        _settings = settings;
        _http = http;
        _manifest = manifest;
        _instances = instances;
        _launch = launch;
    }

    public async Task<InstanceSummary> InstallAsync(string archivePath, IProgress<DownloadProgress>? progress, CancellationToken ct = default)
    {
        if (!File.Exists(archivePath)) throw new InvalidOperationException("找不到整合包文件：" + archivePath);
        progress?.Report(new DownloadProgress("读取整合包", 1, 0, 0, 0, "正在读取整合包结构"));
        var root = _settings.Settings.GameRoot;
        Directory.CreateDirectory(root);
        var work = Path.Combine(_paths.Cache, "modpack-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(work);
        try
        {
            ZipFile.ExtractToDirectory(archivePath, work, true);
            var indexPath = Directory.EnumerateFiles(work, "modrinth.index.json", SearchOption.AllDirectories).FirstOrDefault();
            if (indexPath is null)
            {
                var curseManifest = Directory.EnumerateFiles(work, "manifest.json", SearchOption.AllDirectories).FirstOrDefault();
                if (curseManifest is not null)
                    throw new InvalidOperationException("这是 CurseForge 整合包：下载模组需要 CurseForge API Key，当前版本尚未接入。请改用 Modrinth 的 .mrpack 整合包。");
                throw new InvalidOperationException("这不是有效的整合包：缺少 modrinth.index.json");
            }
            var workRoot = Path.GetDirectoryName(indexPath)!;
            using var doc = JsonDocument.Parse(await File.ReadAllTextAsync(indexPath, ct));
            var index = doc.RootElement;
            var packName = Text(index, "name") ?? Path.GetFileNameWithoutExtension(archivePath);
            var mcVersion = Dependency(index, "minecraft") ?? throw new InvalidOperationException("整合包没有声明 Minecraft 版本");
            var fabric = Dependency(index, "fabric-loader");
            var quilt = Dependency(index, "quilt-loader");
            var forge = Dependency(index, "forge");
            var neoForge = Dependency(index, "neoforge");

            progress?.Report(new DownloadProgress("准备基础版本", 4, 0, 0, 0, "Minecraft " + mcVersion));
            await EnsureVanillaJsonAsync(mcVersion, ct);

            string versionId;
            if (!string.IsNullOrWhiteSpace(fabric)) versionId = await InstallLoaderProfileAsync(mcVersion, "fabric", fabric!, packName, ct);
            else if (!string.IsNullOrWhiteSpace(quilt)) versionId = await InstallLoaderProfileAsync(mcVersion, "quilt", quilt!, packName, ct);
            else if (!string.IsNullOrWhiteSpace(forge) || !string.IsNullOrWhiteSpace(neoForge))
                throw new InvalidOperationException("Forge / NeoForge 整合包的自动安装尚未接入当前版本，请先使用 Fabric 整合包。");
            else versionId = mcVersion;

            var uniqueId = "pack-" + Slug(packName) + "-" + Guid.NewGuid().ToString("N")[..4];
            var sourceJson = Path.Combine(root, "versions", versionId, versionId + ".json");
            var targetDir = Path.Combine(root, "versions", uniqueId);
            Directory.CreateDirectory(targetDir);
            var targetJson = Path.Combine(targetDir, uniqueId + ".json");
            await using (var input = File.OpenRead(sourceJson))
            await using (var output = File.Create(targetJson))
            {
                using var reader = new StreamReader(input, Encoding.UTF8);
                using var writer = new StreamWriter(output, new UTF8Encoding(false));
                await writer.WriteAsync(await reader.ReadToEndAsync(ct));
            }
            var jsonNode = JsonNode.Parse(await File.ReadAllTextAsync(targetJson, ct))!;
            jsonNode["id"] = uniqueId;
            if (!string.IsNullOrWhiteSpace(jsonNode["inheritsFrom"]?.GetValue<string>()))
                jsonNode["inheritsFrom"] = jsonNode["inheritsFrom"]!.GetValue<string>();
            await File.WriteAllTextAsync(targetJson, jsonNode.ToJsonString(new JsonSerializerOptions { WriteIndented = true }), new UTF8Encoding(false), ct);

            var gameDir = Path.Combine(root, "instances", uniqueId);
            Directory.CreateDirectory(gameDir);
            var meta = _instances.GetOrCreateMeta(uniqueId);
            meta.Name = packName;
            meta.Isolated = true;
            meta.GameDir = gameDir;
            meta.Icon = "loader:" + (string.IsNullOrWhiteSpace(fabric) ? (string.IsNullOrWhiteSpace(quilt) ? "Vanilla" : "Quilt") : "Fabric");
            _settings.Save();

            progress?.Report(new DownloadProgress("准备游戏文件", 10, 0, 0, 0, "正在准备 Minecraft " + mcVersion));
            await _launch.EnsureInstallAsync(uniqueId, null, ct);

            var files = index.TryGetProperty("files", out var fa) && fa.ValueKind == JsonValueKind.Array ? fa.EnumerateArray().ToList() : [];
            var downloaded = 0;
            var total = files.Count;
            using var gate = new SemaphoreSlim(6);
            var tasks = new List<Task>();
            foreach (var file in files)
            {
                ct.ThrowIfCancellationRequested();
                var rel = Text(file, "path") ?? "";
                if (string.IsNullOrWhiteSpace(rel)) continue;
                var target = SafeJoin(gameDir, rel);
                var urls = file.TryGetProperty("downloads", out var das) && das.ValueKind == JsonValueKind.Array
                    ? das.EnumerateArray().Select(x => x.GetString()).Where(x => !string.IsNullOrWhiteSpace(x)).Cast<string>().ToList()
                    : [];
                if (urls.Count == 0) continue;
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                tasks.Add(Task.Run(async () =>
                {
                    await gate.WaitAsync(ct).ConfigureAwait(false);
                    try
                    {
                        await DownloadWithFallbackAsync(urls, target, ct).ConfigureAwait(false);
                        var done = Interlocked.Increment(ref downloaded);
                        var pct = 10 + (total == 0 ? 0 : done * 85.0 / total);
                        progress?.Report(new DownloadProgress("下载整合包文件", pct, 0, 0, 0, done + "/" + total + " · " + Path.GetFileName(rel)));
                    }
                    finally { gate.Release(); }
                }, ct));
                if (tasks.Count >= 64)
                {
                    await Task.WhenAll(tasks).ConfigureAwait(false);
                    tasks.Clear();
                }
            }
            if (tasks.Count > 0) await Task.WhenAll(tasks).ConfigureAwait(false);

            progress?.Report(new DownloadProgress("解包配置", 97, 0, 0, 0, "正在写入 overrides"));
            foreach (var overrides in new[] { "overrides", "client-overrides" })
            {
                var dir = Path.Combine(workRoot, overrides);
                if (!Directory.Exists(dir)) continue;
                CopyTree(dir, gameDir);
            }

            meta.LastPlayed = 0;
            _settings.Save();
            progress?.Report(new DownloadProgress("完成", 100, 0, 0, 0, packName + " 已准备就绪"));
            var list = await _instances.ScanAsync(ct);
            return _instances.Find(list, uniqueId) ?? new InstanceSummary(uniqueId, packName, mcVersion, string.IsNullOrWhiteSpace(fabric) ? (string.IsNullOrWhiteSpace(quilt) ? "Vanilla" : "Quilt") : "Fabric", fabric ?? quilt ?? "", mcVersion, gameDir, "loader:Vanilla", 0, 0, 0, true);
        }
        finally
        {
            try { if (Directory.Exists(work)) Directory.Delete(work, true); } catch { }
        }
    }

    private async Task EnsureVanillaJsonAsync(string mcVersion, CancellationToken ct)
    {
        var root = _settings.Settings.GameRoot;
        var path = Path.Combine(root, "versions", mcVersion, mcVersion + ".json");
        if (File.Exists(path)) return;
        var list = await _manifest.GetAsync(false, ct);
        var entry = list.FirstOrDefault(v => string.Equals(v.Id, mcVersion, StringComparison.OrdinalIgnoreCase))
                    ?? throw new InvalidOperationException("Mojang 版本清单中没有 " + mcVersion);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var json = await _http.GetStringAsync(entry.Url, ct);
        await File.WriteAllTextAsync(path, json, new UTF8Encoding(false), ct);
    }

    private async Task<string> InstallLoaderProfileAsync(string mcVersion, string kind, string loaderVersion, string packName, CancellationToken ct)
    {
        var root = _settings.Settings.GameRoot;
        var mc = Uri.EscapeDataString(mcVersion);
        var loader = Uri.EscapeDataString(loaderVersion);
        var urls = kind == "fabric"
            ? new[]
            {
                "https://bmclapi2.bangbang93.com/fabric-meta/v2/versions/loader/" + mc + "/" + loader + "/profile/json",
                "https://meta.fabricmc.net/v2/versions/loader/" + mc + "/" + loader + "/profile/json"
            }
            : new[]
            {
                "https://meta.quiltmc.org/v3/versions/loader/" + mc + "/" + loader + "/profile/json"
            };
        string? json = null;
        Exception? last = null;
        foreach (var url in urls)
        {
            try { json = await _http.GetStringAsync(url, ct); break; }
            catch (Exception ex) { last = ex; }
        }
        if (string.IsNullOrWhiteSpace(json)) throw new InvalidOperationException("无法获取 " + kind + " 加载器配置：" + last?.Message);
        using var doc = JsonDocument.Parse(json);
        var id = Text(doc.RootElement, "id") ?? (kind + "-loader-" + loaderVersion + "-" + mcVersion);
        var dir = Path.Combine(root, "versions", id);
        Directory.CreateDirectory(dir);
        await File.WriteAllTextAsync(Path.Combine(dir, id + ".json"), json, new UTF8Encoding(false), ct);
        var meta = _instances.GetOrCreateMeta(id);
        meta.Name = packName + " · " + (kind == "fabric" ? "Fabric" : "Quilt");
        _settings.Save();
        return id;
    }

    private async Task DownloadWithFallbackAsync(IReadOnlyList<string> urls, string target, CancellationToken ct)
    {
        Exception? last = null;
        foreach (var url in urls)
        {
            try
            {
                var bytes = await _http.GetBytesAsync(url, ct);
                await File.WriteAllBytesAsync(target, bytes, ct);
                return;
            }
            catch (Exception ex) { last = ex; }
        }
        throw new InvalidOperationException("下载失败：" + Path.GetFileName(target) + " · " + last?.Message);
    }

    private static string? Dependency(JsonElement index, string key)
    {
        if (!index.TryGetProperty("dependencies", out var deps) || deps.ValueKind != JsonValueKind.Object) return null;
        foreach (var prop in deps.EnumerateObject())
            if (string.Equals(prop.Name, key, StringComparison.OrdinalIgnoreCase))
                return prop.Value.ValueKind == JsonValueKind.String ? prop.Value.GetString() : prop.Value.ToString();
        return null;
    }

    private static string? Text(JsonElement e, string key)
        => e.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static string SafeJoin(string root, string relative)
    {
        var full = Path.GetFullPath(Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar)));
        if (!full.StartsWith(Path.GetFullPath(root) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("整合包文件路径越界：" + relative);
        return full;
    }

    private static void CopyTree(string source, string target)
    {
        foreach (var dir in Directory.EnumerateDirectories(source, "*", SearchOption.AllDirectories))
            Directory.CreateDirectory(dir.Replace(source, target));
        foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
        {
            var dest = file.Replace(source, target);
            Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
            File.Copy(file, dest, true);
        }
    }

    private static string Slug(string name)
    {
        var builder = new StringBuilder();
        foreach (var c in name.ToLowerInvariant())
        {
            if (char.IsLetterOrDigit(c)) builder.Append(c);
            else if (builder.Length > 0 && builder[^1] != '-') builder.Append('-');
        }
        var slug = builder.ToString().Trim('-');
        if (slug.Length > 24) slug = slug[..24].Trim('-');
        return string.IsNullOrWhiteSpace(slug) ? "modpack" : slug;
    }
}