namespace NovaLink.Lumen.Core.Plugins;

/// <summary>
/// 类型 A（assembly）插件的唯一入口。宿主创建实例后调用一次 <see cref="Initialize"/>，
/// 退出前调用一次 <see cref="Shutdown"/>。实现必须是 public 且带无参构造函数。
/// Initialize 抛异常 = 只禁用该插件，绝不影响启动器与其它插件。
/// </summary>
public interface ILumenPlugin
{
    /// <summary>初始化：注册事件、导航项、挂钩子。禁止在这里做长时间阻塞操作。</summary>
    void Initialize(IPluginContext context);

    /// <summary>退出前清理。宿主保证只调用一次，并会捕获这里抛出的任何异常。</summary>
    void Shutdown();
}

/// <summary>
/// 宿主提供给单个插件的上下文门面。插件只能通过它接触启动器能力。
/// 注意：类型 A 插件本身是任意代码，"能力声明"约束的是宿主门面暴露面，不是安全沙箱。
/// </summary>
public interface IPluginContext
{
    /// <summary>插件 id，等于 manifest.id 与插件目录名。</summary>
    string PluginId { get; }

    /// <summary>写进启动器日志（logs/lumen-*.log，area 前缀 plugin:&lt;id&gt;）。</summary>
    IPluginLogger Log { get; }

    /// <summary>宿主代管的持久化键值设置（plugins/&lt;id&gt;/settings.json，700ms 节流落盘）。</summary>
    IPluginSettings Settings { get; }

    /// <summary>启动器能力门面。P0 未接线时是一个"不可用"的空实现，不会返回 null。</summary>
    IPluginHost Host { get; }

    /// <summary>UI 注入门面。P0 未接线时 IsAvailable == false，AddNavigation 为空操作。</summary>
    IPluginUi Ui { get; }

    /// <summary>主题门面（v1.5.1）：读深/浅色、取主题色、订阅主题切换。永远不为 null。</summary>
    IPluginTheme Theme => NullPluginTheme.Instance;

    /// <summary>订阅插件事件；返回的对象 Dispose 后取消订阅。宿主会吞掉处理器的异常并记日志。</summary>
    IDisposable Subscribe(PluginEvent evt, Action<PluginEventArgs> handler);
}

/// <summary>插件日志门面（写入启动器统一日志，自动带插件 id）。</summary>
public interface IPluginLogger
{
    void Info(string message);
    void Warn(string message);
    void Error(string message, Exception? ex = null);
}

/// <summary>
/// 插件设置门面。宿主负责落盘与并发，插件无需关心文件路径。
/// 值一律按字符串保存；复杂结构请自行 JSON 序列化。
/// </summary>
public interface IPluginSettings
{
    /// <summary>读取设置；键不存在返回 null。</summary>
    string? Get(string key);

    /// <summary>写入设置（内存立即生效，落盘由宿主节流）。</summary>
    void Set(string key, string value);

    /// <summary>立即落盘（宿主通常已自动节流，正常流程不需要调用）。</summary>
    void Flush();
}
