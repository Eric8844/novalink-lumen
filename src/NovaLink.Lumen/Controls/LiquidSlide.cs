using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using NovaLink.Lumen.Services;
using Windows.Foundation;
using Windows.UI;

namespace NovaLink.Lumen.Controls;

/// <summary>
/// 会滑动、会液化的胶囊指示器（Lens 的 <c>.lg-segment-bar &gt; .lg-indicator</c>）。
/// <para>
/// 选中的那一格不是「按钮自己变个底色」，而是底下有一枚胶囊从旧位置滑到新位置：
/// 横向栏（发现/库/论坛的分段按钮）水平滑，左侧导航栏竖直滑。滑动用
/// <c>cubic-bezier(.16,1,.3,1)</c> 的减速曲线，再叠一层「拉伸-回弹」的液态形变
/// （沿运动方向拉长到 1.12、垂直方向压到 0.90，最后用弹簧曲线 <c>.2,.9,.25,1.22</c> 归位）。
/// </para>
/// <para>
/// 宿主约定：一格 <c>Grid</c>，第一个子元素是底槽 <c>Border</c>、后面是装按钮的
/// <c>StackPanel</c>（左侧导航栏则是整个 NavRail 的 Grid）。胶囊插在两者之间的
/// <c>Canvas</c> 里，坐标用 <c>TransformToVisual(Canvas)</c> 直接量，
/// 所以即使宿主在横向 ScrollViewer 里滚动，药丸也不会错位（旧实现量的是窗口坐标，会飘）。
/// </para>
/// </summary>
public static class LiquidSlide
{
    private static readonly ConditionalWeakTable<Panel, State> States = new();
    private static readonly List<WeakReference<Panel>> Hosts = new();
    private static bool _themeHooked;

    private sealed class State
    {
        public Panel Host = null!;
        public Canvas Layer = null!;
        public Grid Pill = null!;
        public Border Fill = null!;
        public Border Bloom = null!;
        public ScaleTransform Squash = null!;
        public TranslateTransform Slide = null!;
        public Func<Brush>? FillFactory;
        public bool Vertical;
        public double FixedRadius;
        public FrameworkElement? Target;
    }

    /// <summary>把胶囊挂到宿主上。同一宿主重复调用只更新配色，不会叠出第二枚。</summary>
    public static void Enable(Panel host, bool vertical = false, Func<Brush>? fill = null, double fixedRadius = 0)
    {
        if (host is null) return;
        HookTheme();

        if (States.TryGetValue(host, out var known))
        {
            known.FillFactory = fill ?? known.FillFactory;
            known.Vertical = vertical;
            known.FixedRadius = fixedRadius;
            ApplyFill(known);
            Reposition(host, animate: false);
            return;
        }

        var layer = new Canvas { IsHitTestVisible = false };
        if (host is Grid grid && grid.RowDefinitions.Count > 0) Grid.SetRowSpan(layer, grid.RowDefinitions.Count);

        var fillBorder = new Border { BorderThickness = new Thickness(1), IsHitTestVisible = false };
        var bloom = new Border
        {
            IsHitTestVisible = false,
            Background = new LinearGradientBrush
            {
                StartPoint = new Point(0, 0),
                EndPoint = new Point(0, 1),
                GradientStops =
                {
                    new GradientStop { Color = Color.FromArgb(0x38, 0xFF, 0xFF, 0xFF), Offset = 0 },
                    new GradientStop { Color = Color.FromArgb(0x00, 0xFF, 0xFF, 0xFF), Offset = 0.62 }
                }
            }
        };

        var squash = new ScaleTransform { ScaleX = 1, ScaleY = 1 };
        var slide = new TranslateTransform();
        var group = new TransformGroup();
        group.Children.Add(squash);
        group.Children.Add(slide);

        var pill = new Grid
        {
            IsHitTestVisible = false,
            Visibility = Visibility.Collapsed,
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Top,
            RenderTransformOrigin = new Point(0.5, 0.5),
            RenderTransform = group
        };
        pill.Children.Add(fillBorder);
        pill.Children.Add(bloom);
        layer.Children.Add(pill);

        var index = host.Children.Count > 0 ? 1 : 0;
        host.Children.Insert(Math.Min(index, host.Children.Count), layer);

        var state = new State
        {
            Host = host,
            Layer = layer,
            Pill = pill,
            Fill = fillBorder,
            Bloom = bloom,
            Squash = squash,
            Slide = slide,
            FillFactory = fill,
            Vertical = vertical,
            FixedRadius = fixedRadius
        };
        States.Add(host, state);
        Hosts.Add(new WeakReference<Panel>(host));
        ApplyFill(state);

        host.SizeChanged += (_, _) => Reposition(host, animate: false);
    }

    /// <summary>选中某个元素：胶囊滑过去。传 null 表示收起。</summary>
    public static void Select(Panel host, FrameworkElement? target, bool animate = true)
    {
        if (host is null) return;
        if (!States.TryGetValue(host, out var state))
        {
            Enable(host);
            if (!States.TryGetValue(host, out state)) return;
        }

        state.Target = target;
        if (target is null)
        {
            state.Pill.Visibility = Visibility.Collapsed;
            return;
        }

        if (!target.IsLoaded || target.ActualWidth <= 0 || target.ActualHeight <= 0)
        {
            // 首次布局还没跑完：先藏起来，等这一格量出尺寸再摆。
            state.Pill.Visibility = Visibility.Collapsed;
            EventHandler<object>? once = null;
            once = (_, _) =>
            {
                target.LayoutUpdated -= once;
                if (ReferenceEquals(state.Target, target)) MoveTo(state, target, animate: false);
            };
            target.LayoutUpdated += once;
            return;
        }

        MoveTo(state, target, animate);
    }

    /// <summary>主题切换后重刷胶囊配色，并按当前选中项重新摆一次（不动画）。</summary>
    public static void Refresh(Panel host)
    {
        if (host is null || !States.TryGetValue(host, out var state)) return;
        ApplyFill(state);
        Reposition(host, animate: false);
    }

    /// <summary>把胶囊按当前占位重新量一次（窗口尺寸变化、按钮增减后调用）。</summary>
    public static void Relayout(Panel host) => Reposition(host, animate: false);

    private static void HookTheme()
    {
        if (_themeHooked) return;
        _themeHooked = true;
        ThemeManager.Changed += (_, _) => RefreshAll();
    }

    /// <summary>
    /// 主题切换：胶囊的底色是 <c>CapsuleBrush()</c> 现算出来的新画刷（不是被原地改色的资源），
    /// 所以必须挨个重刷一遍；用弱引用登记宿主，页面被丢掉后不会留着。
    /// </summary>
    private static void RefreshAll()
    {
        for (var i = Hosts.Count - 1; i >= 0; i--)
        {
            if (!Hosts[i].TryGetTarget(out var host))
            {
                Hosts.RemoveAt(i);
                continue;
            }

            if (!States.TryGetValue(host, out var state)) continue;
            ApplyFill(state);
            Reposition(host, animate: false);
        }
    }

    private static void Reposition(Panel host, bool animate)
    {
        if (!States.TryGetValue(host, out var state) || state.Target is null) return;
        MoveTo(state, state.Target, animate);
    }

    private static void MoveTo(State state, FrameworkElement target, bool animate)
    {
        if (target.Visibility != Visibility.Visible || state.Layer.ActualWidth <= 0)
        {
            state.Pill.Visibility = Visibility.Collapsed;
            return;
        }

        Point point;
        double width, height;
        try
        {
            point = target.TransformToVisual(state.Layer).TransformPoint(new Point(0, 0));
            width = target.ActualWidth;
            height = target.ActualHeight;
        }
        catch (Exception ex)
        {
            AppLog.Warn("liquid-slide", "胶囊定位跳过：" + ex.Message);
            state.Pill.Visibility = Visibility.Collapsed;
            return;
        }

        if (width <= 0 || height <= 0)
        {
            state.Pill.Visibility = Visibility.Collapsed;
            return;
        }

        var radius = state.FixedRadius > 0
            ? Math.Min(state.FixedRadius, Math.Min(width, height) / 2)
            : Math.Max(8, Math.Min(width, height) / 2);
        var corner = new CornerRadius(radius);
        state.Fill.CornerRadius = corner;
        state.Bloom.CornerRadius = corner;
        var firstShow = state.Pill.Visibility != Visibility.Visible || double.IsNaN(state.Pill.Width);
        state.Pill.Visibility = Visibility.Visible;

        if (!animate || firstShow)
        {
            // 第一次出现（或宿主刚刚布局完、主题切换）：直接就位，不从左上角飞过来。
            state.Pill.Width = width;
            state.Pill.Height = height;
            state.Slide.X = Math.Round(point.X);
            state.Slide.Y = Math.Round(point.Y);
            state.Squash.ScaleX = 1;
            state.Squash.ScaleY = 1;
            return;
        }

        var dx = point.X - state.Slide.X;
        var dy = point.Y - state.Slide.Y;
        var horizontal = !state.Vertical && Math.Abs(dx) >= Math.Abs(dy);
        var ms = 300 + (int)Math.Min(90, (Math.Abs(dx) + Math.Abs(dy)) * 0.35);

        var storyboard = new Storyboard();

        storyboard.Children.Add(Keyframe(state.Slide, "X", state.Slide.X, Math.Round(point.X), ms, Decelerate()));
        storyboard.Children.Add(Keyframe(state.Slide, "Y", state.Slide.Y, Math.Round(point.Y), ms, Decelerate()));
        if (Math.Abs(state.Pill.Width - width) > 0.5)
            storyboard.Children.Add(Keyframe(state.Pill, "Width", state.Pill.Width, width, ms, Decelerate()));
        if (Math.Abs(state.Pill.Height - height) > 0.5)
            storyboard.Children.Add(Keyframe(state.Pill, "Height", state.Pill.Height, height, ms, Decelerate()));

        // 液态：沿运动方向先拉长、垂直方向压扁，再用弹簧曲线归位。
        var along = horizontal ? "ScaleX" : "ScaleY";
        var across = horizontal ? "ScaleY" : "ScaleX";
        storyboard.Children.Add(Squash(state.Squash, along, 1.12, ms));
        storyboard.Children.Add(Squash(state.Squash, across, 0.90, ms));

        try { storyboard.Begin(); }
        catch (Exception ex) { AppLog.Warn("liquid-slide", "胶囊动效跳过：" + ex.Message); }
    }

    private static void ApplyFill(State state)
    {
        var fill = state.FillFactory?.Invoke() ?? ThemeManager.Brush("BrushFillSubtle", ThemeManager.ControlFill);
        state.Fill.Background = fill;
        state.Fill.BorderBrush = ThemeManager.Brush("BrushGlassRim", ThemeManager.CardBorder);
        state.Bloom.Opacity = ThemeManager.IsLight ? 1 : 0.9;
    }

    private static DoubleAnimationUsingKeyFrames Keyframe(DependencyObject target, string property, double from, double to, int ms, KeySpline spline)
    {
        var animation = new DoubleAnimationUsingKeyFrames();
        animation.KeyFrames.Add(new LinearDoubleKeyFrame { KeyTime = TimeSpan.Zero, Value = from });
        animation.KeyFrames.Add(new SplineDoubleKeyFrame { KeyTime = TimeSpan.FromMilliseconds(ms), Value = to, KeySpline = spline });
        Storyboard.SetTarget(animation, target);
        Storyboard.SetTargetProperty(animation, property);
        return animation;
    }

    private static DoubleAnimationUsingKeyFrames Squash(DependencyObject target, string property, double peak, int ms)
    {
        var animation = new DoubleAnimationUsingKeyFrames();
        animation.KeyFrames.Add(new LinearDoubleKeyFrame { KeyTime = TimeSpan.Zero, Value = 1 });
        animation.KeyFrames.Add(new EasingDoubleKeyFrame
        {
            KeyTime = TimeSpan.FromMilliseconds((int)(ms * 0.34)),
            Value = peak,
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
        });
        animation.KeyFrames.Add(new SplineDoubleKeyFrame
        {
            KeyTime = TimeSpan.FromMilliseconds(ms + 90),
            Value = 1,
            KeySpline = Spring()
        });
        Storyboard.SetTarget(animation, target);
        Storyboard.SetTargetProperty(animation, property);
        return animation;
    }

    private static KeySpline Decelerate() => new() { ControlPoint1 = new Point(0.16, 1), ControlPoint2 = new Point(0.3, 1) };

    private static KeySpline Spring() => new() { ControlPoint1 = new Point(0.2, 0.9), ControlPoint2 = new Point(0.25, 1.22) };
}
