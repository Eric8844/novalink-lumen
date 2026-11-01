using System;
using System.Diagnostics;
using System.IO;

namespace NovaLink.Lumen.Startup;

/// <summary>重启自身进程。用于"一键重试"与"以安全模式启动"。</summary>
public static class LauncherRestart
{
    /// <summary>启动一个新的实例；成功返回 true。当前进程由调用方退出。</summary>
    public static bool Restart(string extraArguments)
    {
        try
        {
            var exe = ResolveExecutable();
            if (exe is null)
            {
                BootLog.Error("无法定位启动器可执行文件，重试失败。");
                return false;
            }

            var info = new ProcessStartInfo(exe)
            {
                UseShellExecute = true,
                WorkingDirectory = Path.GetDirectoryName(exe) ?? AppContext.BaseDirectory
            };
            if (!string.IsNullOrWhiteSpace(extraArguments)) info.Arguments = extraArguments;
            Process.Start(info);
            BootLog.Info("已请求重启启动器，参数：" + (info.Arguments.Length == 0 ? "(无)" : info.Arguments));
            return true;
        }
        catch (Exception ex)
        {
            BootLog.Error("重启启动器失败。", ex);
            return false;
        }
    }

    private static string? ResolveExecutable()
    {
        var process = Environment.ProcessPath;
        if (!string.IsNullOrWhiteSpace(process) && File.Exists(process) &&
            process.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
            return process;

        var candidate = Path.Combine(AppContext.BaseDirectory, "NovaLink.Lumen.exe");
        return File.Exists(candidate) ? candidate : null;
    }
}
