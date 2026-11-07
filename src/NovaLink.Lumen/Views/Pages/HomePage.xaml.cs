using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using Microsoft.UI.Xaml.Media.Imaging;
using NovaLink.Lumen.Controls;
using NovaLink.Lumen.Core.Models;
using NovaLink.Lumen.Services;
using NovaLink.Lumen.Services.Minecraft;
using NovaLink.Lumen.Views;
using NovaLink.Lumen.Views.Dialogs;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Windows.Foundation;
using Windows.Storage.Pickers;
using Windows.System;

namespace NovaLink.Lumen.Views.Pages;

public sealed partial class HomePage : Page
{
    private const int RenderBatchSize = 6;

    private IReadOnlyList<InstanceSummary> _instances = Array.Empty<InstanceSummary>();
    private bool _hooked;
    private bool _loading;
    private bool _launching;
    private LoadingState? _busy;
    private int _renderToken;
    private CancellationTokenSource? _greetingCts;
    private CancellationTokenSource? _taglineCts;
    private CancellationTokenSource? _heroArtCts;
    private CancellationTokenSource? _skinCts;
    private string _lastGreeting = "";
    private string _lastTagline = "";
    private string _taglineLoadingVersion = "";
    private string _lastHeroArt = "";

    public HomePage()
    {
        InitializeComponent();
        EnsureBusy().Hide();
        Loaded += OnLoaded;
        GlassPanes.Track(this);
        Unloaded += OnUnloaded;
    }

    private LoadingState EnsureBusy()
    {
        if (_busy is null || _busy.Parent is null)
            _busy = LoadingState.Attach(PageRoot, "正在加载首页…");
        return _busy;
    }

    private void ShowBusy(string message)
    {
        var busy = EnsureBusy();
        busy.SetMessage(message);
        busy.Show();
    }

    private void HideBusy() => _busy?.Hide();

    private void HomePage_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        var compact = e.NewSize.Width < 1180;
        HeroQuote.Visibility = compact ? Visibility.Collapsed : Visibility.Visible;
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (!_hooked)
        {
            _hooked = true;
            LumenRuntime.Accounts.AccountsChanged += OnDataChanged;
            LumenRuntime.Settings.Changed += OnDataChanged;
        }
        RenderGreeting(true);
        await ReloadAsync();
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        // 页面离开：作废进行中的渲染，摘掉加载层，避免动画在后台继续跑。
        _renderToken++;
        _loading = false;
        _busy?.Detach();
        _busy = null;
        if (!_hooked) return;
        _hooked = false;
        LumenRuntime.Accounts.AccountsChanged -= OnDataChanged;
        LumenRuntime.Settings.Changed -= OnDataChanged;
        _greetingCts?.Cancel();
        _taglineCts?.Cancel();
        _heroArtCts?.Cancel();
    }


    private void OnDataChanged(object? sender, EventArgs e)
        => DispatcherQueue.TryEnqueue(() => _ = ReloadAsync());

    /// <summary>外壳在游戏退出后调用，让「上次游玩」和数据统计立即反映这一局。</summary>
    public void Reload() => _ = ReloadAsync();

    private void RenderGreeting(bool force = false)
    {
        var now = DateTime.Now;
        var hour = now.Hour;
        var word = hour < 5 ? "夜深了" : hour < 11 ? "早上好" : hour < 14 ? "中午好" : hour < 18 ? "下午好" : "晚上好";
        var account = LumenRuntime.Accounts.SelectedPublic();
        var name = account?.Name ?? "Nova";
        var greeting = word + "，" + name + "。";
        if (force || greeting != _lastGreeting)
        {
            _lastGreeting = greeting;
            StartTypewriter(Greeting, greeting, ref _greetingCts);
        }
        var weekday = now.ToString("dddd", new CultureInfo("zh-CN")).Replace("星期", "周");
        DateText.Text = now.ToString("M月d日", new CultureInfo("zh-CN")) + weekday;
        GreetingSub.Text = hour is >= 23 or < 5
            ? "夜里的光更安静。世界仍在等你。"
            : "光已经折射就绪。你的世界正在等待下一次启程。";
        GameRootText.Text = ShortPath(LumenRuntime.Settings.Settings.GameRoot);
    }

    private static string ShortPath(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return ".lumen";
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return path.StartsWith(home, StringComparison.OrdinalIgnoreCase)
            ? "~" + path[home.Length..]
            : path;
    }

    private async Task ReloadAsync()
    {
        if (_loading) return;
        _loading = true;
        var token = ++_renderToken;
        ShowBusy("正在加载首页…");
        try
        {
            // 先把加载动画画上一帧，再去后台扫盘。
            await UiYield.NextFrameAsync();
            // 内容一端出现就收起加载层，避免转圈与已渲染内容叠在一起。
            HideBusy();
            RenderGreeting(false);
            var scanned = await Task.Run(async () => await LumenRuntime.Instances.ScanAsync());
            if (token != _renderToken) return;
            _instances = scanned;
            var current = ActiveInstance;
            RenderHero(current);
            RenderAccount();
            await UiYield.NextFrameAsync();
            if (token != _renderToken) return;
            await RenderVersionsAsync(token);
            UpdatePlayState(LumenRuntime.Launch.State);
            await RenderNewsAsync(false, token);
            if (token != _renderToken) return;
            DispatcherQueue.TryEnqueue(() => GlassPanes.Track(this));
        }
        catch (Exception ex)
        {
            AppLog.Error("home", "加载首页失败", ex);
        }
        finally
        {
            if (token == _renderToken)
            {
                _loading = false;
                HideBusy();
            }
        }
    }

    private InstanceSummary? ActiveInstance
    {
        get
        {
            var id = LumenRuntime.Settings.Settings.SelectedInstanceId;
            return _instances.FirstOrDefault(x => string.Equals(x.Id, id, StringComparison.OrdinalIgnoreCase))
                ?? _instances.FirstOrDefault();
        }
    }

    private void RenderHero(InstanceSummary? current)
    {
        var settings = LumenRuntime.Settings.Settings;
        if (current is null)
        {
            _heroArtCts?.Cancel();
            ApplyHeroArt("ms-appx:///Assets/Images/hero-wilderness.jpg");
            HeroVersion.Text = "—";
            HeroKernel.Text = "尚未安装版本";
            HeroState.Text = "NO INSTANCE";
            HeroName.Text = "前往版本页下载并安装你的第一个 Minecraft 实例";
            HeroMods.Text = "0 个模组";
            HeroWorlds.Text = "0 个世界";
            HeroMemory.Text = (settings.Memory > 0 ? settings.Memory : 4096) + " MB";
            HeroJava.Text = "JAVA —";
            LastPlayedTop.Text = "尚未启动过";
            ContinueName.Text = "尚未安装版本";
            ContinueMeta.Text = "从版本页开始";
            ContinueWorlds.Text = "0 个世界";
            ContinueMods.Text = "0 个模组";
            ReadyText.Text = "未就绪";
            SetTagline("先去版本页挑一束光，再启程。", true);
            return;
        }

        _ = RefreshHeroArtAsync(current);
        HeroVersion.Text = current.Kernel;
        HeroKernel.Text = "内核 " + current.Kernel + (string.IsNullOrWhiteSpace(current.LoaderVersion) ? "" : " · " + current.Loader);
        HeroState.Text = "INSTANCE READY";
        HeroName.Text = current.Name;
        HeroMods.Text = current.Mods + " 个模组";
        HeroWorlds.Text = current.Worlds + " 个世界";
        HeroMemory.Text = (settings.Memory > 0 ? settings.Memory : 4096) + " MB";
        HeroJava.Text = "JAVA " + (string.IsNullOrWhiteSpace(settings.JavaPath) ? "自动" : "已配置");
        LastPlayedTop.Text = current.LastPlayed > 0 ? "上次游玩 " + Ago(current.LastPlayed) : "尚未启动过";
        ContinueName.Text = current.Name;
        ContinueMeta.Text = current.Kernel + " · " + (current.Loader ?? "Vanilla") + " · " + Ago(current.LastPlayed);
        ContinueWorlds.Text = current.Worlds + " 个世界";
        ContinueMods.Text = current.Mods + " 个模组";
        ReadyText.Text = "就绪";

        var cached = LumenRuntime.HeroTaglines.Cached(current.Kernel);
        if (cached.Count > 0)
        {
            SetTagline(PickTagline(cached), true);
        }
        else
        {
            SetTagline(LumenRuntime.HeroTaglines.Fallback(current.Kernel), true);
            if (_taglineLoadingVersion != current.Kernel)
            {
                _taglineLoadingVersion = current.Kernel;
                _ = RefreshTaglineAsync(current.Kernel);
            }
        }
    }

    private async Task RefreshHeroArtAsync(InstanceSummary current)
    {
        _heroArtCts?.Cancel();
        var cts = new CancellationTokenSource();
        _heroArtCts = cts;
        try
        {
            var source = await Task.Run(async () => await LumenRuntime.HeroArt.GetAsync(
                current.Kernel, current.Id, current.Loader ?? "Vanilla", cts.Token));
            if (cts.IsCancellationRequested) return;
            DispatcherQueue.TryEnqueue(() =>
            {
                if (!cts.IsCancellationRequested) ApplyHeroArt(source);
            });
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            AppLog.Error("home", "更新启动卡片背景失败", ex);
        }
    }

    private void ApplyHeroArt(string source)
    {
        if (string.IsNullOrWhiteSpace(source) || source == _lastHeroArt) return;
        try
        {
            _lastHeroArt = source;
            HeroArt.Background = new ImageBrush
            {
                ImageSource = new BitmapImage(new Uri(source)),
                Stretch = Stretch.UniformToFill
            };
            HeroArt.Opacity = 1;
            var fade = new DoubleAnimation
            {
                From = 0,
                To = 1,
                Duration = new Duration(TimeSpan.FromMilliseconds(Motion.RevealMs)),
                EnableDependentAnimation = true
            };
            Storyboard.SetTarget(fade, HeroArt);
            Storyboard.SetTargetProperty(fade, "Opacity");
            var story = new Storyboard();
            story.Children.Add(fade);
            story.Begin();
        }
        catch (Exception ex)
        {
            AppLog.Warn("home", "应用启动卡片背景失败：" + ex.Message);
        }
    }

    private async Task RefreshTaglineAsync(string version)
    {
        try
        {
            var lines = await Task.Run(async () => await LumenRuntime.HeroTaglines.GetAsync(version));
            if (lines.Count == 0) return;
            DispatcherQueue.TryEnqueue(() => SetTagline(PickTagline(lines), false));
        }
        catch (Exception ex)
        {
            AppLog.Error("home", "更新启动标语失败", ex);
        }
        finally
        {
            if (_taglineLoadingVersion == version) _taglineLoadingVersion = "";
        }
    }

    private string PickTagline(IReadOnlyList<string> lines)
    {
        if (lines.Count == 0) return "";
        if (lines.Count == 1) return lines[0];
        for (var attempt = 0; attempt < 4; attempt++)
        {
            var pick = lines[Random.Shared.Next(lines.Count)];
            if (pick != _lastTagline) return pick;
        }
        return lines[0];
    }

    private void SetTagline(string text, bool force)
    {
        if (string.IsNullOrWhiteSpace(text)) return;
        if (!force && text == _lastTagline) return;
        _lastTagline = text;
        StartTypewriter(HeroTagline, text, ref _taglineCts);
    }

    private void StartTypewriter(TextBlock target, string text, ref CancellationTokenSource? slot)
    {
        slot?.Cancel();
        slot?.Dispose();
        slot = new CancellationTokenSource();
        _ = TypeTextAsync(target, text, slot.Token);
    }

    private static async Task TypeTextAsync(TextBlock target, string text, CancellationToken ct)
    {
        try
        {
            target.Text = "";
            for (var i = 1; i <= text.Length; i++)
            {
                target.Text = text[..i];
                await Task.Delay(i == 1 ? 110 : 30, ct);
            }
            target.Text = text;
        }
        catch (OperationCanceledException)
        {
        }
    }

    private void RenderAccount()
    {
        var account = LumenRuntime.Accounts.SelectedPublic();
        if (account is null)
        {
            HomeAccountName.Text = "未登录";
            HomeAccountKind.Text = "添加一个账户开始游戏";
            AccountState.Text = "等待账户";
            AccountDot.Fill = (Brush)Application.Current.Resources["BrushTextTertiary"];
            AccountState.Foreground = (Brush)Application.Current.Resources["BrushTextTertiary"];
            AccountInitialHome.Text = "N";
            HomeAvatar.Source = null;
            HomeAvatar.Visibility = Visibility.Collapsed;
            _skinCts?.Cancel();
            AccountModel.Slim = false;
            AccountModel.UseDefaultSkin();
            return;
        }

        HomeAccountName.Text = account.Name;
        HomeAccountKind.Text = account.Subtitle;
        AccountState.Text = "已连接";
        AccountDot.Fill = (Brush)Application.Current.Resources["BrushMint"];
        AccountState.Foreground = (Brush)Application.Current.Resources["BrushMint"];
        AccountInitialHome.Text = string.IsNullOrWhiteSpace(account.Initial) ? "N" : account.Initial;
        HomeAvatar.Visibility = Visibility.Visible;
        try
        {
            HomeAvatar.Source = string.IsNullOrWhiteSpace(account.AvatarUrl) ? null : new BitmapImage(new Uri(account.AvatarUrl));
        }
        catch { HomeAvatar.Source = null; }

        // 3D 人物要的是完整账号记录（皮肤路径 / UUID / 皮肤变体），公开投影里没有这些字段。
        var record = LumenRuntime.Accounts.Selected;
        if (record is not null) _ = LoadAccountSkinAsync(record);
    }

    /// <summary>
    /// 把当前账号的皮肤喂给 3D 人物：先用记录里已有的本地皮肤（离线账号上传的 / 之前缓存过的）
    /// 立刻上屏，再等 <see cref="LumenRuntime.Skins"/> 按 UUID 拉到整张皮肤后换上去。
    /// 任何一步失败都保持当前画面，最终默认是内置史蒂夫。
    /// </summary>
    private async Task LoadAccountSkinAsync(AccountRecord account)
    {
        _skinCts?.Cancel();
        var cts = new CancellationTokenSource();
        _skinCts = cts;

        try
        {
            AccountModel.Slim = IsSlimSkin(account);

            var local = account.SkinPath;
            if (!string.IsNullOrWhiteSpace(local) && File.Exists(local))
                await AccountModel.LoadSkinAsync(local);
            else
                AccountModel.UseDefaultSkin();

            var path = await LumenRuntime.Skins.EnsureAsync(account, cts.Token);
            if (cts.IsCancellationRequested || path is null) return;
            await AccountModel.LoadSkinAsync(path);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            AppLog.Warn("skin", "加载主页人物皮肤失败：" + ex.Message);
        }
    }

    private static bool IsSlimSkin(AccountRecord account)
    {
        var variant = account.Skins?.FirstOrDefault()?.Variant;
        return variant is not null && variant.Contains("slim", StringComparison.OrdinalIgnoreCase);
    }

    private async Task RenderVersionsAsync(int token)
    {
        VersionCountText.Text = _instances.Count + " 个已安装";
        VersionList.Children.Clear();
        var active = ActiveInstance;
        var visible = (active is null
            ? _instances.Take(3)
            : _instances.OrderByDescending(x => string.Equals(x.Id, active.Id, StringComparison.OrdinalIgnoreCase)).Take(3))
            .ToList();
        var built = 0;
        foreach (var instance in visible)
        {
            // 分批上屏并让出帧，避免一次建太多视觉树把 UI 线程占满。
            if (token != _renderToken) return;
            var card = BuildVersionCard(instance);
            VersionList.Children.Add(card);
            Motion.RowIn(card, built % RenderBatchSize);
            if (++built % RenderBatchSize == 0) await UiYield.NextFrameAsync();
        }
        if (_instances.Count == 0)
        {
            var empty = new Border
            {
                Height = 112,
                CornerRadius = new CornerRadius(18),
                BorderThickness = new Thickness(1),
                BorderBrush = (Brush)Application.Current.Resources["BrushStroke"],
                Background = (Brush)Application.Current.Resources["BrushGlassFaint"],
                Child = new TextBlock
                {
                    Text = "还没有版本实例",
                    FontSize = 12.5,
                    Foreground = (Brush)Application.Current.Resources["BrushTextTertiary"],
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center
                }
            };
            VersionList.Children.Add(empty);
        }
    }

    private FrameworkElement BuildVersionCard(InstanceSummary instance)
    {
        var isActive = string.Equals(instance.Id, ActiveInstance?.Id, StringComparison.OrdinalIgnoreCase);
        var card = new Border
        {
            Width = 202,
            Height = 112,
            CornerRadius = new CornerRadius(18),
            BorderThickness = new Thickness(isActive ? 1.4 : 1),
            BorderBrush = isActive
                ? (Brush)Application.Current.Resources["BrushMint"]
                : (Brush)Application.Current.Resources["BrushStroke"],
            Background = (Brush)Application.Current.Resources["BrushGlassFaint"],
            Padding = new Thickness(14, 12, 14, 12),
            Tag = instance.Id
        };
        card.Tapped += VersionCard_Tapped;
        Motion.Hover(card, 1.012f, 3f);

        var grid = new Grid();
        // 水印用加载器首字母（F/V/M），不要用名字首字母：版本名多以数字开头（26.2-…），
        // 会变成一个大号「2」，看起来像排版事故。
        var loader = instance.Loader;
        var watermarkText = string.IsNullOrWhiteSpace(loader)
            ? "V"
            : char.ToUpperInvariant(loader![0]).ToString();
        var watermark = new TextBlock
        {
            Text = watermarkText,
            FontSize = 62,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            Foreground = ThemeManager.BrushText,
            Opacity = 0.055,
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Bottom,
            Margin = new Thickness(0, 0, 2, -10)
        };
        grid.Children.Add(watermark);

        var stack = new StackPanel { Spacing = 6 };
        var badge = new Border
        {
            HorizontalAlignment = HorizontalAlignment.Left,
            CornerRadius = new CornerRadius(6),
            Padding = new Thickness(8, 3, 8, 3),
            Background = ThemeManager.Brush("BrushAccentSoft", ThemeManager.Accent),
            BorderBrush = ThemeManager.Brush("BrushNavSelectedBorder", ThemeManager.Accent),
            BorderThickness = new Thickness(1),
            Child = new TextBlock
            {
                Text = (instance.Loader ?? "Vanilla").ToUpperInvariant(),
                FontSize = 8.5,
                CharacterSpacing = 90,
                FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                Foreground = (Brush)Application.Current.Resources["BrushMint"]
            }
        };
        stack.Children.Add(badge);
        stack.Children.Add(new TextBlock
        {
            Text = instance.Name,
            FontSize = 14,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            TextTrimming = TextTrimming.CharacterEllipsis,
            Margin = new Thickness(0, 8, 0, 0)
        });
        stack.Children.Add(new TextBlock
        {
            Text = "内核 " + instance.Kernel + " · " + instance.Mods + " 模组 · " + instance.Worlds + " 世界",
            FontSize = 9.5,
            Foreground = (Brush)Application.Current.Resources["BrushTextTertiary"],
            TextTrimming = TextTrimming.CharacterEllipsis
        });
        grid.Children.Add(stack);
        card.Child = grid;
        return card;
    }

    private async Task RenderNewsAsync(bool force, int token)
    {
        var items = await Task.Run(async () => await LumenRuntime.News.GetAsync(force));
        if (token != _renderToken) return;
        NewsList.Children.Clear();
        var built = 0;
        foreach (var item in items.Take(4))
        {
            if (token != _renderToken) return;
            var content = new Grid();
            content.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(54) });
            content.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

            var thumb = new Border
            {
                Width = 50,
                Height = 44,
                CornerRadius = new CornerRadius(10),
                Background = (Brush)Application.Current.Resources["BrushGlassFaint"],
                VerticalAlignment = VerticalAlignment.Center
            };
            try
            {
                thumb.Background = new ImageBrush
                {
                    ImageSource = new BitmapImage(new Uri(item.ImageUrl)),
                    Stretch = Stretch.UniformToFill
                };
            }
            catch { }
            content.Children.Add(thumb);

            var text = new StackPanel { Spacing = 1, VerticalAlignment = VerticalAlignment.Center };
            text.Children.Add(new TextBlock
            {
                Text = item.Title,
                FontSize = 12,
                FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                TextTrimming = TextTrimming.CharacterEllipsis,
                MaxLines = 1
            });
            text.Children.Add(new TextBlock
            {
                Text = item.Category.ToUpperInvariant() + " · " + FriendlyDate(item.Date),
                FontSize = 8.5,
                CharacterSpacing = 60,
                Foreground = (Brush)Application.Current.Resources["BrushTextTertiary"],
                TextTrimming = TextTrimming.CharacterEllipsis,
                MaxLines = 1
            });
            Grid.SetColumn(text, 1);
            content.Children.Add(text);

            var row = new Button
            {
                Height = 58,
                Tag = item,
                Background = new SolidColorBrush(Windows.UI.Color.FromArgb(0, 0, 0, 0)),
                BorderThickness = new Thickness(0),
                Padding = new Thickness(0),
                CornerRadius = new CornerRadius(10),
                HorizontalAlignment = HorizontalAlignment.Stretch,
                HorizontalContentAlignment = HorizontalAlignment.Stretch,
                VerticalContentAlignment = VerticalAlignment.Stretch,
                Content = content
            };
            row.Click += NewsRow_Click;
            NewsList.Children.Add(row);
            Motion.RowIn(row, built % RenderBatchSize);
            Motion.Hover(row, 1.006f, 1.4f);
            if (++built % RenderBatchSize == 0) await UiYield.NextFrameAsync();
        }
    }


    private static string FriendlyDate(string date)
    {
        if (DateTimeOffset.TryParse(date, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var parsed))
            return parsed.ToString("yyyy年M月d日", new CultureInfo("zh-CN"));
        return date;
    }

    private static string Ago(long unixMs)
    {
        if (unixMs <= 0) return "尚未启动";
        var span = DateTimeOffset.UtcNow - DateTimeOffset.FromUnixTimeMilliseconds(unixMs);
        if (span.TotalMinutes < 1) return "刚刚";
        if (span.TotalMinutes < 60) return (int)span.TotalMinutes + " 分钟前";
        if (span.TotalHours < 24) return (int)span.TotalHours + " 小时前";
        if (span.TotalDays < 30) return (int)span.TotalDays + " 天前";
        return DateTimeOffset.FromUnixTimeMilliseconds(unixMs).ToLocalTime().ToString("yyyy年M月d日");
    }

    private void HomeAvatar_ImageFailed(object sender, ExceptionRoutedEventArgs e)
        => HomeAvatar.Visibility = Visibility.Collapsed;

    private void VersionCard_Tapped(object sender, TappedRoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: string id } && !string.IsNullOrWhiteSpace(id))
        {
            var settings = LumenRuntime.Settings.Settings;
            if (!string.Equals(settings.SelectedInstanceId, id, StringComparison.OrdinalIgnoreCase))
            {
                settings.SelectedInstanceId = id;
                LumenRuntime.Settings.Save();
            }
        }
        ShellPage.Current?.Navigate("versions");
    }

    private async void NewsRow_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: NewsItem item })
            await NewsDialog.ShowAsync(XamlRoot, item);
    }

    private async void Play_Click(object sender, RoutedEventArgs e)
    {
        var instance = ActiveInstance;
        if (instance is null)
        {
            ShellPage.Current?.Navigate("versions");
            return;
        }
        var state = LumenRuntime.Launch.State;
        if (state.Phase == GamePhase.Running)
        {
            if (string.Equals(state.InstanceId, instance.Id, StringComparison.OrdinalIgnoreCase))
                LumenRuntime.Launch.Kill();
            else
                await ShowAsync("游戏正在运行", "请先关闭当前游戏，再启动另一个实例。");
            return;
        }
        if (state.Phase is GamePhase.Preparing or GamePhase.Starting)
        {
            ProgressCenter.OpenOverlay();
            return;
        }
        if (_launching) return;
        var account = LumenRuntime.Accounts.Selected;
        if (account is null)
        {
            var name = await OfflineAccountDialog.ShowAsync(XamlRoot);
            if (string.IsNullOrWhiteSpace(name)) return;
            try
            {
                await LumenRuntime.Accounts.CreateOfflineAsync(name);
                account = LumenRuntime.Accounts.Selected;
            }
            catch (Exception ex)
            {
                await ShowAsync("创建账户失败", ex.Message);
                return;
            }
        }
        if (account is null) return;
        await StartLaunchAsync(instance);
    }

    public void UpdatePlayState(GameState state)
    {
        var active = ActiveInstance;
        var same = active is not null && string.Equals(state.InstanceId, active.Id, StringComparison.OrdinalIgnoreCase);
        if (state.Phase == GamePhase.Running && same)
        {
            HomePlayLabel.Text = "停止游戏";
            HomePlayIcon.Glyph = "\uE71A";
        }
        else if (state.Phase is GamePhase.Preparing or GamePhase.Starting)
        {
            HomePlayLabel.Text = "正在启动";
            HomePlayIcon.Glyph = "\uE895";
        }
        else
        {
            HomePlayLabel.Text = "开始游戏";
            HomePlayIcon.Glyph = "\uE768";
        }
    }

    private async Task StartLaunchAsync(InstanceSummary instance)
    {
        _launching = true;
        try
        {
            await LaunchDialog.ShowAsync(XamlRoot, instance);
        }
        finally
        {
            _launching = false;
        }
    }

    private void Versions_Click(object sender, RoutedEventArgs e)
        => ShellPage.Current?.Navigate("versions");

    private async void GameRoot_Click(object sender, RoutedEventArgs e) => ShowGameRootMenu();

    private void ShowGameRootMenu()
    {
        var settings = LumenRuntime.Settings.Settings;
        var flyout = new MenuFlyout();

        flyout.Items.Add(new MenuFlyoutItem
        {
            Text = "最近的 .minecraft 文件夹",
            IsEnabled = false
        });

        foreach (var entry in settings.GameRootHistory
                     .Where(x => !string.IsNullOrWhiteSpace(x.Path))
                     .OrderByDescending(x => x.LastUsed)
                     .Take(6))
        {
            var path = entry.Path;
            var current = string.Equals(path, settings.GameRoot, StringComparison.OrdinalIgnoreCase);
            var item = new MenuFlyoutItem
            {
                Text = (current ? "✓  " : "    ") + path,
                Tag = path,
                Icon = new FontIcon { Glyph = "\uE8B7", FontSize = 14 }
            };
            item.Click += async (_, _) =>
            {
                if (current) return;
                await SwitchToGameRootAsync(path);
            };
            flyout.Items.Add(item);
        }

        flyout.Items.Add(new MenuFlyoutSeparator());

        var pick = new MenuFlyoutItem
        {
            Text = "切换其他目录…",
            Icon = new FontIcon { Glyph = "\uE8DA", FontSize = 14 }
        };
        pick.Click += async (_, _) => await PickGameRootAsync();
        flyout.Items.Add(pick);

        var open = new MenuFlyoutItem
        {
            Text = "在资源管理器中打开",
            Icon = new FontIcon { Glyph = "\uE8B7", FontSize = 14 }
        };
        open.Click += (_, _) => OpenCurrentRoot();
        flyout.Items.Add(open);

        flyout.ShowAt(GameRootButton);
    }

    private async Task PickGameRootAsync()
    {
        try
        {
            var picker = new FolderPicker();
            picker.FileTypeFilter.Add("*");
            var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(MainWindow.Current);
            WinRT.Interop.InitializeWithWindow.Initialize(picker, hwnd);
            var folder = await picker.PickSingleFolderAsync();
            if (folder is null) return;
            await SwitchToGameRootAsync(folder.Path);
        }
        catch (Exception ex)
        {
            await MessageDialog.ShowAsync(XamlRoot, "切换游戏目录失败", ex.Message);
        }
    }

    private static async Task SwitchToGameRootAsync(string path)
    {
        if (ShellPage.Current is { } shell)
            await shell.SwitchGameRootAsync(path);
    }

    private async void OpenCurrentRoot()
    {
        try
        {
            var path = LumenRuntime.Settings.Settings.GameRoot;
            if (!Directory.Exists(path))
            {
                await MessageDialog.ShowAsync(XamlRoot, "目录不存在", path);
                return;
            }
            Process.Start(new ProcessStartInfo { FileName = path, UseShellExecute = true });
        }
        catch (Exception ex)
        {
            await MessageDialog.ShowAsync(XamlRoot, "打开目录失败", ex.Message);
        }
    }

    private void Account_Click(object sender, RoutedEventArgs e)
        => ShellPage.Current?.OpenAccountMenu();

    private async void RefreshNews_Click(object sender, RoutedEventArgs e)
    {
        try { await RenderNewsAsync(true, _renderToken); }
        catch (Exception ex) { await ShowAsync("刷新失败", ex.Message); }
    }

    private Task ShowAsync(string title, string message)
        => MessageDialog.ShowAsync(XamlRoot, title, message);
}