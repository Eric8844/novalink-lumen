using System;
using System.IO;
using System.Reflection;
using System.Text.Json;
using System.Threading.Tasks;

namespace NovaLink.Lumen.Startup;

/// <summary>给玩家看的失败描述。只包含人话，绝不带异常类型、堆栈或文件路径。</summary>
public sealed record FriendlyFailure(string Title, string Message, string Hint, bool RecommendRestore);

/// <summary>把启动异常翻译成人话，并给出"重试 / 安全模式 / 恢复备份"三条出路。</summary>
public static class StartupFailure
{
    /// <summary>翻译异常；识别不了的异常给通用文案。</summary>
    public static FriendlyFailure Describe(Exception exception)
    {
        var root = Unwrap(exception);

        if (root is UnauthorizedAccessException)
        {
            return new FriendlyFailure(
                "无法访问数据目录",
                "启动器没有权限读写自己的数据目录，可能是安全软件拦截，或文件夹权限被修改。",
                "可以尝试以管理员身份运行；如果之前的数据还在，也可以从最近的备份恢复。",
                true);
        }

        if (root is JsonException)
        {
            return new FriendlyFailure(
                "配置文件已损坏",
                "启动器的配置文件（lumen.json）内容不完整，无法读取。",
                "可以从最近一次的升级备份恢复；恢复不会影响游戏本体与存档。",
                true);
        }

        if (root is IOException || root is DirectoryNotFoundException || root is FileNotFoundException)
        {
            return new FriendlyFailure(
                "读写文件时出错",
                "启动器在读写本地文件时遇到了问题，通常是磁盘空间不足、文件被占用或安全软件锁定。",
                "关闭正在占用文件的程序后重试；必要时可以从备份恢复。",
                true);
        }

        if (root is OutOfMemoryException)
        {
            return new FriendlyFailure(
                "内存不足",
                "启动器没能申请到足够的内存，可能同时打开了太多程序。",
                "关闭一些程序后重试。",
                false);
        }

        if (root is TypeInitializationException || root is System.Runtime.InteropServices.COMException ||
            root is BadImageFormatException)
        {
            return new FriendlyFailure(
                "界面组件加载失败",
                "启动器的界面组件没能正确加载，通常是安装不完整或运行库缺失导致的。",
                "重新运行安装包修复一次通常可以解决；也可以先以安全模式启动排查。",
                false);
        }

        if (root is InvalidOperationException && root.Message.Contains("主窗口尚未准备好", StringComparison.Ordinal))
        {
            return new FriendlyFailure(
                "界面还没准备好",
                "启动器在界面初始化完成之前就尝试显示提示，这通常是一次性的启动顺序问题。",
                "重试一次通常即可恢复。",
                false);
        }

        return new FriendlyFailure(
            "启动时遇到问题",
            "启动器在启动过程中遇到了一次意外情况，已经把它记录到日志里。",
            "可以先重试；如果反复出现，请用安全模式启动，或从最近的备份恢复数据。",
            true);
    }

    /// <summary>取最内层异常，跳过包装器。</summary>
    public static Exception Unwrap(Exception exception)
    {
        var current = exception;
        for (var guard = 0; guard < 8 && current is not null; guard++)
        {
            if (current is TypeInitializationException { InnerException: not null } t) { current = t.InnerException!; continue; }
            if (current is AggregateException { InnerExceptions.Count: > 0 } a) { current = a.InnerExceptions[0]; continue; }
            if (current is TargetInvocationException { InnerException: not null } ti) { current = ti.InnerException!; continue; }
            break;
        }
        return current ?? exception;
    }

    /// <summary>把异常写进日志（日志里保留完整细节，界面不显示）。</summary>
    public static void Log(string stage, Exception exception)
    {
        BootLog.Error("启动失败（" + stage + "）。", exception);
    }

    /// <summary>给界面用的日志目录提示。</summary>
    public static string LogHint() => BootLog.LogDirectory;

    /// <summary>异步等待用户选择，避免阻塞 UI 线程。</summary>
    public static Task<RecoveryChoice> AskUserAsync(FriendlyFailure failure)
        => RecoveryDialog.ShowAsync(failure.Title, failure.Message, failure.Hint);
}
