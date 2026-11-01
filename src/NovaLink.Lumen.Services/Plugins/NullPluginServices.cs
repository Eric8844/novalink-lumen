using NovaLink.Lumen.Core.Plugins;

namespace NovaLink.Lumen.Services.Plugins;

/// <summary>
/// P0 的宿主能力空实现：App 层还没接线时插件不会因为 Host 为 null 而崩溃。
/// 语义上"能力不可用"——联网返回 0/失败，启动实例返回失败任务，通知直接丢弃。
/// </summary>
public sealed class NullPluginHost : IPluginHost
{
    public static readonly NullPluginHost Instance = new();

    public string LauncherVersion => "";

    public string DataRoot => "";

    public string? CurrentAccountName => null;

    public PluginAccount? CurrentAccount => null;

    public IReadOnlyList<InstanceRef> ListInstances() => [];

    public Task LaunchInstanceAsync(string id, CancellationToken cancellationToken = default)
        => Task.FromException(new InvalidOperationException("宿主未提供实例启动能力（P0 阶段 IPluginHost 未注入）。"));

    public Task<PluginHttpResult> HttpGetAsync(string url, IReadOnlyDictionary<string, string>? headers = null, CancellationToken cancellationToken = default)
        => Task.FromResult(new PluginHttpResult(0, "宿主未提供网络能力（P0 阶段 IPluginHost 未注入）。", false));

    public void Notify(string title, string message, string level = "info")
    {
        // 通知能力未接入：丢弃，不抛异常。
    }
}

/// <summary>P0 的 UI 注入空实现：IsAvailable == false，插件的 AddNavigation 被忽略。</summary>
public sealed class NullPluginUi : IPluginUi
{
    public static readonly NullPluginUi Instance = new();

    public bool IsAvailable => false;

    public void AddNavigation(PluginNavItem item)
    {
        // UI 层未接入：忽略注册，插件应自行判断 Ui.IsAvailable。
    }
}
