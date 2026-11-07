using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using NovaLink.Lumen.Controls;
using NovaLink.Lumen.Services;
using NovaLink.Lumen.Services.Hooks;
using NovaLink.Lumen.Views;
using System;
using System.Threading.Tasks;
using Windows.Foundation;
using Windows.System;
using Windows.UI;

namespace NovaLink.Lumen.Views.Dialogs;

/// <summary>A dark glass modal hosted above the whole shell, independent of WinUI ContentDialog chrome.</summary>
public sealed class GlassModal
{
    private readonly Grid _host;
    private readonly Border _dim;
    private readonly Border _card;
    private readonly TextBlock _title;
    private readonly TextBlock _subtitle;
    private readonly Button _primary;
    private readonly Button _secondary;
    private readonly Button _close;
    private readonly TaskCompletionSource<bool> _done = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly ScaleTransform _cardScale = new();
    private readonly TranslateTransform _cardShift = new();
    private bool _allowClose = true;
    private bool _closing;
    private FrameworkElement? _overlay;

    public static GlassModal? Current { get; private set; }

    public StackPanel Body { get; }
    public bool IsClosed => _done.Task.IsCompleted;

    public string PrimaryText { set => _primary.Content = value; }
    public string SecondaryText { set => _secondary.Content = value; }

    public Func<Task>? PrimaryInvoked { get; set; }
    public Func<Task>? SecondaryInvoked { get; set; }
    public event Action? Closed;

    private GlassModal(Grid host, string title, string? subtitle, double width)
    {
        _host = host;
        var dimColor = ThemeManager.Dim;
        _dim = new Border
        {
            Background = new SolidColorBrush(Color.FromArgb(
                ThemeManager.IsLight ? (byte)0x4A : (byte)0x66,
                dimColor.R, dimColor.G, dimColor.B))
        };
        _dim.PointerPressed += (_, e) =>
        {
            if (_allowClose && ReferenceEquals(e.OriginalSource, _dim)) Close();
        };

        _title = new TextBlock
        {
            Text = title,
            FontSize = 21,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            Foreground = new SolidColorBrush(ThemeManager.Text)
        };
        _subtitle = new TextBlock
        {
            Text = subtitle ?? "",
            FontSize = 12.5,
            TextWrapping = TextWrapping.Wrap,
            Foreground = new SolidColorBrush(ThemeManager.TextSecondary),
            Visibility = string.IsNullOrEmpty(subtitle) ? Visibility.Collapsed : Visibility.Visible
        };
        var titleStack = new StackPanel { Spacing = 6 };
        titleStack.Children.Add(_title);
        titleStack.Children.Add(_subtitle);

        _close = new Button
        {
            Content = new FontIcon { Glyph = "\uE711", FontSize = 12 },
            Width = 34,
            Height = 34,
            Padding = new Thickness(0),
            CornerRadius = new CornerRadius(17),
            Background = new SolidColorBrush(ThemeManager.ControlFill),
            BorderThickness = new Thickness(0),
            Foreground = new SolidColorBrush(ThemeManager.TextSecondary),
            VerticalAlignment = VerticalAlignment.Top
        };
        _close.Click += (_, _) => { if (_allowClose) Close(); };

        var header = new Grid();
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        header.Children.Add(titleStack);
        Grid.SetColumn(_close, 1);
        header.Children.Add(_close);

        Body = new StackPanel { Spacing = 13 };

        _primary = new Button
        {
            Content = "确定",
            MinWidth = 118,
            Height = 42,
            Padding = new Thickness(24, 0, 24, 0),
            CornerRadius = new CornerRadius(13),
            Background = new LinearGradientBrush
            {
                StartPoint = new Windows.Foundation.Point(0, 0),
                EndPoint = new Windows.Foundation.Point(1, 1),
                GradientStops =
                {
                    new GradientStop { Color = Color.FromArgb(0xFF, 0x7C, 0xEB, 0xD6), Offset = 0 },
                    new GradientStop { Color = Color.FromArgb(0xFF, 0xA9, 0x96, 0xFF), Offset = 1 }
                }
            },
            Foreground = new SolidColorBrush(Color.FromArgb(0xFF, 0x06, 0x10, 0x1C)),
            BorderThickness = new Thickness(0),
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold
        };
        _primary.Click += async (_, _) =>
        {
            if (PrimaryInvoked is null) { Close(); return; }
            _primary.IsEnabled = false;
            if (_secondary is not null) _secondary.IsEnabled = false;
            try { await PrimaryInvoked(); }
            catch (Exception ex) { AppLog.Error("modal", "弹窗操作失败", ex); }
            finally
            {
                if (!IsClosed)
                {
                    _primary.IsEnabled = true;
                    if (_secondary is not null) _secondary.IsEnabled = true;
                }
            }
        };

        _secondary = new Button
        {
            Content = "取消",
            MinWidth = 96,
            Height = 42,
            Padding = new Thickness(20, 0, 20, 0),
            CornerRadius = new CornerRadius(13),
            Background = new SolidColorBrush(ThemeManager.ControlFill),
            Foreground = new SolidColorBrush(ThemeManager.Text),
            BorderBrush = new SolidColorBrush(ThemeManager.ControlBorder),
            BorderThickness = new Thickness(1)
        };
        _secondary.Click += async (_, _) =>
        {
            if (SecondaryInvoked is null)
            {
                if (_allowClose) Close();
                return;
            }
            _secondary.IsEnabled = false;
            _primary.IsEnabled = false;
            try { await SecondaryInvoked(); }
            catch (Exception ex) { AppLog.Error("modal", "弹窗次要操作失败", ex); }
            finally
            {
                if (!IsClosed)
                {
                    _secondary.IsEnabled = true;
                    _primary.IsEnabled = true;
                }
            }
        };

        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 10,
            HorizontalAlignment = HorizontalAlignment.Right
        };
        buttons.Children.Add(_secondary);
        buttons.Children.Add(_primary);

        var cardGrid = new Grid { Padding = new Thickness(26, 22, 26, 22), RowSpacing = 20 };
        cardGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        cardGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        cardGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        cardGrid.Children.Add(header);
        Grid.SetRow(Body, 1);
        cardGrid.Children.Add(Body);
        Grid.SetRow(buttons, 2);
        cardGrid.Children.Add(buttons);

        // 液态玻璃壳：Acrylic 磨砂底 + 玻璃描边 + 顶部 1.5px 高光，全部取主题画刷，深浅色自动跟随。
        var glassRoot = new Grid();
        glassRoot.Children.Add(cardGrid);
        glassRoot.Children.Add(TopHighlight(22));

        _card = new Border
        {
            Width = width,
            MaxHeight = 720,
            Background = GlassLiquidBrush(),
            BorderBrush = ThemeManager.Brush("BrushGlassRim", ThemeManager.CardBorder),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(22),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            RenderTransformOrigin = new Point(0.5, 0.5),
            RenderTransform = new TransformGroup { Children = { _cardScale, _cardShift } },
            Child = glassRoot,
            RequestedTheme = ThemeManager.IsLight ? ElementTheme.Light : ElementTheme.Dark
        };


        _host.Children.Add(_dim);
        _host.Children.Add(_card);
        _host.Visibility = Visibility.Visible;
        _host.IsHitTestVisible = true;
        _dim.Opacity = 0;
        _card.Opacity = 0;
        Motion.AttachButtons(_card);
        AnimateIn();
    }

    public static GlassModal Show(string title, string? subtitle = null, double width = 520)
    {
        var host = MainWindow.Current?.ModalRoot
            ?? throw new InvalidOperationException("主窗口尚未准备好。");
        if (Current is { IsClosed: false })
            throw new InvalidOperationException("已有弹窗正在显示。");
        // 文本替换钩子：插件可改写所有弹窗的标题/副标题（本地化、术语替换等）。
        var modal = new GlassModal(
            host,
            HookRegistry.ApplyTextReplace(title),
            subtitle is null ? null : HookRegistry.ApplyTextReplace(subtitle),
            width);
        Current = modal;
        return modal;
    }

    public Task<bool> WaitAsync() => _done.Task;

    public void HideButtons()
    {
        _primary.Visibility = Visibility.Collapsed;
        _secondary.Visibility = Visibility.Collapsed;
    }

    public void HideHeader()
    {
        _title.Visibility = Visibility.Collapsed;
        _subtitle.Visibility = Visibility.Collapsed;
        _close.Visibility = Visibility.Collapsed;
    }

    /// <summary>液态玻璃底：优先用主题里的 Acrylic 画刷（BrushGlassLiquid），取不到时退回卡片底色。</summary>
    public static Brush GlassLiquidBrush() => ThemeManager.Brush("BrushGlassLiquid", ThemeManager.CardSurface);

    /// <summary>
    /// 液态玻璃顶部的一层柔光：从顶端往下淡出的竖向渐变，跟容器同圆角、不参与命中测试。
    /// 不再画 1px 硬横线（那种横线在卡片上会被看成多出来的一条分隔线），
    /// 参数 <paramref name="thickness"/> 表示光带高度；传 1~2 这种旧值时按默认 60px 处理。
    /// </summary>
    public static Border TopHighlight(double cornerRadius, double thickness = 1.5) => new()
    {
        Height = thickness <= 2 ? 60 : thickness,
        Margin = new Thickness(1, 0, 1, 0),
        HorizontalAlignment = HorizontalAlignment.Stretch,
        VerticalAlignment = VerticalAlignment.Top,
        CornerRadius = new CornerRadius(cornerRadius),
        Background = ThemeManager.Brush("BrushGlassTopBand", ThemeManager.CardBorder),
        IsHitTestVisible = false
    };

    public void HideSecondary() => _secondary.Visibility = Visibility.Collapsed;
    public void HidePrimary() => _primary.Visibility = Visibility.Collapsed;
    public void SetCloseAllowed(bool allowed) => _allowClose = allowed;

    /// <summary>Shows a full-window layer above the modal card (used by the gallery lightbox).</summary>
    public void ShowOverlay(FrameworkElement overlay)
    {
        ClearOverlay();
        _overlay = overlay;
        _host.Children.Add(overlay);
    }

    public void ClearOverlay()
    {
        if (_overlay is null) return;
        _host.Children.Remove(_overlay);
        _overlay = null;
    }

    public void SetBusy(bool busy)
    {
        _primary.IsEnabled = !busy;
        _secondary.IsEnabled = !busy;
        _close.IsEnabled = !busy;
        _allowClose = !busy;
    }

    public void Close()
    {
        if (IsClosed || _closing) return;
        _closing = true;
        ClearOverlay();
        if (ReferenceEquals(Current, this)) Current = null;
        _host.IsHitTestVisible = false;
        Closed?.Invoke();
        _done.TrySetResult(true);
        _ = AnimateOutAsync();
    }

    private void AnimateIn()
    {
        _cardScale.ScaleX = 0.965;
        _cardScale.ScaleY = 0.965;
        _cardShift.Y = 18;
        var story = new Storyboard();
        // 时长统一走 Motion 常量，别再写裸数字：遮罩 340ms，卡片 230ms 淡入 + 460ms 上浮。
        AddFade(_dim, 0, 1, Motion.RevealMs, 0);
        AddFade(_card, 0, 1, Motion.HoverOutMs, Motion.StaggerMs / 2);
        AddScale(_cardScale, 0.965, 1, Motion.RiseMs, Motion.StaggerMs, new BackEase { Amplitude = 0.18, EasingMode = EasingMode.EaseOut });
        AddY(_cardShift, 18, 0, Motion.RiseMs, Motion.StaggerMs);
        story.Begin();
        _ = FocusAsync();
    }

    private async Task FocusAsync()
    {
        await Task.Delay(Motion.HoverInMs);
        if (!IsClosed) _primary.Focus(FocusState.Programmatic);
    }

    private async Task AnimateOutAsync()
    {
        var story = new Storyboard();
        AddFade(_dim, _dim.Opacity, 0, Motion.HoverInMs, 0);
        AddFade(_card, _card.Opacity, 0, Motion.HoverInMs, 0);
        AddScale(_cardScale, 1, 0.975, Motion.HoverInMs, 0, new CubicEase { EasingMode = EasingMode.EaseIn });
        AddY(_cardShift, _cardShift.Y, 8, Motion.HoverInMs, 0);
        story.Begin();
        await Task.Delay(Motion.HoverOutMs);
        _host.Children.Remove(_dim);
        _host.Children.Remove(_card);
        if (_host.Children.Count == 0)
        {
            _host.Visibility = Visibility.Collapsed;
        }
    }

    private static void AddFade(DependencyObject target, double from, double to, int durationMs, int delayMs)
    {
        var story = new Storyboard();
        var anim = new DoubleAnimation
        {
            From = from,
            To = to,
            Duration = new Duration(TimeSpan.FromMilliseconds(durationMs)),
            BeginTime = TimeSpan.FromMilliseconds(delayMs),
            EnableDependentAnimation = true,
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
        };
        Storyboard.SetTarget(anim, target);
        Storyboard.SetTargetProperty(anim, "Opacity");
        story.Children.Add(anim);
        story.Begin();
    }

    private static void AddScale(ScaleTransform target, double from, double to, int durationMs, int delayMs, EasingFunctionBase ease)
    {
        var story = new Storyboard();
        var x = new DoubleAnimation
        {
            From = from,
            To = to,
            Duration = new Duration(TimeSpan.FromMilliseconds(durationMs)),
            BeginTime = TimeSpan.FromMilliseconds(delayMs),
            EnableDependentAnimation = true,
            EasingFunction = ease
        };
        Storyboard.SetTarget(x, target);
        Storyboard.SetTargetProperty(x, "ScaleX");
        story.Children.Add(x);
        var y = new DoubleAnimation
        {
            From = from,
            To = to,
            Duration = new Duration(TimeSpan.FromMilliseconds(durationMs)),
            BeginTime = TimeSpan.FromMilliseconds(delayMs),
            EnableDependentAnimation = true,
            EasingFunction = ease
        };
        Storyboard.SetTarget(y, target);
        Storyboard.SetTargetProperty(y, "ScaleY");
        story.Children.Add(y);
        story.Begin();
    }

    private static void AddY(TranslateTransform target, double from, double to, int durationMs, int delayMs)
    {
        var story = new Storyboard();
        var anim = new DoubleAnimation
        {
            From = from,
            To = to,
            Duration = new Duration(TimeSpan.FromMilliseconds(durationMs)),
            BeginTime = TimeSpan.FromMilliseconds(delayMs),
            EnableDependentAnimation = true,
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
        };
        Storyboard.SetTarget(anim, target);
        Storyboard.SetTargetProperty(anim, "Y");
        story.Children.Add(anim);
        story.Begin();
    }
}
