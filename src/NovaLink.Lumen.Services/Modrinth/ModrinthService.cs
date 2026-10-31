using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using NovaLink.Lumen.Core;
using NovaLink.Lumen.Core.Models;
using NovaLink.Lumen.Services.Http;

namespace NovaLink.Lumen.Services.Modrinth;

public sealed class ModrinthService
{
    private const string Api = "https://api.modrinth.com/v2";
    private readonly HttpService _http;
    private readonly Dictionary<string, (DateTime At, ModrinthSearchResult Data)> _searchCache = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, ModrinthProject> _projectCache = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, string> _zhTextCache = new(StringComparer.OrdinalIgnoreCase);
    private static readonly SemaphoreSlim ZhGate = new(3, 3);

    public ModrinthService(HttpService http) => _http = http;

    public static readonly IReadOnlyDictionary<string, string> ChineseTerms = new Dictionary<string, string>
    {
        ["钠"] = "Sodium", ["锂"] = "Lithium", ["苹果皮"] = "AppleSkin", ["玉"] = "Jade", ["氪"] = "Krypton",
        ["小地图"] = "Xaero's Minimap", ["世界地图"] = "Xaero's World Map", ["旅行地图"] = "JourneyMap",
        ["机械动力"] = "Create", ["暮色森林"] = "Twilight Forest", ["匠魂"] = "Tinkers' Construct",
        ["工业时代"] = "IndustrialCraft", ["热力膨胀"] = "Thermal Expansion", ["植物魔法"] = "Botania",
        ["星辉魔法"] = "Astral Sorcery", ["血魔法"] = "Blood Magic", ["神秘时代"] = "Thaumcraft",
        ["无尽贪婪"] = "Avaritia", ["龙之研究"] = "Draconic Evolution", ["应用能源"] = "Applied Energistics",
        ["精致存储"] = "Refined Storage", ["存储抽屉"] = "Storage Drawers", ["通用机械"] = "Mekanism",
        ["沉浸工程"] = "Immersive Engineering", ["建筑"] = "BuildCraft", ["林业"] = "Forestry", ["末影接口"] = "Ender IO",
        ["星系"] = "Galacticraft", ["虚无世界"] = "Advent of Ascension", ["超多生物群系"] = "Biomes O Plenty",
        ["农夫乐事"] = "Farmer's Delight", ["潘马斯农场"] = "Pam's HarvestCraft", ["水产养殖"] = "Aquaculture",
        ["夸克"] = "Quark", ["创世神"] = "WorldEdit", ["模组菜单"] = "Mod Menu", ["高清修复"] = "OptiFine",
        ["铷"] = "Rubidium", ["镁"] = "Magnesium", ["飞轮"] = "Flywheel", ["实体渲染优化"] = "Entity Culling",
        ["内存优化"] = "FerriteCore", ["即时渲染优化"] = "ImmediatelyFast", ["铁氧体"] = "FerriteCore",
        ["光影"] = "shader", ["光影包"] = "shaderpack", ["资源包"] = "resourcepack", ["材质包"] = "resourcepack",
        ["整合包"] = "modpack", ["模组"] = "mod", ["字体"] = "font", ["界面"] = "ui", ["汉化"] = "chinese",
        ["优化"] = "optimization", ["性能"] = "performance", ["渲染"] = "rendering", ["科技"] = "technology",
        ["魔法"] = "magic", ["冒险"] = "adventure", ["任务"] = "quest", ["地牢"] = "dungeon", ["家具"] = "furniture",
        ["武器"] = "weapon", ["工具"] = "tool", ["农业"] = "farming", ["食物"] = "food", ["建筑装饰"] = "decoration",
        ["存储"] = "storage", ["能源"] = "energy", ["机械"] = "machinery", ["管道"] = "pipe", ["传送"] = "teleport",
        ["背包"] = "inventory", ["生物"] = "mob", ["维度"] = "dimension", ["低配"] = "low-end", ["高清"] = "hd",
        ["写实"] = "realistic", ["卡通"] = "cartoon", ["奇幻"] = "fantasy", ["科幻"] = "sci-fi",
        ["中世纪"] = "medieval", ["现代"] = "modern", ["原版"] = "vanilla", ["战斗"] = "combat", ["生存"] = "survival"
    };

    public static readonly IReadOnlyDictionary<string, string> EnglishTitlesZh = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
    {
        ["sodium"] = "钠", ["sodium extra"] = "钠扩展", ["lithium"] = "锂", ["krypton"] = "氪",
        ["appleskin"] = "苹果皮", ["jade"] = "玉", ["jei"] = "JEI 物品管理器", ["rei"] = "REI 物品管理器", ["emi"] = "EMI 物品管理器",
        ["create"] = "机械动力", ["twilight forest"] = "暮色森林", ["tinkers construct"] = "匠魂", ["thermal expansion"] = "热力膨胀",
        ["botania"] = "植物魔法", ["astral sorcery"] = "星辉魔法", ["blood magic"] = "血魔法", ["thaumcraft"] = "神秘时代",
        ["mekanism"] = "通用机械", ["immersive engineering"] = "沉浸工程", ["refined storage"] = "精致存储", ["storage drawers"] = "存储抽屉",
        ["applied energistics 2"] = "应用能源2", ["ender io"] = "末影接口", ["galacticraft"] = "星系", ["biomes o plenty"] = "超多生物群系",
        ["farmers delight"] = "农夫乐事", ["pams harvestcraft"] = "潘马斯农场", ["quark"] = "夸克", ["worldedit"] = "创世神",
        ["mod menu"] = "模组菜单", ["optifine"] = "高清修复", ["rubidium"] = "铷", ["magnesium"] = "镁", ["flywheel"] = "飞轮",
        ["entity culling"] = "实体渲染优化", ["ferritecore"] = "铁氧体核心", ["immediatelyfast"] = "即时渲染优化",
        ["iris shaders"] = "鸢尾花光影", ["iris"] = "鸢尾花光影", ["bsl shaders"] = "BSL 光影", ["complementary shaders"] = "互补光影",
        ["photon shader"] = "光子光影", ["solas shader"] = "索拉斯光影", ["rethinking voxels"] = "重新思考体素",
        ["fresh animations"] = "新鲜动画", ["continuity"] = "连续性", ["cloth config api"] = "布料配置 API", ["architectury api"] = "Architectury API",
        ["fabric api"] = "Fabric API", ["fabulously optimized"] = "绝妙优化", ["better mc"] = "更好的 MC",
        ["xaeros minimap"] = "Xaero 的小地图", ["xaeros world map"] = "Xaero 的世界地图", ["journeymap"] = "旅行地图",
        ["distant horizons"] = "遥远的地平线", ["distanthorizons"] = "遥远的地平线", ["physics mod"] = "物理模组",
        ["farmersdelight"] = "农夫乐事", ["jei items"] = "JEI 物品管理器"
    };

    public static bool HasChinese(string text) => Regex.IsMatch(text ?? "", "[\u4e00-\u9fff]");

    public async Task<string> TranslateToChineseAsync(string text, CancellationToken ct = default)
    {
        var plain = Regex.Replace(text ?? "", "<[^>]+>", " ").Trim();
        if (plain.Length == 0 || HasChinese(plain)) return plain;
        if (plain.Length > 450) plain = plain[..450];
        if (_zhTextCache.TryGetValue(plain, out var cached)) return cached;
        await ZhGate.WaitAsync(ct);
        try
        {
            var indexed = await IndexTranslateAsync(plain, ct);
            if (!string.IsNullOrWhiteSpace(indexed) && HasChinese(indexed))
            {
                _zhTextCache[plain] = indexed;
                return indexed;
            }
        }
        catch { }
        finally { ZhGate.Release(); }
        try
        {
            var url = "https://api.mymemory.translated.net/get?q=" + Uri.EscapeDataString(plain) + "&langpair=en|zh-CN";
            var json = await _http.GetJsonAsync<JsonElement>(url, ct);
            if (json.TryGetProperty("responseData", out var data) && data.TryGetProperty("translatedText", out var value))
            {
                var translated = value.GetString();
                if (!string.IsNullOrWhiteSpace(translated) && HasChinese(translated))
                {
                    _zhTextCache[plain] = translated;
                    return translated;
                }
            }
        }
        catch { }
        return plain;
    }

    private async Task<string> IndexTranslateAsync(string text, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "https://index-translate.bilibili.com/translate_stream");
        request.Headers.TryAddWithoutValidation("Origin", "https://www.bilibili.com");
        request.Headers.TryAddWithoutValidation("Referer", "https://www.bilibili.com/");
        request.Headers.TryAddWithoutValidation("Accept", "text/event-stream, application/json");
        var payload = JsonSerializer.Serialize(new
        {
            text,
            source_lang = "en",
            target_lang = "zh",
            model = "index-mt-9b",
            stream = true
        });
        request.Content = new StringContent(payload, Encoding.UTF8, "application/json");
        using var response = await _http.Client.SendAsync(request, HttpCompletionOption.ResponseContentRead, ct);
        if (!response.IsSuccessStatusCode) return "";
        var raw = await response.Content.ReadAsStringAsync(ct);
        return ParseIndexStream(raw);
    }

    private static string ParseIndexStream(string raw)
    {
        var builder = new StringBuilder();
        foreach (var line in raw.Split('\n'))
        {
            var trimmed = line.Trim();
            if (!trimmed.StartsWith("data:", StringComparison.Ordinal)) continue;
            var data = trimmed[5..].Trim();
            if (data.Length == 0 || data == "[DONE]") continue;
            try
            {
                using var doc = JsonDocument.Parse(data);
                var root = doc.RootElement;
                if (root.TryGetProperty("choices", out var choices) && choices.GetArrayLength() > 0)
                {
                    var choice = choices[0];
                    if (choice.TryGetProperty("delta", out var delta) && delta.TryGetProperty("content", out var content))
                        builder.Append(content.GetString());
                }
            }
            catch { }
        }
        if (builder.Length == 0)
        {
            try
            {
                using var doc = JsonDocument.Parse(raw);
                var root = doc.RootElement;
                if (root.TryGetProperty("choices", out var choices) && choices.GetArrayLength() > 0)
                {
                    var choice = choices[0];
                    if (choice.TryGetProperty("message", out var message) && message.TryGetProperty("content", out var content))
                        builder.Append(content.GetString());
                }
            }
            catch { }
        }
        return Regex.Replace(builder.ToString(), "</?think>", "", RegexOptions.IgnoreCase).Trim();
    }

    public static string KnownChineseTitle(string title)
    {
        var key = Regex.Replace(title ?? "", "[’']", "").ToLowerInvariant();
        key = Regex.Replace(key, "[^a-z0-9 .-]", "");
        key = Regex.Replace(key, "\\s+", " ").Trim();
        return EnglishTitlesZh.TryGetValue(key, out var zh) ? zh : "";
    }

    public async Task<ModrinthSearchResult> SearchAsync(string query, string type, string gameVersion, string loader, int offset, int limit, string sort = "relevance", string category = "", CancellationToken ct = default)
    {
        query = (query ?? "").Trim();
        if (!HasChinese(query))
        {
            var plain = await RawSearchAsync(query, type, gameVersion, loader, offset, limit, sort, category, ct);
            foreach (var hit in plain.Hits) hit.ZhTitle = KnownChineseTitle(hit.Title);
            return plain;
        }

        var variants = await ChineseVariantsAsync(query, ct);
        if (offset > 0) return await RawSearchAsync(variants.FirstOrDefault() ?? query, type, gameVersion, loader, offset, limit, sort, category, ct);

        var results = await Task.WhenAll(variants.Take(3).Select(v => RawSearchAsync(v, type, gameVersion, loader, 0, limit, sort, category, ct).ContinueWith(t => t.IsCompletedSuccessfully ? t.Result : null, TaskScheduler.Default)));
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var hits = new List<ModrinthProject>();
        foreach (var result in results)
        {
            if (result?.Hits is null) continue;
            foreach (var hit in result.Hits)
            {
                var key = string.IsNullOrWhiteSpace(hit.ProjectId) ? hit.Slug : hit.ProjectId;
                if (!seen.Add(key)) continue;
                hit.ZhTitle = KnownChineseTitle(hit.Title);
                hits.Add(hit);
            }
        }
        return new ModrinthSearchResult
        {
            Hits = hits.Take(limit).ToList(),
            Offset = 0,
            Limit = limit,
            TotalHits = results.Length == 0 ? 0 : results.Max(r => r?.TotalHits ?? 0)
        };
    }

    public async Task<IReadOnlyList<ModrinthProject>> FeaturedAsync(string type, string gameVersion, string loader, int limit = 12, CancellationToken ct = default)
    {
        var result = await RawSearchAsync("", type, gameVersion, loader, 0, limit, "downloads", "", ct);
        foreach (var hit in result.Hits) hit.ZhTitle = KnownChineseTitle(hit.Title);
        return result.Hits;
    }

    private async Task<ModrinthSearchResult> RawSearchAsync(string query, string type, string gameVersion, string loader, int offset, int limit, string sort, string category, CancellationToken ct)
    {
        var facets = new List<string[]> { new[] { "project_type:" + type } };
        if (!string.IsNullOrWhiteSpace(gameVersion)) facets.Add(new[] { "versions:" + gameVersion });
        if (!string.IsNullOrWhiteSpace(loader) && (type == "mod" || type == "modpack")) facets.Add(new[] { "categories:" + loader.ToLowerInvariant() });
        if (!string.IsNullOrWhiteSpace(category)) facets.Add(new[] { "categories:" + category.ToLowerInvariant() });
        var url = Api + "/search?query=" + Uri.EscapeDataString(query ?? "")
            + "&limit=" + Math.Clamp(limit, 1, 40)
            + "&offset=" + Math.Max(0, offset)
            + "&index=" + Uri.EscapeDataString(sort)
            + "&facets=" + Uri.EscapeDataString(JsonSerializer.Serialize(facets));

        if (_searchCache.TryGetValue(url, out var cached) && DateTime.UtcNow - cached.At < TimeSpan.FromMinutes(2)) return cached.Data;
        var result = await _http.GetJsonAsync<ModrinthSearchResult>(url, ct) ?? new ModrinthSearchResult();
        _searchCache[url] = (DateTime.UtcNow, result);
        return result;
    }

    public async Task<ModrinthProject> ProjectAsync(string idOrSlug, CancellationToken ct = default)
    {
        if (_projectCache.TryGetValue(idOrSlug, out var cached)) return cached;
        var project = await _http.GetJsonAsync<ModrinthProject>(Api + "/project/" + Uri.EscapeDataString(idOrSlug), ct)
            ?? throw new InvalidOperationException("无法读取项目详情");
        // /project/{id|slug} returns "id", not "project_id"; keep a canonical id for version lookups.
        if (string.IsNullOrWhiteSpace(project.ProjectId))
            project.ProjectId = string.IsNullOrWhiteSpace(project.Id) ? idOrSlug : project.Id;
        project.ZhTitle = KnownChineseTitle(project.Title);
        _projectCache[idOrSlug] = project;
        return project;
    }

    public async Task<ModrinthVersion?> LookupByHashAsync(string sha1, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(sha1)) return null;
        using var request = new HttpRequestMessage(HttpMethod.Post,
            Api + "/version_file/" + Uri.EscapeDataString(sha1.Trim().ToLowerInvariant()) + "?algorithm=sha1");
        using var response = await _http.Client.SendAsync(request, ct);
        if (response.StatusCode == HttpStatusCode.NotFound) return null;
        response.EnsureSuccessStatusCode();
        var json = await response.Content.ReadAsStringAsync(ct);
        return JsonSerializer.Deserialize<ModrinthVersion>(json, JsonDefaults.Options);
    }

    public async Task<IReadOnlyList<ModrinthVersion>> VersionsAsync(string idOrSlug, string gameVersion, string loader, CancellationToken ct = default)
    {
        var url = Api + "/project/" + Uri.EscapeDataString(idOrSlug) + "/version";
        var args = new List<string>();
        if (!string.IsNullOrWhiteSpace(gameVersion)) args.Add("game_versions=" + Uri.EscapeDataString(JsonSerializer.Serialize(new[] { gameVersion })));
        if (!string.IsNullOrWhiteSpace(loader)) args.Add("loaders=" + Uri.EscapeDataString(JsonSerializer.Serialize(new[] { loader.ToLowerInvariant() })));
        if (args.Count > 0) url += "?" + string.Join("&", args);
        using var response = await _http.Client.GetAsync(url, ct);
        var text = await response.Content.ReadAsStringAsync(ct);
        if (!response.IsSuccessStatusCode)
            throw new ApiException((int)response.StatusCode, "Modrinth 版本接口返回 HTTP " + (int)response.StatusCode);
        try
        {
            return JsonSerializer.Deserialize<List<ModrinthVersion>>(text, JsonDefaults.Options) ?? new List<ModrinthVersion>();
        }
        catch (JsonException ex)
        {
            AppLog.Error("modrinth", "版本列表解析失败 URL=" + url + " BODY=" + (text.Length > 1200 ? text[..1200] : text), ex);
            throw;
        }
    }

    public async Task<ModrinthVersion> VersionAsync(string versionId, CancellationToken ct = default)
    {
        return await _http.GetJsonAsync<ModrinthVersion>(Api + "/version/" + Uri.EscapeDataString(versionId), ct)
               ?? throw new InvalidOperationException("该版本不存在或已下架");
    }

    public sealed record DependencyInstall(ModrinthProject Project, ModrinthVersion Version, bool Direct);

    /// <summary>递归收集必需依赖（深度 4），跳过可选依赖与已安装的同项目版本。</summary>
    public async Task<IReadOnlyList<DependencyInstall>> RequiredDependenciesAsync(ModrinthVersion root, string gameVersion, string loader, CancellationToken ct = default)
    {
        var output = new List<DependencyInstall>();
        var visitedVersions = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { root.Id };
        var visitedProjects = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { root.ProjectId };
        var queue = new Queue<(ModrinthVersion Version, int Depth)>();
        queue.Enqueue((root, 0));
        while (queue.Count > 0)
        {
            var (current, depth) = queue.Dequeue();
            if (depth >= 4) continue;
            foreach (var dependency in current.Dependencies ?? new List<ModrinthDependency>())
            {
                if (!string.Equals(dependency.DependencyType, "required", StringComparison.OrdinalIgnoreCase)) continue;
                ModrinthVersion? version = null;
                try
                {
                    if (!string.IsNullOrWhiteSpace(dependency.VersionId))
                        version = await VersionAsync(dependency.VersionId!, ct);
                    else if (!string.IsNullOrWhiteSpace(dependency.ProjectId))
                    {
                        var list = await VersionsAsync(dependency.ProjectId!, gameVersion, loader, ct);
                        version = list.FirstOrDefault(v => string.Equals(v.VersionType, "release", StringComparison.OrdinalIgnoreCase)) ?? list.FirstOrDefault();
                    }
                }
                catch { }
                if (version is null || !visitedVersions.Add(version.Id) || !visitedProjects.Add(version.ProjectId)) continue;
                ModrinthProject project;
                try { project = await ProjectAsync(version.ProjectId, ct); }
                catch { project = new ModrinthProject { ProjectId = version.ProjectId, Title = version.Name, Slug = version.ProjectId }; }
                output.Add(new DependencyInstall(project, version, false));
                queue.Enqueue((version, depth + 1));
            }
        }
        return output;
    }

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

    public Task<string> InstallAsync(ModrinthProject project, ModrinthVersion version, string targetDirectory,
        IProgress<DownloadProgress>? progress = null, CancellationToken ct = default)
    {
        var file = version.Files.FirstOrDefault(f => f.Primary) ?? version.Files.FirstOrDefault()
            ?? throw new InvalidOperationException("该版本没有可下载文件");
        return InstallFileAsync(project, version, file, targetDirectory, progress, ct);
    }

    public async Task<string> InstallFileAsync(ModrinthProject project, ModrinthVersion version, ModrinthFile file,
        string targetDirectory, IProgress<DownloadProgress>? progress = null, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(file.Url)) throw new InvalidOperationException("该文件没有下载地址");
        Directory.CreateDirectory(targetDirectory);
        var safeName = Regex.Replace(Path.GetFileName(file.Filename), "[\\/:*?\"<>|]", "_");
        var target = UniqueTarget(targetDirectory, string.IsNullOrWhiteSpace(safeName) ? project.Slug + ".jar" : safeName);
        return await DownloadToTargetAsync(file, target, progress, ct);
    }

    /// <summary>下载到用户在系统保存对话框中选定的完整路径，允许覆盖同名文件。</summary>
    public async Task<string> InstallToPathAsync(ModrinthFile file, string targetPath,
        IProgress<DownloadProgress>? progress = null, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(file.Url)) throw new InvalidOperationException("该文件没有下载地址");
        var directory = Path.GetDirectoryName(targetPath);
        if (!string.IsNullOrWhiteSpace(directory)) Directory.CreateDirectory(directory);
        return await DownloadToTargetAsync(file, targetPath, progress, ct);
    }

    /// <summary>并发分片数；由运行时设置写入，默认 6。</summary>
    public int MaxParallelChunks { get; set; } = 6;

    private async Task<bool> TryParallelDownloadAsync(ModrinthFile file, string temp,
        IProgress<DownloadProgress>? progress, CancellationToken ct)
    {
        if (MaxParallelChunks < 2 || file.Size < 512 * 1024) return false;

        long total;
        try
        {
            using var probeRequest = new HttpRequestMessage(HttpMethod.Get, file.Url);
            probeRequest.Headers.Range = new RangeHeaderValue(0, 0);
            using var probe = await _http.Client.SendAsync(probeRequest, HttpCompletionOption.ResponseHeadersRead, ct);
            if (probe.StatusCode != HttpStatusCode.PartialContent) return false;
            total = probe.Content.Headers.ContentRange?.Length ?? file.Size;
        }
        catch (OperationCanceledException) { throw; }
        catch { return false; }

        if (total < 512 * 1024) return false;
        var chunks = Math.Clamp(MaxParallelChunks, 2, 8);
        while (chunks > 2 && total / chunks < 256 * 1024) chunks--;
        var chunkSize = (total + chunks - 1) / chunks;
        var parts = new string[chunks];
        for (var i = 0; i < chunks; i++) parts[i] = temp + ".c" + i;
        AppLog.Info("modrinth", "并行下载 " + file.Filename + " · " + chunks + " 线程 · " + total + " bytes");

        try
        {
            long received = 0;
            long lastTick = -400;
            var gate = new object();
            var clock = Stopwatch.StartNew();
            var tasks = new Task[chunks];
            for (var i = 0; i < chunks; i++)
            {
                var index = i;
                var start = (long)index * chunkSize;
                var end = Math.Min(total - 1, start + chunkSize - 1);
                tasks[i] = Task.Run(async () =>
                {
                    using var request = new HttpRequestMessage(HttpMethod.Get, file.Url);
                    request.Headers.Range = new RangeHeaderValue(start, end);
                    using var response = await _http.Client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
                    if (response.StatusCode != HttpStatusCode.PartialContent)
                        throw new IOException("服务器未返回分片响应");
                    await using var input = await response.Content.ReadAsStreamAsync(ct);
                    await using var output = new FileStream(parts[index], FileMode.Create, FileAccess.Write, FileShare.None, 1024 * 128, true);
                    var buffer = new byte[1024 * 128];
                    int read;
                    while ((read = await input.ReadAsync(buffer, ct)) > 0)
                    {
                        await output.WriteAsync(buffer.AsMemory(0, read), ct);
                        var now = Interlocked.Add(ref received, read);
                        lock (gate)
                        {
                            if (clock.ElapsedMilliseconds - lastTick < 160) continue;
                            lastTick = clock.ElapsedMilliseconds;
                            var speed = now / Math.Max(0.2, clock.Elapsed.TotalSeconds);
                            progress?.Report(new DownloadProgress("download", total > 0 ? now * 100.0 / total : 0,
                                now, total, speed, "正在并行下载 " + file.Filename + " · " + chunks + " 线程"));
                        }
                    }
                }, ct);
            }

            await Task.WhenAll(tasks);
            await using (var output = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None, 1024 * 256, true))
            {
                foreach (var part in parts)
                {
                    await using var input = new FileStream(part, FileMode.Open, FileAccess.Read, FileShare.Read, 1024 * 256, true);
                    await input.CopyToAsync(output, 1024 * 256, ct);
                }
            }
            foreach (var part in parts) TryDelete(part);
            return true;
        }
        catch (OperationCanceledException)
        {
            CleanupParts(temp, parts);
            throw;
        }
        catch
        {
            CleanupParts(temp, parts);
            return false;
        }
    }

    private static void CleanupParts(string temp, string[] parts)
    {
        foreach (var part in parts) TryDelete(part);
        TryDelete(temp);
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { }
    }

    private async Task<string> DownloadToTargetAsync(ModrinthFile file, string target,
        IProgress<DownloadProgress>? progress, CancellationToken ct)
    {
        var temp = target + ".part";
        try
        {
            if (await TryParallelDownloadAsync(file, temp, progress, ct))
            {
                if (File.Exists(target)) File.Delete(target);
                File.Move(temp, target);
                progress?.Report(new DownloadProgress("done", 100, file.Size, file.Size, 0, "已安装 " + file.Filename));
                return target;
            }
            using var res = await _http.Client.GetAsync(file.Url, HttpCompletionOption.ResponseHeadersRead, ct);
            res.EnsureSuccessStatusCode();
            var total = res.Content.Headers.ContentLength ?? file.Size;
            await using (var input = await res.Content.ReadAsStreamAsync(ct))
            await using (var output = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None, 1024 * 128, true))
            {
                var buffer = new byte[1024 * 128];
                long received = 0;
                var sw = Stopwatch.StartNew();
                long lastTick = 0;
                int read;
                while ((read = await input.ReadAsync(buffer, ct)) > 0)
                {
                    await output.WriteAsync(buffer.AsMemory(0, read), ct);
                    received += read;
                    if (sw.ElapsedMilliseconds - lastTick > 180)
                    {
                        lastTick = sw.ElapsedMilliseconds;
                        var speed = received / Math.Max(0.2, sw.Elapsed.TotalSeconds);
                        progress?.Report(new DownloadProgress("download", total > 0 ? received * 100.0 / total : 0, received, total, speed, "正在下载 " + file.Filename));
                    }
                }
            }
            if (File.Exists(target)) File.Delete(target);
            File.Move(temp, target);
            progress?.Report(new DownloadProgress("done", 100, file.Size, file.Size, 0, "已安装 " + file.Filename));
            return target;
        }
        catch
        {
            try { if (File.Exists(temp)) File.Delete(temp); } catch { }
            throw;
        }
    }

    public sealed record ModFileUpdate(LibraryItem Item, ModrinthVersion Version, string FileName, string Url, long Size);

    public async Task<IReadOnlyList<ModFileUpdate>> CheckUpdatesAsync(IReadOnlyList<LibraryItem> mods, string gameVersion, string loader, CancellationToken ct = default)
    {
        var entries = new List<(LibraryItem Item, string Hash)>();
        foreach (var item in mods.Where(m => m.Kind == "mod" && !m.IsDirectory && File.Exists(m.Path)))
        {
            try
            {
                await using var stream = File.OpenRead(item.Path);
                var hash = Convert.ToHexString(await SHA1.HashDataAsync(stream, ct)).ToLowerInvariant();
                entries.Add((item, hash));
            }
            catch { }
        }
        if (entries.Count == 0) return Array.Empty<ModFileUpdate>();

        var body = new Dictionary<string, object>
        {
            ["hashes"] = entries.Select(e => e.Hash).Distinct(StringComparer.OrdinalIgnoreCase).ToList(),
            ["algorithm"] = "sha1"
        };
        if (!string.IsNullOrWhiteSpace(gameVersion)) body["game_versions"] = new[] { gameVersion };
        if (!string.IsNullOrWhiteSpace(loader)) body["loaders"] = new[] { loader.ToLowerInvariant() };

        var latest = await _http.PostJsonAsync<Dictionary<string, ModrinthVersion>>(Api + "/version_files/update", body, ct)
                     ?? new Dictionary<string, ModrinthVersion>();
        var updates = new List<ModFileUpdate>();
        foreach (var (item, hash) in entries)
        {
            if (!latest.TryGetValue(hash, out var version)) continue;
            var file = version.Files.FirstOrDefault(f => f.Primary) ?? version.Files.FirstOrDefault();
            if (file is null || string.IsNullOrWhiteSpace(file.Url)) continue;
            file.Hashes.TryGetValue("sha1", out var newestHash);
            if (!string.IsNullOrWhiteSpace(newestHash) && string.Equals(newestHash, hash, StringComparison.OrdinalIgnoreCase)) continue;
            updates.Add(new ModFileUpdate(item, version, file.Filename, file.Url, file.Size));
        }
        return updates;
    }

    public async Task<string> ApplyUpdateAsync(ModFileUpdate update, IProgress<DownloadProgress>? progress, CancellationToken ct = default)
    {
        var directory = Path.GetDirectoryName(update.Item.Path)
            ?? throw new InvalidOperationException("无法定位模组目录");
        var safeName = Regex.Replace(Path.GetFileName(update.FileName), "[\\/:*?\"<>|]", "_");
        if (string.IsNullOrWhiteSpace(safeName)) safeName = Path.GetFileName(update.Item.Path);
        var target = Path.Combine(directory, safeName + (update.Item.Enabled ? "" : ".disabled"));
        var temp = target + ".part";

        using (var res = await _http.Client.GetAsync(update.Url, HttpCompletionOption.ResponseHeadersRead, ct))
        {
            res.EnsureSuccessStatusCode();
            var total = res.Content.Headers.ContentLength ?? update.Size;
            await using var input = await res.Content.ReadAsStreamAsync(ct);
            await using var output = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None, 131072, true);
            var buffer = new byte[131072];
            long received = 0;
            var sw = Stopwatch.StartNew();
            long lastTick = 0;
            int read;
            while ((read = await input.ReadAsync(buffer, ct)) > 0)
            {
                await output.WriteAsync(buffer.AsMemory(0, read), ct);
                received += read;
                if (sw.ElapsedMilliseconds - lastTick > 180)
                {
                    lastTick = sw.ElapsedMilliseconds;
                    var speed = received / Math.Max(0.2, sw.Elapsed.TotalSeconds);
                    progress?.Report(new DownloadProgress("update", total > 0 ? received * 100.0 / total : 0, received, total, speed,
                        "正在更新 " + Path.GetFileName(update.Item.Path) + " · " + (speed / 1048576.0).ToString("0.0") + " MB/s"));
                }
            }
        }
        try
        {
            if (update.Item.Path != target && File.Exists(update.Item.Path)) File.Delete(update.Item.Path);
            if (File.Exists(target)) File.Delete(target);
            File.Move(temp, target);
        }
        catch
        {
            try { if (File.Exists(temp)) File.Delete(temp); } catch { }
            throw;
        }
        progress?.Report(new DownloadProgress("done", 100, update.Size, update.Size, 0, "已更新 " + safeName));
        return target;
    }

    public static string FolderFor(string type) => type switch
    {
        "resourcepack" => "resourcepacks",
        "shader" => "shaderpacks",
        "modpack" => "modpacks",
        _ => "mods"
    };

    private async Task<List<string>> ChineseVariantsAsync(string query, CancellationToken ct)
    {
        var list = new List<string>();
        if (ChineseTerms.TryGetValue(query, out var exact)) list.Add(exact);
        var mapped = query;
        foreach (var pair in ChineseTerms.OrderByDescending(p => p.Key.Length))
            if (mapped.Contains(pair.Key, StringComparison.Ordinal)) mapped = mapped.Replace(pair.Key, " " + pair.Value + " ", StringComparison.Ordinal);
        mapped = Regex.Replace(mapped, "\\s+", " ").Trim();
        if (!string.IsNullOrWhiteSpace(mapped) && !HasChinese(mapped)) list.Add(mapped);

        try
        {
            var translated = await _http.GetJsonAsync<JsonElement>("https://api.mymemory.translated.net/get?q=" + Uri.EscapeDataString(query) + "&langpair=zh-CN|en", ct);
            if (translated.ValueKind == JsonValueKind.Object && translated.TryGetProperty("responseData", out var rd) && rd.TryGetProperty("translatedText", out var t))
            {
                var text = t.GetString() ?? "";
                if (!string.IsNullOrWhiteSpace(text) && !HasChinese(text)) list.Add(text);
            }
        }
        catch { }

        list.Add(query);
        return list.Select(x => x.Trim()).Where(x => x.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).Take(4).ToList();
    }
}