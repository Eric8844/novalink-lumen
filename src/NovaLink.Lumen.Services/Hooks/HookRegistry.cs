using NovaLink.Lumen.Core.Plugins;

namespace NovaLink.Lumen.Services.Hooks;

/// <summary>
/// 宿主向插件暴露的进程内钩子槽（v1.4.1 设计文档 B 段）。
/// 设计约定：
/// 1) 每个槽都是可空委托，未注册 == 未启用；调用方必须按"没有插件"的路径正常工作。
/// 2) 钩子只能做"补充/改写"，不能改变宿主流程的控制权（插件抛异常一律回落到宿主实现）。
/// 3) 静态状态在整个进程内共享；App 退出时调用 <see cref="Reset"/> 清理，避免热重载残留。
/// </summary>
public static class HookRegistry
{
    /// <summary>下载地址解析：插件可把官方地址换成镜像/加速地址；返回 null 表示不接管。</summary>
    public static Func<DownloadRequest, CancellationToken, Task<DownloadSource?>>? DownloadResolve { get; set; }

    /// <summary>文本替换：宿主在拼接可展示文本时调用，插件可改写（如本地化/术语替换）。</summary>
    public static Func<string, string>? TextReplace { get; set; }

    /// <summary>通知过滤：返回 false 表示该通知不展示。</summary>
    public static Func<string, string, bool>? NotificationFilter { get; set; }

    /// <summary>启动完成事件（所有插件 Initialize 之后触发）。</summary>
    public static event EventHandler<PluginEventArgs>? AppStartup;

    /// <summary>即将退出事件（插件 Shutdown 之前触发）。</summary>
    public static event EventHandler<PluginEventArgs>? AppShutdown;

    /// <summary>是否存在已注册的下载解析钩子。</summary>
    public static bool HasDownloadResolve => DownloadResolve is not null;

    /// <summary>调用下载解析钩子；无插件或插件失败时返回 null（宿主继续走官方地址）。</summary>
    public static async Task<DownloadSource?> ResolveDownloadAsync(DownloadRequest request, CancellationToken cancellationToken = default)
    {
        if (request is null) throw new ArgumentNullException(nameof(request));

        var hook = DownloadResolve;
        if (hook is null) return null;

        try
        {
            return await hook(request, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // 取消语义必须原样传给宿主（可能是用户点了取消）。
            throw;
        }
        catch
        {
            // 插件钩子失败绝不影响下载主流程。
            return null;
        }
    }

    /// <summary>应用文本替换钩子；无插件时原样返回。</summary>
    public static string ApplyTextReplace(string text)
    {
        if (string.IsNullOrEmpty(text)) return text;

        var hook = TextReplace;
        if (hook is null) return text;

        try
        {
            return hook(text) ?? text;
        }
        catch
        {
            return text;
        }
    }

    /// <summary>询问是否展示通知；无插件时默认展示。</summary>
    public static bool ShouldShowNotification(string title, string message)
    {
        var hook = NotificationFilter;
        if (hook is null) return true;

        try
        {
            return hook(title ?? "", message ?? "");
        }
        catch
        {
            return true;
        }
    }

    /// <summary>触发启动事件；单个订阅者异常不影响其它订阅者。</summary>
    public static void RaiseAppStartup(IReadOnlyDictionary<string, object?>? data = null)
        => Raise(AppStartup, nameof(PluginEvent.AppStartup), data);

    /// <summary>触发退出事件。</summary>
    public static void RaiseAppShutdown(IReadOnlyDictionary<string, object?>? data = null)
        => Raise(AppShutdown, nameof(PluginEvent.AppShutdown), data);

    /// <summary>清空所有钩子与事件订阅（App 退出/热重载时调用）。</summary>
    public static void Reset()
    {
        DownloadResolve = null;
        TextReplace = null;
        NotificationFilter = null;
        AppStartup = null;
        AppShutdown = null;
    }

    private static void Raise(EventHandler<PluginEventArgs>? handler, string name, IReadOnlyDictionary<string, object?>? data)
    {
        if (handler is null) return;

        var args = new PluginEventArgs(name, data);
        foreach (var callback in handler.GetInvocationList().Cast<EventHandler<PluginEventArgs>>())
        {
            try
            {
                callback.Invoke(null, args);
            }
            catch
            {
                // 订阅者异常隔离：钩子的失败不能让启动/退出流程中断。
            }
        }
    }
}

/// <summary>下载请求（宿主 → 插件）。</summary>
public sealed class DownloadRequest
{
    public DownloadRequest(
        string url,
        string? sha1 = null,
        string? fileName = null,
        long? size = null,
        string? purpose = null,
        IReadOnlyDictionary<string, string>? headers = null)
    {
        Url = url ?? throw new ArgumentNullException(nameof(url));
        Sha1 = sha1;
        FileName = fileName;
        Size = size;
        Purpose = purpose;
        Headers = headers;
    }

    /// <summary>原始下载地址（官方源）。</summary>
    public string Url { get; }

    /// <summary>期望的 SHA1（宿主完整性校验值）；插件镜像必须返回同样内容。</summary>
    public string? Sha1 { get; }

    /// <summary>建议保存的文件名。</summary>
    public string? FileName { get; }

    /// <summary>期望字节数（未知为 null）。</summary>
    public long? Size { get; }

    /// <summary>用途标识，如 "java" / "game" / "assets" / "modloader"。</summary>
    public string? Purpose { get; }

    /// <summary>宿主原本要带的请求头（插件可参考，但不得依赖）。</summary>
    public IReadOnlyDictionary<string, string>? Headers { get; }
}

/// <summary>下载地址解析结果（插件 → 宿主）；返回 null 表示不接管。</summary>
public sealed class DownloadSource
{
    public DownloadSource(
        string url,
        IReadOnlyDictionary<string, string>? headers = null,
        string? sha1 = null,
        string? displayName = null)
    {
        Url = url ?? throw new ArgumentNullException(nameof(url));
        Headers = headers;
        Sha1 = sha1;
        DisplayName = displayName;
    }

    /// <summary>替换后的下载地址。</summary>
    public string Url { get; }

    /// <summary>附加请求头（可为 null）。</summary>
    public IReadOnlyDictionary<string, string>? Headers { get; }

    /// <summary>插件声明的 SHA1（宿主仍以自身校验值为准）。</summary>
    public string? Sha1 { get; }

    /// <summary>展示给用户的来源名（如"镜像 A"）。</summary>
    public string? DisplayName { get; }
}
