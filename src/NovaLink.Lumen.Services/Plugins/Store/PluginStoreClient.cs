using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using NovaLink.Lumen.Core;
using NovaLink.Lumen.Core.Models;
using NovaLink.Lumen.Core.Plugins;
using NovaLink.Lumen.Services.Http;

namespace NovaLink.Lumen.Services.Plugins.Store;

/// <summary>
/// 插件商店的数据源：扫描 GitHub 上打了 topic <see cref="PluginStoreTopics.Topic"/> 的公开仓库，
/// 读取仓库根目录的 plugin.json（安装清单）与可选的 store.json（商店元数据），组装成可展示、可安装的条目。
/// 设计要点：
/// 1) 一次查询只打 1 次 api.github.com（search 接口，未登录配额 10 次/分钟），
///    其余的仓库清单全部走 raw.githubusercontent.com（不占 API 配额）。
/// 2) 搜索结果与仓库清单都写本地缓存（&lt;DataRoot&gt;\plugin-store-cache.json），清单缓存 6 小时、搜索缓存 30 分钟；
///    限流或离线时回落缓存，并用 <see cref="PluginStorePage.Notice"/> 告诉用户“这是旧数据”。
/// 3) 安装包地址优先取 store.json 的 download，其次按 asset 推导 GitHub Release 地址，
///    最后按 &lt;插件 id&gt;.lumenplugin 推断；推导过程不调用 releases API，避免烧掉 60 次/小时的核心配额。
/// </summary>
public sealed class PluginStoreClient
{
    private const string LogArea = "plugin-store";

    /// <summary>仓库清单（plugin.json / store.json）缓存时长。</summary>
    private static readonly TimeSpan ManifestTtl = TimeSpan.FromHours(6);

    /// <summary>同一时刻最多并发读几个仓库的清单（raw.githubusercontent 没有配额，但别把网络打满）。</summary>
    private const int MaxConcurrency = 4;

    private static readonly JsonSerializerOptions CacheJson = new()
    {
        WriteIndented = false,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private readonly HttpService _http;
    private readonly PluginPaths _paths;
    private readonly object _gate = new();
    private StoreCache? _cache;

    /// <summary>创建商店客户端。</summary>
    /// <param name="http">复用的 HTTP 服务（已带 User-Agent 与解压配置）。</param>
    /// <param name="paths">插件路径计算入口，只用到 DataRoot 与 PluginsRoot。</param>
    public PluginStoreClient(HttpService http, PluginPaths paths)
    {
        _http = http ?? throw new ArgumentNullException(nameof(http));
        _paths = paths ?? throw new ArgumentNullException(nameof(paths));
    }

    /// <summary>当前启动器版本（形如 1.4.1），用于计算条目是否与本机兼容；为空时不判断兼容性。</summary>
    public string LauncherVersion { get; set; } = "";

    /// <summary>GitHub API 根地址（测试时可指向本地桩服务）。</summary>
    public string ApiBaseUrl { get; set; } = "https://api.github.com";

    /// <summary>GitHub 原始内容根地址（测试时可指向本地桩服务）。</summary>
    public string RawBaseUrl { get; set; } = "https://raw.githubusercontent.com";

    /// <summary>GitHub 网页根地址（用于拼仓库页与 Release 下载地址）。</summary>
    public string WebBaseUrl { get; set; } = "https://github.com";

    /// <summary>缓存文件路径：&lt;DataRoot&gt;\plugin-store-cache.json。</summary>
    public string CacheFile => Path.Combine(_paths.DataRoot, PluginStoreTopics.CacheFileName);

    /// <summary>
    /// 查询商店。永远不会抛异常：网络/限流/解析问题都会落到 <see cref="PluginStorePage.Notice"/> 上。
    /// </summary>
    public async Task<PluginStorePage> SearchAsync(PluginStoreQuery query, CancellationToken ct = default)
    {
        query ??= new PluginStoreQuery();
        var page = Math.Max(1, query.Page);
        var size = Math.Clamp(query.PageSize, 1, 50);
        var key = BuildCacheKey(query, page, size);

        var cache = LoadCache();
        CachedSearch? cachedSearch;
        lock (_gate) { cache.Searches.TryGetValue(key, out cachedSearch); }

        if (!query.ForceRefresh && cachedSearch is not null && IsFresh(cachedSearch.SavedAt, PluginStoreTopics.CacheTtl))
            return await BuildPageAsync(cachedSearch, cache, fromCache: true, notice: null, ct).ConfigureAwait(false);

        SearchPayload payload;
        try
        {
            payload = await FetchSearchAsync(query, page, size, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return await FallbackAsync(cachedSearch, cache, "连接 GitHub 超时了，已显示上次缓存的结果，请稍后重试。", ct).ConfigureAwait(false);
        }
        catch (ApiException ex) when (ex.StatusCode == 403 || ex.StatusCode == 429)
        {
            AppLog.Warn(LogArea, "GitHub 搜索接口限流（" + ex.StatusCode + "）：" + ex.Message);
            return await FallbackAsync(cachedSearch, cache, "GitHub 接口暂时限流了（未登录时每分钟最多 10 次搜索），稍等几分钟再刷新。", ct).ConfigureAwait(false);
        }
        catch (ApiException ex)
        {
            AppLog.Warn(LogArea, "GitHub 搜索接口返回 " + ex.StatusCode + "：" + ex.Message);
            return await FallbackAsync(cachedSearch, cache, "GitHub 接口返回了错误（HTTP " + ex.StatusCode + "），请稍后重试。", ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException)
        {
            AppLog.Warn(LogArea, "连不上 GitHub：" + ex.Message);
            return await FallbackAsync(cachedSearch, cache, "连不上 GitHub，请检查网络后重试。", ct).ConfigureAwait(false);
        }

        var search = new CachedSearch { SavedAt = DateTimeOffset.UtcNow, TotalCount = payload.TotalCount, Repos = payload.Repos };
        lock (_gate)
        {
            cache.Searches[key] = search;
            cache.SavedAt = DateTimeOffset.UtcNow;
        }

        if (payload.TotalCount == 0)
            return new PluginStorePage
            {
                Items = Array.Empty<PluginStoreEntry>(),
                TotalCount = 0,
                FetchedAt = DateTimeOffset.UtcNow
            };

        return await BuildPageAsync(search, cache, fromCache: false, notice: null, ct).ConfigureAwait(false);
    }

    /// <summary>把条目的安装包下载到临时目录，返回 .lumenplugin 的绝对路径（供 PluginPackageInstaller 安装）。</summary>
    public async Task<string> DownloadPackageAsync(PluginStoreEntry entry, IProgress<DownloadProgress>? progress = null, CancellationToken ct = default)
    {
        if (entry is null) throw new ArgumentNullException(nameof(entry));
        if (!Uri.TryCreate(entry.DownloadUrl, UriKind.Absolute, out var uri) ||
            (uri.Scheme != Uri.UriSchemeHttps && uri.Scheme != Uri.UriSchemeHttp))
            throw new InvalidOperationException("这个插件没有提供可用的安装包下载地址。");

        var name = SafeFileName(entry.PackageName) ?? SafeFileName(entry.Id + PluginStoreTopics.PackageExtension) ?? "plugin.lumenplugin";
        if (!name.EndsWith(PluginStoreTopics.PackageExtension, StringComparison.OrdinalIgnoreCase))
            name += PluginStoreTopics.PackageExtension;

        var directory = Path.Combine(Path.GetTempPath(), "novalink-store");
        Directory.CreateDirectory(directory);
        var target = Path.Combine(directory, name);
        TryDeleteFile(target);

        AppLog.Info(LogArea, "开始下载插件包：" + entry.Id + " <- " + entry.DownloadUrl);
        await _http.DownloadFileAsync(entry.DownloadUrl, target, progress, "plugin-store", ct).ConfigureAwait(false);

        var size = 0L;
        try { size = new FileInfo(target).Length; } catch { }
        if (size <= 0 || size > PluginManifestReader.MaxPluginBytes)
        {
            TryDeleteFile(target);
            throw new InvalidOperationException("下载到的插件包大小不正常（" + size + " 字节），已放弃安装。");
        }

        AppLog.Info(LogArea, "插件包已下载：" + name + "（" + size + " 字节）");
        return target;
    }

    /// <summary>清空内存与磁盘缓存（设置页“刷新”到极限或开发者排查时用）。</summary>
    public void ClearCache()
    {
        lock (_gate) { _cache = new StoreCache(); }
        TryDeleteFile(CacheFile);
        AppLog.Info(LogArea, "插件商店缓存已清空。");
    }

    // ------------------------------------------------------------------
    // 检索
    // ------------------------------------------------------------------

    private async Task<PluginStorePage> FallbackAsync(CachedSearch? cached, StoreCache cache, string notice, CancellationToken ct)
    {
        if (cached is null)
            return new PluginStorePage { Items = Array.Empty<PluginStoreEntry>(), Notice = notice, FetchedAt = DateTimeOffset.UtcNow };

        AppLog.Info(LogArea, "回落到缓存结果：" + notice);
        var result = await BuildPageAsync(cached, cache, fromCache: true, notice: notice, ct).ConfigureAwait(false);
        return result;
    }

    private async Task<SearchPayload> FetchSearchAsync(PluginStoreQuery query, int page, int size, CancellationToken ct)
    {
        var q = new StringBuilder("topic:").Append(PluginStoreTopics.Topic);
        var words = (query.Search ?? "").Trim();
        if (words.Length > 0)
        {
            var safe = new string(words.Where(c => !char.IsControl(c) && c != '"' && c != '\\').ToArray());
            if (safe.Length > 0) q.Append(' ').Append(safe).Append(" in:name,description");
        }

        var sort = query.Sort == PluginStoreSort.Updated ? "updated" : "stars";
        var url = ApiBaseUrl.TrimEnd('/') + "/search/repositories?q=" + Uri.EscapeDataString(q.ToString())
                  + "&sort=" + sort + "&order=desc&per_page=" + size + "&page=" + page;

        AppLog.Info(LogArea, "查询插件商店：" + url);
        var text = await _http.GetStringAsync(url, ct).ConfigureAwait(false);

        var repos = new List<RepoItem>();
        var total = 0;
        using (var doc = JsonDocument.Parse(text))
        {
            var root = doc.RootElement;
            if (root.TryGetProperty("total_count", out var totalElement) && totalElement.TryGetInt32(out var parsedTotal))
                total = parsedTotal;

            if (root.TryGetProperty("items", out var items) && items.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in items.EnumerateArray())
                {
                    var repo = RepoItem.FromJson(item);
                    if (repo is not null) repos.Add(repo);
                }
            }
        }

        AppLog.Info(LogArea, "GitHub 返回 " + repos.Count + " 个仓库（共 " + total + " 个命中）。");
        return new SearchPayload { TotalCount = total, Repos = repos };
    }

    private async Task<PluginStorePage> BuildPageAsync(CachedSearch search, StoreCache cache, bool fromCache, string? notice, CancellationToken ct)
    {
        // Name 排序 GitHub 不支持，取回后在内存里排序。
        var source = search.Repos.ToList();
        if (source.Count == 0)
            return new PluginStorePage
            {
                Items = Array.Empty<PluginStoreEntry>(),
                TotalCount = search.TotalCount,
                FromCache = fromCache,
                Notice = notice,
                FetchedAt = search.SavedAt
            };

        var installed = ReadInstalledVersions();
        var results = new PluginStoreEntry?[source.Count];
        using var limiter = new SemaphoreSlim(MaxConcurrency, MaxConcurrency);
        var skipped = 0;

        var tasks = new List<Task>(source.Count);
        for (var index = 0; index < source.Count; index++)
        {
            var i = index;
            var repo = source[i];
            tasks.Add(Task.Run(async () =>
            {
                await limiter.WaitAsync(ct).ConfigureAwait(false);
                try
                {
                    var entry = await BuildEntryAsync(repo, installed, cache, ct).ConfigureAwait(false);
                    if (entry is null) Interlocked.Increment(ref skipped);
                    results[i] = entry;
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex)
                {
                    Interlocked.Increment(ref skipped);
                    AppLog.Warn(LogArea, "跳过仓库 " + repo.FullName + "：" + ex.Message);
                }
                finally { limiter.Release(); }
            }, ct));
        }

        await Task.WhenAll(tasks).ConfigureAwait(false);
        ct.ThrowIfCancellationRequested();

        var items = results.Where(e => e is not null).Select(e => e!).ToList();
        if (items.Count > 0) SaveCache(cache);
        AppLog.Info(LogArea, "商店条目组装完成：可用 " + items.Count + " 个，跳过 " + skipped + " 个仓库。");

        return new PluginStorePage
        {
            Items = items,
            TotalCount = search.TotalCount,
            Scanned = source.Count,
            Skipped = skipped,
            FromCache = fromCache,
            Notice = notice,
            FetchedAt = search.SavedAt
        };
    }

    private async Task<PluginStoreEntry?> BuildEntryAsync(RepoItem repo, Dictionary<string, string> installed, StoreCache cache, CancellationToken ct)
    {
        var manifestJson = await ReadRepoFileAsync(repo, PluginStoreTopics.ManifestFileName, cache, ct).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(manifestJson)) return null;

        PluginManifest? manifest;
        try { manifest = JsonSerializer.Deserialize<PluginManifest>(manifestJson!, JsonDefaults.Options); }
        catch (JsonException) { return null; }

        if (manifest is null || !PluginPaths.IsValidId(manifest.Id) || string.IsNullOrWhiteSpace(manifest.Name) || string.IsNullOrWhiteSpace(manifest.Version))
            return null;

        if (manifest.ApiVersion != PluginApiVersion.Value)
            AppLog.Info(LogArea, "仓库 " + repo.FullName + " 的 API 版本是 " + manifest.ApiVersion + "，与当前 " + PluginApiVersion.Value + " 不同，仍会展示但标记为不可安装。");

        var storeJson = await ReadRepoFileAsync(repo, PluginStoreTopics.StoreFileName, cache, ct).ConfigureAwait(false);
        PluginStoreRepoManifest? storeManifest = null;
        if (!string.IsNullOrWhiteSpace(storeJson))
        {
            try { storeManifest = JsonSerializer.Deserialize<PluginStoreRepoManifest>(storeJson!, JsonDefaults.Options); }
            catch (JsonException) { storeManifest = null; }
        }

        var packageName = SafeFileName(storeManifest?.Asset) ?? (manifest.Id + PluginStoreTopics.PackageExtension);
        if (!packageName.EndsWith(PluginStoreTopics.PackageExtension, StringComparison.OrdinalIgnoreCase))
            packageName += PluginStoreTopics.PackageExtension;

        var download = ResolveDownloadUrl(repo, storeManifest, packageName);

        var installedVersion = installed.TryGetValue(manifest.Id, out var local) ? local : null;
        var updateAvailable = installedVersion is not null && CompareVersion(manifest.Version, installedVersion) > 0;

        return new PluginStoreEntry
        {
            Id = manifest.Id,
            Name = manifest.Name,
            Version = manifest.Version,
            Author = string.IsNullOrWhiteSpace(manifest.Author) ? repo.Owner : manifest.Author!,
            Description = manifest.Description ?? "",
            Kind = manifest.Kind ?? "",
            ApiVersion = manifest.ApiVersion,
            MinLauncher = manifest.MinLauncher,
            MaxLauncher = manifest.MaxLauncher,
            RepoOwner = repo.Owner,
            RepoName = repo.Name,
            RepoUrl = string.IsNullOrWhiteSpace(repo.HtmlUrl) ? WebBaseUrl + "/" + repo.FullName : repo.HtmlUrl,
            Stars = repo.Stars,
            Updated = repo.Updated,
            DownloadUrl = download,
            PackageName = packageName,
            Installed = installedVersion is not null,
            InstalledVersion = installedVersion,
            UpdateAvailable = updateAvailable,
            Incompatible = CheckCompatibility(manifest),
            Tags = storeManifest?.Tags is null ? Array.Empty<string>() : storeManifest.Tags.Where(t => !string.IsNullOrWhiteSpace(t)).Take(4).ToArray()
        };
    }

    private string ResolveDownloadUrl(RepoItem repo, PluginStoreRepoManifest? store, string packageName)
    {
        if (store is not null && !string.IsNullOrWhiteSpace(store.Download) &&
            Uri.TryCreate(store.Download!.Trim(), UriKind.Absolute, out var direct) &&
            (direct.Scheme == Uri.UriSchemeHttps || direct.Scheme == Uri.UriSchemeHttp))
            return direct.ToString();

        return WebBaseUrl.TrimEnd('/') + "/" + repo.FullName + "/releases/latest/download/" + Uri.EscapeDataString(packageName);
    }

    private string? CheckCompatibility(PluginManifest manifest)
    {
        if (manifest.ApiVersion != PluginApiVersion.Value)
            return "这个插件使用的是插件 API 版本 " + manifest.ApiVersion + "，当前启动器只支持 " + PluginApiVersion.Value + "，需要请作者更新。";

        var launcher = LauncherVersion;
        if (string.IsNullOrWhiteSpace(launcher)) return null;

        if (!string.IsNullOrWhiteSpace(manifest.MinLauncher) && CompareVersion(launcher, manifest.MinLauncher!) < 0)
            return "需要启动器 " + manifest.MinLauncher + " 或更高版本，当前是 " + launcher + "。";

        if (!string.IsNullOrWhiteSpace(manifest.MaxLauncher) && CompareVersion(launcher, manifest.MaxLauncher!) > 0)
            return "这个插件只支持到启动器 " + manifest.MaxLauncher + "，当前是 " + launcher + "。";

        return null;
    }

    // ------------------------------------------------------------------
    // 仓库清单读取（带缓存）
    // ------------------------------------------------------------------

    private async Task<string?> ReadRepoFileAsync(RepoItem repo, string fileName, StoreCache cache, CancellationToken ct)
    {
        CachedManifest? cached;
        lock (_gate) { cache.Manifests.TryGetValue(repo.FullName + "/" + fileName, out cached); }
        if (cached is not null && IsFresh(cached.FetchedAt, ManifestTtl) && cached.Content is not null)
            return cached.Content;

        var branches = new List<string>();
        if (!string.IsNullOrWhiteSpace(repo.DefaultBranch)) branches.Add(repo.DefaultBranch);
        foreach (var fallback in new[] { "main", "master" })
            if (!branches.Contains(fallback)) branches.Add(fallback);

        foreach (var branch in branches)
        {
            var url = RawBaseUrl.TrimEnd('/') + "/" + repo.FullName + "/" + branch + "/" + fileName;
            try
            {
                var text = await _http.GetStringAsync(url, ct).ConfigureAwait(false);
                lock (_gate) { cache.Manifests[repo.FullName + "/" + fileName] = new CachedManifest { FetchedAt = DateTimeOffset.UtcNow, Content = text }; }
                return text;
            }
            catch (ApiException ex) when (ex.StatusCode == 404)
            {
                // 换个分支名再试
            }
            catch (Exception ex) when (ex is ApiException or HttpRequestException or IOException)
            {
                AppLog.Warn(LogArea, "读取 " + url + " 失败：" + ex.Message);
                if (cached?.Content is not null) return cached.Content;
                return null;
            }
        }

        lock (_gate) { cache.Manifests[repo.FullName + "/" + fileName] = new CachedManifest { FetchedAt = DateTimeOffset.UtcNow, Content = null }; }
        return null;
    }

    // ------------------------------------------------------------------
    // 本机安装状态
    // ------------------------------------------------------------------

    private Dictionary<string, string> ReadInstalledVersions()
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            if (!Directory.Exists(_paths.PluginsRoot)) return result;
            foreach (var directory in Directory.EnumerateDirectories(_paths.PluginsRoot))
            {
                var manifestFile = Path.Combine(directory, PluginPaths.ManifestFileName);
                if (!File.Exists(manifestFile)) continue;
                try
                {
                    var manifest = JsonSerializer.Deserialize<PluginManifest>(File.ReadAllText(manifestFile, Encoding.UTF8), JsonDefaults.Options);
                    if (manifest is not null && !string.IsNullOrWhiteSpace(manifest.Id))
                        result[manifest.Id] = manifest.Version ?? "";
                }
                catch { }
            }
        }
        catch (Exception ex)
        {
            AppLog.Warn(LogArea, "读取本机已安装插件版本失败：" + ex.Message);
        }
        return result;
    }

    // ------------------------------------------------------------------
    // 缓存
    // ------------------------------------------------------------------

    private static string BuildCacheKey(PluginStoreQuery query, int page, int size)
        => query.Sort + "|" + page + "|" + size + "|" + (query.Search ?? "").Trim().ToLowerInvariant();

    private StoreCache LoadCache()
    {
        lock (_gate)
        {
            if (_cache is not null) return _cache;
            var loaded = new StoreCache();
            try
            {
                if (File.Exists(CacheFile))
                {
                    var json = File.ReadAllText(CacheFile, Encoding.UTF8);
                    var parsed = JsonSerializer.Deserialize<StoreCache>(json, JsonDefaults.Options);
                    if (parsed is not null) loaded = parsed;
                }
            }
            catch (Exception ex)
            {
                AppLog.Warn(LogArea, "读取商店缓存失败，按空缓存处理：" + ex.Message);
            }

            _cache = loaded;
            return loaded;
        }
    }

    private void SaveCache(StoreCache cache)
    {
        try
        {
            lock (_gate)
            {
                var json = JsonSerializer.Serialize(cache, CacheJson);
                File.WriteAllText(CacheFile, json, new UTF8Encoding(false));
            }
        }
        catch (Exception ex)
        {
            AppLog.Warn(LogArea, "写入商店缓存失败（不影响使用）：" + ex.Message);
        }
    }

    private static bool IsFresh(DateTimeOffset savedAt, TimeSpan ttl) => DateTimeOffset.UtcNow - savedAt <= ttl;

    // ------------------------------------------------------------------
    // 小工具
    // ------------------------------------------------------------------

    private static string? SafeFileName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return null;
        var trimmed = name.Trim();
        if (trimmed.Length > 120) return null;
        foreach (var c in trimmed)
            if (!(char.IsLetterOrDigit(c) || c == '.' || c == '-' || c == '_')) return null;
        if (trimmed.Contains("..", StringComparison.Ordinal)) return null;
        return trimmed;
    }

    private static int CompareVersion(string? left, string? right)
    {
        var a = SplitVersion(left);
        var b = SplitVersion(right);
        var length = Math.Max(a.Length, b.Length);
        for (var i = 0; i < length; i++)
        {
            var x = i < a.Length ? a[i] : 0;
            var y = i < b.Length ? b[i] : 0;
            if (x != y) return x < y ? -1 : 1;
        }
        return 0;
    }

    private static int[] SplitVersion(string? version)
    {
        if (string.IsNullOrWhiteSpace(version)) return Array.Empty<int>();
        var numbers = new List<int>();
        var current = 0;
        var hasDigit = false;
        foreach (var c in version)
        {
            if (char.IsDigit(c))
            {
                current = current * 10 + (c - '0');
                hasDigit = true;
            }
            else if (hasDigit)
            {
                numbers.Add(current);
                current = 0;
                hasDigit = false;
            }
        }
        if (hasDigit) numbers.Add(current);
        return numbers.ToArray();
    }

    private static void TryDeleteFile(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { }
    }

    // ------------------------------------------------------------------
    // 内部模型（只用于缓存与解析，不对外）
    // ------------------------------------------------------------------

    private sealed class SearchPayload
    {
        public int TotalCount { get; init; }
        public List<RepoItem> Repos { get; init; } = new();
    }

    private sealed class RepoItem
    {
        public string Owner { get; init; } = "";
        public string Name { get; init; } = "";
        public string FullName { get; init; } = "";
        public string HtmlUrl { get; init; } = "";
        public string DefaultBranch { get; init; } = "main";
        public int Stars { get; init; }
        public DateTimeOffset Updated { get; init; }
        public string Description { get; init; } = "";

        public static RepoItem? FromJson(JsonElement element)
        {
            try
            {
                var fullName = element.TryGetProperty("full_name", out var fullNameElement) ? fullNameElement.GetString() ?? "" : "";
                if (string.IsNullOrWhiteSpace(fullName)) return null;
                var slash = fullName.IndexOf('/');
                if (slash <= 0 || slash == fullName.Length - 1) return null;

                var name = element.TryGetProperty("name", out var nameElement) ? nameElement.GetString() ?? "" : fullName[(slash + 1)..];
                var owner = element.TryGetProperty("owner", out var ownerElement) && ownerElement.TryGetProperty("login", out var loginElement)
                    ? loginElement.GetString() ?? fullName[..slash]
                    : fullName[..slash];
                var htmlUrl = element.TryGetProperty("html_url", out var htmlElement) ? htmlElement.GetString() ?? "" : "";
                var branch = element.TryGetProperty("default_branch", out var branchElement) ? branchElement.GetString() ?? "main" : "main";
                var stars = element.TryGetProperty("stargazers_count", out var starsElement) && starsElement.TryGetInt32(out var parsedStars) ? parsedStars : 0;
                var updated = ParseDate(element, "pushed_at") ?? ParseDate(element, "updated_at") ?? DateTimeOffset.MinValue;

                return new RepoItem
                {
                    Owner = owner,
                    Name = name,
                    FullName = fullName,
                    HtmlUrl = htmlUrl,
                    DefaultBranch = string.IsNullOrWhiteSpace(branch) ? "main" : branch,
                    Stars = stars,
                    Updated = updated,
                    Description = element.TryGetProperty("description", out var descElement) ? descElement.GetString() ?? "" : ""
                };
            }
            catch
            {
                return null;
            }
        }

        private static DateTimeOffset? ParseDate(JsonElement element, string property)
        {
            if (!element.TryGetProperty(property, out var value) || value.ValueKind != JsonValueKind.String) return null;
            return DateTimeOffset.TryParse(value.GetString(), out var parsed) ? parsed : null;
        }
    }

    private sealed class StoreCache
    {
        [JsonPropertyName("savedAt")] public DateTimeOffset SavedAt { get; set; }
        [JsonPropertyName("searches")] public Dictionary<string, CachedSearch> Searches { get; set; } = new(StringComparer.Ordinal);
        [JsonPropertyName("manifests")] public Dictionary<string, CachedManifest> Manifests { get; set; } = new(StringComparer.Ordinal);
    }

    private sealed class CachedSearch
    {
        [JsonPropertyName("savedAt")] public DateTimeOffset SavedAt { get; set; }
        [JsonPropertyName("totalCount")] public int TotalCount { get; set; }
        [JsonPropertyName("repos")] public List<RepoItem> Repos { get; set; } = new();
    }

    private sealed class CachedManifest
    {
        [JsonPropertyName("fetchedAt")] public DateTimeOffset FetchedAt { get; set; }
        [JsonPropertyName("content")] public string? Content { get; set; }
    }
}
