using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using NovaLink.Lumen.Core;
using NovaLink.Lumen.Core.Models;
using NovaLink.Lumen.Services.Http;

namespace NovaLink.Lumen.Services.Updates;

public sealed record LauncherUpdate(bool Available, string Current, string Latest, string Notes, string DownloadUrl, long Size, string AssetName);

public sealed class UpdateService
{
    public const string CurrentVersion = "1.4.0";
    private const string LatestReleaseApi = "https://api.github.com/repos/Eric8844/novalink-lumen-releases/releases/latest";
    private readonly AppPaths _paths;
    private readonly HttpService _http;

    public UpdateService(AppPaths paths, HttpService http)
    {
        _paths = paths;
        _http = http;
    }

    public async Task<LauncherUpdate> CheckAsync(CancellationToken ct = default)
    {
        var json = await _http.GetStringAsync(LatestReleaseApi, ct);
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        var tag = root.TryGetProperty("tag_name", out var t) ? t.GetString() ?? "" : "";
        var notes = root.TryGetProperty("body", out var b) ? b.GetString() ?? "" : "";
        var version = ExtractVersion(tag) ?? CurrentVersion;
        var available = Compare(version, CurrentVersion) > 0;

        string url = "", name = "";
        long size = 0;
        if (root.TryGetProperty("assets", out var assets) && assets.ValueKind == JsonValueKind.Array)
        {
            var entries = assets.EnumerateArray().Select(a => new
            {
                Name = a.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "",
                Url = a.TryGetProperty("browser_download_url", out var u) ? u.GetString() ?? "" : "",
                Size = a.TryGetProperty("size", out var s) && s.TryGetInt64(out var v) ? v : 0
            }).ToList();
            // The Electron "NovaLinkLumen-Setup.exe" shares this repository and must never replace the
            // native launcher. Native installers are versioned, e.g. NovaLinkLumen-WinUI-Setup-1.4.0.exe.
            var asset = entries.FirstOrDefault(a =>
                a.Name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) &&
                (a.Name.Contains("WinUI", StringComparison.OrdinalIgnoreCase) ||
                 Regex.IsMatch(a.Name, @"-Setup-\d+\.\d+\.\d+", RegexOptions.IgnoreCase)));
            if (asset is not null && available)
            {
                url = asset.Url;
                name = asset.Name;
                size = asset.Size;
            }
        }

        return new LauncherUpdate(available && url.Length > 0, CurrentVersion, version, notes, url, size, name);
    }

    public async Task<string> DownloadAsync(LauncherUpdate update, IProgress<DownloadProgress>? progress, CancellationToken ct = default)
    {
        if (!update.Available || string.IsNullOrWhiteSpace(update.DownloadUrl))
            throw new InvalidOperationException("当前没有可用的启动器更新。");
        var dir = Path.Combine(_paths.Cache, "updates");
        Directory.CreateDirectory(dir);
        var target = Path.Combine(dir, string.IsNullOrWhiteSpace(update.AssetName) ? "NovaLinkLumen-WinUI-Setup-" + update.Latest + ".exe" : update.AssetName);
        if (File.Exists(target)) File.Delete(target);
        await _http.DownloadFileAsync(update.DownloadUrl, target, progress, "update", ct);
        return target;
    }

    public static void RunInstaller(string path)
    {
        Process.Start(new ProcessStartInfo
        {
            FileName = path,
            Arguments = "/S",
            UseShellExecute = true
        });
    }

    private static string? ExtractVersion(string tag)
    {
        var match = Regex.Match(tag ?? "", @"(\d+)\.(\d+)\.(\d+)");
        return match.Success ? $"{match.Groups[1].Value}.{match.Groups[2].Value}.{match.Groups[3].Value}" : null;
    }

    private static int Compare(string a, string b)
    {
        static int[] Parts(string v) => v.Split('.').Select(x => int.TryParse(x, out var n) ? n : 0).ToArray();
        var pa = Parts(a);
        var pb = Parts(b);
        for (var i = 0; i < Math.Max(pa.Length, pb.Length); i++)
        {
            var x = i < pa.Length ? pa[i] : 0;
            var y = i < pb.Length ? pb[i] : 0;
            if (x != y) return x.CompareTo(y);
        }
        return 0;
    }
}
