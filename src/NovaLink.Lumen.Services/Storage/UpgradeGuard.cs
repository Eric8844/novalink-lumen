using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using NovaLink.Lumen.Core;

namespace NovaLink.Lumen.Services.Storage;

/// <summary>升级判定的三种结果。</summary>
public enum UpgradeKind
{
    /// <summary>全新安装：LauncherData 里没有 lumen.json（但旧 Electron / Preview 的数据可能还在，仍要备份）。</summary>
    FreshInstall,

    /// <summary>需要"先备份再 schema 迁移"。</summary>
    NeedsUpgrade,

    /// <summary>已经是当前版本（schema 与版本戳都到位）：第二次启动走这里，保证备份幂等、不重复建目录。</summary>
    AlreadyCurrent
}

/// <summary>
/// 升级计划 = Inspect() 的产物。App 层按它决定"备份 →（成功才）迁移 → 提示 → 写版本戳"。
/// </summary>
public sealed record UpgradePlan(
    UpgradeKind Kind,
    string? SourceVersion,
    int SourceSchema,
    string? BackupRoot,
    bool LauncherDataNeedsBackup,
    IReadOnlyList<BackupTree> ExtraTrees);

/// <summary>单独备份的旧数据树。Origin 用于决定复制策略（旧 Electron 数据根里必须跳过 Chromium profile 目录）。</summary>
public sealed record BackupTree(string Origin, string SourcePath, string DisplayName);

/// <summary>
/// 备份结果。Success=false 时调用方**必须跳过 schema 迁移**（数据安全优先：没有备份就不动数据）。
/// AlreadyExisted=true 表示备份目录早就有了（幂等跳过，绝不覆盖用户已有备份）。
/// </summary>
public sealed record BackupOutcome(bool Success, bool AlreadyExisted, string? BackupPath, string? Error);

/// <summary>
/// v1.4.1 升级数据防线：在 SettingsService 被构造**之前**，把用户数据原子地快照到
/// %LOCALAPPDATA%\NovaLink Lumen\Data_Backup_v&lt;源版本&gt;\，并支撑 --restore-data-backup 回滚。
///
/// 三条硬约束（违反会静默丢数据）：
/// 1. 备份根必须与数据根**同级**，绝不能落进 LauncherData 子树 —— 卸载器 installer.nsi:98
///    会 RMDir /r ...\LauncherData，备份一旦在里面就跟着被删。
/// 2. 完成标记 BACKUP-INFO.json 最后写：它存在 == 备份完整（与 AppPaths.TryMigrateLegacyData
///    "最后复制 lumen.json" 的约定一致）。
/// 3. 备份失败不阻塞启动，但调用方必须跳过 schema 迁移；失败现场写到数据根的 backup-failed.txt。
/// </summary>
public sealed class UpgradeGuard
{
    /// <summary>备份目录前缀：Data_Backup_v + 检测到的源版本。</summary>
    public const string BackupDirectoryPrefix = "Data_Backup_v";

    /// <summary>备份完成标记文件名（最后写）。</summary>
    public const string BackupInfoFileName = "BACKUP-INFO.json";

    /// <summary>备份里主数据子树的目录名。</summary>
    public const string LauncherTreeName = "LauncherData";

    /// <summary>旧 Electron 数据子树名。</summary>
    public const string ElectronTreeName = "ElectronData";

    /// <summary>旧 Preview 数据子树名。</summary>
    public const string PreviewTreeName = "PreviewData";

    private const string LauncherOrigin = "launcher";
    private const string ElectronOrigin = "electron";
    private const string PreviewOrigin = "preview";
    private const string FailureMarkerFileName = "backup-failed.txt";
    private const int CopyBufferSize = 81920;
    private const int MaxDepth = 32;

    /// <summary>本版本号快捷方式。</summary>
    public static string TargetVersion => VersionSourceDetector.TargetVersion;

    /// <summary>
    /// true 时连 LauncherData\cache 一起备份（对应命令行 --backup-include-cache）。
    /// 默认 false：cache 可重建、可能数 GB，升级不改它的格式。
    /// </summary>
    public bool IncludeCache { get; set; }

    private readonly AppPaths _paths;

    public UpgradeGuard(AppPaths paths)
    {
        _paths = paths ?? throw new ArgumentNullException(nameof(paths));
    }

    // ------------------------------------------------------------------
    // 检测
    // ------------------------------------------------------------------

    /// <summary>
    /// 判定本次启动属于哪种升级场景。该方法在启动主路径上，**任何时候都不抛异常**：
    /// 读不出来就退化成"需要升级且源版本未知"，宁可多备份一次也不能让启动崩。
    /// </summary>
    public UpgradePlan Inspect()
    {
        try
        {
            var probe = VersionSourceDetector.Inspect(_paths.SettingsFile);
            var extraTrees = CollectExtraTrees();

            if (!probe.Exists)
            {
                // 全新安装：LauncherData 没有配置可备份，但旧 Electron / Preview 的数据是用户真数据。
                // AppPaths.TryMigrateLegacyData 只顺手搬了 skins/icons/java/instances/lumen.json，
                // 且失败时没有回滚，所以这里必须再留一份原样快照。
                var legacyVersion = ResolveLegacyVersion(extraTrees) ?? VersionSourceDetector.FallbackSourceVersion;
                return new UpgradePlan(
                    UpgradeKind.FreshInstall,
                    legacyVersion,
                    probe.Schema,
                    BackupRootPath(_paths, legacyVersion),
                    false,
                    extraTrees);
            }

            if (probe.Unreadable)
            {
                // 坏配置也是证据：原样备份，然后交给 SettingsService 走它自己的
                // lumen.broken-&lt;ts&gt;.json 逻辑。命名用 unknown，避免冒充某个真实版本。
                var unknown = VersionSourceDetector.UnknownSourceVersion;
                return new UpgradePlan(
                    UpgradeKind.NeedsUpgrade,
                    null,
                    probe.Schema,
                    BackupRootPath(_paths, unknown),
                    true,
                    extraTrees);
            }

            var sourceVersion = VersionSourceDetector.NormalizeSourceVersion(probe.LastSeenVersion);
            var isCurrent = VersionSourceDetector.Compare(sourceVersion, TargetVersion) >= 0
                            && probe.Schema >= SchemaMigrator.CurrentSchema;
            if (isCurrent)
            {
                return new UpgradePlan(UpgradeKind.AlreadyCurrent, sourceVersion, probe.Schema, null, false, []);
            }

            return new UpgradePlan(
                UpgradeKind.NeedsUpgrade,
                sourceVersion,
                probe.Schema,
                BackupRootPath(_paths, sourceVersion),
                true,
                extraTrees);
        }
        catch (Exception ex)
        {
            AppLog.Error("upgrade", "升级检测失败，按未知源版本处理", ex);
            var unknown = VersionSourceDetector.UnknownSourceVersion;
            return new UpgradePlan(
                UpgradeKind.NeedsUpgrade,
                null,
                0,
                BackupRootPath(_paths, unknown),
                true,
                []);
        }
    }

    /// <summary>收集确实存在的旧数据树（旧 Electron Data、旧 Preview Data）。</summary>
    private static IReadOnlyList<BackupTree> CollectExtraTrees()
    {
        var trees = new List<BackupTree>(2);
        AddIfPresent(trees, ElectronOrigin, AppPaths.ElectronRoot, ElectronTreeName);
        AddIfPresent(trees, PreviewOrigin, AppPaths.LegacyRoot, PreviewTreeName);
        return trees;
    }

    private static void AddIfPresent(List<BackupTree> trees, string origin, string sourcePath, string displayName)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(sourcePath) || !Directory.Exists(sourcePath)) return;
            if (!Directory.EnumerateFileSystemEntries(sourcePath).Any()) return;
            trees.Add(new BackupTree(origin, sourcePath, displayName));
        }
        catch (Exception ex)
        {
            AppLog.Warn("upgrade", "检查旧数据目录失败：" + sourcePath + "：" + ex.Message);
        }
    }

    /// <summary>全新安装时用旧树里最新的 lastSeenVersion 给备份目录命名（本机实测两棵树都是 1.3.0）。</summary>
    private static string? ResolveLegacyVersion(IReadOnlyList<BackupTree> trees)
    {
        string? newest = null;
        foreach (var tree in trees)
        {
            var probe = VersionSourceDetector.Inspect(Path.Combine(tree.SourcePath, "lumen.json"));
            if (!probe.Exists || probe.Unreadable) continue;
            var version = VersionSourceDetector.NormalizeSourceVersion(probe.LastSeenVersion);
            if (version == VersionSourceDetector.UnknownSourceVersion) continue;
            if (newest is null || VersionSourceDetector.Compare(version, newest) > 0) newest = version;
        }
        return newest;
    }

    /// <summary>备份根路径：与数据根同级，名字带源版本（跨 1.3.0 / 1.4.0 迁移时不会互相覆盖）。</summary>
    public static string BackupRootPath(AppPaths paths, string version)
    {
        ArgumentNullException.ThrowIfNull(paths);
        var name = BackupDirectoryPrefix +
                   (string.IsNullOrWhiteSpace(version) ? VersionSourceDetector.UnknownSourceVersion : version.Trim());
        return Path.Combine(BackupParent(paths.Root), name);
    }

    private static string BackupParent(string root)
    {
        var trimmed = Path.TrimEndingDirectorySeparator(root);
        var parent = Path.GetDirectoryName(trimmed);
        // 正常布局：<LOCALAPPDATA>\NovaLink Lumen\LauncherData → 父目录 NovaLink Lumen。
        // 自定义 NOVALINK_DATA 时同理取父目录；驱动器根这种极端情况用 _backup 兜底，
        // 后面的 IsInside 校验会把它判成"在数据根内部"并拒绝，绝不会写到危险位置。
        return string.IsNullOrEmpty(parent) ? Path.Combine(trimmed, "_backup") : parent;
    }

    // ------------------------------------------------------------------
    // 备份
    // ------------------------------------------------------------------

    /// <summary>
    /// 建升级前备份。调用方必须等它 Success 之后才允许调用 SchemaMigrator.Migrate。
    /// AlreadyCurrent / 没有可备份内容时返回 Success=true 且 BackupPath=null。
    /// </summary>
    public Task<BackupOutcome> EnsureBackupAsync(
        UpgradePlan plan,
        IProgress<string>? progress = null,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(plan);
        return Task.Run(() => Backup(plan, progress, ct), ct);
    }

    private BackupOutcome Backup(UpgradePlan plan, IProgress<string>? progress, CancellationToken ct)
    {
        if (plan.Kind == UpgradeKind.AlreadyCurrent)
            return new BackupOutcome(true, false, null, null);

        var trees = new List<BackupTree>(plan.ExtraTrees.Count + 1);
        if (plan.LauncherDataNeedsBackup)
            trees.Add(new BackupTree(LauncherOrigin, _paths.Root, LauncherTreeName));
        trees.AddRange(plan.ExtraTrees);

        if (trees.Count == 0)
        {
            // 新装且没有任何旧数据：没什么可备份的，直接放行。
            AppLog.Info("upgrade", "没有需要备份的数据，跳过升级前备份");
            return new BackupOutcome(true, false, null, null);
        }

        var backupRoot = plan.BackupRoot;
        if (string.IsNullOrWhiteSpace(backupRoot))
            return Fail(null, "升级计划里没有备份目标目录");

        string fullRoot;
        try
        {
            fullRoot = Path.GetFullPath(backupRoot);
        }
        catch (Exception ex)
        {
            return Fail(null, "备份目标路径非法：" + ex.Message);
        }

        if (IsInside(_paths.Root, fullRoot))
            return Fail(fullRoot, "备份目录不能位于数据目录内部（卸载器会连带删除）：" + fullRoot);

        if (Directory.Exists(fullRoot) && HasCompletedBackup(fullRoot))
        {
            AppLog.Info("upgrade", "升级前备份已存在，跳过（幂等）：" + fullRoot);
            CleanupFailureMarker();
            return new BackupOutcome(true, true, fullRoot, null);
        }

        // 先写 .tmp：任何一步失败都不会留下"看起来像完整备份"的目录。
        var tmpRoot = fullRoot + ".tmp";
        var stopwatch = Stopwatch.StartNew();
        try
        {
            progress?.Report("正在准备备份目录…");
            TryDeleteDirectory(tmpRoot);
            Directory.CreateDirectory(tmpRoot);

            var totalFiles = 0;
            var totalBytes = 0L;
            var failures = new List<string>();
            var criticalFailures = new List<string>();
            var excluded = new List<string>();
            var items = new JsonArray();

            foreach (var tree in trees)
            {
                ct.ThrowIfCancellationRequested();
                if (!Directory.Exists(tree.SourcePath))
                {
                    AppLog.Warn("upgrade", "跳过不存在的备份源：" + tree.SourcePath);
                    continue;
                }

                progress?.Report("正在备份 " + tree.DisplayName + "…");
                var target = Path.Combine(tmpRoot, tree.DisplayName);
                var result = CopyTree(tree, target, progress, ct);
                totalFiles += result.Files;
                totalBytes += result.Bytes;
                failures.AddRange(result.Failures);
                criticalFailures.AddRange(result.CriticalFailures);
                excluded.AddRange(result.Excluded);

                items.Add(new JsonObject
                {
                    ["origin"] = tree.Origin,
                    ["displayName"] = tree.DisplayName,
                    ["sourcePath"] = tree.SourcePath,
                    ["files"] = result.Files,
                    ["bytes"] = result.Bytes,
                    ["excluded"] = ToJsonArray(result.Excluded),
                    ["failures"] = ToJsonArray(result.Failures)
                });

                // 关键文件（lumen.json / instances-*.json）失败 = 整次失败：
                // 没有这两类文件，备份就恢复不了账户与实例，继续迁移等于赌。
                if (criticalFailures.Count > 0)
                {
                    throw new InvalidOperationException(
                        "关键文件备份失败：" + string.Join("；", criticalFailures));
                }
            }

            ct.ThrowIfCancellationRequested();

            if (totalFiles == 0)
            {
                // 计划与执行之间旧树被清空：不留一个空备份目录误导用户。
                TryDeleteDirectory(tmpRoot);
                AppLog.Info("upgrade", "没有需要备份的数据，跳过升级前备份：" + fullRoot);
                return new BackupOutcome(true, false, null, null);
            }

            stopwatch.Stop();
            var info = new JsonObject
            {
                ["schema"] = 1,
                ["sourceVersion"] = plan.SourceVersion ?? VersionSourceDetector.UnknownSourceVersion,
                ["sourceSchema"] = plan.SourceSchema,
                ["targetVersion"] = TargetVersion,
                ["createdUtc"] = DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture),
                ["includeCache"] = IncludeCache,
                ["fileCount"] = totalFiles,
                ["totalBytes"] = totalBytes,
                ["durationMs"] = stopwatch.ElapsedMilliseconds,
                ["items"] = items,
                ["excluded"] = ToJsonArray(excluded),
                ["failures"] = ToJsonArray(failures)
            };

            // 完成标记最后写：它存在 == 备份完整。
            progress?.Report("正在写入备份完成标记…");
            WriteJsonNode(Path.Combine(tmpRoot, BackupInfoFileName), info);

            // 目标目录存在但缺少完成标记 = 上次中断的残留（也可能是用户手抄的同名目录）：
            // 改名让路，绝不删除、也绝不覆盖。
            if (Directory.Exists(fullRoot))
            {
                var aside = fullRoot + ".incomplete-" + DateTime.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
                Directory.Move(fullRoot, aside);
                AppLog.Warn("upgrade", "备份目标已存在但缺少 " + BackupInfoFileName + "，已改名保留：" + aside);
            }

            Directory.Move(tmpRoot, fullRoot);
            CleanupFailureMarker();
            AppLog.Info("upgrade", "升级前备份完成：" + fullRoot +
                                    "（" + totalFiles + " 个文件，" + FormatBytes(totalBytes) + "）");
            return new BackupOutcome(true, false, fullRoot, null);
        }
        catch (OperationCanceledException)
        {
            TryDeleteDirectory(tmpRoot);
            AppLog.Warn("upgrade", "升级前备份被取消");
            return new BackupOutcome(false, false, null, "备份已取消");
        }
        catch (Exception ex)
        {
            TryDeleteDirectory(tmpRoot);
            WriteFailureMarker(fullRoot, ex.Message);
            AppLog.Error("upgrade", "升级前备份失败：" + ex.Message, ex);
            return new BackupOutcome(false, false, null, ex.Message);
        }
    }

    private BackupOutcome Fail(string? backupRoot, string message)
    {
        WriteFailureMarker(backupRoot, message);
        AppLog.Warn("upgrade", message);
        return new BackupOutcome(false, false, null, message);
    }

    // ------------------------------------------------------------------
    // 复制（白名单 + 排除 + 原子性）
    // ------------------------------------------------------------------

    /// <summary>一定会备份的目录（用户不可再生资产 + 重装代价高的运行时）。</summary>
    private static readonly string[] BackupDirectoryWhitelist = ["skins", "icons", "logs", "java", "plugins"];

    /// <summary>Chromium profile 里可重建的目录。旧 Electron 数据根里这些占绝大多数体积，永不备份。</summary>
    private static readonly HashSet<string> ChromiumNoiseDirectories = new(StringComparer.OrdinalIgnoreCase)
    {
        "blob_storage",
        "Code Cache",
        "Crashpad",
        "DawnCache",
        "Dictionaries",
        "GPUCache",
        "GrShaderCache",
        "Local Storage",
        "Network",
        "Partitions",
        "Service Worker",
        "Session Storage",
        "ShaderCache",
        "Shared Dictionary",
        "component_crx_cache",
        "extensions_crx_cache"
    };

    private sealed class TreeCopyResult
    {
        public int Files;
        public long Bytes;
        public readonly List<string> Excluded = [];
        public readonly List<string> Failures = [];
        public readonly List<string> CriticalFailures = [];
    }

    /// <summary>复制一棵数据树。目录内容选择规则见 ShouldSkipDirectory / 白名单。</summary>
    private TreeCopyResult CopyTree(BackupTree tree, string targetRoot, IProgress<string>? progress, CancellationToken ct)
    {
        var result = new TreeCopyResult();
        var sourceRoot = new DirectoryInfo(tree.SourcePath);
        if (!sourceRoot.Exists) return result;

        var isLauncher = tree.Origin.Equals(LauncherOrigin, StringComparison.OrdinalIgnoreCase);
        var includeCache = IncludeCache && isLauncher;
        Directory.CreateDirectory(targetRoot);

        foreach (var entry in sourceRoot.EnumerateFileSystemInfos())
        {
            ct.ThrowIfCancellationRequested();

            if (entry is DirectoryInfo directory)
            {
                var name = directory.Name;
                if (IsReparsePoint(directory))
                {
                    result.Excluded.Add(name + "（符号链接/联接）");
                    continue;
                }

                if (IsWhitelistedDirectory(name))
                {
                    CopyDirectoryTree(directory, sourceRoot.FullName, Path.Combine(targetRoot, name), false,
                        tree.DisplayName, result, progress, ct, 1);
                    continue;
                }

                if (!isLauncher)
                {
                    // 旧 Electron / Preview 数据根里除白名单目录外几乎全是 Chromium profile 垃圾
                    // （本机实测 Data 516 个文件 / 194 MB，绝大部分是缓存），一律不进备份。
                    result.Excluded.Add(name + "（旧版数据根的非白名单目录）");
                    continue;
                }

                // 我们自己的数据根：白名单之外的新目录也要带上（未来版本可能新增数据目录），
                // 只排除已知缓存 / 临时目录 / 备份自身。
                if (ShouldSkipDirectory(name, includeCache))
                {
                    result.Excluded.Add(name + (name.Equals("cache", StringComparison.OrdinalIgnoreCase)
                        ? "（缓存，可用 --backup-include-cache 包含）"
                        : "（缓存/临时目录）"));
                    continue;
                }

                CopyDirectoryTree(directory, sourceRoot.FullName, Path.Combine(targetRoot, name), includeCache,
                    tree.DisplayName, result, progress, ct, 1);
                continue;
            }

            if (entry is FileInfo file)
            {
                if (ShouldSkipFile(file.Name))
                {
                    result.Excluded.Add(file.Name + "（临时文件）");
                    continue;
                }
                // 根目录零散文件一律带上（welcome-glass-v2.done、lumen.broken-*.json 之类都是现场）。
                CopyOneFile(file, Path.Combine(targetRoot, file.Name), file.Name,
                    IsCriticalRootFile(file.Name), tree.DisplayName, result, progress);
            }
        }

        return result;
    }

    private static void CopyDirectoryTree(
        DirectoryInfo source,
        string sourceRoot,
        string targetAbsolute,
        bool includeCache,
        string displayName,
        TreeCopyResult result,
        IProgress<string>? progress,
        CancellationToken ct,
        int depth)
    {
        if (depth > MaxDepth)
        {
            result.Excluded.Add(RelativeOf(sourceRoot, source.FullName) + "（层级过深，已跳过）");
            return;
        }

        ct.ThrowIfCancellationRequested();
        Directory.CreateDirectory(targetAbsolute);

        foreach (var entry in source.EnumerateFileSystemInfos())
        {
            ct.ThrowIfCancellationRequested();

            if (entry is DirectoryInfo directory)
            {
                if (IsReparsePoint(directory))
                {
                    result.Excluded.Add(RelativeOf(sourceRoot, directory.FullName) + "（符号链接/联接）");
                    continue;
                }
                if (ShouldSkipDirectory(directory.Name, includeCache))
                {
                    result.Excluded.Add(RelativeOf(sourceRoot, directory.FullName) + "（缓存/临时目录）");
                    continue;
                }
                CopyDirectoryTree(directory, sourceRoot, Path.Combine(targetAbsolute, directory.Name), includeCache,
                    displayName, result, progress, ct, depth + 1);
                continue;
            }

            if (entry is FileInfo file)
            {
                if (ShouldSkipFile(file.Name))
                {
                    result.Excluded.Add(RelativeOf(sourceRoot, file.FullName) + "（临时文件）");
                    continue;
                }
                CopyOneFile(file, Path.Combine(targetAbsolute, file.Name), RelativeOf(sourceRoot, file.FullName),
                    false, displayName, result, progress);
            }
        }
    }

    private static void CopyOneFile(
        FileInfo file,
        string targetPath,
        string relative,
        bool critical,
        string displayName,
        TreeCopyResult result,
        IProgress<string>? progress)
    {
        try
        {
            if (!File.Exists(file.FullName))
            {
                result.Failures.Add(relative + "：复制前源文件已消失");
                if (critical) result.CriticalFailures.Add(relative + "：复制前源文件已消失");
                return;
            }

            var directory = Path.GetDirectoryName(targetPath);
            if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);

            // 源用 FileShare.ReadWrite | Delete 打开：日志与自动保存正在写盘时也能读到。
            using (var input = new FileStream(file.FullName, FileMode.Open, FileAccess.Read,
                       FileShare.ReadWrite | FileShare.Delete, CopyBufferSize, FileOptions.SequentialScan))
            using (var output = new FileStream(targetPath, FileMode.Create, FileAccess.Write,
                       FileShare.None, CopyBufferSize, FileOptions.SequentialScan))
            {
                input.CopyTo(output, CopyBufferSize);
            }

            try { File.SetLastWriteTimeUtc(targetPath, file.LastWriteTimeUtc); } catch { }

            result.Files++;
            result.Bytes += file.Length;
            if (progress is not null && result.Files % 200 == 0)
            {
                progress.Report(displayName + "：已复制 " + result.Files + " 个文件（" + FormatBytes(result.Bytes) + "）");
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            // 单个文件失败不中断：cache 之外的文件缺失不算失败（单个日志被独占锁住很正常）；
            // 但 lumen.json / instances-*.json 失败 = 整次备份失败，调用方因此跳过 schema 迁移。
            var detail = relative + "：" + ex.Message;
            result.Failures.Add(detail);
            if (critical) result.CriticalFailures.Add(detail);
        }
    }

    private static bool IsWhitelistedDirectory(string name) =>
        BackupDirectoryWhitelist.Any(item => name.Equals(item, StringComparison.OrdinalIgnoreCase));

    private static bool ShouldSkipDirectory(string name, bool includeCache)
    {
        if (name.Equals("cache", StringComparison.OrdinalIgnoreCase)) return !includeCache;
        return ChromiumNoiseDirectories.Contains(name)
               || name.StartsWith("Data_Backup_", StringComparison.OrdinalIgnoreCase)
               || name.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase)
               || name.Contains(".rollback-", StringComparison.OrdinalIgnoreCase)
               || name.Contains(".incomplete-", StringComparison.OrdinalIgnoreCase);
    }

    private static bool ShouldSkipFile(string name) => name.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase);

    /// <summary>关键文件：账户（lumen.json）与实例元数据（instances-*.json），丢了就等于升级失败。</summary>
    private static bool IsCriticalRootFile(string name)
    {
        if (name.Equals("lumen.json", StringComparison.OrdinalIgnoreCase)) return true;
        return name.StartsWith("instances-", StringComparison.OrdinalIgnoreCase)
               && name.EndsWith(".json", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsReparsePoint(DirectoryInfo directory) =>
        (directory.Attributes & FileAttributes.ReparsePoint) != 0;

    private static string RelativeOf(string sourceRoot, string fullPath)
    {
        var relative = Path.GetRelativePath(sourceRoot, fullPath);
        return string.IsNullOrEmpty(relative) ? Path.GetFileName(fullPath) : relative;
    }

    // ------------------------------------------------------------------
    // 恢复（--restore-data-backup）
    // ------------------------------------------------------------------

    /// <summary>列出可恢复的备份根（按创建时间从新到旧）。数据根父目录下所有带完成标记的 Data_Backup_v*。</summary>
    public static IReadOnlyList<string> BuildRestorePlan(AppPaths paths)
    {
        ArgumentNullException.ThrowIfNull(paths);
        var candidates = new List<(string Path, DateTime CreatedUtc)>();

        var parent = BackupParent(paths.Root);
        try
        {
            if (!Directory.Exists(parent)) return [];
            foreach (var directory in Directory.EnumerateDirectories(parent, BackupDirectoryPrefix + "*", SearchOption.TopDirectoryOnly))
            {
                // 安全：备份必须在数据根之外（防止自定义根目录把备份放进 LauncherData）。
                if (IsInside(paths.Root, directory)) continue;
                if (!HasCompletedBackup(directory)) continue;
                if (!Directory.Exists(Path.Combine(directory, LauncherTreeName))) continue;
                candidates.Add((directory, ReadCreatedUtc(directory)));
            }
        }
        catch (Exception ex)
        {
            AppLog.Warn("upgrade", "枚举数据备份失败：" + ex.Message);
            return [];
        }

        return candidates
            .OrderByDescending(item => item.CreatedUtc)
            .ThenByDescending(item => item.Path, StringComparer.OrdinalIgnoreCase)
            .Select(item => item.Path)
            .ToList();
    }

    /// <summary>
    /// --restore-data-backup：把当前 LauncherData 整体改名保留（不删），再把备份里的 LauncherData 复制回来。
    /// 调用方负责在成功后提示用户重启（不要在同一次启动里继续跑 UI）。
    /// </summary>
    public static Task<BackupOutcome> RestoreAsync(AppPaths paths, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(paths);
        return Task.Run(() => Restore(paths, ct), ct);
    }

    private static BackupOutcome Restore(AppPaths paths, CancellationToken ct)
    {
        var candidates = BuildRestorePlan(paths);
        if (candidates.Count == 0)
            return new BackupOutcome(false, false, null, "未找到可恢复的数据备份（缺少 " + BackupInfoFileName + "）");

        var backupRoot = candidates[0];
        var source = Path.Combine(backupRoot, LauncherTreeName);
        if (!Directory.Exists(source))
            return new BackupOutcome(false, false, backupRoot, "备份里没有 " + LauncherTreeName + " 子树，无法恢复");

        try
        {
            var root = Path.TrimEndingDirectorySeparator(paths.Root);

            // 先把当前数据整体改名（不删）：任何一步失败，用户都还能把 .rollback-<ts> 改回来。
            if (Directory.Exists(root))
            {
                var aside = root + ".rollback-" + DateTime.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
                Directory.Move(root, aside);
                AppLog.Info("upgrade", "当前数据已改名保留：" + aside);
            }
            Directory.CreateDirectory(root);

            var result = new TreeCopyResult();
            CopyDirectoryTree(new DirectoryInfo(source), source, root, true, LauncherTreeName, result, null, ct, 1);
            if (result.CriticalFailures.Count > 0)
            {
                return new BackupOutcome(false, false, backupRoot,
                    "恢复关键文件失败：" + string.Join("；", result.CriticalFailures));
            }

            AppLog.Info("upgrade", "已从备份恢复数据：" + backupRoot + " → " + root +
                                    "（" + result.Files + " 个文件）");
            return new BackupOutcome(true, false, backupRoot, null);
        }
        catch (OperationCanceledException)
        {
            return new BackupOutcome(false, false, backupRoot, "恢复已取消");
        }
        catch (Exception ex)
        {
            AppLog.Error("upgrade", "从备份恢复失败", ex);
            return new BackupOutcome(false, false, backupRoot, ex.Message);
        }
    }

    // ------------------------------------------------------------------
    // 完成标记与工具
    // ------------------------------------------------------------------

    /// <summary>备份目录是否完整（存在且 BACKUP-INFO.json 可解析）。这是唯一可信的"备份可用"判据。</summary>
    public static bool HasCompletedBackup(string backupRoot)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(backupRoot)) return false;
            var marker = Path.Combine(backupRoot, BackupInfoFileName);
            if (!File.Exists(marker)) return false;
            using var document = JsonDocument.Parse(File.ReadAllText(marker));
            return document.RootElement.ValueKind == JsonValueKind.Object
                   && document.RootElement.TryGetProperty("schema", out var schema)
                   && schema.ValueKind == JsonValueKind.Number;
        }
        catch
        {
            return false;
        }
    }

    private static DateTime ReadCreatedUtc(string backupRoot)
    {
        try
        {
            var marker = Path.Combine(backupRoot, BackupInfoFileName);
            using var document = JsonDocument.Parse(File.ReadAllText(marker));
            if (document.RootElement.TryGetProperty("createdUtc", out var created) &&
                created.ValueKind == JsonValueKind.String &&
                DateTime.TryParse(created.GetString(), CultureInfo.InvariantCulture,
                    DateTimeStyles.RoundtripKind, out var parsed))
            {
                return parsed.ToUniversalTime();
            }
        }
        catch
        {
            // 标记读不出来时退回目录创建时间，只影响排序，不影响能否恢复。
        }

        try { return Directory.GetCreationTimeUtc(backupRoot); }
        catch { return DateTime.MinValue; }
    }

    /// <summary>备份失败现场：数据根下的 backup-failed.txt（启动首帧提示读它）。</summary>
    private void WriteFailureMarker(string? backupRoot, string message)
    {
        try
        {
            var text = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture) +
                       " 升级前数据备份失败：" + message + Environment.NewLine +
                       "备份目标：" + (backupRoot ?? "(未确定)") + Environment.NewLine +
                       "本次已跳过 schema 迁移（数据安全优先：没有备份就不动数据）。" + Environment.NewLine;
            File.WriteAllText(Path.Combine(_paths.Root, FailureMarkerFileName), text, new UTF8Encoding(false));
        }
        catch (Exception ex)
        {
            AppLog.Warn("upgrade", "写入备份失败标记失败：" + ex.Message);
        }
    }

    private void CleanupFailureMarker()
    {
        try
        {
            var marker = Path.Combine(_paths.Root, FailureMarkerFileName);
            if (File.Exists(marker)) File.Delete(marker);
        }
        catch
        {
            // 清不掉不影响任何行为，下次成功备份时还会再清一次。
        }
    }

    private static bool IsInside(string root, string candidate)
    {
        try
        {
            var fullRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
            var fullCandidate = Path.TrimEndingDirectorySeparator(Path.GetFullPath(candidate));
            return fullCandidate.Equals(fullRoot, StringComparison.OrdinalIgnoreCase)
                   || fullCandidate.StartsWith(fullRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            // 路径非法时按"包含"处理：宁可拒绝对数据目录做任何事。
            return true;
        }
    }

    private static void TryDeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path)) Directory.Delete(path, recursive: true);
        }
        catch (Exception ex)
        {
            AppLog.Warn("upgrade", "清理临时目录失败：" + path + "：" + ex.Message);
        }
    }

    private static void WriteJsonNode(string path, JsonNode node)
    {
        var json = node.ToJsonString(JsonDefaults.Options);
        File.WriteAllText(path, json, new UTF8Encoding(false));
    }

    private static JsonArray ToJsonArray(IEnumerable<string> values)
    {
        var array = new JsonArray();
        foreach (var value in values) array.Add(value);
        return array;
    }

    private static string FormatBytes(long bytes) => bytes switch
    {
        >= 1L << 30 => (bytes / (double)(1L << 30)).ToString("0.##", CultureInfo.InvariantCulture) + " GB",
        >= 1L << 20 => (bytes / (double)(1L << 20)).ToString("0.##", CultureInfo.InvariantCulture) + " MB",
        >= 1L << 10 => (bytes / (double)(1L << 10)).ToString("0.##", CultureInfo.InvariantCulture) + " KB",
        _ => bytes.ToString(CultureInfo.InvariantCulture) + " B"
    };
}
