namespace NovaLink.Lumen.Services.Plugins;

/// <summary>
/// manifest 里 minLauncher / maxLauncher 的简单版本比较（最多四段数字，忽略 -preview 之类后缀）。
/// 刻意不引入 NuGet 版本库：插件兼容判断只需要"够不够新"，不需要 SemVer 全集语义。
/// </summary>
public static class PluginVersion
{
    /// <summary>解析 1 ~ 4 段数字版本；失败返回 false。允许首位 v 前缀。</summary>
    public static bool TryParse(string? text, out Version version)
    {
        version = new Version(0, 0, 0, 0);
        if (string.IsNullOrWhiteSpace(text)) return false;

        var span = text.Trim();
        if (span.StartsWith("v", StringComparison.OrdinalIgnoreCase)) span = span[1..];

        var parts = span.Split('.', StringSplitOptions.TrimEntries);
        if (parts.Length is 0 or > 4) return false;

        var numbers = new int[4];
        for (var i = 0; i < parts.Length; i++)
        {
            // 允许 1.4.1-beta 这类写法：取 '-' 之前的部分。
            var piece = parts[i];
            var dash = piece.IndexOfAny(['-', '+', ' ']);
            if (dash >= 0) piece = piece[..dash];
            if (piece.Length == 0 || !int.TryParse(piece, out var value) || value < 0) return false;
            numbers[i] = value;
        }

        version = new Version(numbers[0], numbers[1], numbers[2], numbers[3]);
        return true;
    }

    /// <summary>
    /// 判断插件声明的启动器版本区间是否包含当前启动器版本。
    /// 任一参数缺失或无法解析时一律视为兼容 —— 兼容性判断宁可放过，也不能因为版本字符串古怪就禁用全部插件。
    /// </summary>
    public static bool IsCompatible(string? minLauncher, string? maxLauncher, string? launcherVersion)
    {
        if (!TryParse(launcherVersion, out var current)) return true;

        if (TryParse(minLauncher, out var min) && current < min) return false;
        if (TryParse(maxLauncher, out var max) && current > max) return false;
        return true;
    }

    /// <summary>把版本对象格式化成 4 段文本（日志用）。</summary>
    public static string Describe(string? text) => TryParse(text, out var v) ? v.ToString(4) : text ?? "null";
}
