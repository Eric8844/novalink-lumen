using System;
using System.Globalization;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using NovaLink.Lumen.Core.Plugins;
using Windows.UI;

namespace Hello.World;

/// <summary>
/// 插件自带的页面。注意：这里用的是插件自己程序集里的类型，宿主通过 .pri 解析。
/// 本页同时演示 v1.5.1 的两件事：宿主玻璃组件（Ui.CreateGlassCard/ApplyGlassCard）与主题令牌（Theme.Colors/Changed）。
/// </summary>
public sealed partial class MainPage : UserControl
{
    private readonly IPluginContext _ctx;

    /// <summary>是否已经挂在 Theme.Changed 上（页面会被反复重建，必须成对加减）。</summary>
    private bool _themeWired;

    public MainPage(IPluginContext context)
    {
        _ctx = context;
        InitializeComponent();

        // 页面每次导航进来都会新建一个实例：Loaded 挂、Unloaded 摘，避免旧页面被事件拖住不放。
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        // 1) 把宿主玻璃配方套到我们自己在 XAML 里写的元素上（Border/Grid 都行）。
        //    插件不同步这些画刷，宿主切换深浅主题时会自己更新。
        _ctx.Ui.ApplyGlassCard(HeroCard);
        _ctx.Ui.ApplyGlassCard(InfoCard);

        // 2) 让宿主造一张玻璃卡片：内容由插件提供，标题行也是宿主的排版与配色。
        GlassCardHost.Content = _ctx.Ui.CreateGlassCard(BuildGlassContent(), "宿主造的玻璃卡片");

        // 3) 让宿主造一个胶囊玻璃按钮，回调里抛出异常也只记插件日志，不会影响启动器。
        GlassButtonHost.Content = _ctx.Ui.CreateGlassButton("宿主造的按钮", () =>
        {
            StatusText.Text = "宿主按钮被点击了（" + DateTime.Now.ToString("HH:mm:ss") + "）";
        });

        if (!_themeWired)
        {
            _ctx.Theme.Changed += OnThemeChanged;
            _themeWired = true;
        }

        ApplyTheme();
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        // 静态/宿主级事件必须在 Unloaded 摘掉，否则页面泄漏、切主题时还会去改已经不在树上的控件。
        if (!_themeWired) return;
        _ctx.Theme.Changed -= OnThemeChanged;
        _themeWired = false;
    }

    /// <summary>宿主切主题时会调这里：事件可能来自其他线程，先挪回 UI 线程再动控件。</summary>
    private void OnThemeChanged(object? sender, EventArgs e)
    {
        if (DispatcherQueue.HasThreadAccess)
        {
            ApplyTheme();
        }
        else
        {
            DispatcherQueue.TryEnqueue(ApplyTheme);
        }
    }

    /// <summary>按当前主题重刷本页自己需要的颜色（玻璃元素不用管，宿主负责）。</summary>
    private void ApplyTheme()
    {
        var theme = _ctx.Theme;

        ThemeText.Text = theme.IsLight
            ? "当前是浅色主题（" + theme.Name + "）：下面的色块与文字颜色都来自 context.Theme.Colors。"
            : "当前是深色主题（" + theme.Name + "）：下面的色块与文字颜色都来自 context.Theme.Colors。";

        TitleText.Foreground = Solid("Text");
        ThemeText.Foreground = Solid("TextSecondary");
        StatusText.Foreground = Solid("TextSecondary");
        AccentText.Foreground = Solid("TextSecondary");

        var accent = ColorOf("Accent", Colors.Transparent);
        AccentSwatch.Background = new SolidColorBrush(accent);
        AccentText.Text = "Accent " + ToHex(accent);
    }

    /// <summary>宿主造的卡片内容：这里用主题颜色写字，跟着深浅主题一起变。</summary>
    private UIElement BuildGlassContent()
    {
        var panel = new StackPanel { Spacing = 8 };

        panel.Children.Add(new TextBlock
        {
            Text = "这张卡片由 context.Ui.CreateGlassCard 造出来，圆角、描边、边缘折射与悬停高光都是启动器同款。",
            TextWrapping = TextWrapping.Wrap,
            Foreground = Solid("Text")
        });

        panel.Children.Add(new TextBlock
        {
            Text = "卡片里的文字颜色取 context.Theme.Colors 的 TextSecondary 键，浅色主题下会自动变深。",
            TextWrapping = TextWrapping.Wrap,
            Foreground = Solid("TextSecondary")
        });

        return panel;
    }

    private async void OnPingClick(object sender, RoutedEventArgs e)
    {
        try
        {
            StatusText.Text = "正在请求…";
            var result = await _ctx.Host.HttpGetAsync("https://api.github.com/repos/Eric8844/novalink-lumen-releases");
            StatusText.Text = result.IsSuccess
                ? "宿主 API 正常（HTTP " + result.StatusCode + "）"
                : "宿主拒绝了请求（HTTP " + result.StatusCode + "）";
        }
        catch (Exception ex)
        {
            // 插件自己的异常必须自己兜住：宿主只会禁用它，不会让整个启动器崩。
            _ctx.Log.Error("请求失败", ex);
            StatusText.Text = "请求失败，详情见插件日志。";
        }
    }

    private SolidColorBrush Solid(string key) => new(ColorOf(key, Colors.Transparent));

    /// <summary>从主题色表取颜色；键不存在或格式不对时用兜底色，绝不抛异常。</summary>
    private Color ColorOf(string key, Color fallback)
    {
        if (!_ctx.Theme.Colors.TryGetValue(key, out var text) || string.IsNullOrWhiteSpace(text)) return fallback;

        var raw = text.Trim().TrimStart('#');
        if (raw.Length == 8 && uint.TryParse(raw, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var argb))
        {
            return Color.FromArgb((byte)(argb >> 24), (byte)(argb >> 16), (byte)(argb >> 8), (byte)argb);
        }

        if (raw.Length == 6 && uint.TryParse(raw, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var rgb))
        {
            return Color.FromArgb(255, (byte)(rgb >> 16), (byte)(rgb >> 8), (byte)rgb);
        }

        return fallback;
    }

    private static string ToHex(Color color) => "#" + color.A.ToString("X2") + color.R.ToString("X2") + color.G.ToString("X2") + color.B.ToString("X2");
}
