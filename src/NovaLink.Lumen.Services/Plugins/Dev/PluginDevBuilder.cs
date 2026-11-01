using System.ComponentModel;
using System.Diagnostics;
using System.IO.Compression;
using System.Text;
using System.Text.Json;
using NovaLink.Lumen.Core;
using NovaLink.Lumen.Core.Plugins;
using NovaLink.Lumen.Services.Plugins;

namespace NovaLink.Lumen.Services.Plugins.Dev;

/// <summary>
/// 开发者工具：把一个插件源码目录编译、打包成 <c>&lt;id&gt;.lumenplugin</c>（本质是 zip）。
///
/// 与运行时安装的区别：这里**不做**"目录名必须等于插件 id"的校验（开发者源码目录通常叫
/// HelloPlugin 之类），也不校验 schema / apiVersion —— 那些是安装期的事，开发者需要能先打出包
/// 再在宿主里试装，校验失败时看到的是宿主给出的清单错误，而不是打包器拒绝打包。
///
/// 两种插件类型分别处理：
/// <list type="bullet">
/// <item>script：不编译，直接把源码目录（排除 bin/obj/dist 等）压成包。</item>
/// <item>assembly：目录里有 build.ps1 就交给它（脚本自己产出包）；否则用 dotnet build 编译，
/// 再按清单（entry / resources.pri / loadContext）把产物组装成包。</item>
/// </list>
///
/// 所有失败都转成可以直接展示给开发者的中文 <see cref="DevBuildResult.Message"/>，异常文本只进
/// <see cref="DevBuildResult.BuildLog"/>，绝不把堆栈抛到 UI。
/// </summary>
public sealed record DevBuildResult(
    bool Ok,
    string Message,
    string? PackagePath,
    string? PluginId,
    string BuildLog,
    TimeSpan Duration);

/// <summary>
/// 源码目录 → .lumenplugin 的打包器。无状态、线程安全：所有路径都由参数传入，不读全局设置，
/// 由设置页在"开发者工具"里直接调用。
/// </summary>
public static class PluginDevBuilder
{
    /// <summary>日志分区名（%LOCALAPPDATA%\NovaLink Lumen\LauncherData\logs\lumen-*.log）。</summary>
    private const string LogArea = "plugin-dev";

    /// <summary>构建输出的最大保留字符数，避免把整份 MSBuild 日志塞进 UI。</summary>
    private const int MaxBuildLogChars = 20000;

    /// <summary>编译 / 打包脚本的单次超时时间。</summary>
    private static readonly TimeSpan ToolTimeout = TimeSpan.FromMinutes(10);

    /// <summary>插件包扩展名（zip 格式，宿主只认这个后缀）。</summary>
    private const string PackageExtension = ".lumenplugin";

    /// <summary>源码目录里永远不打包的目录名（比较时忽略大小写）。</summary>
    private static readonly HashSet<string> ExcludedFolderNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "bin", "obj", ".git", ".vs", ".idea", "dist", "node_modules", "TestResults", "artifacts"
    };

    /// <summary>找 build.ps1 产出的包时，第一轮跳过的目录（第二轮兜底会放开）。</summary>
    private static readonly HashSet<string> BuildOutputFolderNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "bin", "obj", ".git", ".vs", "node_modules", "TestResults", "artifacts"
    };

    /// <summary>
    /// 打包入口。全部失败都以 <see cref="DevBuildResult.Ok"/> = false 返回，不抛异常。
    /// </summary>
    /// <param name="sourceDirectory">插件源码目录（必须含 plugin.json）。</param>
    /// <param name="outputDirectory">
    /// 包输出目录；为 null/空时用 <c>&lt;sourceDirectory&gt;\dist</c>。
    /// 注意：走 build.ps1 分支时，包由脚本自己产出，本参数不参与（避免与脚本的输出位置打架）。
    /// </param>
    /// <param name="progress">阶段性中文文案（"正在运行 build.ps1…"之类），供 UI 显示。</param>
    /// <param name="ct">取消令牌。</param>
    public static async Task<DevBuildResult> BuildAsync(
        string sourceDirectory,
        string? outputDirectory = null,
        IProgress<string>? progress = null,
        CancellationToken ct = default)
    {
        var watch = Stopwatch.StartNew();
        var log = new StringBuilder();
        // 临时目录/临时包由工作区持有，finally 统一清理（异步方法不能用 ref 参数带出来）。
        var workspace = new BuildWorkspace();

        try
        {
            progress?.Report("正在检查插件工程…");

            if (string.IsNullOrWhiteSpace(sourceDirectory))
                return Fail("请先选择一个插件源码目录。", log, watch);

            string source;
            try
            {
                source = Path.GetFullPath(sourceDirectory);
            }
            catch (Exception ex)
            {
                return Fail("插件源码目录无效：" + ex.Message, log, watch, ex);
            }

            if (!Directory.Exists(source))
                return Fail("插件源码目录不存在：" + source, log, watch);

            // 1) 清单：必须能解析，且 id / name / version 齐全。
            var manifestPath = Path.Combine(source, PluginPaths.ManifestFileName);
            if (!File.Exists(manifestPath))
                return Fail("这个目录里没有 plugin.json，不是插件工程目录。", log, watch);

            PluginManifest? manifest;
            try
            {
                var json = File.ReadAllText(manifestPath);
                manifest = JsonSerializer.Deserialize<PluginManifest>(json, JsonDefaults.Options);
            }
            catch (JsonException ex)
            {
                var position = ex.LineNumber is null ? "" : "（第 " + (ex.LineNumber + 1) + " 行）";
                return Fail("plugin.json 不是合法 JSON" + position + "：" + ex.Message, log, watch, ex);
            }
            catch (Exception ex)
            {
                return Fail("读取 plugin.json 失败：" + ex.Message, log, watch, ex);
            }

            if (manifest is null)
                return Fail("plugin.json 内容为空。", log, watch);

            var id = (manifest.Id ?? "").Trim();
            manifest.Id = id;
            if (!PluginPaths.IsValidId(id))
                return Fail("plugin.json 里的 id 不合法（只能用 3~64 个小写字母、数字、点或连字符，且以字母或数字开头）：\"" + id + "\"", log, watch);
            if (string.IsNullOrWhiteSpace(manifest.Name))
                return Fail("plugin.json 里的 name 不能为空。", log, watch);
            if (string.IsNullOrWhiteSpace(manifest.Version))
                return Fail("plugin.json 里的 version 不能为空。", log, watch);

            var output = string.IsNullOrWhiteSpace(outputDirectory)
                ? Path.Combine(source, "dist")
                : Path.GetFullPath(outputDirectory!);

            // 2) 脚本插件：不编译，直接压源码。
            if (manifest.IsScript)
            {
                progress?.Report("正在打包脚本插件…");
                return await BuildScriptPackageAsync(manifest, manifestPath, source, output, log, watch, progress, ct)
                    .ConfigureAwait(false);
            }

            // 3) assembly 插件。
            if (!manifest.IsAssembly)
                return Fail("plugin.json 里的 kind 必须是 assembly 或 script（当前为 \"" + (manifest.Kind ?? "") + "\"），开发版打包器暂不支持其它类型。", log, watch);

            return await BuildAssemblyPackageAsync(manifest, manifestPath, source, output, log, workspace, watch, progress, ct)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            return Fail("已取消打包。", log, watch);
        }
        catch (Exception ex)
        {
            return Fail("打包失败：" + ex.Message + " 可以把下面的日志发给插件开发者。", log, watch, ex);
        }
        finally
        {
            TryDeleteDirectory(workspace.Staging);
            TryDeleteFile(workspace.TempPackage);
        }
    }

    /// <summary>脚本插件：把源码目录压成包（排除编译产物与已有插件包）。</summary>
    private static Task<DevBuildResult> BuildScriptPackageAsync(
        PluginManifest manifest,
        string manifestPath,
        string source,
        string output,
        StringBuilder log,
        Stopwatch watch,
        IProgress<string>? progress,
        CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        // 清单声明的脚本文件缺失不阻断打包（宿主安装时会再校验），但要在日志里点出来。
        foreach (var script in manifest.Scripts ?? [])
        {
            var file = (script?.File ?? "").Trim();
            if (file.Length == 0) continue;
            if (PluginManifestReader.ResolveInside(source, file) is not { } resolved || !File.Exists(resolved))
                log.AppendLine("[package] 警告：清单声明的脚本 " + file + " 在目录里不存在，宿主安装时会拒绝这个包。");
        }

        var files = EnumerateSourceFiles(source, output);
        if (files.Count == 0)
            return Task.FromResult(Fail("插件目录里没有可以打包的文件。", log, watch));

        CheckFileLimits(files, out var limitMessage);
        if (limitMessage is not null)
            return Task.FromResult(Fail(limitMessage, log, watch));

        Directory.CreateDirectory(output);
        var packagePath = Path.Combine(output, manifest.Id + PackageExtension);
        var tempPackage = Path.Combine(output, ".tmp-" + manifest.Id + "-" + Guid.NewGuid().ToString("N") + PackageExtension);

        try
        {
            progress?.Report("正在打包 " + manifest.Id + PackageExtension + "…");
            using (var archive = ZipFile.Open(tempPackage, ZipArchiveMode.Create))
            {
                foreach (var (fullPath, relativePath) in files)
                {
                    ct.ThrowIfCancellationRequested();
                    archive.CreateEntryFromFile(fullPath, relativePath.Replace('\\', '/'), CompressionLevel.Optimal);
                }
            }

            File.Move(tempPackage, packagePath, overwrite: true);
        }
        finally
        {
            TryDeleteFile(tempPackage);
        }

        log.AppendLine("[package] " + files.Count + " 个文件 → " + packagePath);
        progress?.Report("打包完成。");
        return Task.FromResult(Ok(manifest, packagePath, log, watch,
            "脚本插件已打包完成：" + Path.GetFileName(packagePath) + "（" + FormatSize(new FileInfo(packagePath).Length) + "）。"));
    }

    /// <summary>assembly 插件：build.ps1 优先，否则 dotnet build + 按清单组装。</summary>
    private static async Task<DevBuildResult> BuildAssemblyPackageAsync(
        PluginManifest manifest,
        string manifestPath,
        string source,
        string output,
        StringBuilder log,
        BuildWorkspace workspace,
        Stopwatch watch,
        IProgress<string>? progress,
        CancellationToken ct)
    {
        var entryAssembly = (manifest.Entry?.Assembly ?? "").Trim();
        if (entryAssembly.Length == 0)
            return Fail("kind=assembly 的插件必须在 plugin.json 里声明 entry.assembly（插件程序集文件名）。", log, watch);

        // ---------- 分支 A：目录里有 build.ps1，交给脚本 ----------
        var buildScript = Path.Combine(source, "build.ps1");
        if (File.Exists(buildScript))
        {
            progress?.Report("正在运行 build.ps1…");
            log.AppendLine("[build.ps1] " + buildScript);

            // 脚本产出的包可能是"刚刚"写的，往前放宽 2 秒抵消文件时间精度差异。
            var startedUtc = DateTime.UtcNow.AddSeconds(-2);
            var run = await RunToolAsync("powershell.exe",
                ["-NoProfile", "-ExecutionPolicy", "Bypass", "-File", buildScript],
                source, ct).ConfigureAwait(false);

            AppendOutput(log, run.Output, "build.ps1");

            if (run.TimedOut)
                return Fail("打包脚本执行超时（10 分钟）。", log, watch);
            if (run.ExitCode != 0)
                return Fail("打包脚本执行失败（退出码 " + run.ExitCode + "），请看下面的构建输出。", log, watch);

            progress?.Report("正在查找脚本产出的插件包…");
            var produced = FindProducedPackage(source, startedUtc);
            if (produced is null)
                return Fail("打包脚本执行成功了，但没有在插件目录里找到新产出的 " + PackageExtension + " 文件。请确认脚本把包输出到插件目录或 dist 子目录下。", log, watch);

            var producedInfo = new FileInfo(produced);
            if (producedInfo.Length <= 0)
                return Fail("打包脚本产出的插件包是空文件：" + produced, log, watch);

            log.AppendLine("[build.ps1] 使用脚本产出的插件包：" + produced);
            progress?.Report("打包完成。");
            return Ok(manifest, produced, log, watch,
                "build.ps1 已生成插件包：" + Path.GetFileName(produced) + "（" + FormatSize(producedInfo.Length) + "）。");
        }

        // ---------- 分支 B：自己编译 + 组装 ----------
        var project = SelectProject(source, manifest.Id);
        if (project.Error is not null)
            return Fail(project.Error, log, watch);

        progress?.Report("正在编译 " + Path.GetFileName(project.Path!) + "…");
        log.AppendLine("[dotnet build] " + project.Path);
        var build = await RunToolAsync("dotnet",
            ["build", project.Path!, "-c", "Release", "-p:Platform=x64", "-nologo", "-v:m"],
            source, ct).ConfigureAwait(false);

        AppendOutput(log, build.Output, "dotnet build");

        if (build.TimedOut)
            return Fail("编译超时（10 分钟）。请检查这个工程能否单独编译。", log, watch);
        if (build.ExitCode != 0)
            return Fail("编译失败，请看下面的构建输出。", log, watch);

        progress?.Report("正在收集编译产物…");
        var assemblyFileName = Path.GetFileName(entryAssembly);
        var outputDir = FindOutputDirectory(Path.Combine(source, "bin"), assemblyFileName);
        if (outputDir is null)
            return Fail("编译完成但没找到插件程序集 " + assemblyFileName + "，请检查 project 的输出路径。", log, watch);
        log.AppendLine("[package] 编译输出目录：" + outputDir);

        // 先把产物组装到临时目录，再整体压缩：压缩包只会在全部成功后才出现。
        var staging = Path.Combine(Path.GetTempPath(), "novalink-devbuild-" + Guid.NewGuid().ToString("N"));
        workspace.Staging = staging;
        Directory.CreateDirectory(staging);

        // 1) plugin.json 原样
        File.Copy(manifestPath, Path.Combine(staging, PluginPaths.ManifestFileName), overwrite: true);

        // 2) 入口程序集（按清单里的相对路径落位，通常是根目录）
        var entryRelative = entryAssembly.Replace('/', '\\').TrimStart('\\');
        var entrySource = Path.Combine(outputDir, entryRelative);
        if (!File.Exists(entrySource))
        {
            entryRelative = assemblyFileName;
            entrySource = Path.Combine(outputDir, entryRelative);
        }

        if (!File.Exists(entrySource))
            return Fail("编译产物里没有插件程序集 " + assemblyFileName + "，请检查 project 的输出路径。", log, watch);
        CopyIntoStaging(entrySource, staging, entryRelative);

        // 3) 输出目录里的所有 .pri 放到包根目录（WinUI 的 XAML 资源靠它定位）
        foreach (var pri in SafeEnumerateFiles(outputDir, "*.pri"))
        {
            CopyIntoStaging(pri, staging, Path.GetFileName(pri));
        }

        // 4) 清单声明的 PRI 资源必须存在（否则宿主加载 XAML 会抛 XamlParseException）
        var declaredPri = (manifest.Resources?.Pri ?? "").Trim();
        if (declaredPri.Length > 0)
        {
            var priName = Path.GetFileName(declaredPri);
            var priExists = File.Exists(Path.Combine(outputDir, declaredPri.Replace('/', '\\')))
                            || SafeEnumerateFiles(outputDir, "*.pri").Any(p => string.Equals(Path.GetFileName(p), priName, StringComparison.OrdinalIgnoreCase));
            if (!priExists)
                return Fail("清单声明的 PRI 资源 " + declaredPri + " 没有产出，检查 project 是否 DisableEmbeddedXbf=false。", log, watch);
        }

        // 5) 私有托管程序集 → 包的 lib\ 子目录
        var privateAssemblies = manifest.LoadContext?.PrivateAssemblies ?? [];
        foreach (var raw in privateAssemblies)
        {
            var name = (raw ?? "").Trim();
            if (name.Length == 0) continue;
            if (string.Equals(Path.GetFileName(name), assemblyFileName, StringComparison.OrdinalIgnoreCase)) continue;

            var candidate = Path.Combine(outputDir, name.Replace('/', '\\'));
            if (!File.Exists(candidate))
            {
                var inLib = Path.Combine(outputDir, "lib", Path.GetFileName(name));
                candidate = File.Exists(inLib) ? inLib : null;
            }

            if (candidate is null)
            {
                log.AppendLine("[package] 警告：清单声明的私有程序集 " + name + " 没有产出，已跳过（宿主加载时可能报找不到程序集）。");
                continue;
            }

            CopyIntoStaging(candidate, Path.Combine(staging, "lib"), Path.GetFileName(name));
        }

        // 6) 私有原生 DLL → 包根目录
        var nativeFiles = manifest.LoadContext?.Native ?? [];
        foreach (var raw in nativeFiles)
        {
            var name = (raw ?? "").Trim();
            if (name.Length == 0) continue;

            var candidate = Path.Combine(outputDir, name.Replace('/', '\\'));
            if (!File.Exists(candidate))
            {
                var inNative = Path.Combine(outputDir, "native", Path.GetFileName(name));
                candidate = File.Exists(inNative) ? inNative : null;
            }

            if (candidate is null)
            {
                log.AppendLine("[package] 警告：清单声明的原生库 " + name + " 没有产出，已跳过。");
                continue;
            }

            CopyIntoStaging(candidate, staging, Path.GetFileName(name));
        }

        // 7) 体积 / 文件数上限（与安装期同一套阈值）
        var staged = EnumerateStagedFiles(staging);
        CheckStagedLimits(staged, out var stagedLimitMessage);
        if (stagedLimitMessage is not null)
            return Fail(stagedLimitMessage, log, watch);

        // 8) 压缩并原子替换
        Directory.CreateDirectory(output);
        var packagePath = Path.Combine(output, manifest.Id + PackageExtension);
        workspace.TempPackage = Path.Combine(output, ".tmp-" + manifest.Id + "-" + Guid.NewGuid().ToString("N") + PackageExtension);

        progress?.Report("正在打包 " + manifest.Id + PackageExtension + "…");
        ZipFile.CreateFromDirectory(staging, workspace.TempPackage, CompressionLevel.Optimal, includeBaseDirectory: false);
        File.Move(workspace.TempPackage, packagePath, overwrite: true);

        log.AppendLine("[package] " + staged.Count + " 个文件 → " + packagePath);
        progress?.Report("打包完成。");
        return Ok(manifest, packagePath, log, watch,
            "插件包已生成：" + Path.GetFileName(packagePath) + "（" + FormatSize(new FileInfo(packagePath).Length) + "）。");
    }

    /// <summary>选择要编译的工程：优先文件名与插件 id 同名的，否则目录里唯一的那个。</summary>
    private static (string? Path, string? Error) SelectProject(string source, string id)
    {
        string[] projects;
        try
        {
            projects = Directory.GetFiles(source, "*.csproj", SearchOption.TopDirectoryOnly);
        }
        catch (Exception ex)
        {
            return (null, "无法读取插件目录：" + ex.Message);
        }

        if (projects.Length == 0)
            return (null, "这个目录里没有 .csproj，也没有 build.ps1。请提供工程文件，或在插件目录里放一个 build.ps1 指定打包方式。");

        var preferred = projects.FirstOrDefault(p =>
            string.Equals(Path.GetFileNameWithoutExtension(p), id, StringComparison.OrdinalIgnoreCase));
        if (preferred is not null) return (preferred, null);

        if (projects.Length == 1) return (projects[0], null);

        return (null, "这个目录里有多个 .csproj，请在插件目录里放一个 build.ps1 指定打包方式。");
    }

    /// <summary>在 bin 下递归找"产出指定程序集"的输出目录，取程序集修改时间最新的那个。</summary>
    private static string? FindOutputDirectory(string binRoot, string assemblyFileName)
    {
        if (!Directory.Exists(binRoot)) return null;

        try
        {
            return Directory.EnumerateDirectories(binRoot, "*", SearchOption.AllDirectories)
                .Select(d => (Directory: d, File: Path.Combine(d, assemblyFileName)))
                .Where(x => File.Exists(x.File))
                .OrderByDescending(x => SafeLastWriteUtc(x.File))
                .Select(x => x.Directory)
                .FirstOrDefault();
        }
        catch (Exception ex)
        {
            AppLog.Warn(LogArea, "扫描 bin 目录失败：" + ex.Message);
            return null;
        }
    }

    /// <summary>找 build.ps1 刚刚产出的插件包：先按深度 ≤3 跳过 bin/obj，找不到再放宽搜索。</summary>
    private static string? FindProducedPackage(string source, DateTime sinceUtc)
    {
        var strict = new List<string>();
        CollectPackages(source, sinceUtc, maxDepth: 3, skipBuildFolders: true, strict, 0);
        if (strict.Count > 0)
            return strict.OrderByDescending(SafeLastWriteUtc).First();

        var loose = new List<string>();
        CollectPackages(source, sinceUtc, maxDepth: 8, skipBuildFolders: false, loose, 0);
        return loose.Count > 0 ? loose.OrderByDescending(SafeLastWriteUtc).First() : null;
    }

    /// <summary>递归收集插件包（权限不足的目录静默跳过，不因个别子目录失败而找不到包）。</summary>
    private static void CollectPackages(string dir, DateTime sinceUtc, int maxDepth, bool skipBuildFolders, List<string> result, int depth)
    {
        if (depth > maxDepth) return;

        try
        {
            foreach (var file in Directory.EnumerateFiles(dir, "*" + PackageExtension, SearchOption.TopDirectoryOnly))
            {
                if (SafeLastWriteUtc(file) >= sinceUtc) result.Add(file);
            }
        }
        catch
        {
            // 无权限的目录不影响整体结果。
        }

        if (depth == maxDepth) return;

        try
        {
            foreach (var sub in Directory.EnumerateDirectories(dir))
            {
                var name = Path.GetFileName(sub);
                if (skipBuildFolders && BuildOutputFolderNames.Contains(name)) continue;
                CollectPackages(sub, sinceUtc, maxDepth, skipBuildFolders, result, depth + 1);
            }
        }
        catch
        {
            // 同上。
        }
    }

    /// <summary>枚举源码目录里要打进脚本插件的文件（排除编译产物、VCS 目录与已有插件包）。</summary>
    private static List<(string FullPath, string RelativePath)> EnumerateSourceFiles(string source, string outputDirectory)
    {
        var result = new List<(string, string)>();
        Walk(source);
        return result;

        void Walk(string dir)
        {
            string[] files;
            try
            {
                files = Directory.GetFiles(dir);
            }
            catch (Exception ex)
            {
                AppLog.Warn(LogArea, "读取目录失败（已跳过）：" + dir + "（" + ex.Message + "）");
                return;
            }

            foreach (var file in files)
            {
                var name = Path.GetFileName(file);
                if (name.StartsWith(".tmp-", StringComparison.OrdinalIgnoreCase)) continue;

                var extension = Path.GetExtension(file);
                if (string.Equals(extension, PackageExtension, StringComparison.OrdinalIgnoreCase)) continue;
                if (string.Equals(extension, ".zip", StringComparison.OrdinalIgnoreCase)) continue;

                result.Add((file, Path.GetRelativePath(source, file)));
            }

            string[] dirs;
            try
            {
                dirs = Directory.GetDirectories(dir);
            }
            catch (Exception ex)
            {
                AppLog.Warn(LogArea, "读取子目录失败（已跳过）：" + dir + "（" + ex.Message + "）");
                return;
            }

            foreach (var sub in dirs)
            {
                var name = Path.GetFileName(sub);
                if (ExcludedFolderNames.Contains(name)) continue;

                // 输出目录若被指定在源码目录内部（自定义 dist 名），整棵跳过，避免把上次的包再打进去。
                if (IsSameOrInside(outputDirectory, sub)) continue;

                Walk(sub);
            }
        }
    }

    /// <summary>枚举组装目录里的文件（相对路径用于统计与日志）。</summary>
    private static List<(string FullPath, string RelativePath)> EnumerateStagedFiles(string root)
    {
        var result = new List<(string, string)>();
        try
        {
            foreach (var file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
                result.Add((file, Path.GetRelativePath(root, file)));
        }
        catch (Exception ex)
        {
            AppLog.Warn(LogArea, "统计待打包文件失败：" + ex.Message);
        }

        return result;
    }

    /// <summary>脚本插件：打包前按文件数与体积上限检查源码集合。</summary>
    private static void CheckFileLimits(List<(string FullPath, string RelativePath)> files, out string? message)
    {
        message = null;
        if (files.Count > PluginManifestReader.MaxPluginFiles)
        {
            message = "插件里的文件太多了（" + files.Count + " 个，上限 " + PluginManifestReader.MaxPluginFiles + " 个），已拒绝打包。";
            return;
        }

        var total = 0L;
        foreach (var (fullPath, _) in files)
        {
            try { total += new FileInfo(fullPath).Length; } catch { }
            if (total > PluginManifestReader.MaxPluginBytes)
            {
                message = "插件太大了（超过 " + FormatSize(PluginManifestReader.MaxPluginBytes) + "），已拒绝打包。";
                return;
            }
        }
    }

    /// <summary>assembly 插件：组装完成后按同一套上限检查。</summary>
    private static void CheckStagedLimits(List<(string FullPath, string RelativePath)> files, out string? message)
    {
        message = null;
        if (files.Count > PluginManifestReader.MaxPluginFiles)
        {
            message = "插件包里的文件太多了（" + files.Count + " 个，上限 " + PluginManifestReader.MaxPluginFiles + " 个），已拒绝打包。检查 loadContext 是否把整个依赖目录都算进去了。";
            return;
        }

        var total = files.Sum(f => { try { return new FileInfo(f.FullPath).Length; } catch { return 0L; } });
        if (total > PluginManifestReader.MaxPluginBytes)
            message = "插件包太大了（" + FormatSize(total) + "，上限 " + FormatSize(PluginManifestReader.MaxPluginBytes) + "），已拒绝打包。";
    }

    /// <summary>运行外部工具（powershell.exe / dotnet），收集 stdout+stderr。</summary>
    private static async Task<ToolRun> RunToolAsync(string fileName, IReadOnlyList<string> arguments, string workingDirectory, CancellationToken ct)
    {
        var info = new ProcessStartInfo
        {
            FileName = fileName,
            WorkingDirectory = workingDirectory,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.Latin1,
            StandardErrorEncoding = Encoding.Latin1
        };
        foreach (var argument in arguments) info.ArgumentList.Add(argument);

        using var process = new Process { StartInfo = info };
        try
        {
            process.Start();
        }
        catch (Win32Exception ex)
        {
            throw new InvalidOperationException("无法启动 " + fileName + "（找不到它，或被安全软件拦截）：" + ex.Message, ex);
        }

        // 用 Latin1 原样收字节，最后再按 UTF-8 → GB18030 的顺序猜测真实编码，
        // 否则 Windows PowerShell 5.1 的中文输出在 GBK 系统上会变成乱码。
        var stdoutTask = process.StandardOutput.ReadToEndAsync();
        var stderrTask = process.StandardError.ReadToEndAsync();

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(ToolTimeout);

        var timedOut = false;
        try
        {
            await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            timedOut = true;
            try { process.Kill(entireProcessTree: true); } catch { }
            try { await process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false); } catch { }
        }
        catch (OperationCanceledException)
        {
            try { process.Kill(entireProcessTree: true); } catch { }
            throw;
        }

        string rawOut;
        string rawErr;
        try
        {
            rawOut = await stdoutTask.ConfigureAwait(false);
            rawErr = await stderrTask.ConfigureAwait(false);
        }
        catch
        {
            rawOut = "";
            rawErr = "";
        }

        var output = DecodeToolOutput(rawOut);
        var errorText = DecodeToolOutput(rawErr);
        if (errorText.Length > 0)
            output = output.Length == 0 ? errorText : output.TrimEnd() + Environment.NewLine + errorText;

        var exitCode = -1;
        if (!timedOut)
        {
            try { exitCode = process.ExitCode; } catch { }
        }

        return new ToolRun(exitCode, output, timedOut);
    }

    /// <summary>把重定向输出还原成文本：逐行嗅探编码，UTF-8 优先，失败再按 GB18030（Windows 中文代码页 936）。</summary>
    /// <remarks>
    /// 必须逐行判断：dotnet CLI 的表头是 UTF-8，而 MSBuild 的某些中文消息走系统 ANSI 代码页（简体中文 936），
    /// 同一次输出里两种编码混在一起。整段判定会让"有一行不是 UTF-8"就整体按 936 解，UTF-8 的行全变乱码。
    /// </remarks>
    private static string DecodeToolOutput(string raw)
    {
        if (string.IsNullOrEmpty(raw)) return "";

        byte[] bytes;
        try
        {
            bytes = Encoding.Latin1.GetBytes(raw);
        }
        catch
        {
            return raw;
        }

        var utf8Strict = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);
        Encoding? ansi = null;
        try
        {
            Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
            ansi = Encoding.GetEncoding(936);
        }
        catch
        {
            // 取不到 936 就只用 UTF-8，解不出来的行保留替换字符。
        }

        var builder = new StringBuilder(bytes.Length + 16);
        var lineStart = 0;
        for (var i = 0; i <= bytes.Length; i++)
        {
            if (i < bytes.Length && bytes[i] != (byte)'\n') continue;

            var lineLength = i - lineStart;
            var hasCarriageReturn = lineLength > 0 && bytes[i - 1] == (byte)'\r';
            var contentLength = hasCarriageReturn ? lineLength - 1 : lineLength;

            if (contentLength > 0)
                builder.Append(DecodeLine(bytes, lineStart, contentLength, utf8Strict, ansi));

            if (hasCarriageReturn) builder.Append('\r');
            if (i < bytes.Length) builder.Append('\n');

            lineStart = i + 1;
        }

        return builder.ToString();
    }

    /// <summary>解一行字节：优先严格 UTF-8，失败再按 936，最后兜底 Latin1。</summary>
    private static string DecodeLine(byte[] bytes, int offset, int count, Encoding utf8Strict, Encoding? ansi)
    {
        try
        {
            return utf8Strict.GetString(bytes, offset, count);
        }
        catch (DecoderFallbackException)
        {
            // 这一行不是 UTF-8，按系统中文代码页再试。
        }

        if (ansi is not null)
        {
            try
            {
                return ansi.GetString(bytes, offset, count);
            }
            catch
            {
                // 继续兜底。
            }
        }

        return Encoding.Latin1.GetString(bytes, offset, count);
    }

    /// <summary>把工具输出追加进日志（带小节标题）。</summary>
    private static void AppendOutput(StringBuilder log, string output, string title)
    {
        if (string.IsNullOrWhiteSpace(output)) return;
        log.AppendLine("----- " + title + " 输出 -----");
        log.AppendLine(output.TrimEnd());
        log.AppendLine("----- " + title + " 输出结束 -----");
    }

    /// <summary>把文件复制到组装目录下的相对位置（自动建子目录）。</summary>
    private static void CopyIntoStaging(string sourceFile, string stagingRoot, string relativePath)
    {
        var target = Path.Combine(stagingRoot, relativePath);
        var directory = Path.GetDirectoryName(target);
        if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
        File.Copy(sourceFile, target, overwrite: true);
    }

    /// <summary>枚举目录下匹配的文件（权限问题静默跳过）。</summary>
    private static IEnumerable<string> SafeEnumerateFiles(string root, string pattern)
    {
        try
        {
            return Directory.EnumerateFiles(root, pattern, SearchOption.AllDirectories).ToList();
        }
        catch (Exception ex)
        {
            AppLog.Warn(LogArea, "枚举 " + pattern + " 失败：" + ex.Message);
            return [];
        }
    }

    private static DateTime SafeLastWriteUtc(string path)
    {
        try { return File.GetLastWriteTimeUtc(path); } catch { return DateTime.MinValue; }
    }

    /// <summary>判断 path 是否等于 root 或位于 root 内部。</summary>
    private static bool IsSameOrInside(string root, string path)
    {
        try
        {
            var normalizedRoot = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            var normalizedPath = Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            if (string.Equals(normalizedRoot, normalizedPath, StringComparison.OrdinalIgnoreCase)) return true;
            return normalizedPath.StartsWith(normalizedRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    /// <summary>成功结果（同时写日志）。</summary>
    private static DevBuildResult Ok(PluginManifest manifest, string packagePath, StringBuilder log, Stopwatch watch, string message)
    {
        AppLog.Info(LogArea, message + " → " + packagePath);
        return new DevBuildResult(true, message, packagePath, manifest.Id, Truncate(log.ToString()), watch.Elapsed);
    }

    /// <summary>失败结果（同时写日志；异常文本只进 BuildLog，不进 Message）。</summary>
    private static DevBuildResult Fail(string message, StringBuilder log, Stopwatch watch, Exception? exception = null)
    {
        if (exception is not null)
        {
            log.AppendLine("[异常] " + exception.GetType().Name + "：" + exception.Message);
            AppLog.Error(LogArea, message, exception);
        }
        else
        {
            AppLog.Warn(LogArea, message);
        }

        return new DevBuildResult(false, message, null, null, Truncate(log.ToString()), watch.Elapsed);
    }

    /// <summary>日志截断到 <see cref="MaxBuildLogChars"/> 字符，保留最有用的尾部。</summary>
    private static string Truncate(string text)
    {
        if (text.Length <= MaxBuildLogChars) return text;
        var keep = MaxBuildLogChars - 60;
        return "…（构建输出过长，已省略前面的部分）" + Environment.NewLine + text[^keep..];
    }

    private static string FormatSize(long bytes) =>
        bytes >= 1024 * 1024
            ? (bytes / 1024.0 / 1024.0).ToString("F1") + " MB"
            : (bytes / 1024.0).ToString("F0") + " KB";

    private static void TryDeleteDirectory(string? directory)
    {
        if (string.IsNullOrWhiteSpace(directory) || !Directory.Exists(directory)) return;
        try { Directory.Delete(directory, recursive: true); }
        catch (Exception ex) { AppLog.Warn(LogArea, "清理临时目录失败：" + directory + "（" + ex.Message + "）"); }
    }

    private static void TryDeleteFile(string? file)
    {
        if (string.IsNullOrWhiteSpace(file) || !File.Exists(file)) return;
        try { File.Delete(file); }
        catch (Exception ex) { AppLog.Warn(LogArea, "清理临时文件失败：" + file + "（" + ex.Message + "）"); }
    }

    /// <summary>一次外部工具调用的结果。</summary>
    private readonly record struct ToolRun(int ExitCode, string Output, bool TimedOut);

    /// <summary>
    /// 单次打包用到的临时路径。异步方法不能带 ref/out 参数，用这个可变对象把
    /// 临时目录/临时包名带回 BuildAsync 的 finally 做统一清理。
    /// </summary>
    private sealed class BuildWorkspace
    {
        /// <summary>组装目录（可能为空）。</summary>
        public string? Staging { get; set; }

        /// <summary>正在写入的临时包文件（可能为空）。</summary>
        public string? TempPackage { get; set; }
    }
}
