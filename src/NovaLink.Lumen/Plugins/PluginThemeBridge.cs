using System;
using System.Collections.Generic;
using Microsoft.UI.Xaml.Media;
using NovaLink.Lumen.Core.Plugins;
using NovaLink.Lumen.Services;
using Windows.UI;

namespace NovaLink.Lumen.Plugins;

/// <summary>
/// <see cref="IPluginTheme"/> 的宿主实现（v1.5.1）：把 <see cref="ThemeManager"/> 的当前调色板
/// 翻译成插件可见的 "#AARRGGBB" 色表，并在主题切换、画刷资源刷新完成之后通知插件。
///
/// 设计取舍：
/// 1) 色表用字符串而不是 WinUI 的 Color —— Core 不引用 WinUI，插件也可能只想拿十六进制拼 XAML；
/// 2) <see cref="ThemeManager.Brush"/> 返回的是注册表里的**同一个**画刷实例（ThemeManager 就地改色），
///    所以色表里的值要在事件触发时重新采样，不能缓存第一次的结果；
/// 3) 单个插件在 Changed 里抛异常只记日志，不影响其它插件，也不会把异常带回主题切换的调用栈。
/// </summary>
public sealed class PluginThemeBridge : IPluginTheme
{
    private const string LogArea = "plugin-theme";

    private static readonly Dictionary<string, string> EmptyColors = new(StringComparer.Ordinal);

    /// <summary>进程内唯一实例：所有插件共用一份色表，主题切换只重建一次。</summary>
    public static PluginThemeBridge Instance { get; } = new();

    /// <summary>色表快照。整体替换（不原地改），读到的永远是一个完整版本。</summary>
    private volatile IReadOnlyDictionary<string, string> _colors = EmptyColors;

    /// <summary>采样时的主题戳。用来兜住"主题在事件接线之前就变过"的情况（例如 Initialize 顺序变化）。</summary>
    private bool _stampLight;
    private Color _stampAccent;
    private bool _stamped;

    private PluginThemeBridge()
    {
        Rebuild();
        try
        {
            ThemeManager.Changed += OnThemeChanged;
        }
        catch (Exception ex)
        {
            AppLog.Warn(LogArea, "订阅主题切换失败，插件将拿不到主题变更通知：" + ex.Message);
        }
    }

    /// <summary>当前是不是浅色主题。</summary>
    public bool IsLight => ThemeManager.IsLight;

    /// <summary>主题名，固定 "light" / "dark"。</summary>
    public string Name => ThemeManager.IsLight ? "light" : "dark";

    /// <summary>
    /// 主题色表（"#AARRGGBB"）。读之前先比对主题戳：万一主题在订阅之前已经变过，
    /// 这里会补一次重建，插件拿到的永远是当前主题的颜色。
    /// </summary>
    public IReadOnlyDictionary<string, string> Colors
    {
        get
        {
            if (!_stamped || _stampLight != ThemeManager.IsLight || _stampAccent != ThemeManager.Accent) Rebuild();
            return _colors;
        }
    }

    /// <summary>主题切换完成事件（此时宿主画刷资源已刷新）。</summary>
    public event EventHandler? Changed;

    private void OnThemeChanged(object? sender, EventArgs e)
    {
        Rebuild();

        var handlers = Changed;
        if (handlers is null) return;

        // 逐个调用：某个插件的处理器炸了不能连累后面的插件。
        foreach (var handler in handlers.GetInvocationList())
        {
            try
            {
                ((EventHandler)handler)(this, EventArgs.Empty);
            }
            catch (Exception ex)
            {
                AppLog.Warn(LogArea, "插件的主题切换处理器抛出异常，已隔离：" + ex.Message);
            }
        }
    }

    /// <summary>按当前主题重采样色表。</summary>
    private void Rebuild()
    {
        _stampLight = ThemeManager.IsLight;
        _stampAccent = ThemeManager.Accent;
        _stamped = true;

        var light = _stampLight;
        _colors = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            // 强调色取资源画刷而不是 ThemeManager.Accent：浅色下 ThemeManager 会把过亮的强调色压暗，
            // 那个"实际在用的"颜色才是界面上的薄荷绿。
            ["Accent"] = Hex(Read("BrushMint", ThemeManager.Accent)),
            ["Text"] = Hex(Read("BrushText", ThemeManager.Text)),
            ["TextSecondary"] = Hex(Read("BrushTextSecondary", ThemeManager.TextSecondary)),
            ["TextTertiary"] = Hex(Read("BrushTextTertiary", ThemeManager.TextTertiary)),
            ["Surface"] = Hex(Read("BrushSurface", ThemeManager.CardSurface)),
            ["SurfaceRaised"] = Hex(Read("BrushSurfaceRaised", ThemeManager.CardSurface)),
            ["GlassCard"] = Hex(ThemeManager.CardSurface),
            ["GlassBorder"] = Hex(Read("BrushBorderSubtle", ThemeManager.CardBorder)),
            ["Field"] = Hex(Read("BrushField", ThemeManager.ControlFill)),
            ["FieldBorder"] = Hex(Read("BrushFieldBorder", ThemeManager.ControlBorder)),
            ["FillSubtle"] = Hex(Read("BrushFillSubtle", ThemeManager.ControlFill)),
            ["Danger"] = Hex(Read("BrushDanger", light
                ? Color.FromArgb(0xFF, 0xD1, 0x3F, 0x62)
                : Color.FromArgb(0xFF, 0xFF, 0x6B, 0x8A))),
            ["Warn"] = Hex(Read("BrushWarn", light
                ? Color.FromArgb(0xFF, 0xB0, 0x6A, 0x00)
                : Color.FromArgb(0xFF, 0xFF, 0xC0, 0x78)))
        };
    }

    /// <summary>读资源画刷的实际颜色；资源缺失（或该键是渐变/亚克力）时退回给的常量。</summary>
    private static Color Read(string key, Color fallback)
    {
        try
        {
            return ThemeManager.Brush(key, fallback) is SolidColorBrush solid ? solid.Color : fallback;
        }
        catch
        {
            // 插件可能在后台线程读色表：跨线程碰资源画刷会抛 COM 异常，这里退回常量。
            return fallback;
        }
    }

    private static string Hex(Color color)
        => "#" + color.A.ToString("X2") + color.R.ToString("X2") + color.G.ToString("X2") + color.B.ToString("X2");
}
