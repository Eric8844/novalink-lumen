using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Markup;
using Microsoft.UI.Xaml.Media;
using NovaLink.Lumen.Services;
using Windows.UI;

namespace NovaLink.Lumen.Controls;

/// <summary>
/// 分段切换：选中的那一项脚下有一枚会滑过去的胶囊（Lens 的 <c>.lg-segment-bar &gt; .lg-indicator</c>）。
/// <para>
/// 胶囊本身由 <see cref="LiquidSlide"/> 负责：它插在底槽与按钮之间，
/// 用 <c>TransformToVisual</c> 相对宿主量坐标，所以分段栏放在横向 ScrollViewer 里也不会错位
/// （早期版本用窗口坐标，一滚动就飘，已废弃）。这里只做三件事：
/// 把按钮变成「圆角 + 悬停纱」的透明底、按选中项换文字色、把选中项交给胶囊。
/// </para>
/// <para>
/// 按钮模板被换成最小的「圆角底 + 内容」，不再走 WinUI 的 Button 视觉状态：
/// 默认模板在 PointerOver/Pressed 时会用主题资源盖掉 Background，鼠标停在选中项上胶囊就会消失；
/// 用元素资源去改那些键会抛 0x800F0902（主题资源不在元素字典里）。自定义模板只剩 TemplateBinding，
/// 悬停反馈由模板里那层极淡的 veil 负责，胶囊永远看得见。
/// </para>
/// 用法：<c>SegmentedSwitch.Enable(SegBar)</c> 之后在切换逻辑里 <c>SegmentedSwitch.Select(SegBar, index)</c>。
/// XAML 约定：bar 是 Grid，里面是装按钮的横向 StackPanel（按钮可以带图标 + 文字）。
/// </summary>
public static class SegmentedSwitch
{
    /// <summary>原来残留药丸的标记，用来清理页面缓存复用留下的旧元素。</summary>
    private const string LegacyIndicatorTag = "segmented-indicator";

    /// <summary>胶囊底色（不透明）：半透明的 BrushSelected 会让下面那层文字透上来，看起来像「穿过」。</summary>
    private static SolidColorBrush? _capsule;
    private static string _capsuleKey = "";

    private static readonly SolidColorBrush TransparentBrush = new(Microsoft.UI.Colors.Transparent);

    private static ControlTemplate? _template;
    private static readonly object TemplateLock = new();

    /// <summary>条件弱表：页面每次导航都会重建，普通字典会把整棵旧树留在内存里。</summary>
    private static readonly ConditionalWeakTable<Panel, State> States = new();

    private sealed class State
    {
        public Panel Bar = null!;
        public List<Button> Buttons = new();
        public int Index = -1;
    }

    /// <summary>启用分段栏：清掉旧药丸、把按钮变成透明胶囊、挂好点击与首次选中。</summary>
    public static void Enable(Panel bar)
    {
        if (bar is null) return;

        if (States.TryGetValue(bar, out var known))
        {
            Apply(known, animate: false);
            return;
        }

        var row = FindRow(bar);
        if (row is null) return;
        var buttons = row.Children.OfType<Button>().ToList();
        if (buttons.Count == 0) return;

        RemoveLegacyIndicators(bar);

        foreach (var button in buttons) PrepareButton(button);

        var state = new State { Bar = bar, Buttons = buttons };
        States.Add(bar, state);

        // 胶囊指示器：滑到选中项下面（同一枚，切换时滑过去，不是每项各自一块底色）。
        LiquidSlide.Enable(bar, vertical: false, fill: CapsuleBrush);

        for (var i = 0; i < buttons.Count; i++)
        {
            var index = i;
            buttons[i].Click += (_, _) =>
            {
                if (buttons[index].Visibility != Visibility.Visible) return;
                Select(bar, index);
            };
        }

        Apply(state, animate: false);
    }

    /// <summary>选中第 index 项：只有它带胶囊底色。</summary>
    public static void Select(Panel bar, int index, bool animate = true)
    {
        if (bar is null) return;

        if (!States.TryGetValue(bar, out var state))
        {
            Enable(bar);
            if (!States.TryGetValue(bar, out state)) return;
        }

        if (index < 0 || index >= state.Buttons.Count) return;

        var changed = state.Index != index;
        state.Index = index;
        Apply(state, animate && changed);
    }

    /// <summary>当前选中项下标；没启用过返回 -1。</summary>
    public static int SelectedIndex(Panel bar)
        => bar is not null && States.TryGetValue(bar, out var state) ? state.Index : -1;

    private static void Apply(State state, bool animate)
    {
        var accent = ThemeManager.BrushAccent;
        var idle = ThemeManager.BrushTextSecondary;
        var selectedInk = ThemeManager.IsLight ? Brush(ThemeManager.Text) : accent;

        for (var i = 0; i < state.Buttons.Count; i++)
        {
            var button = state.Buttons[i];
            var selected = i == state.Index;

            // 底色交给下面那枚会滑动的胶囊，按钮自己始终透明（否则悬停状态又把它盖掉）。
            button.Background = TransparentBrush;
            button.BorderBrush = TransparentBrush;
            button.Foreground = selected ? selectedInk : idle;
        }

        LiquidSlide.Select(state.Bar, state.Index >= 0 && state.Index < state.Buttons.Count ? state.Buttons[state.Index] : null, animate);
    }

    /// <summary>找到真正装按钮的那层 Panel：bar 自身是 Grid 时就是里面那个横向 StackPanel。</summary>
    private static Panel? FindRow(Panel bar)
    {
        foreach (var child in bar.Children)
            if (child is Panel panel && panel.Children.OfType<Button>().Any()) return panel;
        return bar.Children.OfType<Button>().Any() ? bar : null;
    }

    /// <summary>清掉上一版「独立药丸」残留（页面缓存复用、热重载都可能留下）。</summary>
    private static void RemoveLegacyIndicators(Panel bar)
    {
        for (var i = bar.Children.Count - 1; i >= 0; i--)
        {
            if (bar.Children[i] is FrameworkElement element && Equals(element.Tag, LegacyIndicatorTag))
            {
                try { bar.Children.RemoveAt(i); }
                catch (Exception ex) { AppLog.Warn("segmented", "清理旧药丸失败：" + ex.Message); }
            }
        }
    }

    /// <summary>
    /// 按钮换成「圆角底 + 内容 + 悬停纱」的最小模板：底色只认 TemplateBinding.Background。
    /// 圆角按高度的一半算（以前写 999：WinUI 不会把圆角夹到短边一半，填充会溢出按钮边界，
    /// 相邻按钮的巨大椭圆互相压住，就是用户看到的「穿过 + 圆很难看」）。
    /// </summary>
    private static void PrepareButton(Button button)
    {
        button.Template = CapsuleTemplate();
        button.Background = TransparentBrush;
        button.BorderBrush = TransparentBrush;
        button.BorderThickness = new Thickness(1);
        button.CornerRadius = new CornerRadius(17);
        button.SizeChanged += (_, args) =>
        {
            if (args.NewSize.Height <= 0) return;
            var radius = Math.Max(8, args.NewSize.Height / 2);
            if (Math.Abs(button.CornerRadius.TopLeft - radius) > 0.4)
                button.CornerRadius = new CornerRadius(radius);
        };
        button.HorizontalContentAlignment = HorizontalAlignment.Center;
        button.VerticalContentAlignment = VerticalAlignment.Center;

        button.PointerEntered += (_, _) => SetVeil(button, 1);
        button.PointerExited += (_, _) => SetVeil(button, 0);
        button.PointerCaptureLost += (_, _) => SetVeil(button, 0);
    }

    private static ControlTemplate CapsuleTemplate()
    {
        if (_template is not null) return _template;
        lock (TemplateLock)
        {
            if (_template is not null) return _template;
            const string xaml = """
                <ControlTemplate xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
                                 xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
                                 TargetType="Button">
                    <Grid Background="Transparent">
                        <Border Background="{TemplateBinding Background}"
                                BorderBrush="{TemplateBinding BorderBrush}"
                                BorderThickness="{TemplateBinding BorderThickness}"
                                CornerRadius="{TemplateBinding CornerRadius}" />
                        <Border x:Name="HoverVeil" Background="#1FFFFFFF" Opacity="0" IsHitTestVisible="False"
                                CornerRadius="{TemplateBinding CornerRadius}" />
                        <ContentPresenter Padding="{TemplateBinding Padding}"
                                          HorizontalAlignment="{TemplateBinding HorizontalContentAlignment}"
                                          VerticalAlignment="{TemplateBinding VerticalContentAlignment}"
                                          Content="{TemplateBinding Content}"
                                          ContentTemplate="{TemplateBinding ContentTemplate}"
                                          Foreground="{TemplateBinding Foreground}" />
                    </Grid>
                </ControlTemplate>
                """;
            _template = (ControlTemplate)XamlReader.Load(xaml);
            return _template;
        }
    }

    private static void SetVeil(Button button, double opacity)
    {
        if (button.Template is null) return;
        var veil = FindByName(button, "HoverVeil");
        if (veil is not null) veil.Opacity = opacity;
    }

    private static FrameworkElement? FindByName(DependencyObject root, string name)
    {
        var count = VisualTreeHelper.GetChildrenCount(root);
        for (var i = 0; i < count; i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is FrameworkElement element && string.Equals(element.Name, name, StringComparison.Ordinal))
                return element;
            var found = FindByName(child, name);
            if (found is not null) return found;
        }
        return null;
    }

    /// <summary>
    /// 选中项的胶囊底色：用主题「卡片底 + 强调色」调一个不透明的深/浅色块。
    /// 之前直接用 BrushSelected（alpha 0x2E）→ 半透明 → 选中项下面的文字会透出来。
    /// </summary>
    private static SolidColorBrush CapsuleBrush()
    {
        var key = (ThemeManager.IsLight ? "L" : "D") + ThemeManager.Accent.ToString();
        if (_capsule is not null && string.Equals(key, _capsuleKey, StringComparison.Ordinal)) return _capsule;

        var surface = ThemeManager.IsLight
            ? Color.FromArgb(0xFF, 0xF5, 0xFA, 0xFE)
            : Color.FromArgb(0xFF, 0x0F, 0x1B, 0x26);
        var accent = ThemeManager.Accent;
        var mix = ThemeManager.IsLight ? 0.16 : 0.26;
        var capsule = Color.FromArgb(0xFF,
            (byte)(surface.R + (accent.R - surface.R) * mix),
            (byte)(surface.G + (accent.G - surface.G) * mix),
            (byte)(surface.B + (accent.B - surface.B) * mix));
        _capsule = new SolidColorBrush(capsule);
        _capsuleKey = key;
        return _capsule;
    }

    private static SolidColorBrush Brush(Color color) => new(color);
}
