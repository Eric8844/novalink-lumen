using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;
using NovaLink.Lumen.Core;
using NovaLink.Lumen.Core.Models;
using NovaLink.Lumen.Services.Http;

namespace NovaLink.Lumen.Services.News;

public sealed class NewsService
{
    private const string PatchNotesUrl = "https://launchercontent.mojang.com/v2/javaPatchNotes.json";
    private const string ArticleBaseUrl = "https://launchercontent.mojang.com/v2/";
    private readonly AppPaths _paths;
    private readonly HttpService _http;
    private readonly string _cacheFile;
    private IReadOnlyList<NewsItem>? _cache;
    // 正文按条目 id 在内存里缓存：同一条快讯重复打开不再走网络。
    private readonly Dictionary<string, string> _articleCache = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _articleLock = new();

    public NewsService(AppPaths paths, HttpService http)
    {
        _paths = paths;
        _http = http;
        _cacheFile = Path.Combine(paths.Cache, "news.json");
    }

    public async Task<IReadOnlyList<NewsItem>> GetAsync(bool force = false, CancellationToken ct = default)
    {
        if (!force && _cache is not null) return _cache;
        IReadOnlyList<NewsItem>? cached = null;
        if (!force && TryReadCache(out cached, out var age) && age < TimeSpan.FromMinutes(15))
        {
            _cache = cached;
            return cached;
        }

        try
        {
            var json = await _http.GetStringAsync(PatchNotesUrl, ct);
            using var doc = JsonDocument.Parse(json);
            var items = Parse(doc.RootElement);
            if (items.Count > 0)
            {
                _cache = items;
                await WriteCacheAsync(items, ct);
                return items;
            }
        }
        catch (Exception ex)
        {
            AppLog.Warn("news", "获取 Minecraft 快讯失败：" + ex.Message);
        }

        if (cached is { Count: > 0 }) { _cache = cached; return cached; }
        _cache = Fallback();
        return _cache;
    }

    private static List<NewsItem> Parse(JsonElement root)
    {
        var list = new List<NewsItem>();
        if (!root.TryGetProperty("entries", out var entries) || entries.ValueKind != JsonValueKind.Array)
            return list;
        foreach (var entry in entries.EnumerateArray())
        {
            var title = Str(entry, "title");
            if (string.IsNullOrWhiteSpace(title)) continue;
            if (!entry.TryGetProperty("image", out var image) || image.ValueKind != JsonValueKind.Object) continue;
            var url = Str(image, "url");
            if (string.IsNullOrWhiteSpace(url)) continue;
            if (!url.StartsWith("http", StringComparison.OrdinalIgnoreCase))
                url = new Uri(new Uri("https://launchercontent.mojang.com"), url).ToString();
            var type = Str(entry, "type");
            var tag = type switch { "snapshot" => "Snapshot", "release" => "Release", _ => "Java Edition" };
            var version = Str(entry, "version");
            list.Add(new NewsItem(
                Str(entry, "id") is { Length: > 0 } id ? id : (version is { Length: > 0 } ? version : title),
                title,
                version,
                type == "snapshot" ? "Java Edition Snapshot" : "Minecraft: Java Edition",
                Str(entry, "date"),
                url,
                Str(entry, "shortText"),
                Str(entry, "readMoreLink"),
                Str(entry, "contentPath")));
        }
        return list;
    }

    private static string Str(JsonElement e, string name)
        => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";

    /// <summary>读取单条快讯的正文（官方 contentPath 指向的 JSON 里的 body），转成可读纯文本并缓存。失败返回 null，不抛异常。</summary>
    public async Task<string?> GetArticleTextAsync(NewsItem item, CancellationToken ct = default)
    {
        if (item is null || string.IsNullOrWhiteSpace(item.Id)) return null;
        // 没有正文路径（例如本地兜底的示例条目）就没有正文可取，先判空再查缓存。
        if (string.IsNullOrWhiteSpace(item.ContentPath)) return null;
        lock (_articleLock)
        {
            if (_articleCache.TryGetValue(item.Id, out var cached)) return cached;
        }

        var path = item.ContentPath.Trim();
        var url = path.StartsWith("http", StringComparison.OrdinalIgnoreCase)
            ? path
            : ArticleBaseUrl + path.TrimStart('/');

        try
        {
            var json = await _http.GetStringAsync(url, ct);
            using var doc = JsonDocument.Parse(json);
            var body = doc.RootElement.TryGetProperty("body", out var node) && node.ValueKind == JsonValueKind.String
                ? node.GetString()
                : null;
            var text = ToPlainText(body ?? "");
            if (string.IsNullOrWhiteSpace(text)) return null;
            lock (_articleLock) { _articleCache[item.Id] = text; }
            return text;
        }
        catch (OperationCanceledException)
        {
            return null;
        }
        catch (Exception ex)
        {
            AppLog.Warn("news", "读取快讯正文失败（" + item.Id + "）：" + ex.Message);
            return null;
        }
    }

    /// <summary>把更新日志正文的 HTML 转成纯文本：去标签、列表加「· 」、标题前后留空行、解码实体、压缩多余空行。</summary>
    private static string ToPlainText(string html)
    {
        if (string.IsNullOrWhiteSpace(html)) return "";
        var text = html;
        text = Regex.Replace(text, @"<!--.*?-->", " ", RegexOptions.Singleline);
        text = Regex.Replace(text, @"<(script|style)[^>]*>.*?</\1>", " ", RegexOptions.IgnoreCase | RegexOptions.Singleline);
        text = Regex.Replace(text, @"<h[1-6][^>]*>", "\n\n", RegexOptions.IgnoreCase);
        text = Regex.Replace(text, @"</h[1-6]>", "\n\n", RegexOptions.IgnoreCase);
        text = Regex.Replace(text, @"<li[^>]*>", "\n· ", RegexOptions.IgnoreCase);
        text = Regex.Replace(text, @"</li>", "", RegexOptions.IgnoreCase);
        text = Regex.Replace(text, @"<(br|hr)\s*/?>", "\n", RegexOptions.IgnoreCase);
        text = Regex.Replace(text, @"</?(p|div|ul|ol|tr|table|section|article)[^>]*>", "\n\n", RegexOptions.IgnoreCase);
        text = Regex.Replace(text, @"</?t[dh][^>]*>", "  ", RegexOptions.IgnoreCase);
        text = Regex.Replace(text, @"<[^>]+>", "", RegexOptions.Singleline);
        text = WebUtility.HtmlDecode(text);
        text = text.Replace("\r\n", "\n").Replace('\r', '\n').Replace('\u00A0', ' ');
        text = Regex.Replace(text, @"[ \t\f\v]+", " ");
        text = Regex.Replace(text, @" *\n *", "\n");
        text = Regex.Replace(text, @"\n{3,}", "\n\n");
        return text.Trim();
    }

    private bool TryReadCache(out IReadOnlyList<NewsItem> items, out TimeSpan age)
    {
        items = Array.Empty<NewsItem>();
        age = TimeSpan.MaxValue;
        try
        {
            if (!File.Exists(_cacheFile)) return false;
            var dto = JsonSerializer.Deserialize<NewsCache>(File.ReadAllText(_cacheFile));
            if (dto?.Items is null || dto.Items.Count == 0) return false;
            items = dto.Items;
            age = DateTimeOffset.UtcNow - DateTimeOffset.FromUnixTimeMilliseconds(dto.CachedAt);
            return true;
        }
        catch { return false; }
    }

    private async Task WriteCacheAsync(IReadOnlyList<NewsItem> items, CancellationToken ct)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_cacheFile)!);
            var dto = new NewsCache { CachedAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), Items = items.ToList() };
            await File.WriteAllTextAsync(_cacheFile, JsonSerializer.Serialize(dto, JsonDefaults.Options), ct);
        }
        catch (Exception ex) { AppLog.Warn("news", "写入快讯缓存失败：" + ex.Message); }
    }

    private static IReadOnlyList<NewsItem> Fallback() =>
    [
        new("wilderness-bound", "Wilderness Bound 正式发布", "26.3", "Minecraft: Java Edition", "2026-09-15",
            "ms-appx:///Assets/Images/news-herdcraft.jpg", "新的荒野维度与生存整合包现已开放。", "https://www.minecraft.net/"),
        new("snapshot-frost", "26.4 快照 5：冰霜回响", "26.4", "Java Edition Snapshot", "2026-09-24",
            "ms-appx:///Assets/Images/snapshot-frost.jpg", "本次快照调整了寒冷群系的生成与音效。", "https://www.minecraft.net/"),
        new("realms-september", "Java Realms：九月地图精选", "Realms", "Minecraft: Java Edition", "2026-09-28",
            "ms-appx:///Assets/Images/news-realms.jpg", "本月由社区挑选的 Realms 地图与冒险。", "https://www.minecraft.net/"),
        new("serif", "Lumen 光影计划", "1.3.0", "NovaLink", "2026-10-01",
            "ms-appx:///Assets/Images/news-serif.jpg", "液态玻璃界面与真实折射渲染即将完成。", "https://github.com/Eric8844/novalink-lumen-releases")
    ];

    private sealed class NewsCache
    {
        public long CachedAt { get; set; }
        public List<NewsItem> Items { get; set; } = [];
    }
}
