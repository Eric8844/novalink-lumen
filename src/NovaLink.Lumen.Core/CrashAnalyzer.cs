using System.Text.RegularExpressions;

namespace NovaLink.Lumen.Core;

public sealed record CrashDiagnosis(string Title, string Detail, string Suggestion);

public static class CrashAnalyzer
{
    public static CrashDiagnosis? Analyze(string log, int? exitCode = null)
    {
        var text = log ?? "";
        if (Regex.IsMatch(text, "OutOfMemoryError|Java heap space", RegexOptions.IgnoreCase))
            return new("内存不足", "游戏在启动或加载世界时耗尽了 Java 堆内存。", "在版本设置中提高内存，或减少同时加载的模组。");
        if (Regex.IsMatch(text, "UnsupportedClassVersionError", RegexOptions.IgnoreCase))
            return new("Java 版本不匹配", "当前 Java 版本低于该 Minecraft 或模组要求。", "在设置中切换到 Java 21 或更高版本。");
        if (Regex.IsMatch(text, "Missing or unsupported mandatory dependencies|ModResolutionException|Incompatible mod set", RegexOptions.IgnoreCase))
            return new("模组依赖不完整", "缺少前置模组，或模组之间版本不兼容。", "检查缺失依赖，或暂时移出最近添加的模组。");
        if (Regex.IsMatch(text, "Failed to locate library|Failed to load a library|UnsatisfiedLinkError|Could not initialize class org[.]lwjgl", RegexOptions.IgnoreCase))
            return new("本地运行库缺失", "游戏启动时没有找到 LWJGL 等原生库，和模组兼容性无关。", "重新启动一次让启动器补全原生库；仍然失败时检查 Java 架构是否为 64 位。");
        if (Regex.IsMatch(text, "NoSuchMethodError|NoClassDefFoundError|ClassNotFoundException", RegexOptions.IgnoreCase))
            return new("模组与游戏版本不兼容", "某个模组调用了当前版本不存在的类或方法。", "确认模组支持当前 Minecraft 与加载器版本。");
        if (Regex.IsMatch(text, "Pixel format not accelerated|GLFW error 65542|Failed to create GLFW window|OpenGL", RegexOptions.IgnoreCase))
            return new("显卡驱动或 OpenGL 不可用", "游戏无法创建图形窗口。", "更新显卡驱动，或在远程桌面/虚拟机中使用软渲染。");
        if (exitCode is int code && (code == -1073741819 || code == -1073740791))
            return new("游戏进程异常退出", "游戏可能因显卡驱动、模组或本地库崩溃。", "查看日志末尾的 Caused by，或暂时移出最近添加的模组。");
        return null;
    }
}
