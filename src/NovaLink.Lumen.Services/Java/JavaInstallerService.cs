using System;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using NovaLink.Lumen.Core;
using NovaLink.Lumen.Core.Models;
using NovaLink.Lumen.Services.Http;
using NovaLink.Lumen.Services.Storage;

namespace NovaLink.Lumen.Services.Java;

public sealed class JavaInstallerService
{
    private readonly AppPaths _paths;
    private readonly SettingsService _settings;
    private readonly JavaService _java;
    private readonly HttpService _http;

    public JavaInstallerService(AppPaths paths, SettingsService settings, JavaService java, HttpService http)
    {
        _paths = paths;
        _settings = settings;
        _java = java;
        _http = http;
    }

    public async Task<JavaRuntime?> EnsureAsync(int requiredMajor, IProgress<DownloadProgress>? progress, CancellationToken ct = default)
    {
        var runtimes = await _java.DetectAsync(false, ct);
        // Old Minecraft/Forge requires Java 8 exactly. A newer runtime is not a substitute,
        // otherwise we would keep "finding" Java 25 and never download Java 8.
        var existing = runtimes.FirstOrDefault(x => x.Major == requiredMajor);
        if (existing is null && requiredMajor > 16)
            existing = runtimes.Where(x => x.Major > requiredMajor).OrderBy(x => x.Major).FirstOrDefault();
        if (existing is not null) return existing;

        progress?.Report(new DownloadProgress("准备 Java", 1, 0, 0, 0, "正在读取 Adoptium 最新版本…"));
        var api = $"https://api.adoptium.net/v3/assets/latest/{requiredMajor}/hotspot?architecture=x64&image_type=jdk&os=windows&vendor=eclipse";
        var text = await _http.GetStringAsync(api, ct);
        using var doc = JsonDocument.Parse(text);
        if (doc.RootElement.ValueKind != JsonValueKind.Array || doc.RootElement.GetArrayLength() == 0)
            throw new InvalidOperationException("没有找到适配当前系统的 Java " + requiredMajor + " 安装包。");
        var entry = doc.RootElement[0];
        var release = entry.TryGetProperty("release_name", out var rn) ? rn.GetString() ?? ("jdk-" + requiredMajor) : ("jdk-" + requiredMajor);
        var package = entry.GetProperty("binary").GetProperty("package");
        var link = package.GetProperty("link").GetString() ?? throw new InvalidOperationException("Java 下载地址为空。");
        var name = package.TryGetProperty("name", out var n) ? n.GetString() ?? "java.zip" : "java.zip";

        Directory.CreateDirectory(_paths.JavaDir);
        var archive = Path.Combine(_paths.Cache, name);
        progress?.Report(new DownloadProgress("下载 Java", 2, 0, 0, 0, release));
        await _http.DownloadFileAsync(link, archive, new Progress<DownloadProgress>(p =>
        {
            progress?.Report(new DownloadProgress("下载 Java " + requiredMajor, 2 + p.Percent * 0.82, p.Received, p.Total, p.BytesPerSecond, p.Message));
        }), "下载 Java", ct);

        progress?.Report(new DownloadProgress("解压 Java", 86, 0, 0, 0, release));
        var target = Path.Combine(_paths.JavaDir, SafeName(release));
        if (Directory.Exists(target)) Directory.Delete(target, true);
        Directory.CreateDirectory(target);
        ZipFile.ExtractToDirectory(archive, target, true);

        progress?.Report(new DownloadProgress("校验 Java", 95, 0, 0, 0, "正在查找 java.exe"));
        var javaExe = Directory.EnumerateFiles(target, "java.exe", SearchOption.AllDirectories)
            .FirstOrDefault(path => string.Equals(Path.GetFileName(Path.GetDirectoryName(path)), "bin", StringComparison.OrdinalIgnoreCase));
        if (javaExe is null) throw new InvalidOperationException("Java 解压完成但未找到 bin\\java.exe。");

        _settings.Settings.JavaPath = javaExe;
        _settings.Save();
        var runtime = new JavaRuntime(javaExe, requiredMajor, release, "Eclipse Temurin");
        _java.Remember(runtime);
        try { File.Delete(archive); } catch { }
        progress?.Report(new DownloadProgress("完成", 100, 0, 0, 0, "Java " + requiredMajor + " 已准备就绪"));
        return runtime;
    }

    private static string SafeName(string value)
    {
        var invalid = Path.GetInvalidFileNameChars();
        return new string(value.Select(c => invalid.Contains(c) ? '_' : c).ToArray());
    }
}
