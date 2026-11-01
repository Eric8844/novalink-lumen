using System.Collections.Generic;

namespace NovaLink.Lumen.Startup;

/// <summary>
/// 一次启动过程中的共享状态。启动链（App → LumenRuntime → 插件 → Shell）通过它传递
/// "发生了什么、要不要提示用户"，避免让 UI 层反向依赖底层服务。
/// </summary>
public static class BootState
{
    /// <summary>进入安全模式的原因（给界面用的人话，不含异常堆栈）。</summary>
    public static string? SafeModeReason { get; set; }

    /// <summary>本次启动是否创建了升级备份。</summary>
    public static bool UpgradeBackupCreated { get; set; }

    /// <summary>升级备份落在哪个目录。</summary>
    public static string? UpgradeBackupPath { get; set; }

    /// <summary>启动过程中需要提示用户的非致命问题（人话）。</summary>
    public static readonly List<string> Notices = new();

    /// <summary>重置（单元调试与重启前调用）。</summary>
    public static void Reset()
    {
        SafeModeReason = null;
        UpgradeBackupCreated = false;
        UpgradeBackupPath = null;
        Notices.Clear();
    }
}
