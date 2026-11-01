namespace NovaLink.Lumen.Core.Plugins;

/// <summary>
/// 启动器能力门面：插件能做的事全部走这里，方便审计与后续按 capabilities 收口。
/// P0 由 App 层注入真实实现；未接线时宿主注入一个所有方法都明确失败的"不可用"实现。
/// </summary>
public interface IPluginHost
{
    /// <summary>启动器版本号（如 1.4.1）。</summary>
    string LauncherVersion { get; }

    /// <summary>启动器数据根目录（只给字符串，不给任何文件操作能力）。</summary>
    string DataRoot { get; }

    /// <summary>当前选中账户名；未登录为 null。</summary>
    string? CurrentAccountName { get; }

    /// <summary>
    /// 当前选中账户的最小信息：显示名 + UUID + 头像地址；未登录为 null。
    /// 插件不存在任何获取令牌、刷新令牌、皮肤文件路径的宿主 API。
    /// </summary>
    PluginAccount? CurrentAccount { get; }

    /// <summary>列出已安装实例（只读快照）。</summary>
    IReadOnlyList<InstanceRef> ListInstances();

    /// <summary>启动指定实例。失败时应抛异常或由宿主记录错误；P0 未接线时抛 NotSupportedException。</summary>
    Task LaunchInstanceAsync(string id, CancellationToken ct = default);

    /// <summary>受限 HTTP GET（宿主按 capabilities 与白名单校验）。P0 未接线时返回 StatusCode=0 且 IsSuccess=false。</summary>
    Task<PluginHttpResult> HttpGetAsync(
        string url,
        IReadOnlyDictionary<string, string>? headers = null,
        CancellationToken ct = default);

    /// <summary>弹一条通知。level 取 info / success / warning / error。</summary>
    void Notify(string title, string message, string level = "info");
}

/// <summary>实例的只读引用（插件不需要知道目录结构）。</summary>
public sealed record InstanceRef(string Id, string Name, string GameVersion, string Loader);

/// <summary>受限 HTTP 结果。IsSuccess 为 false 表示网络失败或能力未接线，插件应以此判定而不是只看 StatusCode。</summary>
public sealed record PluginHttpResult(int StatusCode, string Body, bool IsSuccess);
