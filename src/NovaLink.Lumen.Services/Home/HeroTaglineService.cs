using NovaLink.Lumen.Core;
using NovaLink.Lumen.Services.Http;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace NovaLink.Lumen.Services.Home;

/// <summary>
/// 为首页启动卡片生成短标语。旧版 JS 使用讯飞星火 Lite，这里沿用同一模型，
/// 并按版本缓存，避免每次进入首页都请求模型。
/// </summary>
public sealed class HeroTaglineService
{
    private const string Endpoint = "https://spark-api-open.xf-yun.com/v1/chat/completions";
    private const string CacheFile = "hero-taglines-v1.json";
    private const string OfficialUrl = "https://launchercontent.mojang.com/v2/javaPatchNotes.json";

    private static readonly string[] Fallbacks =
    {
        "把世界，折射成光。每一次启程，都有一束光在等你。",
        "林间小径的尽头，光正等着被点亮。",
        "让每一次启动，都成为一场安静的远行。"
    };

    private readonly AppPaths _paths;
    private readonly HttpService _http;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly Dictionary<string, string> _official = new(StringComparer.OrdinalIgnoreCase);
    private Dictionary<string, TaglineEntry>? _cache;

    public HeroTaglineService(AppPaths paths, HttpService http)
    {
        _paths = paths;
        _http = http;
    }

    // 开源版本不内置任何第三方密钥；需要使用星火标语时，自行设置 NOVALINK_SPARK_KEY。
    private static string SparkKey
        => Environment.GetEnvironmentVariable("NOVALINK_SPARK_KEY") ?? "";

    private string CachePath => Path.Combine(_paths.Cache, CacheFile);

    public IReadOnlyList<string> Cached(string version)
    {
        var key = Normalize(version);
        if (key.Length == 0) return Array.Empty<string>();
        var entry = LoadCache().GetValueOrDefault(key);
        return entry?.Lines is { Count: > 0 } lines ? lines : Array.Empty<string>();
    }

    public string Fallback(string version)
        => Fallbacks[unchecked((uint)StringComparer.OrdinalIgnoreCase.GetHashCode(version ?? "")) % Fallbacks.Length];

    public async Task<IReadOnlyList<string>> GetAsync(string version, CancellationToken ct = default)
    {
        var key = Normalize(version);
        if (key.Length == 0) return Fallbacks;

        var cached = Cached(key);
        if (cached.Count > 0) return cached;

        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            cached = Cached(key);
            if (cached.Count > 0) return cached;

            var official = await OfficialTextAsync(key, ct).ConfigureAwait(false);
            var lines = await GenerateAsync(key, official, ct).ConfigureAwait(false);
            if (lines.Count == 0)
            {
                AppLog.Info("hero", "标语生成未返回可用文本，使用本地备用文案：" + key);
                return Fallbacks;
            }

            SaveCache(key, lines);
            AppLog.Info("hero", "标语生成完成：" + key + " / " + string.Join(" | ", lines));
            return lines;
        }
        catch (OperationCanceledException)
        {
            return Fallbacks;
        }
        catch (Exception ex)
        {
            AppLog.Error("hero", "标语生成失败：" + key, ex);
            return Fallbacks;
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<string> OfficialTextAsync(string version, CancellationToken ct)
    {
        lock (_official)
            if (_official.TryGetValue(version, out var known)) return known;

        try
        {
            var raw = await _http.GetStringAsync(OfficialUrl, ct).ConfigureAwait(false);
            using var doc = JsonDocument.Parse(raw);
            if (!doc.RootElement.TryGetProperty("entries", out var entries) || entries.ValueKind != JsonValueKind.Array)
                return "";

            JsonElement? exact = null;
            JsonElement? prefix = null;
            foreach (var entry in entries.EnumerateArray())
            {
                var v = entry.TryGetProperty("version", out var vv) ? vv.GetString() ?? "" : "";
                if (string.Equals(v, version, StringComparison.OrdinalIgnoreCase)) { exact = entry; break; }
                if (prefix is null && v.StartsWith(version + "-", StringComparison.OrdinalIgnoreCase)) prefix = entry;
            }

            var picked = exact ?? prefix;
            if (picked is null) return "";
            var text = picked.Value.TryGetProperty("shortText", out var st) ? st.GetString() ?? "" : "";
            text = WebUtility.HtmlDecode(text);
            text = Regex.Replace(text, "<[^>]+>", " ");
            text = Regex.Replace(text, @"\s+", " ").Trim();
            if (text.Length > 700) text = text[..700];
            lock (_official) _official[version] = text;
            return text;
        }
        catch (Exception ex)
        {
            AppLog.Error("hero", "读取版本简介失败：" + version, ex);
            return "";
        }
    }

    private async Task<IReadOnlyList<string>> GenerateAsync(string version, string official, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(SparkKey)) return Array.Empty<string>();

        var system = "你是 NovaLink Lumen（Minecraft Java 启动器）的中文文案编辑。"
                   + "根据版本简介写三行中文短标语，每行 14-26 个汉字，必须是完整的一句话，不要用四字词语拼贴。"
                   + "语气安静、有光感，像一次旅程的开场；不要编号、引号、emoji、英文、标题、解释，不要复述原文。"
                   + "示例（不要照抄）：\n风从林间穿过，世界正等你启程。\n把夜色收进口袋，下一次旅程会更亮。\n光落在方块上，冒险从这一刻开始。"
                   + "严格输出三行，每行一句，用换行分隔。";
        var user = "版本：" + version + "。"
                 + (string.IsNullOrWhiteSpace(official) ? "没有官方简介，请写这个版本给人的启程感。" : "官方简介：" + official)
                 + " 不要出现版本号、英文或“官方文案”等字样。";

        var payload = new
        {
            model = "lite",
            temperature = 0.85,
            max_tokens = 260,
            messages = new object[]
            {
                new { role = "system", content = system },
                new { role = "user", content = user }
            }
        };

        using var request = new HttpRequestMessage(HttpMethod.Post, Endpoint);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", SparkKey);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        request.Content = new StringContent(JsonSerializer.Serialize(payload, JsonDefaults.Options), Encoding.UTF8, "application/json");

        using var res = await _http.Client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        var body = await res.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        if (!res.IsSuccessStatusCode)
        {
            AppLog.Error("hero", "星火 Lite 返回 HTTP " + (int)res.StatusCode + "：" + body[..Math.Min(240, body.Length)]);
            return Array.Empty<string>();
        }

        using var doc = JsonDocument.Parse(body);
        if (!doc.RootElement.TryGetProperty("choices", out var choices) || choices.ValueKind != JsonValueKind.Array || choices.GetArrayLength() == 0)
            return Array.Empty<string>();
        var message = choices[0].TryGetProperty("message", out var m) ? m : default;
        if (message.ValueKind != JsonValueKind.Object || !message.TryGetProperty("content", out var contentEl))
            return Array.Empty<string>();

        return CleanLines(contentEl.GetString() ?? "");
    }

    private static IReadOnlyList<string> CleanLines(string raw)
    {
        var result = new List<string>();
        var text = (raw ?? "").Replace("\r", "").Trim();
        if (text.Length == 0) return result;

        foreach (var rawLine in text.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var line = CleanLine(rawLine);
            if (line.Length == 0) continue;
            if (line.Contains(' ') && line.Length > 34)
            {
                var chunks = line.Split(' ', StringSplitOptions.RemoveEmptyEntries)
                                 .Select(CleanLine)
                                 .Where(x => x.Length > 0)
                                 .ToArray();
                if (chunks.Length >= 2)
                {
                    result.AddRange(chunks);
                    continue;
                }
            }
            result.Add(line);
        }

        if (result.Count == 0)
        {
            var sentences = Regex.Split(text, @"[。！？；\n]+")
                                 .Select(CleanLine)
                                 .Where(x => x.Length > 0);
            result.AddRange(sentences);
        }

        return result
            .Where(x => Regex.Matches(x, @"[\u4e00-\u9fff]").Count >= 6)
            .Distinct()
            .Take(3)
            .ToArray();
    }

    private static string CleanLine(string raw)
    {
        var line = (raw ?? "").Trim().Trim('*', '#', '\x60', '"', '\'', '“', '”', '‘', '’', '「', '」', '『', '』', '《', '》', ' ', '-', '•', '·');
        line = Regex.Replace(line, @"^\s*\d+\s*[\.、\)）:：]\s*", "");
        line = Regex.Replace(line, @"^(标语|文案|输出)\s*[:：]\s*", "");
        line = line.Replace("**", "").Replace("__", "").Trim();
        line = Regex.Replace(line, @"\p{Cs}", "").Trim();
        if (line.Length == 0) return "";
        if (Regex.IsMatch(line, @"[A-Za-z]{3,}")) return "";
        if (line.Contains("抱歉") || line.Contains("无法") || line.Contains("官方文案") || line.Contains("版本号")) return "";
        if (line.Length > 50) line = line[..46].TrimEnd('，', '、', '；', ' ') + "。";
        return line;
    }

    private Dictionary<string, TaglineEntry> LoadCache()
    {
        lock (_official)
        {
            if (_cache is not null) return _cache;
            try
            {
                if (File.Exists(CachePath))
                {
                    var json = File.ReadAllText(CachePath, Encoding.UTF8);
                    _cache = JsonSerializer.Deserialize<Dictionary<string, TaglineEntry>>(json, JsonDefaults.Options)
                             ?? new Dictionary<string, TaglineEntry>(StringComparer.OrdinalIgnoreCase);
                }
                else
                {
                    _cache = new Dictionary<string, TaglineEntry>(StringComparer.OrdinalIgnoreCase);
                }
            }
            catch
            {
                _cache = new Dictionary<string, TaglineEntry>(StringComparer.OrdinalIgnoreCase);
            }
            return _cache;
        }
    }

    private void SaveCache(string version, IReadOnlyList<string> lines)
    {
        var cache = LoadCache();
        cache[version] = new TaglineEntry { Lines = lines.ToList(), UpdatedAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds() };
        try
        {
            Directory.CreateDirectory(_paths.Cache);
            File.WriteAllText(CachePath, JsonSerializer.Serialize(cache, JsonDefaults.Options), new UTF8Encoding(false));
        }
        catch (Exception ex)
        {
            AppLog.Error("hero", "写入标语缓存失败", ex);
        }
    }

    private static string Normalize(string version)
        => (version ?? "").Trim().ToLowerInvariant();

    private sealed class TaglineEntry
    {
        public List<string> Lines { get; set; } = new();
        public long UpdatedAt { get; set; }
    }
}
