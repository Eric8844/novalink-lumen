using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using NovaLink.Lumen.Controls;
using System;
using Windows.System;
using Windows.UI;

namespace NovaLink.Lumen.Views.Dialogs;

/// <summary>图片全屏查看器：快讯配图与模组截图共用，点开后铺满窗口显示，点空白处、关闭按钮或 Esc 退出。</summary>
public static class ImageViewer
{
    /// <summary>在当前弹窗之上打开全屏图片预览；地址不合法时静默忽略。</summary>
    public static void Show(GlassModal modal, string? url)
    {
        if (modal is null || modal.IsClosed) return;
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)) return;

        var overlay = new Grid
        {
            Background = new SolidColorBrush(Color.FromArgb(0xF4, 0x03, 0x07, 0x0D)),
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Stretch,
            IsTabStop = true,
            UseSystemFocusVisuals = false
        };
        AutomationProperties.SetAutomationId(overlay, "ImageViewerOverlay");

        var image = new Image
        {
            Stretch = Stretch.Uniform,
            Margin = new Thickness(48, 56, 48, 56)
        };
        AutomationProperties.SetAutomationId(image, "ImageViewerImage");

        // 大图加载失败时给一句人话提示，而不是留一块黑屏。
        var fallback = new TextBlock
        {
            Text = "该图片无法显示",
            FontSize = 13,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Foreground = new SolidColorBrush(Color.FromArgb(0xB0, 0xFF, 0xFF, 0xFF)),
            Visibility = Visibility.Collapsed
        };
        image.ImageFailed += (_, _) =>
        {
            image.Visibility = Visibility.Collapsed;
            fallback.Visibility = Visibility.Visible;
        };

        overlay.Children.Add(image);
        overlay.Children.Add(fallback);

        var close = new Button
        {
            Content = "关闭预览",
            Height = 32,
            Padding = new Thickness(16, 0, 16, 0),
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(0, 52, 24, 0),
            CornerRadius = new CornerRadius(10),
            FontSize = 10.5,
            BorderThickness = new Thickness(1),
            // 按钮壳统一成主题玻璃配方：浅色下是白玻璃+深字，深色下是淡玻璃+浅字，暗底上都看得清。
            BorderBrush = ThemeManager.Brush("BrushGlassRim", ThemeManager.CardBorder),
            Background = ThemeManager.Brush("BrushField", ThemeManager.ControlFill),
            Foreground = ThemeManager.BrushText
        };
        Motion.Hover(close, 1.03f, 0f);
        AutomationProperties.SetAutomationId(close, "ImageViewerClose");
        AutomationProperties.SetName(close, "关闭图片预览");
        ToolTipService.SetToolTip(close, "关闭预览（Esc）");
        close.Click += (_, _) => modal.ClearOverlay();
        overlay.Children.Add(close);

        // 提示文字也套一层玻璃胶囊，避免在亮图/暗图上糊成一片。
        var hint = new Border
        {
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Bottom,
            Margin = new Thickness(0, 0, 0, 20),
            Padding = new Thickness(14, 7, 14, 7),
            CornerRadius = new CornerRadius(999),
            BorderThickness = new Thickness(1),
            BorderBrush = ThemeManager.Brush("BrushGlassRim", ThemeManager.CardBorder),
            Background = ThemeManager.Brush("BrushField", ThemeManager.ControlFill),
            IsHitTestVisible = false,
            Child = new TextBlock
            {
                Text = "点击空白处或按 Esc 关闭",
                FontSize = 11,
                Foreground = ThemeManager.BrushTextSecondary
            }
        };
        overlay.Children.Add(hint);

        // 点空白处关闭；点到图片本身不算空白，避免误关。
        overlay.Tapped += (_, e) =>
        {
            if (ReferenceEquals(e.OriginalSource, overlay)) modal.ClearOverlay();
        };
        overlay.KeyDown += (_, e) =>
        {
            if (e.Key != VirtualKey.Escape) return;
            modal.ClearOverlay();
            e.Handled = true;
        };
        // 预览层自己拿键盘焦点，Esc 才能在任意时刻生效。
        overlay.Loaded += (_, _) => overlay.Focus(FocusState.Programmatic);

        image.Source = new BitmapImage(uri);
        modal.ShowOverlay(overlay);
    }
}
