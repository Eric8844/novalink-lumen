namespace NovaLink.Lumen.Services;

public static class AppLog
{
    private static readonly object Gate = new();
    private static string? _directory;

    public static void Initialize(string directory)
    {
        _directory = directory;
        try { Directory.CreateDirectory(directory); } catch { }
    }

    public static void Info(string area, string message) => Write("INFO", area, message, null);
    public static void Warn(string area, string message) => Write("WARN", area, message, null);
    public static void Error(string area, string message, Exception? ex = null) => Write("ERROR", area, message, ex);

    private static void Write(string level, string area, string message, Exception? ex)
    {
        var line = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} [{level}] [{area}] {message}";
        if (ex is not null) line += Environment.NewLine + ex;
        System.Diagnostics.Debug.WriteLine(line);
        if (_directory is null) return;
        try
        {
            lock (Gate)
            {
                File.AppendAllText(Path.Combine(_directory, "lumen-" + DateTime.Now.ToString("yyyyMMdd") + ".log"), line + Environment.NewLine);
            }
        }
        catch { }
    }
}
