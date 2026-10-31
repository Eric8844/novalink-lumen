using System.Text.Json;
using NovaLink.Lumen.Core;
using NovaLink.Lumen.Core.Models;
using NovaLink.Lumen.Services.Storage;

namespace NovaLink.Lumen.Services.Minecraft;

public sealed class InstanceService
{
    private readonly SettingsService _settings;
    private readonly VersionManifestService _manifest;

    public InstanceService(SettingsService settings, VersionManifestService manifest)
    {
        _settings = settings;
        _manifest = manifest;
    }

    public string GameRoot => _settings.Settings.GameRoot;

    public async Task<IReadOnlyList<InstanceSummary>> ScanAsync(CancellationToken ct = default)
    {
        var root = GameRoot;
        var versions = Path.Combine(root, "versions");
        if (!Directory.Exists(versions)) return [];
        var results = new List<InstanceSummary>();
        var inheritedBases = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var releaseTimes = await _manifest.ReleaseTimesAsync(ct);
        var profileNames = ReadProfileNames(root);
        foreach (var dir in Directory.EnumerateDirectories(versions))
        {
            ct.ThrowIfCancellationRequested();
            var id = Path.GetFileName(dir);
            var jsonPath = Path.Combine(dir, id + ".json");
            if (!File.Exists(jsonPath)) continue;
            try
            {
                var json = JsonSerializer.Deserialize<VersionJson>(await File.ReadAllTextAsync(jsonPath, ct), JsonDefaults.Options);
                if (json is null) continue;
                _settings.Settings.InstanceMeta.TryGetValue(id, out var meta);
                profileNames.TryGetValue(id, out var profileName);
                var (loader, loaderVersion, kernel, name) = LoaderDetector.Detect(id, json, meta?.Name, profileName);
                var (gameDir, isolated) = ResolveGameData(root, dir, id, meta);
                var mods = CountMods(Path.Combine(gameDir, "mods"));
                var worlds = CountWorlds(Path.Combine(gameDir, "saves"));
                var icon = ResolveIcon(dir, meta?.Icon, loader);
                var lastPlayed = meta?.LastPlayed ?? 0;
                var releaseTime = releaseTimes.TryGetValue(kernel, out var released) ? released : 0;
                if (releaseTime == 0)
                {
                    try { releaseTime = new DateTimeOffset(File.GetLastWriteTimeUtc(jsonPath)).ToUnixTimeMilliseconds(); } catch { }
                }
                if (loader != "Vanilla" && !string.Equals(kernel, id, StringComparison.OrdinalIgnoreCase))
                    inheritedBases.Add(kernel);
                results.Add(new InstanceSummary(id, name, kernel, loader, loaderVersion, json.Id ?? id, gameDir, icon, mods, worlds, lastPlayed, isolated, releaseTime));
            }
            catch (Exception ex)
            {
                AppLog.Warn("instances", "跳过损坏的版本 " + id + "：" + ex.Message);
            }
        }
        return results
            .Where(x => !(x.Loader == "Vanilla" && inheritedBases.Contains(x.Id)))
            .OrderByDescending(x => x.LastPlayed > 0 ? x.LastPlayed : x.ReleaseTime)
            .ThenBy(x => x.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
    }

    public VersionJson? ReadVersion(string id)
    {
        var path = Path.Combine(GameRoot, "versions", id, id + ".json");
        if (!File.Exists(path)) return null;
        try { return JsonSerializer.Deserialize<VersionJson>(File.ReadAllText(path), JsonDefaults.Options); }
        catch { return null; }
    }

    public InstanceMeta GetOrCreateMeta(string id)
    {
        if (!_settings.Settings.InstanceMeta.TryGetValue(id, out var meta))
        {
            meta = new InstanceMeta();
            _settings.Settings.InstanceMeta[id] = meta;
        }
        return meta;
    }

    public void SaveMeta() => _settings.Save();

    public void ForgetMeta(string id)
    {
        if (_settings.Settings.InstanceMeta.Remove(id)) _settings.Save();
    }

    public Task DeleteAsync(string id, CancellationToken ct = default)
    {
        var root = _settings.Settings.GameRoot;
        var versions = Path.Combine(root, "versions");
        var dir = Path.Combine(versions, id);
        if (!Directory.Exists(dir)) throw new InvalidOperationException("实例目录不存在：" + dir);

        var inheritedBase = ReadInheritsFrom(dir, id);
        RecycleDirectory(dir);
        ForgetMeta(id);

        // A loader version and its vanilla base are shown as one instance.
        // Remove the base as well when no other installed version still inherits it,
        // otherwise the next scan would surface a leftover "Minecraft x.y" entry.
        if (!string.IsNullOrWhiteSpace(inheritedBase)
            && !string.Equals(inheritedBase, id, StringComparison.OrdinalIgnoreCase))
        {
            var baseDir = Path.Combine(versions, inheritedBase);
            if (Directory.Exists(baseDir) && !IsBaseStillUsed(versions, inheritedBase))
            {
                try
                {
                    RecycleDirectory(baseDir);
                    ForgetMeta(inheritedBase);
                    AppLog.Info("instances", "删除 " + id + " 时一并清理未被引用的原版内核 " + inheritedBase);
                }
                catch (Exception ex)
                {
                    AppLog.Warn("instances", "原版内核清理失败 " + inheritedBase + "：" + ex.Message);
                }
            }
        }

        if (string.Equals(_settings.Settings.SelectedInstanceId, id, StringComparison.OrdinalIgnoreCase)
            || string.Equals(_settings.Settings.SelectedInstanceId, inheritedBase, StringComparison.OrdinalIgnoreCase))
        {
            _settings.Settings.SelectedInstanceId = null;
            _settings.Save();
        }
        return Task.CompletedTask;
    }

    private static string? ReadInheritsFrom(string versionDir, string id)
    {
        var jsonPath = Path.Combine(versionDir, id + ".json");
        if (!File.Exists(jsonPath)) return null;
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(jsonPath));
            return doc.RootElement.TryGetProperty("inheritsFrom", out var value)
                ? value.GetString()?.Trim()
                : null;
        }
        catch
        {
            return null;
        }
    }

    private static bool IsBaseStillUsed(string versionsRoot, string baseId)
    {
        foreach (var dir in Directory.EnumerateDirectories(versionsRoot))
        {
            var id = Path.GetFileName(dir);
            if (string.Equals(id, baseId, StringComparison.OrdinalIgnoreCase)) continue;
            var inherits = ReadInheritsFrom(dir, id);
            if (string.Equals(inherits, baseId, StringComparison.OrdinalIgnoreCase)) return true;
        }
        return false;
    }

    private static void RecycleDirectory(string path)
    {
        global::Microsoft.VisualBasic.FileIO.FileSystem.DeleteDirectory(path,
            global::Microsoft.VisualBasic.FileIO.UIOption.OnlyErrorDialogs,
            global::Microsoft.VisualBasic.FileIO.RecycleOption.SendToRecycleBin);
    }

    public InstanceSummary? Find(IEnumerable<InstanceSummary> list, string id)
        => list.FirstOrDefault(x => string.Equals(x.Id, id, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// 解析实例真正使用的游戏数据目录。优先使用 Lumen 自己记录的隔离目录；
    /// 若没有记录，则识别 PCL2 等启动器把 mods/saves/resourcepacks/options.txt
    /// 放在 versions/&lt;id&gt;/ 下的版本隔离布局。
    /// </summary>
    private static (string GameDir, bool Isolated) ResolveGameData(string root, string versionDir, string id, InstanceMeta? meta)
    {
        var stored = string.IsNullOrWhiteSpace(meta?.GameDir) ? null : Environment.ExpandEnvironmentVariables(meta.GameDir!.Trim());
        if (!string.IsNullOrWhiteSpace(stored))
        {
            var expanded = Path.GetFullPath(stored);
            var isolated = meta?.Isolated == true || !PathEquals(expanded, root);
            return (expanded, isolated);
        }
        if (meta?.Isolated == true)
            return (Path.Combine(root, "instances", id), true);
        if (LooksLikeVersionIsolation(versionDir))
            return (versionDir, true);
        return (root, false);
    }

    private static bool LooksLikeVersionIsolation(string versionDir)
    {
        foreach (var name in new[] { "mods", "saves", "resourcepacks", "shaderpacks", "options.txt" })
        {
            var path = Path.Combine(versionDir, name);
            if (Directory.Exists(path) || File.Exists(path)) return true;
        }
        return false;
    }

    private static bool PathEquals(string left, string right)
    {
        try
        {
            return string.Equals(Path.GetFullPath(left).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
                Path.GetFullPath(right).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
                StringComparison.OrdinalIgnoreCase);
        }
        catch { return string.Equals(left, right, StringComparison.OrdinalIgnoreCase); }
    }

    private static Dictionary<string, string> ReadProfileNames(string root)
    {
        var names = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var path = Path.Combine(root, "launcher_profiles.json");
        if (!File.Exists(path)) return names;
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            if (!doc.RootElement.TryGetProperty("profiles", out var profiles) || profiles.ValueKind != JsonValueKind.Object)
                return names;
            foreach (var property in profiles.EnumerateObject())
            {
                if (property.Value.ValueKind != JsonValueKind.Object) continue;
                var display = property.Value.TryGetProperty("name", out var nameElement) ? nameElement.GetString()?.Trim() : null;
                if (string.IsNullOrWhiteSpace(display)) continue;
                if (property.Value.TryGetProperty("lastVersionId", out var versionElement))
                {
                    var version = versionElement.GetString()?.Trim();
                    if (!string.IsNullOrWhiteSpace(version)) names[version] = display;
                }
                names[property.Name] = display;
            }
        }
        catch (Exception ex)
        {
            AppLog.Warn("instances", "读取 launcher_profiles.json 失败：" + ex.Message);
        }
        return names;
    }

    private static int CountMods(string dir)
    {
        try
        {
            if (!Directory.Exists(dir)) return 0;
            return Directory.EnumerateFiles(dir)
                .Count(f => f.EndsWith(".jar", StringComparison.OrdinalIgnoreCase)
                         || f.EndsWith(".jar.disabled", StringComparison.OrdinalIgnoreCase));
        }
        catch { return 0; }
    }

    private static int CountWorlds(string dir)
    {
        try
        {
            if (!Directory.Exists(dir)) return 0;
            return Directory.EnumerateDirectories(dir).Count(d => File.Exists(Path.Combine(d, "level.dat")));
        }
        catch { return 0; }
    }

    private static string ResolveIcon(string versionDir, string? metaIcon, string loader)
    {
        var candidates = new List<string>();
        if (!string.IsNullOrWhiteSpace(metaIcon))
        {
            candidates.Add(Path.IsPathRooted(metaIcon) ? metaIcon : Path.Combine(versionDir, metaIcon));
            candidates.Add(Path.Combine(versionDir, "icons", metaIcon));
        }
        candidates.Add(Path.Combine(versionDir, "icon.png"));
        candidates.Add(Path.Combine(versionDir, "icon.jpg"));
        foreach (var c in candidates)
            if (File.Exists(c)) return c;
        return "loader:" + loader;
    }
}