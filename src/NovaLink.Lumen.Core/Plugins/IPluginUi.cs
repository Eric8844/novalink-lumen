namespace NovaLink.Lumen.Core.Plugins;

/// <summary>
/// 插件 UI 注入门面。P0 不接线（IsAvailable == false），P1 由 App 层实现并负责 DynamicXaml / PRI / 导航重建。
/// 动态 UI 注册只在启动阶段接受，运行期调用应抛 InvalidOperationException（由实现负责）。
/// </summary>
public interface IPluginUi
{
    /// <summary>宿主是否具备 UI 注入能力。为 false 时插件不应调用 AddNavigation。</summary>
    bool IsAvailable { get; }

    /// <summary>注册一个导航项。Tag 必须以 plugin: 开头（manifest 校验已强制）。</summary>
    void AddNavigation(PluginNavItem item);
}

/// <summary>
/// 导航项。PageFactory 返回 object 而不是 FrameworkElement，是为了让 Core 保持零 WinUI 依赖；
/// App 层在内部把它转成 Page/UserControl。PageFactory 为 null 表示由宿主按 manifest 的 page 类型反射构造。
/// </summary>
public sealed record PluginNavItem(
    string Tag,
    string Title,
    Func<object>? PageFactory,
    int Order = 500,
    string? Glyph = null);
