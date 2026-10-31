using System.Text.Json;
using NovaLink.Lumen.Core;
using NovaLink.Lumen.Core.Models;
using NovaLink.Lumen.Services.Http;

namespace NovaLink.Lumen.Services.News;

public sealed class NewsService
{
    private const string PatchNotesUrl = "https://launchercontent.mojang.com/v2/javaPatchNotes.json";
    private readonly AppPaths _paths;
    private readonly HttpService _http;
    private readonly string _cacheFile;
    private IReadOnlyList<NewsItem>? _cache;

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
                Str(entry, "readMoreLink")));
        }
        return list;
    }

    private static string Str(JsonElement e, string name)
        => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";

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
