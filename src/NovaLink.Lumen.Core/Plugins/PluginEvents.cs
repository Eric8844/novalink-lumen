namespace NovaLink.Lumen.Core.Plugins;

/// <summary>宿主广播给插件的事件类型。P0 只提供定义与订阅管道，具体发布点由 P2/P3 接入。</summary>
public enum PluginEvent
{
    /// <summary>启动器启动完成。</summary>
    AppStartup,

    /// <summary>启动器即将退出。</summary>
    AppShutdown,

    /// <summary>实例启动之前。</summary>
    LaunchPre,

    /// <summary>实例启动之后（进程已创建）。</summary>
    LaunchPost,

    /// <summary>下载源解析（P2 接入 HookRegistry.DownloadResolve）。</summary>
    DownloadResolve,

    /// <summary>文本渲染前的替换（P2 接入 HookRegistry.TextReplace）。</summary>
    TextReplace
}

/// <summary>
/// 事件载荷。Name 与 event 的枚举名一致（便于日志与脚本侧使用），Data 是只读字典。
/// </summary>
public sealed class PluginEventArgs
{
    /// <summary>事件名（与 <see cref="PluginEvent"/> 枚举名一致）。</summary>
    public string Name { get; init; } = "";

    /// <summary>事件数据。没有数据时为空的只读字典，绝不为 null。</summary>
    public IReadOnlyDictionary<string, object?> Data { get; init; } = new Dictionary<string, object?>();

    /// <summary>供反序列化/测试使用的无参构造。</summary>
    public PluginEventArgs()
    {
    }

    public PluginEventArgs(string name, IReadOnlyDictionary<string, object?>? data = null)
    {
        Name = name ?? "";
        Data = data ?? new Dictionary<string, object?>();
    }
}
