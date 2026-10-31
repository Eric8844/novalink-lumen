using System.Globalization;
using System.Text.Json;
using NovaLink.Lumen.Core;
using NovaLink.Lumen.Core.Models;
using NovaLink.Lumen.Services.Http;

namespace NovaLink.Lumen.Services.Minecraft;

public sealed class VersionManifestService
{
    private const string ManifestUrl = "https://piston-meta.mojang.com/mc/game/version_manifest_v2.json";
    private readonly AppPaths _paths;
    private readonly HttpService _http;
    private readonly string _cacheFile;
    private IReadOnlyList<ManifestVersion>? _cache;
    private DateTime _cachedAt;

    public VersionManifestService(AppPaths paths, HttpService http)
    {
        _paths = paths;
        _http = http;
        _cacheFile = Path.Combine(paths.Cache, "version_manifest.json");
    }

    public async Task<IReadOnlyList<ManifestVersion>> GetAsync(bool force = false, CancellationToken ct = default)
    {
        if (!force && _cache is not null && DateTime.UtcNow - _cachedAt < TimeSpan.FromMinutes(15)) return _cache;
        if (!force)
        {
            var disk = ReadCache();
            if (disk is { Count: > 0 } && DateTime.UtcNow - _cachedAt < TimeSpan.FromHours(12))
            {
                _cache = disk;
                return disk;
            }
        }

        try
        {
            using var doc = JsonDocument.Parse(await _http.GetStringAsync(ManifestUrl, ct));
            if (!doc.RootElement.TryGetProperty("versions", out var versions) || versions.ValueKind != JsonValueKind.Array)
                throw new InvalidOperationException("版本清单格式异常");
            var list = new List<ManifestVersion>();
            foreach (var entry in versions.EnumerateArray())
            {
                var id = Str(entry, "id");
                var url = Str(entry, "url");
                if (string.IsNullOrWhiteSpace(id) || string.IsNullOrWhiteSpace(url)) continue;
                var type = Str(entry, "type");
                var sha = Str(entry, "sha1");
                var release = DateTimeOffset.TryParse(Str(entry, "releaseTime"), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var parsed)
                    ? parsed : DateTimeOffset.MinValue;
                list.Add(new ManifestVersion(id, type, url, sha, release));
            }
            if (list.Count > 0)
            {
                _cache = list;
                _cachedAt = DateTime.UtcNow;
                await WriteCacheAsync(list, ct);
                return list;
            }
        }
        catch (Exception ex)
        {
            AppLog.Warn("manifest", "获取版本清单失败：" + ex.Message);
        }

        var fallback = ReadCache();
        if (fallback is { Count: > 0 })
        {
            _cache = fallback;
            return fallback;
        }
        return [];
    }

    public async Task<Dictionary<string, long>> ReleaseTimesAsync(CancellationToken ct = default)
    {
        var list = await GetAsync(false, ct);
        var map = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in list)
            if (item.ReleaseTime > DateTimeOffset.UnixEpoch)
                map[item.Id] = item.ReleaseTime.ToUnixTimeMilliseconds();
        return map;
    }

    private IReadOnlyList<ManifestVersion>? ReadCache()
    {
        try
        {
            if (!File.Exists(_cacheFile)) return null;
            using var doc = JsonDocument.Parse(File.ReadAllText(_cacheFile));
            if (!doc.RootElement.TryGetProperty("versions", out var versions)) return null;
            var list = new List<ManifestVersion>();
            foreach (var entry in versions.EnumerateArray())
            {
                var id = Str(entry, "id");
                var url = Str(entry, "url");
                if (string.IsNullOrWhiteSpace(id) || string.IsNullOrWhiteSpace(url)) continue;
                var release = DateTimeOffset.TryParse(Str(entry, "releaseTime"), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var parsed)
                    ? parsed : DateTimeOffset.MinValue;
                list.Add(new ManifestVersion(id, Str(entry, "type"), url, Str(entry, "sha1"), release));
            }
            _cachedAt = new DateTimeOffset(File.GetLastWriteTimeUtc(_cacheFile), TimeSpan.Zero).UtcDateTime;
            return list;
        }
        catch { return null; }
    }

    private async Task WriteCacheAsync(IReadOnlyList<ManifestVersion> list, CancellationToken ct)
    {
        try
        {
            Directory.CreateDirectory(_paths.Cache);
            var payload = new
            {
                fetchedAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                versions = list.Select(v => new { id = v.Id, type = v.Type, url = v.Url, sha1 = v.Sha1, releaseTime = v.ReleaseTime.ToString("o") })
            };
            await File.WriteAllTextAsync(_cacheFile, JsonSerializer.Serialize(payload, JsonDefaults.Options), ct);
        }
        catch (Exception ex) { AppLog.Warn("manifest", "写入版本清单缓存失败：" + ex.Message); }
    }

    private static string Str(JsonElement e, string name)
        => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";
}
