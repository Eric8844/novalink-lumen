using System;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using NovaLink.Lumen.Services;
using Windows.UI;

namespace NovaLink.Lumen.Controls;

/// <summary>
/// 通用加载动画：转圈 + 一句说明 + 骨架条呼吸效果。
/// 页面加载数据时把它盖在内容区上，加载完成后 Hide()。
/// 全部是 XAML 动画（ProgressRing + Storyboard），不依赖 Win2D。
/// </summary>
public sealed class LoadingState : UserControl
{
    private readonly ProgressRing _ring;
    private readonly TextBlock _message;
    private readonly StackPanel _skeleton;
    private Storyboard? _pulse;
    private bool _attached;

    public LoadingState(string message = "正在加载…")
    {
        IsHitTestVisible = false;
        HorizontalAlignment = HorizontalAlignment.Stretch;
        VerticalAlignment = VerticalAlignment.Stretch;

        _message = new TextBlock
        {
            Text = message,
            FontSize = 12.5,
            HorizontalAlignment = HorizontalAlignment.Center,
            Foreground = Res("BrushTextSecondary", ThemeManager.TextSecondary)
        };

        _ring = new ProgressRing
        {
            IsActive = true,
            Width = 34,
            Height = 34,
            HorizontalAlignment = HorizontalAlignment.Center,
            Foreground = Res("BrushMint", ThemeManager.Accent)
        };

        _skeleton = new StackPanel { Spacing = 8, Margin = new Thickness(0, 18, 0, 0), Opacity = 0.35 };
        for (var i = 0; i < 4; i++)
        {
            _skeleton.Children.Add(new Border
            {
                Height = 56,
                CornerRadius = new CornerRadius(14),
                Background = new SolidColorBrush(Color.FromArgb(14, 255, 255, 255)),
                BorderBrush = new SolidColorBrush(Color.FromArgb(22, 255, 255, 255)),
                BorderThickness = new Thickness(1)
            });
        }

        var stack = new StackPanel
        {
            Spacing = 0,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Top,
            Padding = new Thickness(8, 24, 8, 8)
        };
        stack.Children.Add(_ring);
        stack.Children.Add(_message);
        stack.Children.Add(_skeleton);

        Content = stack;
        StartPulse();
    }

    /// <summary>换一句加载提示。</summary>
    public void SetMessage(string text) => _message.Text = text;

    public void Show()
    {
        Visibility = Visibility.Visible;
        StartPulse();
    }

    public void Hide()
    {
        Visibility = Visibility.Collapsed;
        StopPulse();
    }

    /// <summary>把加载层放进宿主 Grid（自动跨所有行），并返回实例。</summary>
    public static LoadingState Attach(Grid host, string message = "正在加载…")
    {
        var state = new LoadingState(message);
        if (host.RowDefinitions.Count > 1)
            Grid.SetRowSpan(state, host.RowDefinitions.Count);
        if (host.ColumnDefinitions.Count > 1)
            Grid.SetColumnSpan(state, host.ColumnDefinitions.Count);
        Grid.SetRow(state, 0);
        Grid.SetColumn(state, 0);
        host.Children.Add(state);
        state._attached = true;
        return state;
    }

    /// <summary>从宿主上摘掉（页面卸载时调用，避免动画一直跑）。</summary>
    public void Detach()
    {
        StopPulse();
        if (!_attached) return;
        if (Parent is Panel panel) panel.Children.Remove(this);
        _attached = false;
    }

    private void StartPulse()
    {
        if (_pulse is not null) { _pulse.Resume(); return; }
        var animation = new DoubleAnimation
        {
            From = 0.28,
            To = 0.7,
            Duration = new Duration(TimeSpan.FromSeconds(1.1)),
            EnableDependentAnimation = false,
            AutoReverse = true,
            RepeatBehavior = RepeatBehavior.Forever
        };
        Storyboard.SetTarget(animation, _skeleton);
        Storyboard.SetTargetProperty(animation, "Opacity");
        _pulse = new Storyboard();
        _pulse.Children.Add(animation);
        _pulse.Begin();
    }

    private void StopPulse()
    {
        try { _pulse?.Stop(); } catch (Exception ex) { AppLog.Warn("loading", "停止加载动画失败：" + ex.Message); }
        _pulse = null;
    }

    private static Brush Res(string key, Color fallback)
    {
        try
        {
            if (Application.Current?.Resources is { } resources && resources.TryGetValue(key, out var value) && value is Brush brush)
                return brush;
        }
        catch (Exception ex)
        {
            AppLog.Warn("loading", "读取主题画刷失败：" + key + " " + ex.Message);
        }
        return new SolidColorBrush(fallback);
    }
}
