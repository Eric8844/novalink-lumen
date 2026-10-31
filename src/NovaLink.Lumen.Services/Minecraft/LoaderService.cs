using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Diagnostics;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using NovaLink.Lumen.Core;
using NovaLink.Lumen.Core.Models;
using NovaLink.Lumen.Services.Http;
using NovaLink.Lumen.Services.Java;
using NovaLink.Lumen.Services.Storage;

namespace NovaLink.Lumen.Services.Minecraft;

public sealed class LoaderService
{
    private readonly AppPaths _paths;
    private readonly SettingsService _settings;
    private readonly HttpService _http;
    private readonly VersionManifestService _manifest;
    private readonly InstanceService _instances;
    private readonly LaunchService _launch;
    private readonly JavaService _java;

    public LoaderService(AppPaths paths, SettingsService settings, HttpService http,
        VersionManifestService manifest, InstanceService instances, LaunchService launch, JavaService java)
    {
        _paths = paths;
        _settings = settings;
        _http = http;
        _manifest = manifest;
        _instances = instances;
        _launch = launch;
        _java = java;
    }

    public async Task<IReadOnlyList<string>> VersionsAsync(string loader, string minecraft, CancellationToken ct = default)
    {
        var urls = loader switch
        {
            "Fabric" => new[]
            {
                "https://bmclapi2.bangbang93.com/fabric-meta/v2/versions/loader/" + Uri.EscapeDataString(minecraft),
                "https://meta.fabricmc.net/v2/versions/loader/" + Uri.EscapeDataString(minecraft)
            },
            "Quilt" => new[]
            {
                "https://meta.quiltmc.org/v3/versions/loader/" + Uri.EscapeDataString(minecraft)
            },
            "Forge" => new[]
            {
                "https://bmclapi2.bangbang93.com/forge/minecraft/" + Uri.EscapeDataString(minecraft)
            },
            "NeoForge" => new[]
            {
                "https://bmclapi2.bangbang93.com/neoforge/list/" + Uri.EscapeDataString(minecraft)
            },
            _ => Array.Empty<string>()
        };
        if (urls.Length == 0) return Array.Empty<string>();
        Exception? last = null;
        foreach (var url in urls)
        {
            try
            {
                var text = await _http.GetStringAsync(url, ct);
                var node = JsonNode.Parse(text);
                if (node is not JsonArray array) continue;
                if (loader is "Forge" or "NeoForge")
                {
                    return array.Select(item => item?["version"]?.GetValue<string>())
                        .Where(v => !string.IsNullOrWhiteSpace(v)).Select(v => v!).Distinct()
                        .OrderByDescending(v => v, StringComparer.OrdinalIgnoreCase).ToList();
                }
                return array.Select(item => item?["loader"]?["version"]?.GetValue<string>())
                    .Where(v => !string.IsNullOrWhiteSpace(v)).Select(v => v!).Distinct().ToList();
            }
            catch (Exception ex) { last = ex; }
        }
        throw new InvalidOperationException("无法读取 " + loader + " 加载器版本：" + (last?.Message ?? "网络错误"));
    }

    public async Task<InstanceSummary> InstallAsync(string loader, string minecraft, string? loaderVersion,
        string? displayName, bool isolated, IProgress<DownloadProgress>? progress, CancellationToken ct = default)
    {
        if (loader is "Forge" or "NeoForge")
            return await InstallForgeLikeAsync(loader, minecraft, loaderVersion, displayName, isolated, progress, ct);
        if (loader is not ("Fabric" or "Quilt"))
            throw new InvalidOperationException(loader + " 的自动安装尚未接入当前版本");
        progress?.Report(new DownloadProgress("loader", 4, 0, 0, 0, "正在解析 " + loader + " 版本"));
        await EnsureVanillaAsync(minecraft, progress, ct);
        var versions = await VersionsAsync(loader, minecraft, ct);
        var version = string.IsNullOrWhiteSpace(loaderVersion) ? versions.FirstOrDefault() : loaderVersion;
        if (string.IsNullOrWhiteSpace(version)) throw new InvalidOperationException("没有可用的 " + loader + " 版本");
        progress?.Report(new DownloadProgress("loader", 22, 0, 0, 0, "正在生成 " + loader + " " + version + " 配置"));

        var profile = await GetProfileAsync(loader, minecraft, version, ct);
        var id = loader.ToLowerInvariant() + "-loader-" + version + "-" + minecraft;
        profile["id"] = id;
        profile["inheritsFrom"] = minecraft;
        var root = _settings.Settings.GameRoot;
        var dir = Path.Combine(root, "versions", id);
        Directory.CreateDirectory(dir);
        var jsonPath = Path.Combine(dir, id + ".json");
        await File.WriteAllTextAsync(jsonPath, profile.ToJsonString(), ct);

        var meta = _instances.GetOrCreateMeta(id);
        meta.Name = string.IsNullOrWhiteSpace(displayName) ? loader + " " + version : displayName.Trim();
        ApplyIsolation(meta, id, isolated);
        _settings.Save();

        progress?.Report(new DownloadProgress("libraries", 46, 0, 0, 0, "正在下载加载器库文件"));
        await _launch.EnsureInstallAsync(id, progress, ct);
        progress?.Report(new DownloadProgress("done", 100, 0, 0, 0, "安装完成"));
        var list = await _instances.ScanAsync(ct);
        return _instances.Find(list, id) ?? throw new InvalidOperationException("加载器已安装，但未能读取实例");
    }

    private async Task<InstanceSummary> InstallForgeLikeAsync(string loader, string minecraft, string? loaderVersion,
        string? displayName, bool isolated, IProgress<DownloadProgress>? progress, CancellationToken ct)
    {
        progress?.Report(new DownloadProgress("loader", 3, 0, 0, 0, "正在解析 " + loader + " 版本"));
        await EnsureVanillaAsync(minecraft, progress, ct);
        var versions = await VersionsAsync(loader, minecraft, ct);
        var version = string.IsNullOrWhiteSpace(loaderVersion) ? versions.FirstOrDefault() : loaderVersion;
        if (string.IsNullOrWhiteSpace(version)) throw new InvalidOperationException("没有可用的 " + loader + " 版本");

        var root = _settings.Settings.GameRoot;
        var launcherProfiles = Path.Combine(root, "launcher_profiles.json");
        if (!File.Exists(launcherProfiles)) await File.WriteAllTextAsync(launcherProfiles, "{\"profiles\":{},\"settings\":{},\"version\":3}", ct);

        var urls = loader == "Forge"
            ? new[]
            {
                "https://bmclapi2.bangbang93.com/maven/net/minecraftforge/forge/" + minecraft + "-" + version + "/forge-" + minecraft + "-" + version + "-installer.jar",
                "https://maven.minecraftforge.net/net/minecraftforge/forge/" + minecraft + "-" + version + "/forge-" + minecraft + "-" + version + "-installer.jar"
            }
            : new[]
            {
                "https://bmclapi2.bangbang93.com/maven/net/neoforged/neoforge/" + version + "/neoforge-" + version + "-installer.jar",
                "https://maven.neoforged.net/releases/net/neoforged/neoforge/" + version + "/neoforge-" + version + "-installer.jar"
            };

        Directory.CreateDirectory(_paths.Cache);
        var installer = Path.Combine(_paths.Cache, loader.ToLowerInvariant() + "-" + version + "-installer.jar");
        progress?.Report(new DownloadProgress("installer", 18, 0, 0, 0, "正在下载 " + loader + " " + version + " 安装器"));
        await DownloadWithFallbackAsync(urls, installer, ct);

        var java = await _java.BestAsync(21, ct);
        if (java is null) throw new InvalidOperationException("安装 " + loader + " 需要 Java，请先在设置中自动安装 Java 21。");
        progress?.Report(new DownloadProgress("installer", 26, 0, 0, 0, "正在运行 " + loader + " 安装器"));
        var start = DateTime.UtcNow.AddSeconds(-10);
        var info = new ProcessStartInfo
        {
            FileName = java.Path,
            WorkingDirectory = root,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        info.ArgumentList.Add("-Djava.awt.headless=true");
        info.ArgumentList.Add("-jar");
        info.ArgumentList.Add(installer);
        info.ArgumentList.Add("--installClient");
        info.ArgumentList.Add(root);
        using var process = new Process { StartInfo = info };
        var output = new System.Text.StringBuilder();
        process.OutputDataReceived += (_, e) => { if (e.Data is not null) output.AppendLine(e.Data); };
        process.ErrorDataReceived += (_, e) => { if (e.Data is not null) output.AppendLine(e.Data); };
        process.Start();
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromMinutes(12));
        try { await process.WaitForExitAsync(timeout.Token); }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            try { process.Kill(true); } catch { }
            throw new InvalidOperationException(loader + " 安装器超时，请检查网络后重试。");
        }
        if (process.ExitCode != 0)
            throw new InvalidOperationException(loader + " 安装器返回代码 " + process.ExitCode + "：" + Tail(output.ToString(), 600));

        progress?.Report(new DownloadProgress("libraries", 88, 0, 0, 0, "正在校验安装结果"));
        var id = FindInstalledVersionId(root, loader, minecraft, start)
            ?? throw new InvalidOperationException(loader + " 安装器已完成，但没有找到生成的版本目录。");
        var meta = _instances.GetOrCreateMeta(id);
        meta.Name = string.IsNullOrWhiteSpace(displayName) ? loader + " " + version : displayName.Trim();
        ApplyIsolation(meta, id, isolated);
        _settings.Save();
        try { File.Delete(installer); } catch { }
        progress?.Report(new DownloadProgress("done", 100, 0, 0, 0, "安装完成"));
        var list = await _instances.ScanAsync(ct);
        return _instances.Find(list, id) ?? throw new InvalidOperationException("加载器已安装，但未能读取实例");
    }

    private void ApplyIsolation(InstanceMeta meta, string id, bool isolated)
    {
        meta.Isolated = isolated;
        meta.GameDir = isolated
            ? Path.Combine(_settings.Settings.GameRoot, "instances", id)
            : null;
    }

    private static string? FindInstalledVersionId(string root, string loader, string minecraft, DateTime since)
    {
        var dir = Path.Combine(root, "versions");
        if (!Directory.Exists(dir)) return null;
        foreach (var candidate in Directory.GetDirectories(dir).OrderByDescending(Directory.GetLastWriteTimeUtc))
        {
            var id = Path.GetFileName(candidate);
            if (!id.Contains(loader, StringComparison.OrdinalIgnoreCase)) continue;
            if (!id.Contains(minecraft, StringComparison.OrdinalIgnoreCase)) continue;
            if (Directory.GetLastWriteTimeUtc(candidate) < since) continue;
            var json = Path.Combine(candidate, id + ".json");
            if (File.Exists(json)) return id;
        }
        return null;
    }

    private static string Tail(string text, int max)
        => text.Length <= max ? text.Trim() : text.Substring(text.Length - max).Trim();

    private async Task DownloadWithFallbackAsync(IReadOnlyList<string> urls, string target, CancellationToken ct)
    {
        Exception? last = null;
        foreach (var url in urls)
        {
            try
            {
                await _http.DownloadFileAsync(url, target, null, "download", ct);
                return;
            }
            catch (Exception ex) { last = ex; }
        }
        throw new InvalidOperationException("下载失败：" + (last?.Message ?? "网络错误"));
    }

    private async Task EnsureVanillaAsync(string minecraft, IProgress<DownloadProgress>? progress, CancellationToken ct)
    {
        var root = _settings.Settings.GameRoot;
        var dir = Path.Combine(root, "versions", minecraft);
        var jsonPath = Path.Combine(dir, minecraft + ".json");
        if (File.Exists(jsonPath)) return;
        var manifest = await _manifest.GetAsync(false, ct);
        var entry = manifest.FirstOrDefault(v => string.Equals(v.Id, minecraft, StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidOperationException("版本清单中找不到 " + minecraft);
        Directory.CreateDirectory(dir);
        progress?.Report(new DownloadProgress("kernel", 10, 0, 0, 0, "正在下载 " + minecraft + " 版本配置"));
        var text = await _http.GetStringAsync(entry.Url, ct);
        await File.WriteAllTextAsync(jsonPath, text, ct);
        await _launch.EnsureInstallAsync(minecraft, progress, ct);
    }

    private async Task<JsonObject> GetProfileAsync(string loader, string minecraft, string version, CancellationToken ct)
    {
        var urls = loader == "Fabric"
            ? new[]
            {
                "https://bmclapi2.bangbang93.com/fabric-meta/v2/versions/loader/" + minecraft + "/" + version + "/profile/json",
                "https://meta.fabricmc.net/v2/versions/loader/" + minecraft + "/" + version + "/profile/json"
            }
            : new[]
            {
                "https://meta.quiltmc.org/v3/versions/loader/" + minecraft + "/" + version + "/profile/json"
            };
        Exception? last = null;
        foreach (var url in urls)
        {
            try
            {
                var text = await _http.GetStringAsync(url, ct);
                var node = JsonNode.Parse(text) as JsonObject;
                if (node is not null) return node;
            }
            catch (Exception ex) { last = ex; }
        }
        throw new InvalidOperationException("无法获取 " + loader + " 配置：" + (last?.Message ?? "网络错误"));
    }
}