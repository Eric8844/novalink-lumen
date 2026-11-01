using System.Text.Json;
using NovaLink.Lumen.Core;
using NovaLink.Lumen.Core.Plugins;

namespace NovaLink.Lumen.Services.Plugins;

/// <summary>
/// 读取并校验 &lt;插件目录&gt;\plugin.json。校验只做"能否安全加载"的判定，
/// 每条规则给出明确中文原因（绝不静默跳过），由 PluginRegistry 汇总成 Disabled 状态。
/// </summary>
public static class PluginManifestReader
{
    /// <summary>插件目录允许的最大字节数（200 MB）—— 防止误拷整个 VC 运行时。</summary>
    public const long MaxPluginBytes = 200L * 1024 * 1024;

    /// <summary>插件目录允许的最大文件数。</summary>
    public const int MaxPluginFiles = 5000;

    /// <summary>
    /// 类型 C（原生 DLL）插件的 kind 取值。本版**不实现**加载，但清单校验放行，
    /// 由 PluginRegistry 在加载阶段给出明确中文原因后禁用（不静默忽略）。
    /// </summary>
    public const string NativeKind = "native";

    /// <summary>id 保留前缀（宿主自用，第三方插件不得占用）。</summary>
    private static readonly string[] ReservedIdPrefixes = ["novalink.", "system."];

    /// <summary>
    /// 读取并校验插件清单。manifest 为 null 表示"连 JSON 都读不出来"（缺失/坏 JSON/目录不存在），
    /// 此时无法登记插件，只能记日志；manifest 非 null 但 errors 非空表示"能识别但不合法"，登记为 Disabled。
    /// </summary>
    public static PluginManifestResult Load(string pluginDirectory)
    {
        if (string.IsNullOrWhiteSpace(pluginDirectory))
            return new PluginManifestResult(null, ["插件目录不能为空"]);

        string dir;
        try
        {
            dir = Path.GetFullPath(pluginDirectory);
        }
        catch (Exception ex)
        {
            return new PluginManifestResult(null, ["插件目录无效：" + ex.Message]);
        }

        if (!Directory.Exists(dir))
            return new PluginManifestResult(null, ["插件目录不存在：" + dir]);

        var manifestPath = Path.Combine(dir, PluginPaths.ManifestFileName);
        if (!File.Exists(manifestPath))
            return new PluginManifestResult(null, ["缺少 " + PluginPaths.ManifestFileName + "：" + manifestPath]);

        PluginManifest? manifest;
        try
        {
            var json = File.ReadAllText(manifestPath);
            manifest = JsonSerializer.Deserialize<PluginManifest>(json, JsonDefaults.Options);
        }
        catch (JsonException ex)
        {
            var position = ex.LineNumber is null ? "" : "（第 " + (ex.LineNumber + 1) + " 行）";
            return new PluginManifestResult(null, [PluginPaths.ManifestFileName + " 不是合法 JSON" + position + "：" + ex.Message]);
        }
        catch (Exception ex)
        {
            return new PluginManifestResult(null, ["读取 " + PluginPaths.ManifestFileName + " 失败：" + ex.Message]);
        }

        if (manifest is null)
            return new PluginManifestResult(null, [PluginPaths.ManifestFileName + " 内容为空"]);

        var errors = new List<string>();
        Validate(dir, manifest, errors);
        return new PluginManifestResult(manifest, errors);
    }

    /// <summary>
    /// 把清单里的相对路径解析成插件目录内的绝对路径。含 ".."、绝对路径、跳出去的一律返回 null。
    /// entry.assembly / scripts[].file / resources.pri 都走这里，防目录穿越。
    /// </summary>
    public static string? ResolveInside(string pluginDirectory, string relativePath)
    {
        if (string.IsNullOrWhiteSpace(pluginDirectory) || string.IsNullOrWhiteSpace(relativePath)) return null;
        if (relativePath.Contains("..", StringComparison.Ordinal)) return null;

        try
        {
            var root = Path.GetFullPath(pluginDirectory)
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            var candidate = relativePath.Trim();
            if (Path.IsPathRooted(candidate)) return null;

            var full = Path.GetFullPath(Path.Combine(root, candidate));
            var prefix = root + Path.DirectorySeparatorChar;
            return full.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) ? full : null;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>执行全部校验规则，向 errors 追加明确原因（不短路，一次报全）。</summary>
    private static void Validate(string dir, PluginManifest manifest, List<string> errors)
    {
        var dirName = Path.GetFileName(dir);

        // 规则 1：id 合法、等于目录名、不占用保留前缀。
        var id = (manifest.Id ?? "").Trim();
        manifest.Id = id;
        if (id.Length == 0)
        {
            errors.Add("id 不能为空");
        }
        else
        {
            if (!PluginPaths.IsValidId(id))
                errors.Add("id 不符合规则 ^[a-z0-9][a-z0-9.\\-]{2,63}$：" + id);
            if (!string.Equals(id, dirName, StringComparison.OrdinalIgnoreCase))
                errors.Add("id（" + id + "）必须与插件目录名（" + dirName + "）一致");
            foreach (var prefix in ReservedIdPrefixes)
            {
                if (id.StartsWith(prefix, StringComparison.Ordinal))
                {
                    errors.Add("id 使用了保留前缀（" + prefix + "）：" + id);
                    break;
                }
            }
        }

        // 规则 2：schema 必须是当前版本。
        if (manifest.Schema != PluginManifest.CurrentSchema)
            errors.Add("schema 必须为 " + PluginManifest.CurrentSchema + "，实际为 " + manifest.Schema);

        // 规则 3：apiVersion 必须精确匹配宿主。
        if (manifest.ApiVersion != PluginApiVersion.Value)
            errors.Add("apiVersion 必须为 " + PluginApiVersion.Value + "，该插件声明 " + manifest.ApiVersion +
                       "（该插件需要更新版本的启动器或插件格式）");

        // 规则 4/5：kind 合法，且与 entry / scripts 匹配。
        var kind = (manifest.Kind ?? "").Trim();
        manifest.Kind = kind;
        if (string.Equals(kind, NativeKind, StringComparison.OrdinalIgnoreCase))
        {
            // 类型 C（原生 DLL）本版不实现：清单校验先放行，由 PluginRegistry 在加载阶段
            // 给出"原生依赖插件暂不支持"的明确原因并禁用，绝不静默忽略。
        }
        else if (!manifest.IsAssembly && !manifest.IsScript)
        {
            errors.Add("kind 必须是 assembly 或 script，实际为 \"" + kind + "\"");
        }
        else if (manifest.IsAssembly)
        {
            ValidateEntry(dir, manifest, errors);
        }
        else
        {
            ValidateScripts(dir, manifest, errors);
        }

        // 规则 6：UI 导航 tag 必须以 plugin: 开头且不重复。
        ValidateUi(manifest, errors);

        // 规则 7：capabilities 归一化（去空白、去空项）。
        if (manifest.Capabilities is null) manifest.Capabilities = [];
        for (var i = manifest.Capabilities.Count - 1; i >= 0; i--)
        {
            var capability = (manifest.Capabilities[i] ?? "").Trim();
            if (capability.Length == 0) manifest.Capabilities.RemoveAt(i);
            else manifest.Capabilities[i] = capability;
        }

        // 规则 8/9：目录体积与文件数。
        if (TryMeasureDirectory(dir, out var bytes, out var fileCount))
        {
            if (bytes > MaxPluginBytes)
                errors.Add("插件目录体积 " + (bytes / 1024.0 / 1024.0).ToString("F1") + " MB 超过上限 200 MB，已拒绝");
            if (fileCount > MaxPluginFiles)
                errors.Add("插件目录文件数 " + fileCount + " 超过上限 " + MaxPluginFiles + "，已拒绝");
        }
        else
        {
            errors.Add("无法读取插件目录内容（权限不足或目录损坏）：" + dir);
        }
    }

    /// <summary>校验 assembly 类插件的 entry 配置与入口文件。</summary>
    private static void ValidateEntry(string dir, PluginManifest manifest, List<string> errors)
    {
        var entry = manifest.Entry;
        if (entry is null)
        {
            errors.Add("kind=assembly 的插件必须提供 entry（assembly + type）");
            return;
        }

        var assembly = (entry.Assembly ?? "").Trim();
        entry.Assembly = assembly;
        if (assembly.Length == 0)
        {
            errors.Add("entry.assembly 不能为空");
        }
        else if (assembly.Contains("..", StringComparison.Ordinal))
        {
            errors.Add("entry.assembly 不能包含 \"..\"：" + assembly);
        }
        else
        {
            var resolved = ResolveInside(dir, assembly);
            if (resolved is null || !resolved.EndsWith(".dll", StringComparison.OrdinalIgnoreCase))
                errors.Add("entry.assembly 必须指向插件目录内的 .dll 文件：" + assembly);
            else if (!File.Exists(resolved))
                errors.Add("entry.assembly 指向的程序集不存在：" + assembly);
        }

        var type = (entry.Type ?? "").Trim();
        entry.Type = type;
        if (type.Length == 0) errors.Add("entry.type 不能为空");
        else if (type.Contains("..", StringComparison.Ordinal)) errors.Add("entry.type 不能包含 \"..\"：" + type);
    }

    /// <summary>校验 script 类插件的脚本列表。</summary>
    private static void ValidateScripts(string dir, PluginManifest manifest, List<string> errors)
    {
        if (manifest.Scripts is null || manifest.Scripts.Count == 0)
        {
            errors.Add("kind=script 的插件必须提供非空的 scripts 列表");
            return;
        }

        for (var i = 0; i < manifest.Scripts.Count; i++)
        {
            var script = manifest.Scripts[i];
            if (script is null)
            {
                errors.Add("scripts[" + i + "] 为空");
                continue;
            }

            var file = (script.File ?? "").Trim();
            script.File = file;
            if (file.Length == 0)
            {
                errors.Add("scripts[" + i + "].file 不能为空");
                continue;
            }

            if (file.Contains("..", StringComparison.Ordinal))
            {
                errors.Add("scripts[" + i + "].file 不能包含 \"..\"：" + file);
                continue;
            }

            var resolved = ResolveInside(dir, file);
            if (resolved is null) errors.Add("scripts[" + i + "].file 必须位于插件目录内部：" + file);
            else if (!File.Exists(resolved)) errors.Add("scripts[" + i + "].file 指向的脚本不存在：" + file);
        }
    }

    /// <summary>校验 UI 导航声明：tag 前缀、非空标题、tag 不重复。</summary>
    private static void ValidateUi(PluginManifest manifest, List<string> errors)
    {
        var nav = manifest.Ui?.Nav;
        if (nav is null || nav.Count == 0) return;

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < nav.Count; i++)
        {
            var item = nav[i];
            if (item is null)
            {
                errors.Add("ui.nav[" + i + "] 为空");
                continue;
            }

            var tag = (item.Tag ?? "").Trim();
            item.Tag = tag;
            if (tag.Length == 0)
            {
                errors.Add("ui.nav[" + i + "].tag 不能为空");
            }
            else if (!tag.StartsWith("plugin:", StringComparison.Ordinal))
            {
                errors.Add("ui.nav[" + i + "].tag 必须以 plugin: 开头（避免与内置导航冲突）：" + tag);
            }
            else if (!seen.Add(tag))
            {
                errors.Add("ui.nav[" + i + "].tag 重复：" + tag);
            }

            var title = (item.Title ?? "").Trim();
            item.Title = title;
            if (title.Length == 0) errors.Add("ui.nav[" + i + "].title 不能为空");
        }
    }

    /// <summary>统计插件目录字节数与文件数；失败返回 false（权限/IO 问题）。</summary>
    private static bool TryMeasureDirectory(string dir, out long bytes, out int fileCount)
    {
        bytes = 0;
        fileCount = 0;
        try
        {
            foreach (var file in Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories))
            {
                fileCount++;
                try { bytes += new FileInfo(file).Length; } catch { }
                if (bytes > MaxPluginBytes && fileCount > MaxPluginFiles) break;
            }
            return true;
        }
        catch
        {
            return false;
        }
    }
}

/// <summary>manifest 读取结果：Manifest 为 null 表示文件层面就读不出来；Errors 为空且 Manifest 非 null 才算合法。</summary>
public sealed record PluginManifestResult(PluginManifest? Manifest, IReadOnlyList<string> Errors)
{
    /// <summary>是否完全合法。</summary>
    public bool IsValid => Manifest is not null && Errors.Count == 0;

    /// <summary>把错误拼成一行，供 Disabled 状态显示。</summary>
    public string ErrorText => string.Join("；", Errors);
}
