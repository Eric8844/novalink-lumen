namespace NovaLink.Lumen.Core.Plugins;

/// <summary>
/// 插件 UI 注入门面。动态 UI 注册只在启动阶段接受，运行期调用应抛 InvalidOperationException（由实现负责）。
/// v1.5.1 起额外提供"用启动器默认液态玻璃样式造组件"的工厂方法：返回值是宿主创建的 WinUI 元素
/// （Core 不引用 WinUI，所以这里是 object），插件把它塞进自己的 ContentPresenter / ContentControl 即可。
/// 未接线时所有工厂方法返回 null，插件应先用 <see cref="IsAvailable"/> 判断。
/// </summary>
public interface IPluginUi
{
    /// <summary>宿主是否具备 UI 注入能力。为 false 时插件不应调用 AddNavigation 与玻璃工厂方法。</summary>
    bool IsAvailable { get; }

    /// <summary>注册一个导航项。Tag 必须以 plugin: 开头（manifest 校验已强制）。</summary>
    void AddNavigation(PluginNavItem item);

    /// <summary>
    /// 造一张和启动器一致的液态玻璃卡片：圆角、半透明底、1px 描边、内高光都走宿主主题资源。
    /// <paramref name="content"/> 是插件自己的 UI 元素（可为 null）；<paramref name="title"/> 非空时在内容上方加标题行。
    /// 返回的对象可以直接放进 ContentControl.Content。插件不需要自己写颜色，浅色/深色由宿主自动适配。
    /// </summary>
    object? CreateGlassCard(object? content, string? title = null) => null;

    /// <summary>造一块玻璃面板（无标题、更轻的底色，适合卡片内部再分组）。</summary>
    object? CreateGlassPanel(object? content) => null;

    /// <summary>造一个玻璃按钮：默认胶囊形、悬停有高光，<paramref name="onClick"/> 在 UI 线程触发。</summary>
    object? CreateGlassButton(string text, Action onClick) => null;

    /// <summary>把宿主默认的玻璃卡片样式套到插件自己的元素上（适合插件已经用 XAML 写好容器的情况）。</summary>
    void ApplyGlassCard(object element) { }
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
