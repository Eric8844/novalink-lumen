using NovaLink.Lumen.Core;
using NovaLink.Lumen.Core.Models;
using NovaLink.Lumen.Services.Http;
using NovaLink.Lumen.Services.News;

namespace NovaLink.Lumen.Services.Home;

/// <summary>
/// Picks the launch-card artwork for the selected instance. Prefer the official Mojang
/// patch-note image for that exact game version, cache it locally, and fall back to a
/// stable local image so every version still gets its own backdrop when offline.
/// </summary>
public sealed class HeroArtService
{
    private readonly HttpService _http;
    private readonly NewsService _news;
    private readonly string _directory;

    private static readonly string[] LocalArt =
    [
        "ms-appx:///Assets/Images/hero-wilderness.jpg",
        "ms-appx:///Assets/Images/news-herdcraft.jpg",
        "ms-appx:///Assets/Images/snapshot-frost.jpg",
        "ms-appx:///Assets/Images/news-realms.jpg",
        "ms-appx:///Assets/Images/news-serif.jpg"
    ];

    public HeroArtService(AppPaths paths, HttpService http, NewsService news)
    {
        _http = http;
        _news = news;
        _directory = Path.Combine(paths.Cache, "hero-art");
    }

    public async Task<string> GetAsync(string kernel, string instanceId, string loader, CancellationToken ct = default)
    {
        var key = !string.IsNullOrWhiteSpace(kernel) ? kernel : instanceId;
        try
        {
            var items = await _news.GetAsync(false, ct);
            var item = Find(items, kernel, instanceId);
            if (item is not null)
            {
                var cached = CachePath(item);
                if (File.Exists(cached) && new FileInfo(cached).Length > 1024) return cached;

                try
                {
                    var bytes = await _http.GetBytesAsync(Absolute(item.ImageUrl), ct);
                    if (bytes.Length > 1024)
                    {
                        Directory.CreateDirectory(_directory);
                        await File.WriteAllBytesAsync(cached, bytes, ct);
                        return cached;
                    }
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex)
                {
                    AppLog.Warn("hero", "启动卡片背景下载失败，使用远程地址：" + ex.Message);
                }
                return Absolute(item.ImageUrl);
            }
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            AppLog.Warn("hero", "读取启动卡片背景失败：" + ex.Message);
        }

        return Fallback(key, instanceId, loader);
    }

    private static NewsItem? Find(IReadOnlyList<NewsItem> items, string kernel, string instanceId)
    {
        if (items.Count == 0) return null;

        var exact = items.FirstOrDefault(x => Same(x.Version, instanceId) || Same(x.Id, instanceId));
        if (exact is not null) return exact;
        exact = items.FirstOrDefault(x => Same(x.Version, kernel));
        if (exact is not null) return exact;

        // A custom instance such as 26.2-Fabric生存 usually has kernel 26.2; the exact
        // version above already covers it. This branch keeps snapshot ids useful too.
        if (!string.IsNullOrWhiteSpace(kernel))
        {
            exact = items.FirstOrDefault(x => x.Version.StartsWith(kernel + "-", StringComparison.OrdinalIgnoreCase));
            if (exact is not null) return exact;
        }
        return null;
    }

    private static bool Same(string a, string b)
        => !string.IsNullOrWhiteSpace(a) && !string.IsNullOrWhiteSpace(b) &&
           string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

    private string CachePath(NewsItem item)
    {
        var name = Safe(item.Version is { Length: > 0 } v ? v : item.Id);
        return Path.Combine(_directory, name + "-" + StableHash(item.ImageUrl).ToString("x8") + ".jpg");
    }

    private static string Safe(string value)
    {
        var chars = value.Select(ch => char.IsLetterOrDigit(ch) || ch is '.' or '-' or '_' ? ch : '_').ToArray();
        var text = new string(chars).Trim('_');
        return string.IsNullOrWhiteSpace(text) ? "hero" : text[..Math.Min(48, text.Length)];
    }

    private static string Absolute(string url)
        => url.StartsWith("http", StringComparison.OrdinalIgnoreCase)
            ? url
            : new Uri(new Uri("https://launchercontent.mojang.com"), url).ToString();

    private static string Fallback(string key, string instanceId, string loader)
    {
        var seed = StableHash(key + "|" + instanceId + "|" + loader);
        return LocalArt[(int)(seed % (uint)LocalArt.Length)];
    }

    private static uint StableHash(string value)
    {
        unchecked
        {
            var hash = 2166136261u;
            foreach (var ch in value)
            {
                hash ^= ch;
                hash *= 16777619u;
            }
            return hash;
        }
    }
}
