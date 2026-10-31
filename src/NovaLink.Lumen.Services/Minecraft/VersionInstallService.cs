using System.Security.Cryptography;
using System.Text.Json;
using NovaLink.Lumen.Core;
using NovaLink.Lumen.Core.Models;
using NovaLink.Lumen.Services.Http;
using NovaLink.Lumen.Services.Storage;

namespace NovaLink.Lumen.Services.Minecraft;

public sealed class VersionInstallService
{
    private readonly AppPaths _paths;
    private readonly SettingsService _settings;
    private readonly HttpService _http;
    private readonly VersionManifestService _manifest;
    private readonly InstanceService _instances;
    private readonly LaunchService _launch;

    public VersionInstallService(AppPaths paths, SettingsService settings, HttpService http,
        VersionManifestService manifest, InstanceService instances, LaunchService launch)
    {
        _paths = paths;
        _settings = settings;
        _http = http;
        _manifest = manifest;
        _instances = instances;
        _launch = launch;
    }

    public async Task<InstanceSummary> InstallVanillaAsync(string versionId, string? displayName, bool isolated,
        IProgress<DownloadProgress>? progress = null, CancellationToken ct = default)
    {
        var manifest = await _manifest.GetAsync(false, ct);
        var entry = manifest.FirstOrDefault(v => string.Equals(v.Id, versionId, StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidOperationException("版本清单中找不到 " + versionId);
        progress?.Report(new DownloadProgress("manifest", 1, 0, 0, 0, "正在获取版本配置"));

        var root = _settings.Settings.GameRoot;
        var dir = Path.Combine(root, "versions", entry.Id);
        Directory.CreateDirectory(dir);
        var jsonPath = Path.Combine(dir, entry.Id + ".json");
        if (!File.Exists(jsonPath))
        {
            var text = await _http.GetStringAsync(entry.Url, ct);
            if (!string.IsNullOrWhiteSpace(entry.Sha1) && !Sha1Matches(text, entry.Sha1))
                throw new InvalidOperationException("版本配置校验失败，请重试");
            await File.WriteAllTextAsync(jsonPath, text, ct);
        }

        var meta = _instances.GetOrCreateMeta(entry.Id);
        if (!string.IsNullOrWhiteSpace(displayName)) meta.Name = displayName.Trim();
        meta.Isolated = isolated;
        if (isolated)
        {
            var gameDir = Path.Combine(root, "instances", entry.Id);
            Directory.CreateDirectory(gameDir);
            meta.GameDir = gameDir;
        }
        _settings.Save();

        progress?.Report(new DownloadProgress("download", 8, 0, 0, 0, "正在补全客户端与库文件"));
        await _launch.EnsureInstallAsync(entry.Id, progress, ct);
        progress?.Report(new DownloadProgress("done", 100, 0, 0, 0, "安装完成"));
        var list = await _instances.ScanAsync(ct);
        var found = _instances.Find(list, entry.Id);
        if (found is not null) return found;

        // Vanilla versions used as a loader kernel are hidden from the instance list.
        // Keep the install successful and return a synthetic summary for the dialog.
        return new InstanceSummary(
            entry.Id,
            string.IsNullOrWhiteSpace(meta.Name) ? entry.Id : meta.Name!,
            entry.Id,
            "Vanilla",
            "",
            entry.Id,
            string.IsNullOrWhiteSpace(meta.GameDir) ? root : meta.GameDir!,
            string.IsNullOrWhiteSpace(meta.Icon) ? "loader:Vanilla" : meta.Icon!,
            0,
            0,
            meta.LastPlayed,
            isolated);
    }

    private static bool Sha1Matches(string text, string expected)
    {
        try
        {
            var hash = SHA1.HashData(System.Text.Encoding.UTF8.GetBytes(text));
            return Convert.ToHexString(hash).Equals(expected, StringComparison.OrdinalIgnoreCase);
        }
        catch { return true; }
    }
}
