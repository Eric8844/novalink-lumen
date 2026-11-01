using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text.Json;
using System.Threading;
using NovaLink.Lumen.Core;
using NovaLink.Lumen.Core.Plugins;
using NovaLink.Lumen.Services;
using NovaLink.Lumen.Services.Plugins;

namespace NovaLink.Lumen.Plugins;

/// <summary>
/// 从 .lumenplugin（zip）文件安装插件：解压到临时目录 → 校验清单 → 搬到 &lt;DataRoot&gt;\plugins\&lt;id&gt;。
/// 只做"放到位"，不触发热加载：插件在下次启动启动器时才会被加载（与 docs/PLUGINS.md 一致）。
/// 所有失败都返回给用户看得懂的中文原因，绝不把异常堆栈抛到 UI。
/// </summary>
public static class PluginPackageInstaller
{
    private const string LogArea = "plugins";

    /// <summary>安装结果。Message 是可以直接显示的整句中文人话。</summary>
    public sealed record InstallResult(bool Ok, string PluginId, string Message);

    /// <summary>支持的插件包扩展名（.lumenplugin 与 .zip 都是 zip）。</summary>
    private static readonly string[] SupportedExtensions = [".lumenplugin", ".zip"];

    public static InstallResult Install(string packageFilePath, string pluginsRoot)
    {
        string? extractRoot = null;
        string? stageRoot = null;
        try
        {
            if (string.IsNullOrWhiteSpace(packageFilePath) || !File.Exists(packageFilePath))
                return Fail("找不到要安装的插件文件。");

            var extension = Path.GetExtension(packageFilePath);
            if (!SupportedExtensions.Any(e => string.Equals(e, extension, StringComparison.OrdinalIgnoreCase)))
                return Fail("只支持 .lumenplugin 插件包（它其实就是一个 zip 压缩包）。");

            if (string.IsNullOrWhiteSpace(pluginsRoot))
                return Fail("插件目录不可用，请先点「打开插件目录」确认数据目录是否正常。");

            var packageBytes = new FileInfo(packageFilePath).Length;
            if (packageBytes <= 0)
                return Fail("这个插件包是空文件，可能没有下载完整。");
            if (packageBytes > PluginManifestReader.MaxPluginBytes)
                return Fail("插件包太大了（超过 200 MB），已拒绝安装。");

            extractRoot = Path.Combine(Path.GetTempPath(), "novalink-plugin-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(extractRoot);

            try
            {
                ZipFile.ExtractToDirectory(packageFilePath, extractRoot);
            }
            catch (InvalidDataException)
            {
                return Fail("这个文件不是有效的插件包，可能已经损坏或不是 zip 格式。");
            }
            catch (IOException ex)
            {
                AppLog.Warn(LogArea, "解压插件包失败：" + ex.Message);
                return Fail("解压插件包失败，可能包含不安全的路径或文件正被占用。");
            }

            var packageRoot = FindPackageRoot(extractRoot);
            if (packageRoot is null)
                return Fail("插件包里没有找到 plugin.json，结构不正确。");

            if (!CheckPackageSize(packageRoot, out var limitMessage))
                return Fail(limitMessage!);

            var declaredId = TryReadId(packageRoot);
            if (!PluginPaths.IsValidId(declaredId))
                return Fail("plugin.json 里的 id 不合法（只能用 3~64 个小写字母、数字、点或连字符）。");

            // PluginManifestReader 要求 id 与目录名一致：先搬到以 id 命名的暂存目录再校验。
            stageRoot = Path.Combine(Path.GetTempPath(), "novalink-plugin-stage-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(stageRoot);
            var stagedDirectory = Path.Combine(stageRoot, declaredId!);
            Directory.Move(packageRoot, stagedDirectory);

            var manifestResult = PluginManifestReader.Load(stagedDirectory);
            if (manifestResult.Manifest is null)
                return Fail("插件的 plugin.json 无法读取：" + FirstError(manifestResult.Errors));
            if (manifestResult.Errors.Count > 0)
                return Fail("插件清单有问题，已取消安装：" + FirstError(manifestResult.Errors));

            var manifest = manifestResult.Manifest;
            var targetDirectory = Path.Combine(pluginsRoot, manifest.Id);
            if (Directory.Exists(targetDirectory))
                return Fail("已经安装过插件「" + DisplayName(manifest) + "」，请先在插件目录里删除它再安装新版本。");

            Directory.CreateDirectory(pluginsRoot);
            Directory.Move(stagedDirectory, targetDirectory);

            AppLog.Info(LogArea, "已安装插件：" + manifest.Id + " -> " + targetDirectory);
            return new InstallResult(true, manifest.Id,
                "已安装插件「" + DisplayName(manifest) + "」。重启启动器后它就会出现在插件列表里。");
        }
        catch (UnauthorizedAccessException ex)
        {
            AppLog.Error(LogArea, "安装插件时没有写入权限。", ex);
            return Fail("没有权限写入插件目录，请检查启动器的安装位置或换一个数据目录。");
        }
        catch (Exception ex)
        {
            AppLog.Error(LogArea, "安装插件失败。", ex);
            return Fail("安装插件失败，请稍后再试。如果一直失败，可以手动解压插件包到插件目录。");
        }
        finally
        {
            TryDelete(extractRoot);
            TryDelete(stageRoot);
        }
    }

    /// <summary>待替换队列目录：与 plugins 平级的 plugin-updates（放在 plugins 外面，免得被插件扫描当成插件目录）。</summary>
    public static string UpdatesDirectory(string pluginsRoot)
        => Path.Combine(SiblingRoot(pluginsRoot), "plugin-updates");

    /// <summary>插件回收目录：与 plugins 平级，避免被插件扫描当成插件目录。</summary>
    public static string TrashDirectory(string pluginsRoot)
        => Path.Combine(SiblingRoot(pluginsRoot), "plugin-trash");

    /// <summary>plugins 的父目录（队列与回收站都放在它下面，绕开插件扫描）。</summary>
    private static string SiblingRoot(string pluginsRoot)
    {
        var parent = Path.GetDirectoryName(Path.TrimEndingDirectorySeparator(pluginsRoot));
        return string.IsNullOrEmpty(parent) ? pluginsRoot : parent;
    }

    /// <summary>
    /// 插件正在运行时无法覆盖它的 DLL（文件被进程占用），改成把新包排进队列：
    /// 下次启动、插件还没加载时由 <see cref="ApplyPendingUpdates"/> 就地替换。
    /// </summary>
    public static InstallResult QueueReplace(string packageFilePath, string pluginsRoot)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(packageFilePath) || !File.Exists(packageFilePath))
                return Fail("找不到要安装的插件文件。");

            var extension = Path.GetExtension(packageFilePath);
            if (!SupportedExtensions.Any(e => string.Equals(e, extension, StringComparison.OrdinalIgnoreCase)))
                return Fail("只支持 .lumenplugin 插件包（它其实就是一个 zip 压缩包）。");

            if (string.IsNullOrWhiteSpace(pluginsRoot))
                return Fail("插件目录不可用，请先点「打开插件目录」确认数据目录是否正常。");

            var packageBytes = new FileInfo(packageFilePath).Length;
            if (packageBytes <= 0)
                return Fail("这个插件包是空文件，可能没有下载完整。");
            if (packageBytes > PluginManifestReader.MaxPluginBytes)
                return Fail("插件包太大了（超过 200 MB），已拒绝安装。");

            var id = TryReadIdFromPackage(packageFilePath);
            if (!PluginPaths.IsValidId(id))
                return Fail("plugin.json 里的 id 不合法（只能用 3~64 个小写字母、数字、点或连字符）。");

            var updates = UpdatesDirectory(pluginsRoot);
            Directory.CreateDirectory(updates);
            var queuePath = Path.Combine(updates, id + ".lumenplugin");
            File.Copy(packageFilePath, queuePath, overwrite: true);

            AppLog.Info(LogArea, "插件更新已排进队列：" + id + " -> " + queuePath);
            return new InstallResult(true, id!,
                "插件正在运行，新版本已经排进队列。重启启动器时会自动替换成新版本。");
        }
        catch (UnauthorizedAccessException ex)
        {
            AppLog.Error(LogArea, "把插件更新排进队列时没有写入权限。", ex);
            return Fail("没有权限写入插件目录，请检查启动器的安装位置或换一个数据目录。");
        }
        catch (Exception ex)
        {
            AppLog.Error(LogArea, "把插件更新排进队列失败。", ex);
            return Fail("无法准备这次替换，请重启启动器后再试一次。");
        }
    }

    /// <summary>
    /// 启动时落地排队中的插件更新。必须在插件加载（Discover）之前调用：
    /// 那时插件程序集还没被映射进进程，旧目录才删得掉。
    /// </summary>
    public static int ApplyPendingUpdates(string pluginsRoot)
    {
        var applied = 0;
        try
        {
            // 先把上次没删干净的回收站目录清掉（旧版本 DLL 当时可能还被进程占着）。
            var trash = TrashDirectory(pluginsRoot);
            if (Directory.Exists(trash)) CleanupTrash(trash);

            var updates = UpdatesDirectory(pluginsRoot);
            if (!Directory.Exists(updates)) return 0;

            foreach (var package in Directory.EnumerateFiles(updates, "*.lumenplugin").ToArray())
            {
                var id = TryReadIdFromPackage(package);
                if (!PluginPaths.IsValidId(id))
                {
                    AppLog.Warn(LogArea, "排队中的插件包 id 不合法，已跳过：" + Path.GetFileName(package));
                    continue;
                }

                var target = Path.Combine(pluginsRoot, id!);
                if (Directory.Exists(target) && !MoveToTrash(target, id!, pluginsRoot))
                {
                    // 旧进程还占着文件：包留在队列里，下次启动再试。
                    continue;
                }

                var result = Install(package, pluginsRoot);
                if (!result.Ok)
                {
                    AppLog.Warn(LogArea, "排队中的插件更新落地失败：" + id + "（" + result.Message + "）");
                    continue;
                }

                try { File.Delete(package); } catch (Exception ex) { AppLog.Warn(LogArea, "清理已落地的插件包失败：" + ex.Message); }
                applied++;
                AppLog.Info(LogArea, "已应用排队中的插件更新：" + id);
            }
        }
        catch (Exception ex)
        {
            AppLog.Error(LogArea, "处理插件更新队列失败。", ex);
        }

        return applied;
    }

    /// <summary>待删除队列目录：与 plugins 平级的 plugin-deletes（放在 plugins 外面，免得被扫描当成插件目录）。</summary>
    public static string DeletesDirectory(string pluginsRoot)
        => Path.Combine(SiblingRoot(pluginsRoot), "plugin-deletes");

    /// <summary>
    /// 插件正在运行时删不掉它自己的目录（DLL 被进程占用），改成写一个删除标记排进队列：
    /// 下次启动、插件还没加载时由 <see cref="ApplyPendingDeletes"/> 在 Discover 之前把目录挪进回收站。
    /// 幂等：同一个插件反复排队只会留下一个标记文件。
    /// </summary>
    public static void QueueDelete(string pluginId, string pluginsRoot)
    {
        try
        {
            if (!PluginPaths.IsValidId(pluginId) || string.IsNullOrWhiteSpace(pluginsRoot))
            {
                AppLog.Warn(LogArea, "插件删除没有排队：id 或插件目录不合法（" + (pluginId ?? "") + "）。");
                return;
            }

            var deletes = DeletesDirectory(pluginsRoot);
            Directory.CreateDirectory(deletes);
            var marker = Path.Combine(deletes, pluginId + ".delete");
            File.WriteAllText(marker, DateTime.UtcNow.ToString("O"));
            AppLog.Info(LogArea, "插件删除已排进队列：" + pluginId + " -> " + marker);
        }
        catch (Exception ex)
        {
            AppLog.Error(LogArea, "把插件删除排进队列失败：" + pluginId, ex);
        }
    }

    /// <summary>
    /// 启动时落地排队中的插件删除。必须在插件加载（Discover）之前调用：
    /// 那时插件程序集还没被映射进进程，目录才删得掉。删除是安全操作，安全模式下也照做。
    /// 旧进程还占着文件时标记留在队列里，下次启动再试。
    /// </summary>
    public static int ApplyPendingDeletes(string pluginsRoot)
    {
        var applied = 0;
        try
        {
            var deletes = DeletesDirectory(pluginsRoot);
            if (!Directory.Exists(deletes)) return 0;

            foreach (var marker in Directory.EnumerateFiles(deletes, "*.delete").ToArray())
            {
                var id = Path.GetFileNameWithoutExtension(marker);
                if (!PluginPaths.IsValidId(id))
                {
                    AppLog.Warn(LogArea, "排队中的插件删除标记不合法，已清理：" + Path.GetFileName(marker));
                    TryDeleteMarker(marker);
                    continue;
                }

                var target = Path.Combine(pluginsRoot, id);
                if (Directory.Exists(target) && !MoveToTrash(target, id, pluginsRoot))
                {
                    // 旧进程还占着文件：标记留在队列里，下次启动再试。
                    continue;
                }

                TryDeleteMarker(marker);
                applied++;
                AppLog.Info(LogArea, "已应用排队中的插件删除：" + id);
            }
        }
        catch (Exception ex)
        {
            AppLog.Error(LogArea, "处理插件删除队列失败。", ex);
        }

        return applied;
    }

    /// <summary>
    /// 立即移除插件目录：整体改名挪进 plugin-trash，再尽力清理。
    /// 只用改名、不递归删除 —— 递归删到一半失败会留下"只剩 dll、没有 plugin.json"的半个插件，
    /// 那比删不掉更糟（下次启动会报"缺少 plugin.json"）。返回 false 表示目录被占用，调用方应排队到下次启动。
    /// </summary>
    public static bool TryRemoveNow(string pluginId, string pluginsRoot)
    {
        if (!PluginPaths.IsValidId(pluginId) || string.IsNullOrWhiteSpace(pluginsRoot)) return false;

        var directory = Path.Combine(pluginsRoot, pluginId);
        if (!Directory.Exists(directory)) return true;

        return MoveToTrash(directory, pluginId, pluginsRoot);
    }

    /// <summary>清掉一个删除标记；删不掉就留着，下次启动再试（不影响插件本身）。</summary>
    private static void TryDeleteMarker(string marker)
    {
        try
        {
            File.Delete(marker);
        }
        catch (Exception ex)
        {
            AppLog.Warn(LogArea, "清理插件删除标记失败：" + ex.Message);
        }
    }

    /// <summary>
    /// 把旧插件目录整体改名挪进 plugin-trash，再往 plugins\&lt;id&gt; 装新版本。
    /// 用目录改名而不是递归删除：改名要么整体成功要么整体失败，不会像删除那样在中途
    /// 失败后留下"只剩一个 dll、没有 plugin.json"的半个插件。
    /// 旧进程可能还没完全退出（文件句柄还没松开），所以重试几次。
    /// </summary>
    private static bool MoveToTrash(string pluginDirectory, string id, string pluginsRoot)
    {
        var trash = TrashDirectory(pluginsRoot);
        try
        {
            Directory.CreateDirectory(trash);
        }
        catch (Exception ex)
        {
            AppLog.Warn(LogArea, "创建插件回收目录失败，本次先跳过替换：" + ex.Message);
            return false;
        }

        Exception? last = null;
        for (var attempt = 1; attempt <= 6; attempt++)
        {
            try
            {
                var destination = Path.Combine(trash, id + "-" + DateTime.UtcNow.ToString("yyyyMMddHHmmssfff") + "-" + attempt);
                Directory.Move(pluginDirectory, destination);
                AppLog.Info(LogArea, "旧版本插件目录已挪进回收站：" + id);
                CleanupTrash(trash);
                return true;
            }
            catch (Exception ex)
            {
                last = ex;
                // 正在退出的旧进程还没松开 DLL：等一会儿再试。
                Thread.Sleep(400);
            }
        }

        AppLog.Warn(LogArea, "排队中的插件更新暂时无法落地（旧目录仍被占用）：" + id + "（" + last?.Message + "）");
        return false;
    }

    /// <summary>尽力清空回收目录；个别文件仍被占用就留到下次启动。</summary>
    private static void CleanupTrash(string trashDirectory)
    {
        try
        {
            foreach (var directory in Directory.EnumerateDirectories(trashDirectory).ToArray())
            {
                try
                {
                    Directory.Delete(directory, true);
                }
                catch (Exception ex)
                {
                    AppLog.Warn(LogArea, "回收目录里的旧插件暂时删不掉，留到下次启动：" + ex.Message);
                }
            }
        }
        catch (Exception ex)
        {
            AppLog.Warn(LogArea, "清理插件回收目录失败：" + ex.Message);
        }
    }

    /// <summary>只从压缩包里的 plugin.json 读 id（不解压、不做整份校验）；失败返回 null。</summary>
    private static string? TryReadIdFromPackage(string packageFilePath)
    {
        try
        {
            using var archive = ZipFile.OpenRead(packageFilePath);
            foreach (var entry in archive.Entries)
            {
                var name = entry.FullName.Replace('\\', '/');
                if (!name.EndsWith(PluginPaths.ManifestFileName, StringComparison.OrdinalIgnoreCase)) continue;
                if (name.Count(c => c == '/') > 1) continue;

                using var stream = entry.Open();
                using var reader = new StreamReader(stream);
                var json = reader.ReadToEnd();
                var manifest = JsonSerializer.Deserialize<PluginManifest>(json, JsonDefaults.Options);
                return manifest?.Id;
            }
        }
        catch (Exception ex)
        {
            AppLog.Warn(LogArea, "读取插件包里的 plugin.json 失败：" + ex.Message);
        }

        return null;
    }

    private static InstallResult Fail(string message) => new(false, "", message);

    private static string DisplayName(PluginManifest manifest)
        => string.IsNullOrWhiteSpace(manifest.Name) ? manifest.Id : manifest.Name;

    private static string FirstError(IReadOnlyList<string> errors)
        => errors.Count == 0 ? "原因未知。" : errors[0] + (errors.Count > 1 ? "（共 " + errors.Count + " 处问题）" : "");

    /// <summary>插件包根目录的两种常见形态：plugin.json 在压缩包根，或包在唯一一层子目录里。</summary>
    private static string? FindPackageRoot(string extractRoot)
    {
        if (File.Exists(Path.Combine(extractRoot, PluginPaths.ManifestFileName))) return extractRoot;

        // 只要一层：<zip>/<插件名>/plugin.json。再深就不猜了，避免把无关目录当插件。
        foreach (var directory in Directory.EnumerateDirectories(extractRoot))
        {
            if (File.Exists(Path.Combine(directory, PluginPaths.ManifestFileName))) return directory;
        }

        return null;
    }

    /// <summary>只读 id（不做整份校验），便于先确定目录名；解析失败返回 null。</summary>
    private static string? TryReadId(string pluginDirectory)
    {
        try
        {
            var manifestPath = Path.Combine(pluginDirectory, PluginPaths.ManifestFileName);
            var json = File.ReadAllText(manifestPath);
            var manifest = JsonSerializer.Deserialize<PluginManifest>(json, JsonDefaults.Options);
            return manifest?.Id;
        }
        catch (Exception ex)
        {
            AppLog.Warn(LogArea, "读取插件包里的 plugin.json 失败：" + ex.Message);
            return null;
        }
    }

    private static bool CheckPackageSize(string pluginDirectory, out string? message)
    {
        message = null;
        try
        {
            var total = 0L;
            var count = 0;
            foreach (var file in Directory.EnumerateFiles(pluginDirectory, "*", SearchOption.AllDirectories))
            {
                count++;
                if (count > PluginManifestReader.MaxPluginFiles)
                {
                    message = "插件包里的文件太多了（超过 " + PluginManifestReader.MaxPluginFiles + " 个），已拒绝安装。";
                    return false;
                }

                total += new FileInfo(file).Length;
                if (total > PluginManifestReader.MaxPluginBytes)
                {
                    message = "插件包解压后太大了（超过 200 MB），已拒绝安装。";
                    return false;
                }
            }

            return true;
        }
        catch (Exception ex)
        {
            AppLog.Warn(LogArea, "统计插件包大小时失败：" + ex.Message);
            message = "无法读取插件包里的文件，已取消安装。";
            return false;
        }
    }

    private static void TryDelete(string? directory)
    {
        if (string.IsNullOrWhiteSpace(directory) || !Directory.Exists(directory)) return;
        try
        {
            Directory.Delete(directory, true);
        }
        catch (Exception ex)
        {
            AppLog.Warn(LogArea, "清理临时目录失败：" + directory + "（" + ex.Message + "）");
        }
    }
}
