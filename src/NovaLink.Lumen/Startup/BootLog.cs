using System;
using System.IO;
using System.Text;

namespace NovaLink.Lumen.Startup;

/// <summary>
/// 启动阶段的日志。AppLog 要等 Settings 与数据目录就绪才能工作，所以在那之前（包括崩溃现场）
/// 都走这个文件，保证"启动失败时一定有日志可看"。
/// </summary>
public static class BootLog
{
    private const long MaxBytes = 1024 * 1024;
    private static readonly object Gate = new();

    /// <summary>日志目录，默认 LauncherData\logs；解析到自定义数据根后会重定向。</summary>
    public static string LogDirectory { get; private set; } = DefaultLogDirectory();

    /// <summary>启动日志文件路径。</summary>
    public static string FilePath => Path.Combine(LogDirectory, "boot.log");

    /// <summary>把启动日志重定向到指定数据根目录下的 logs。</summary>
    public static void Configure(string dataRoot)
    {
        if (string.IsNullOrWhiteSpace(dataRoot)) return;
        try { LogDirectory = Path.Combine(dataRoot, "logs"); }
        catch { /* 保持默认路径 */ }
    }

    /// <summary>记录一条普通信息。</summary>
    public static void Info(string message) => Write("INFO", message, null);

    /// <summary>记录一条警告。</summary>
    public static void Warn(string message) => Write("WARN", message, null);

    /// <summary>记录一条错误。</summary>
    public static void Error(string message, Exception? exception = null) => Write("ERROR", message, exception);

    private static void Write(string level, string message, Exception? exception)
    {
        try
        {
            lock (Gate)
            {
                Directory.CreateDirectory(LogDirectory);
                var file = new FileInfo(FilePath);
                if (file.Exists && file.Length > MaxBytes)
                {
                    var rolled = Path.Combine(LogDirectory, "boot.1.log");
                    try
                    {
                        if (File.Exists(rolled)) File.Delete(rolled);
                        File.Move(FilePath, rolled);
                    }
                    catch { /* 滚动失败不阻塞启动 */ }
                }

                var builder = new StringBuilder();
                builder.Append(DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff"))
                       .Append(" [").Append(level).Append("] ").Append(message);
                if (exception is not null)
                    builder.Append(Environment.NewLine).Append(exception);
                builder.AppendLine();
                File.AppendAllText(FilePath, builder.ToString(), Encoding.UTF8);
            }
        }
        catch { /* 日志本身失败时绝不抛 */ }
    }

    private static string DefaultLogDirectory()
    {
        try
        {
            return Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "NovaLink Lumen", "LauncherData", "logs");
        }
        catch
        {
            return Path.Combine(Path.GetTempPath(), "NovaLinkLumen-logs");
        }
    }
}
