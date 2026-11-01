using System;
using System.Collections.Generic;
using System.IO;

namespace NovaLink.Lumen.Startup;

/// <summary>
/// 启动参数与环境变量。全仓只在这里解析命令行；其余服务只读取解析结果，不再直接看 args。
/// </summary>
public sealed class StartupOptions
{
    /// <summary>禁用全部插件（含脚本插件），但保留数据与更新功能。</summary>
    public const string FlagSafeMode = "--safe-mode";

    /// <summary>只禁用插件、其余行为不变；用于排查"是不是插件引起的"。</summary>
    public const string FlagNoPlugins = "--no-plugins";

    /// <summary>开发者模式：放宽脚本沙箱、允许热重载脚本、把插件加载细节写进日志。</summary>
    public const string FlagDevMode = "--dev-mode";

    /// <summary>启动时用最近一次升级备份覆盖当前数据目录，然后正常启动。</summary>
    public const string FlagRestoreDataBackup = "--restore-data-backup";

    /// <summary>升级备份时把 cache 目录也带上（默认排除）。</summary>
    public const string FlagBackupIncludeCache = "--backup-include-cache";

    /// <summary>临时指定数据根目录，便于开发与排障。</summary>
    public const string FlagDataRoot = "--data-root";

    /// <summary>本次启动开启实验性功能：启动器插件（对应设置里的同名开关，只对本次进程生效）。</summary>
    public const string FlagExperimentalPlugins = "--experimental-plugins";

    /// <summary>本进程是否以安全模式运行（禁用插件）。</summary>
    public bool SafeMode { get; init; }

    /// <summary>本进程是否禁用插件。</summary>
    public bool NoPlugins { get; init; }

    /// <summary>本进程是否处于开发者模式。</summary>
    public bool DevMode { get; init; }

    /// <summary>本进程是否要在启动前回滚数据。</summary>
    public bool RestoreDataBackup { get; init; }

    /// <summary>升级备份是否包含 cache。</summary>
    public bool BackupIncludeCache { get; init; }

    /// <summary>命令行指定的数据根目录（已展开环境变量），没有则为 null。</summary>
    public string? DataRoot { get; init; }

    /// <summary>本次启动是否开启实验性功能「启动器插件」（设置里的开关打开时也算开启）。</summary>
    public bool ExperimentalPlugins { get; init; }

    /// <summary>是否完全禁止加载插件。</summary>
    public bool PluginsDisabled => SafeMode || NoPlugins;

    /// <summary>是否处于任何一种诊断/开发者态（界面会因此多显示提示条）。</summary>
    public bool IsDiagnostic => SafeMode || NoPlugins || DevMode;

    /// <summary>当前进程的启动参数。</summary>
    public static StartupOptions Current { get; private set; } = new();

    /// <summary>由 <see cref="App"/> 在启动最开始调用一次。</summary>
    public static void Initialize(StartupOptions options) => Current = options;

    /// <summary>从 <see cref="Environment.GetCommandLineArgs"/> 与同名环境变量解析。</summary>
    public static StartupOptions FromEnvironment()
    {
        string[] args;
        try { args = Environment.GetCommandLineArgs(); }
        catch { args = Array.Empty<string>(); }
        return Parse(args);
    }

    /// <summary>解析参数列表（第一个元素是 exe 路径，会自动跳过）。未知参数忽略。</summary>
    public static StartupOptions Parse(IReadOnlyList<string> args)
    {
        var safe = false;
        var noPlugins = false;
        var dev = false;
        var restore = false;
        var includeCache = false;
        var experimentalPlugins = false;
        string? dataRoot = null;

        for (var i = 0; i < args.Count; i++)
        {
            var raw = args[i] ?? string.Empty;
            if (raw.Length == 0 || (i == 0 && !raw.StartsWith('-') && !raw.StartsWith('/'))) continue;
            var arg = raw.Trim();
            switch (arg.ToLowerInvariant())
            {
                case FlagSafeMode:
                    safe = true;
                    break;
                case FlagNoPlugins:
                    noPlugins = true;
                    break;
                case FlagDevMode:
                    dev = true;
                    break;
                case FlagRestoreDataBackup:
                    restore = true;
                    break;
                case FlagBackupIncludeCache:
                    includeCache = true;
                    break;
                case FlagExperimentalPlugins:
                    experimentalPlugins = true;
                    break;
                case FlagDataRoot:
                    if (i + 1 < args.Count && !string.IsNullOrWhiteSpace(args[i + 1]))
                    {
                        dataRoot = args[i + 1];
                        i++;
                    }
                    break;
                default:
                    if (arg.StartsWith(FlagDataRoot + "=", StringComparison.OrdinalIgnoreCase))
                        dataRoot = arg[(FlagDataRoot.Length + 1)..];
                    break;
            }
        }

        if (IsTruthy("NOVALINK_SAFE_MODE")) safe = true;
        if (IsTruthy("NOVALINK_NO_PLUGINS")) noPlugins = true;
        if (IsTruthy("NOVALINK_DEV_MODE")) dev = true;
        if (IsTruthy("NOVALINK_RESTORE_DATA_BACKUP")) restore = true;
        if (IsTruthy("NOVALINK_EXPERIMENTAL_PLUGINS")) experimentalPlugins = true;
        if (string.IsNullOrWhiteSpace(dataRoot)) dataRoot = Environment.GetEnvironmentVariable("NOVALINK_DATA_ROOT");

        if (!string.IsNullOrWhiteSpace(dataRoot))
        {
            try { dataRoot = Path.GetFullPath(Environment.ExpandEnvironmentVariables(dataRoot.Trim())); }
            catch { dataRoot = null; }
        }

        return new StartupOptions
        {
            SafeMode = safe,
            NoPlugins = noPlugins,
            DevMode = dev,
            RestoreDataBackup = restore,
            BackupIncludeCache = includeCache,
            ExperimentalPlugins = experimentalPlugins,
            DataRoot = string.IsNullOrWhiteSpace(dataRoot) ? null : dataRoot
        };
    }

    /// <summary>如果指定了命令行数据目录，就沿用 AppPaths 已经支持的 NOVALINK_DATA 环境变量。</summary>
    public void ApplyDataRootEnvironment()
    {
        if (DataRoot is null) return;
        try { Environment.SetEnvironmentVariable("NOVALINK_DATA", DataRoot); } catch { /* 只影响本次进程 */ }
    }

    /// <summary>生成重启用的命令行（不含 exe 路径）。</summary>
    public string BuildArguments(bool forceSafeMode, bool restoreDataBackup = false)
    {
        var parts = new List<string>();
        // --experimental-plugins 是一次性开关：不写进重启参数，避免用户关掉开关后又被上一次启动带回开启态。
        if (forceSafeMode || SafeMode) parts.Add(FlagSafeMode);
        else if (NoPlugins) parts.Add(FlagNoPlugins);
        // 设置页「从备份恢复」用它重启一次：恢复必须在服务初始化之前发生。
        if (restoreDataBackup && !RestoreDataBackup) parts.Add(FlagRestoreDataBackup);
        if (DevMode) parts.Add(FlagDevMode);
        if (BackupIncludeCache) parts.Add(FlagBackupIncludeCache);
        if (DataRoot is not null) parts.Add(FlagDataRoot + " \"" + DataRoot + "\"");
        return string.Join(' ', parts);
    }

    private static bool IsTruthy(string name)
    {
        var value = Environment.GetEnvironmentVariable(name);
        if (string.IsNullOrWhiteSpace(value)) return false;
        return value.Trim() switch
        {
            "1" => true,
            "true" => true,
            "TRUE" => true,
            "True" => true,
            "yes" => true,
            "on" => true,
            _ => false
        };
    }
}
