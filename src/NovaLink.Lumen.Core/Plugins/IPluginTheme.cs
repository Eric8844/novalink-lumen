namespace NovaLink.Lumen.Core.Plugins;

/// <summary>
/// 主题门面（v1.5.1 新增）：插件用它读当前是深色还是浅色，并在主题切换时得到通知。
/// 宿主在 <see cref="IPluginContext.Theme"/> 上注入真实实现；未接线时是一个固定的深色空实现。
/// 插件不需要（也不应该）自己去读注册表或 FollowSystemSetting。
/// </summary>
public interface IPluginTheme
{
    /// <summary>当前是不是浅色主题。切换主题后会先刷新画刷资源，再触发 <see cref="Changed"/>。</summary>
    bool IsLight { get; }

    /// <summary>主题名，固定是 "light" 或 "dark"，方便写日志与判断。</summary>
    string Name { get; }

    /// <summary>
    /// 主题色表：键是稳定的语义名，值是 "#AARRGGBB" 字符串。
    /// 常用键：Accent / Text / TextSecondary / TextTertiary / Surface / SurfaceRaised /
    /// GlassCard / GlassBorder / Field / FieldBorder / FillSubtle / Danger / Warn。
    /// 切主题后本表内容会更新，插件应在 <see cref="Changed"/> 里重新取色。
    /// </summary>
    IReadOnlyDictionary<string, string> Colors { get; }

    /// <summary>主题切换完成事件。触发时宿主画刷资源已经刷新完毕，可以直接重建画刷。</summary>
    event EventHandler? Changed;
}

/// <summary>未接线时的空主题（深色、无变更事件），保证 <see cref="IPluginContext.Theme"/> 永远不为 null。</summary>
public sealed class NullPluginTheme : IPluginTheme
{
    public static readonly NullPluginTheme Instance = new();

    private static readonly Dictionary<string, string> Palette = new()
    {
        ["Accent"] = "#FF7CEBD6",
        ["Text"] = "#FFF3F7FE",
        ["TextSecondary"] = "#FF91A0B8",
        ["TextTertiary"] = "#FF5E6B81",
        ["Surface"] = "#B30A101B",
        ["SurfaceRaised"] = "#D00E1624",
        ["GlassCard"] = "#FA0A111E",
        ["GlassBorder"] = "#2EFFFFFF",
        ["Field"] = "#0EFFFFFF",
        ["FieldBorder"] = "#1FFFFFFF",
        ["FillSubtle"] = "#14FFFFFF",
        ["Danger"] = "#FFFF6B8A",
        ["Warn"] = "#FFFFC078"
    };

    public bool IsLight => false;

    public string Name => "dark";

    public IReadOnlyDictionary<string, string> Colors => Palette;

    public event EventHandler? Changed
    {
        add { }
        remove { }
    }
}
