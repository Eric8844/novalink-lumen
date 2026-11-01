using NovaLink.Lumen.Core.Plugins;

namespace NovaLink.Lumen.Services.Plugins;

/// <summary>
/// 一次启动内的插件运行参数。DataRoot 由 App 层传入（AppPaths.Root），
/// LauncherVersion 用于 minLauncher/maxLauncher 兼容判断，SafeMode 表示"本次启动不加载任何插件"。
/// </summary>
public sealed class PluginRuntimeOptions
{
    /// <summary>启动器数据根目录（插件根 = DataRoot\plugins）。为空时插件系统整体降级为"无插件"。</summary>
    public string DataRoot { get; init; } = "";

    /// <summary>当前启动器版本（如 "1.4.1"）。为空时跳过版本区间校验。</summary>
    public string LauncherVersion { get; init; } = "";

    /// <summary>安全模式：本次启动禁用全部插件（看门狗判定崩溃循环时置位）。</summary>
    public bool SafeMode { get; init; }

    /// <summary>开发模式：额外记录发现/跳过细节，便于插件作者排查。</summary>
    public bool DevMode { get; init; }

    /// <summary>宿主能力实现；P0 允许为 null（插件拿到的是安全空实现）。</summary>
    public IPluginHost? Host { get; init; }

    /// <summary>UI 注入实现；P0 允许为 null（Ui.IsAvailable == false，插件应跳过 UI 注册）。</summary>
    public IPluginUi? Ui { get; init; }

    /// <summary>
    /// 查询某个插件是否被用户启用（读 lumen.json 的 plugins.&lt;id&gt;.enabled）。
    /// 返回 null 表示未配置 —— 缺省启用。未注入时所有插件都按启用处理。
    /// </summary>
    public Func<string, bool?>? PluginEnabledQuery { get; init; }
}
