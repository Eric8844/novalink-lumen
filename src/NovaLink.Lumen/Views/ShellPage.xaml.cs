using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using Microsoft.UI.Xaml.Media.Imaging;
using Microsoft.UI.Xaml.Shapes;
using NovaLink.Lumen.Controls;
using NovaLink.Lumen.Core;
using NovaLink.Lumen.Core.Models;
using NovaLink.Lumen.Core.Plugins;
using NovaLink.Lumen.Plugins;
using NovaLink.Lumen.Services;
using NovaLink.Lumen.Services.Minecraft;
using NovaLink.Lumen.Services.Updates;
using NovaLink.Lumen.Views.Dialogs;
using NovaLink.Lumen.Views.Pages;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Windows.ApplicationModel.DataTransfer;
using Windows.Storage;
using Windows.Media.Core;
using Windows.Media.Playback;
using Windows.System;
using Windows.UI;

namespace NovaLink.Lumen.Views;

public sealed partial class ShellPage : Page
{
    private string _active = "home";
    private readonly List<Button> _pluginNavButtons = new();
    private readonly List<(string Title, string Message, string Level)> _pluginNotices = new();
    private int _toastToken;
    private Microsoft.UI.Dispatching.DispatcherQueueTimer? _dockTimer;
    private bool _rootSwitching;
    private Microsoft.UI.Xaml.Media.Animation.Storyboard? _rootSwitchSpin;
    private readonly BackgroundMusicService _music;
    private bool _musicProgressSyncing;
    private bool _musicVolumeSyncing;
    private bool _musicVolumeDirty;
    private bool _musicUiReady;
    private MediaPlayer? _backgroundVideoPlayer;
    private Microsoft.UI.Dispatching.DispatcherQueueTimer? _musicPulseTimer;
    private Microsoft.UI.Dispatching.DispatcherQueueTimer? _musicProgressTimer;
    private bool _musicDetailOpen;
    private bool _musicDetailPinned;
    private GamePhase _lastGamePhase = GamePhase.Idle;
    private Storyboard? _musicDetailStory;
    public AuroraBackdrop GlassBackdrop => Aurora;
    public BackgroundMusicService Music => _music;

    public static ShellPage? Current { get; private set; }

    public ShellPage()
    {
        InitializeComponent();
        Current = this;
        _music = new BackgroundMusicService(DispatcherQueue);
        _music.Changed += (_, _) => UpdateMusicDock();
        ContentFrame.Navigated += ContentFrame_Navigated;
        AddHandler(UIElement.PointerMovedEvent, new PointerEventHandler(Shell_PointerMoved), true);
        AddHandler(UIElement.PointerExitedEvent, new PointerEventHandler(Shell_PointerExited), true);
        Loaded += OnLoaded;
        Unloaded += (_, _) =>
        {
            try
            {
                _musicPulseTimer?.Stop();
                _musicProgressTimer?.Stop();
                if (_backgroundVideoPlayer is { } player)
                {
                    player.Pause();
                    player.Source = null;
                    player.Dispose();
                    _backgroundVideoPlayer = null;
                }
            }
            catch { }
            PluginUiService.Instance.DetachShell(this);
            LumenPluginHost.Notice -= OnPluginNotice;
            _music.Dispose();
        };
        SizeChanged += (_, _) => { UpdateTopBar(); LayoutCustomBackground(); };
        foreach (var button in NavButtons) AttachNavButton(button);
        Motion.RotateOnHover(BrandLogo, 4f, 1.04f);

        // 插件通知（App 层广播）→ 右下角气泡 + 铃铛历史。
        LumenPluginHost.Notice += OnPluginNotice;

        // 插件在窗口创建之前就已加载完，导航项已排队；这里补渲染一次。
        PluginUiService.Instance.AttachShell(this);

        // XAML 解析阶段 Slider 的初始 Value 会触发 ValueChanged（此时后面的 TextBlock 还没创建），
        // 所以所有音乐控件回调都以这个标志为准，构造完成后才允许响应。
        _musicUiReady = true;

        // 版本徽章走通道信息：测试版显示 "1.5.0 Preview"，正式版显示 "1.5.0"。
        // 写在这里而不是 XAML，是为了让"当前是不是测试版"只有 LauncherChannelInfo 一个真源。
        try
        {
            VersionChipText.Text = LauncherChannelInfo.DisplayVersion;
            ToolTipService.SetToolTip(VersionChipText, LauncherChannelInfo.ChannelLabel + " " + LauncherChannelInfo.DisplayVersion);
        }
        catch { }
    }

    private Button[] BuiltInNavButtons => new[] { NavHome, NavVersions, NavDiscover, NavForum, NavLibrary, NavSettings };

    private IEnumerable<Button> NavButtons
    {
        get
        {
            foreach (var button in BuiltInNavButtons) yield return button;
            foreach (var button in _pluginNavButtons) yield return button;
        }
    }

    /// <summary>当前激活的导航 tag（插件页面 retry 之类的场景用得到）。</summary>
    public string ActiveTag => _active;

    private void AttachNavButton(Button button)
    {
        Motion.Hover(button, 1.035f, 1f);
        button.PointerEntered += (_, _) =>
        {
            if ((button.Tag as string) == _active) return;
            button.Background = (Brush)Application.Current.Resources["BrushNavHover"];
            var strong = (Brush)Application.Current.Resources["BrushText"];
            button.Foreground = strong;
            ApplyNavIconBrush(button, strong);
        };
        button.PointerExited += (_, _) => UpdateNav();
    }

    /// <summary>统一处理内置（Path 描边图标）与插件（FontIcon）两种导航项的着色。</summary>
    private static void ApplyNavIconBrush(Button button, Brush brush)
    {
        if (button.Content is not Grid grid || grid.Children.Count == 0 || grid.Children[0] is not StackPanel panel) return;

        if (panel.Children.Count > 0)
        {
            switch (panel.Children[0])
            {
                case Microsoft.UI.Xaml.Shapes.Path path:
                    path.Stroke = brush;
                    break;
                case FontIcon icon:
                    icon.Foreground = brush;
                    break;
            }
        }

        if (panel.Children.Count > 1 && panel.Children[1] is TextBlock label) label.Foreground = brush;
    }

    // ------------------------------------------------------------------
    // 插件导航项注入（由 PluginUiService 在插件登记完成后调用）
    // ------------------------------------------------------------------

    /// <summary>
    /// 用插件登记的导航项重建「插件区」。内置导航永不被顶掉，插件项追加在下面。
    /// 可在任意线程调用：非 UI 线程会排队到 UI 线程执行。
    /// </summary>
    public void SetPluginNavigation(IReadOnlyList<PluginNavItem> items)
    {
        if (!DispatcherQueue.HasThreadAccess)
        {
            DispatcherQueue.TryEnqueue(() => SetPluginNavigation(items));
            return;
        }

        foreach (var old in _pluginNavButtons) NavItemsPanel.Children.Remove(old);
        _pluginNavButtons.Clear();

        if (items is { Count: > 0 })
        {
            foreach (var item in items)
            {
                var button = CreatePluginNavButton(item);
                NavItemsPanel.Children.Add(button);
                _pluginNavButtons.Add(button);
            }
        }

        UpdateNav();
    }

    private Button CreatePluginNavButton(PluginNavItem item)
    {
        var icon = new FontIcon
        {
            Glyph = string.IsNullOrWhiteSpace(item.Glyph) ? "\uE8F1" : item.Glyph!,
            FontSize = 22,
            Margin = new Thickness(0, 0, 0, 6),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center
        };

        var label = new TextBlock
        {
            Text = string.IsNullOrWhiteSpace(item.Title) ? "插件" : item.Title!,
            FontSize = 10,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            LineHeight = 12,
            LineStackingStrategy = LineStackingStrategy.BlockLineHeight,
            TextAlignment = TextAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0),
            Padding = new Thickness(0),
            MaxWidth = 64,
            TextTrimming = TextTrimming.CharacterEllipsis
        };

        var stack = new StackPanel
        {
            Orientation = Orientation.Vertical,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center
        };
        stack.Children.Add(icon);
        stack.Children.Add(label);

        var grid = new Grid { Width = 64, Height = 64, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        grid.Children.Add(stack);

        var button = new Button
        {
            Tag = item.Tag,
            Content = grid,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Style = (Style)Application.Current.Resources["NavButton"]
        };

        Microsoft.UI.Xaml.Automation.AutomationProperties.SetAutomationId(button, "NavPlugin_" + item.Tag.Replace(':', '_').Replace('.', '_'));
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(button, string.IsNullOrWhiteSpace(item.Title) ? "插件" : item.Title!);
        ToolTipService.SetToolTip(button, string.IsNullOrWhiteSpace(item.Title) ? item.Tag : item.Title);
        button.Click += Nav_Click;
        AttachNavButton(button);
        return button;
    }

    public void ApplyTheme(bool light)
    {
        Aurora.ApplyTheme(light);
        ContentFrame.RequestedTheme = light ? ElementTheme.Light : ElementTheme.Dark;
        BackdropTint.Fill = Solid(light ? Color.FromArgb(0x0C, 0xFF, 0xFF, 0xFF) : Color.FromArgb(0x18, 0x04, 0x0A, 0x12));
        NavRail.Background = Solid(light ? Color.FromArgb(0xC4, 0xF5, 0xF8, 0xFC) : Color.FromArgb(0x38, 0x06, 0x0B, 0x13));
        NavRail.BorderBrush = Solid(light ? Color.FromArgb(0x16, 0x00, 0x00, 0x00) : Color.FromArgb(0x12, 0xFF, 0xFF, 0xFF));
        VersionChip.Background = Solid(light ? Color.FromArgb(0xC4, 0xFF, 0xFF, 0xFF) : Color.FromArgb(0x12, 0xFF, 0xFF, 0xFF));
        VersionChip.BorderBrush = Solid(light ? Color.FromArgb(0x1C, 0x00, 0x00, 0x00) : Color.FromArgb(0x1F, 0xFF, 0xFF, 0xFF));
        if (!SearchBox.FocusState.Equals(FocusState.Unfocused))
            SearchShell.BorderBrush = (Brush)Application.Current.Resources["BrushMint"];
        else
            SearchShell.BorderBrush = Solid(light ? Color.FromArgb(0x26, 0x0B, 0x1B, 0x2B) : Color.FromArgb(0x2E, 0xFF, 0xFF, 0xFF));
        SearchShortcut.Background = Solid(light ? Color.FromArgb(0x12, 0x0B, 0x1B, 0x2B) : Color.FromArgb(0x18, 0xFF, 0xFF, 0xFF));
        GameStatusChip.Background = Solid(light ? Color.FromArgb(0x2A, 0x0C, 0x94, 0x78) : Color.FromArgb(0x2A, 0x0E, 0x2A, 0x22));
        GameStatusChip.BorderBrush = Solid(light ? Color.FromArgb(0x55, 0x0C, 0x94, 0x78) : Color.FromArgb(0x55, 0x7C, 0xEB, 0xD6));
        ProgressDock.Background = Solid(light ? Color.FromArgb(0xF7, 0xFF, 0xFF, 0xFF) : Color.FromArgb(0xF2, 0x0A, 0x12, 0x20));
        ProgressDock.BorderBrush = Solid(light ? Color.FromArgb(0x28, 0x0B, 0x1B, 0x2B) : Color.FromArgb(0x33, 0xFF, 0xFF, 0xFF));
        ProgressDockBar.Background = Solid(light ? Color.FromArgb(0x1A, 0x0B, 0x1B, 0x2B) : Color.FromArgb(0x18, 0xFF, 0xFF, 0xFF));
        if (CustomBackgroundVeil.Visibility == Visibility.Visible)
            UpdateBackgroundVeil(LumenRuntime.Settings.Settings.BackgroundDim);
    }

    private static SolidColorBrush Solid(Color color) => new(color);

    public void OpenAccountMenu() => Account_Click(AccountButton, new RoutedEventArgs());

    /// <summary>
    /// The Win2D canvas composites independently of the XAML opacity tree, so its stars stay
    /// bright above a translucent modal scrim and read as white noise. Collapse it while a
    /// modal is open instead of letting it float above the dim layer.
    /// </summary>
    public void SetBackdropVisible(bool visible)
    {
        if (Aurora is null) return;
        Aurora.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
    }

    private void ContentFrame_Navigated(object sender, Microsoft.UI.Xaml.Navigation.NavigationEventArgs e)
    {
        if (e.Content is not FrameworkElement page) return;
        if (page.IsLoaded)
        {
            GlassPanes.Track(page);
            DispatcherQueue.TryEnqueue(() =>
            {
                Motion.AttachButtons(page);
                Motion.EnterPage(page);
            });
        }
        else
        {
            page.Loaded += (_, _) =>
            {
                GlassPanes.Track(page);
                DispatcherQueue.TryEnqueue(() =>
                {
                    Motion.AttachButtons(page);
                    Motion.EnterPage(page);
                });
            };
        }
    }

    private void Root_DragOver(object sender, DragEventArgs e)
    {
        if (!e.DataView.Contains(StandardDataFormats.StorageItems)) return;
        e.AcceptedOperation = DataPackageOperation.Copy;
        e.DragUIOverride.Caption = "拖入以导入整合包";
        e.DragUIOverride.IsCaptionVisible = true;
        e.DragUIOverride.IsGlyphVisible = true;
    }

    private async void Root_Drop(object sender, DragEventArgs e)
    {
        if (!e.DataView.Contains(StandardDataFormats.StorageItems)) return;
        var items = await e.DataView.GetStorageItemsAsync();
        var file = items.OfType<StorageFile>().FirstOrDefault(f =>
            f.Name.EndsWith(".mrpack", StringComparison.OrdinalIgnoreCase) ||
            f.Name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase));
        if (file is null) return;
        await Dialogs.InstallModpackDialog.InstallAsync(XamlRoot, file.Path);
        if (ContentFrame.Content is Pages.VersionsPage page) page.Reload();
    }

    private void Shell_PointerMoved(object sender, PointerRoutedEventArgs e)
    {
        var point = e.GetCurrentPoint(Aurora).Position;
        Aurora.SetPointer(point.X, point.Y);
    }

    private void Shell_PointerExited(object sender, PointerRoutedEventArgs e) => Aurora.ClearPointer();

    private void SearchBox_GotFocus(object sender, RoutedEventArgs e)
    {
        SearchShell.BorderBrush = (Brush)Application.Current.Resources["BrushMint"];
        SearchShell.BorderThickness = new Thickness(1.2);
    }

    private void SearchBox_LostFocus(object sender, RoutedEventArgs e)
    {
        SearchShell.BorderBrush = Solid(ThemeManager.IsLight
            ? Color.FromArgb(0x26, 0x0B, 0x1B, 0x2B)
            : Color.FromArgb(0x2E, 0xFF, 0xFF, 0xFF));
        SearchShell.BorderThickness = new Thickness(1);
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        LumenRuntime.Accounts.AccountsChanged += (_, _) => DispatcherQueue.TryEnqueue(RefreshAccount);
        LumenRuntime.Settings.Changed += (_, _) => DispatcherQueue.TryEnqueue(RefreshAccount);
        ProgressCenter.Changed += () => DispatcherQueue.TryEnqueue(RefreshProgressDock);
        LumenRuntime.Launch.StateChanged += state => DispatcherQueue.TryEnqueue(() => ApplyGameState(state));
        RefreshAccount();
        Navigate("home");
        Motion.Pulse(StatusDot, 0.55, 1, 2800);
        ApplyGameState(LumenRuntime.Launch.State);
        _ = RefreshJavaAsync();
        UpdateTopBar();
        ApplyBackground();
        ConfigureMusic();
        _musicPulseTimer ??= DispatcherQueue.CreateTimer();
        _musicPulseTimer.Interval = TimeSpan.FromMilliseconds(50);
        _musicPulseTimer.IsRepeating = true;
        _musicPulseTimer.Tick += (_, _) => Aurora.SetMusicSpectrum(_music.GetSpectrum());
        _musicPulseTimer.Start();

        // 音乐台详情区的进度刷新：450ms 一次即可，比频谱的 50ms 定时器轻得多。
        if (_musicProgressTimer is null)
        {
            _musicProgressTimer = DispatcherQueue.CreateTimer();
            _musicProgressTimer.Interval = TimeSpan.FromMilliseconds(450);
            _musicProgressTimer.IsRepeating = true;
            _musicProgressTimer.Tick += (_, _) => RefreshMusicProgress();
        }
        _musicProgressTimer.Start();
    }

    public void ApplyBackgroundAndMusic()
    {
        ApplyBackground();
        ConfigureMusic(false);
        UpdateMusicDock();
    }

    public void ApplyBackground()
    {
        var s = LumenRuntime.Settings.Settings;
        // 复用同一个 MediaPlayer：反复 SetMediaPlayer(null) / Dispose / 新建播放器会让
        // MediaPlayerElement 在视频解码器还没释放时重建，WinUI 会直接原生崩溃。
        try { _backgroundVideoPlayer?.Pause(); } catch { }
        CustomBackgroundImage.Visibility = Visibility.Collapsed;
        CustomBackgroundVideo.Visibility = Visibility.Collapsed;
        CustomBackgroundVeil.Visibility = Visibility.Collapsed;

        var kind = s.BackgroundKind ?? "default";
        var path = s.BackgroundPath ?? "";
        if (kind == "image" && File.Exists(path))
        {
            try
            {
                CustomBackgroundImage.Source = new BitmapImage(new Uri(path));
                CustomBackgroundImage.Visibility = Visibility.Visible;
                CustomBackgroundImage.Opacity = 1.0 - Math.Clamp(s.BackgroundDim, 0, 0.7);
                Aurora.Visibility = Visibility.Collapsed;
            }
            catch { kind = "default"; }
        }
        else if (kind == "video" && File.Exists(path))
        {
            try
            {
                var player = EnsureBackgroundVideoPlayer();
                player.IsLoopingEnabled = true;
                player.IsMuted = !s.BackgroundVideoSound;
                player.AutoPlay = true;
                player.Source = MediaSource.CreateFromUri(new Uri(path));
                CustomBackgroundVideo.Visibility = Visibility.Visible;
                CustomBackgroundVideo.Opacity = 1.0 - Math.Clamp(s.BackgroundDim, 0, 0.7);
                Aurora.Visibility = Visibility.Collapsed;
            }
            catch (Exception ex)
            {
                AppLog.Warn("background", "视频背景加载失败：" + ex.Message);
                kind = "default";
            }
        }
        else
        {
            kind = "default";
        }

        if (kind != "video" && _backgroundVideoPlayer is { } oldPlayer)
        {
            try { oldPlayer.Pause(); oldPlayer.Source = null; } catch { }
        }

        if (kind == "default")
        {
            Aurora.Visibility = Visibility.Visible;
            CustomBackgroundImage.Source = null;
        }
        else
        {
            UpdateBackgroundVeil(s.BackgroundDim);
        }
        LayoutCustomBackground();
    }

    private MediaPlayer EnsureBackgroundVideoPlayer()
    {
        if (_backgroundVideoPlayer is not null) return _backgroundVideoPlayer;
        var player = new MediaPlayer
        {
            IsLoopingEnabled = true,
            IsMuted = true,
            AutoPlay = false
        };
        player.MediaFailed += (_, args) =>
        {
            AppLog.Warn("background", "视频背景播放失败：" + args.ErrorMessage);
            DispatcherQueue.TryEnqueue(() =>
            {
                CustomBackgroundVideo.Visibility = Visibility.Collapsed;
                CustomBackgroundVeil.Visibility = Visibility.Collapsed;
                Aurora.Visibility = Visibility.Visible;
            });
        };
        _backgroundVideoPlayer = player;
        CustomBackgroundVideo.SetMediaPlayer(player);
        return player;
    }

    private void UpdateBackgroundVeil(double dim)
    {
        var light = ThemeManager.IsLight;
        CustomBackgroundVeil.Fill = new SolidColorBrush(light
            ? Windows.UI.Color.FromArgb(255, 246, 249, 252)
            : Windows.UI.Color.FromArgb(255, 5, 10, 18));
        CustomBackgroundVeil.Opacity = Math.Clamp(0.38 + dim * 0.55, 0.38, 0.82);
        CustomBackgroundVeil.Visibility = Visibility.Visible;
    }

    private void LayoutCustomBackground()
    {
        var s = LumenRuntime.Settings.Settings;
        var scale = Math.Clamp(s.BackgroundScale <= 0 ? 1 : s.BackgroundScale, 1, 2.6);
        foreach (var transform in new[] { CustomImageTransform, CustomVideoTransform })
        {
            transform.CenterX = ActualWidth / 2;
            transform.CenterY = ActualHeight / 2;
            transform.ScaleX = scale;
            transform.ScaleY = scale;
            transform.TranslateX = s.BackgroundOffsetX * ActualWidth * 0.42;
            transform.TranslateY = s.BackgroundOffsetY * ActualHeight * 0.42;
        }
    }

    private void ConfigureMusic(bool autoplay = true)
    {
        var s = LumenRuntime.Settings.Settings;
        _music.Configure(s.MusicTracks, s.MusicMode, s.MusicVolume);
        if (autoplay && s.MusicAutoplay && _music.HasTracks)
        {
            _music.PlayIndex(0);
        }
        UpdateMusicDock();
    }

    private void UpdateMusicDock()
    {
        var has = _music.HasTracks;
        MusicDock.Visibility = has ? Visibility.Visible : Visibility.Collapsed;
        if (!has)
        {
            // 曲目被清空时顺手把悬停详情收回去，避免下次显示时停在展开态。
            ResetMusicDetail();
            return;
        }
        MusicTitle.Text = _music.CurrentTitle;
        MusicSubtitle.Text = _music.Tracks.Count + " 首 · " + BackgroundMusicService.ModeLabel(_music.Mode);
        MusicPlayIcon.Glyph = _music.IsPlaying ? "" : "";
        MusicModeIcon.Glyph = _music.Mode switch
        {
            "one" => "",
            "shuffle" => "",
            _ => ""
        };
        ToolTipService.SetToolTip(MusicModeButton, "播放方式：" + BackgroundMusicService.ModeLabel(_music.Mode));
        RefreshMusicProgress();
    }

    private void MusicToggle_Click(object sender, RoutedEventArgs e) => _music.Toggle();
    private void MusicNext_Click(object sender, RoutedEventArgs e) => _music.Next();
    private void MusicPrevious_Click(object sender, RoutedEventArgs e) => _music.Previous();

    private void MusicMode_Click(object sender, RoutedEventArgs e)
    {
        var mode = _music.CycleMode();
        LumenRuntime.Settings.Settings.MusicMode = mode;
        LumenRuntime.Settings.Save();
        UpdateMusicDock();
    }

    // ---- 音乐台：悬停放大 + 详情区（进度、时间、第几首、播放方式、音量）----

    private void MusicDock_PointerEntered(object sender, PointerRoutedEventArgs e) => SetMusicDetailOpen(true);

    /// <summary>
    /// 移出就收起，但**拖动进度/音量时不能收**：按住鼠标往两边拖，指针很容易瞬间离开音乐台，
    /// 一离开就塌回去会让滑块看起来「一拖就没」。
    /// </summary>
    private void MusicDock_PointerExited(object sender, PointerRoutedEventArgs e)
    {
        // 拖动时指针跟着走，松手落到浮层外面也会先收到一次 Exited；正在拖就一律不收起。
        if (e.Pointer.IsInContact) return;
        // 点击展开的是「钉住」状态：鼠标移开不收起，再点一下音乐台才关，避免拖完滑块一松手就塌掉。
        if (_musicDetailPinned) return;
        SetMusicDetailOpen(false);
    }

    /// <summary>
    /// 点一下音乐台也能展开/收起详情：悬停展开在触屏和部分鼠标/远程桌面场景下不一定触发，
    /// 给一个明确的点击入口，进度条与音量就不会「看不见也点不到」。
    /// 详情区内部的点击（滑块、播放按钮、音量）不算「点音乐台」——否则点一下滑块会把整个浮层关掉，
    /// 这就是「拖动条根本用不了」的直接原因。
    /// </summary>
    private void MusicDock_Tapped(object sender, TappedRoutedEventArgs e)
    {
        if (IsInsideDetail(e.OriginalSource)) return;
        if (_musicDetailOpen && _musicDetailPinned)
        {
            _musicDetailPinned = false;
            SetMusicDetailOpen(false);
            return;
        }
        _musicDetailPinned = true;
        SetMusicDetailOpen(true);
    }

    private bool IsInsideDetail(object? source)
    {
        var node = source as DependencyObject;
        while (node is not null)
        {
            if (ReferenceEquals(node, MusicDetail) || node is Button) return true;
            node = VisualTreeHelper.GetParent(node);
        }
        return false;
    }

    private void SetMusicDetailOpen(bool open)
    {
        if (_musicDetailOpen == open) return;
        _musicDetailOpen = open;
        if (open)
        {
            // 详情区必须可点击：之前这里一直是 IsHitTestVisible=False，进度条与音量都点不动。
            MusicDetail.IsHitTestVisible = true;
            RefreshMusicProgress();
        }
        else
        {
            MusicDetail.IsHitTestVisible = false;
            PersistMusicVolume();
        }

        AnimateMusicDetail(open);
        AnimateMusicDockScale(open ? 1.05 : 1, open ? 180 : 150);
    }

    /// <summary>
    /// 展开/收起是「一段」动画，不是「内容先消失、外框后塌」两段。
    /// 旧实现收起时只淡出内容，等淡出动画结束后才把 Visibility 设成 Collapsed，
    /// 于是进度条与音量先没了、玻璃框再跳一下，视觉上被切成两截。
    /// 现在高度和透明度走同一条时间线：框一边缩、内容一边淡出。
    /// </summary>
    private void AnimateMusicDetail(bool open)
    {
        _musicDetailStory?.Stop();
        _musicDetailStory = null;

        if (open)
        {
            MusicDetail.Visibility = Visibility.Visible;
            MusicDetail.Height = double.NaN;
            MusicDetail.UpdateLayout();
            var target = MusicDetail.ActualHeight;

            MusicDetail.Opacity = 0;
            MusicDetailShift.Y = 8;

            if (target < 1)
            {
                // 首次布局还没算出来高度：只淡入，不硬做高度动画（宁可少一段，也不要闪一下）。
                var fade = new Storyboard();
                AddMusicDetailAnimation(fade, MusicDetail, "Opacity", 0, 1, 140, 0, MusicEaseOut());
                fade.Begin();
                return;
            }

            MusicDetail.Height = 0;
            var story = new Storyboard();
            AddMusicDetailAnimation(story, MusicDetail, "Height", 0, target, 180, 0, MusicEaseOut());
            AddMusicDetailAnimation(story, MusicDetail, "Opacity", 0, 1, 140, 30, MusicEaseOut());
            AddMusicDetailAnimation(story, MusicDetailShift, "Y", 8, 0, 180, 0, MusicEaseOut());
            story.Completed += (_, _) =>
            {
                if (!_musicDetailOpen) return;
                story.Stop();
                MusicDetail.Height = double.NaN;
                MusicDetail.Opacity = 1;
                MusicDetailShift.Y = 0;
            };
            _musicDetailStory = story;
            story.Begin();
            return;
        }

        var current = MusicDetail.ActualHeight;
        if (current < 1)
        {
            MusicDetail.Visibility = Visibility.Collapsed;
            MusicDetail.Height = double.NaN;
            MusicDetail.Opacity = 0;
            MusicDetailShift.Y = 8;
            return;
        }

        // 固定住当前高度，变成可以过渡的确定值。
        MusicDetail.Height = current;
        var closing = new Storyboard();
        AddMusicDetailAnimation(closing, MusicDetail, "Height", current, 0, 160, 0, MusicEaseInOut());
        AddMusicDetailAnimation(closing, MusicDetail, "Opacity", 1, 0, 160, 0, MusicEaseInOut());
        AddMusicDetailAnimation(closing, MusicDetailShift, "Y", 0, 8, 160, 0, MusicEaseInOut());
        closing.Completed += (_, _) =>
        {
            if (_musicDetailOpen) return;
            closing.Stop();
            MusicDetail.Visibility = Visibility.Collapsed;
            MusicDetail.Height = double.NaN;
            MusicDetail.Opacity = 0;
            MusicDetailShift.Y = 8;
        };
        _musicDetailStory = closing;
        closing.Begin();
    }

    private void ResetMusicDetail()
    {
        _musicDetailOpen = false;
        _musicDetailPinned = false;
        _musicDetailStory?.Stop();
        _musicDetailStory = null;
        MusicDetail.Visibility = Visibility.Collapsed;
        MusicDetail.Height = double.NaN;
        MusicDetail.Opacity = 0;
        MusicDetailShift.Y = 8;
    }

    private void AnimateMusicDockScale(double to, int durationMs)
    {
        var story = new Storyboard();
        AddMusicDockScale(story, to, durationMs, to > 1 ? MusicEaseOut() : MusicEaseInOut());
        story.Begin();
    }

    private static EasingFunctionBase MusicEaseOut() => new CubicEase { EasingMode = EasingMode.EaseOut };

    private static EasingFunctionBase MusicEaseInOut() => new CubicEase { EasingMode = EasingMode.EaseInOut };

    private void AddMusicDockScale(Storyboard story, double to, int durationMs, EasingFunctionBase ease)
    {
        AddMusicDetailAnimation(story, MusicDockScale, "ScaleX", 1, to, durationMs, 0, ease);
        AddMusicDetailAnimation(story, MusicDockScale, "ScaleY", 1, to, durationMs, 0, ease);
    }

    private static void AddMusicDetailAnimation(Storyboard story, DependencyObject target, string property,
        double from, double to, int durationMs, int delayMs, EasingFunctionBase ease)
    {
        var animation = new DoubleAnimation
        {
            From = from,
            To = to,
            Duration = new Duration(TimeSpan.FromMilliseconds(durationMs)),
            BeginTime = TimeSpan.FromMilliseconds(delayMs),
            EasingFunction = ease,
            EnableDependentAnimation = true
        };
        Storyboard.SetTarget(animation, target);
        Storyboard.SetTargetProperty(animation, property);
        story.Children.Add(animation);
    }

    /// <summary>把当前播放进度、时间与曲目信息写进详情区；没有曲目时进度归零、时间显示 00:00 / 00:00。</summary>
    private void RefreshMusicProgress()
    {
        if (!_musicUiReady) return;
        if (MusicDock.Visibility != Visibility.Visible) return;

        var duration = _music.Duration;
        var position = _music.Position;
        if (duration <= TimeSpan.Zero)
        {
            duration = TimeSpan.Zero;
            position = TimeSpan.Zero;
        }
        else if (position > duration)
        {
            position = duration;
        }
        if (position < TimeSpan.Zero) position = TimeSpan.Zero;

        // 程序回写滑块时要屏蔽 ValueChanged，否则拖动/播放会被自己打断。
        _musicProgressSyncing = true;
        try
        {
            MusicProgress.Value = duration > TimeSpan.Zero ? position.TotalSeconds / duration.TotalSeconds : 0;
            _musicVolumeSyncing = true;
            try
            {
                var volume = Math.Clamp(_music.Volume, 0, 1);
                MusicVolume.Value = volume;
                MusicVolumeText.Text = (int)Math.Round(volume * 100) + "%";
            }
            finally { _musicVolumeSyncing = false; }
        }
        finally { _musicProgressSyncing = false; }
        MusicTimeText.Text = FormatMusicTime(position) + " / " + FormatMusicTime(duration);
        MusicMetaText.Text = BuildMusicMeta();
    }

    /// <summary>拖动进度条：按当前曲目总时长换算成秒并跳转，播放状态保持不变。</summary>
    private void MusicProgress_ValueChanged(object sender, RangeBaseValueChangedEventArgs e)
    {
        if (!_musicUiReady || _musicProgressSyncing) return;
        var duration = _music.Duration;
        if (duration <= TimeSpan.Zero)
        {
            RefreshMusicProgress();
            return;
        }
        var target = TimeSpan.FromSeconds(Math.Clamp(e.NewValue, 0, 1) * duration.TotalSeconds);
        _music.Seek(target);
        MusicTimeText.Text = FormatMusicTime(target) + " / " + FormatMusicTime(duration);
    }

    /// <summary>拖动音量：立刻作用于播放器；真正落盘等悬停收起时一次写完，避免拖动过程频繁写盘。</summary>
    private void MusicVolume_ValueChanged(object sender, RangeBaseValueChangedEventArgs e)
    {
        if (!_musicUiReady || _musicVolumeSyncing) return;
        // 取两位小数：MediaPlayer.Volume 内部按 float 存，不取整会把 0.35 写成 0.34999999…
        var volume = Math.Round(Math.Clamp(e.NewValue, 0, 1), 2);
        _music.Volume = volume;
        MusicVolumeText.Text = (int)Math.Round(volume * 100) + "%";
        _musicVolumeDirty = true;
    }

    private void PersistMusicVolume()
    {
        if (!_musicVolumeDirty) return;
        _musicVolumeDirty = false;
        try
        {
            var settings = LumenRuntime.Settings.Settings;
            settings.MusicVolume = Math.Round(Math.Clamp(_music.Volume, 0, 1), 2);
            LumenRuntime.Settings.Save();
        }
        catch (Exception ex)
        {
            AppLog.Warn("music", "保存音量失败：" + ex.Message);
        }
    }

    private string BuildMusicMeta()
    {
        var count = _music.TrackCount;
        var index = _music.TrackIndex;
        var track = count == 0 || index < 0 ? "暂无曲目" : "第 " + (index + 1) + " / " + count + " 首";
        return track + " · " + BackgroundMusicService.ModeLabel(_music.Mode);
    }

    private static string FormatMusicTime(TimeSpan value)
    {
        if (value < TimeSpan.Zero) value = TimeSpan.Zero;
        var minutes = (int)value.TotalMinutes;
        return minutes.ToString("00") + ":" + value.Seconds.ToString("00");
    }

    private void UpdateTopBar()
    {
        // 顶栏改成两列后这里只管「搜索框最多多宽」：窄窗口或游戏运行时压窄搜索框，
        // 并把右侧不关键的 Java 胶囊 / Ctrl K 提示让出来，保证状态条永远不和搜索框重叠。
        var gameActive = GameStatusChip.Visibility == Visibility.Visible;
        var compact = ActualWidth < 1180 || gameActive;
        SearchShell.MaxWidth = compact ? 286 : 372;
        JavaChip.Visibility = compact ? Visibility.Collapsed : Visibility.Visible;
        SearchShortcut.Visibility = compact ? Visibility.Collapsed : Visibility.Visible;
    }

    public async Task RefreshJavaAsync()
    {
        try
        {
            var best = await LumenRuntime.Java.BestAsync();
            JavaChipText.Text = best is null ? "Java 自动" : "Java " + best.Major;
        }
        catch
        {
            JavaChipText.Text = "Java 自动";
        }
    }

    private void RefreshAccount()
    {
        var account = LumenRuntime.Accounts.SelectedPublic();
        if (account is null)
        {
            AccountName.Text = "未登录";
            AccountSubtitle.Text = "离线账户";
            AccountInitial.Text = "N";
            AccountAvatar.Source = null;
            AccountAvatar.Visibility = Visibility.Collapsed;
            return;
        }

        AccountName.Text = account.Name;
        AccountSubtitle.Text = account.Subtitle;
        AccountInitial.Text = string.IsNullOrWhiteSpace(account.Initial) ? "N" : account.Initial;
        AccountAvatar.Visibility = Visibility.Visible;
        try
        {
            if (!string.IsNullOrWhiteSpace(account.AvatarUrl))
                AccountAvatar.Source = new BitmapImage(new Uri(account.AvatarUrl));
            else
                AccountAvatar.Source = null;
        }
        catch { AccountAvatar.Source = null; }
    }

    private void AccountAvatar_ImageFailed(object sender, ExceptionRoutedEventArgs e)
    {
        AccountAvatar.Visibility = Visibility.Collapsed;
    }

    private void Nav_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button button && button.Tag is string tag)
            Navigate(tag);
    }

    public void Navigate(string tag, bool force = false)
    {
        _active = tag;

        // 插件页面全部复用 PluginPageHost，靠导航参数区分是哪一个插件。
        var isPlugin = tag.StartsWith("plugin:", StringComparison.Ordinal);
        var type = isPlugin ? typeof(PluginPageHost) : tag switch
        {
            "versions" => typeof(VersionsPage),
            "discover" => typeof(DiscoverPage),
            "forum" => typeof(ForumPage),
            "library" => typeof(LibraryPage),
            "settings" => typeof(SettingsPage),
            _ => typeof(HomePage)
        };
        // 两个插件页面之间切换时页面类型相同，不能走上面的去重判断，必须强制导航。
        if (force || isPlugin || ContentFrame.CurrentSourcePageType != type)
        {
            // 背景与页面用同一条时间线一起淡入，避免只有底层先出现。
            Backdrop.Opacity = 0.15;
            ContentFrame.Opacity = 0.15;
            ContentFrame.Navigate(type, isPlugin ? tag : null, new Microsoft.UI.Xaml.Media.Animation.SuppressNavigationTransitionInfo());
            BeginStageFade();
        }
        UpdateNav();
        var activeButton = NavButtons.FirstOrDefault(button => (button.Tag as string) == _active);
        if (activeButton is not null) Motion.Bump(activeButton, 1.045f);
    }

    /// <summary>Versions → Discover, landing directly on the Mojang vanilla catalogue.</summary>
    public void OpenVanillaInstall()
    {
        Navigate("discover", true);
        if (ContentFrame.Content is Pages.DiscoverPage page)
            page.ShowVanilla();
    }

    /// <summary>Animated .minecraft switch: veil, spinning arc, rescan, then a fresh page tree.</summary>
    public async Task SwitchGameRootAsync(string path)
    {
        if (_rootSwitching || string.IsNullOrWhiteSpace(path)) return;
        _rootSwitching = true;
        try
        {
            ShowRootSwitch(path);
            await Task.Delay(180);
            var count = await LumenRuntime.SwitchGameRootAsync(path, stage =>
            {
                if (DispatcherQueue.HasThreadAccess) RootSwitchStatus.Text = stage;
                else DispatcherQueue.TryEnqueue(() => RootSwitchStatus.Text = stage);
            });
            RootSwitchStatus.Text = count == 0
                ? "目录已切换 · 这个目录还没有已安装版本"
                : "目录已切换 · 已载入 " + count + " 个实例";
            await Task.Delay(260);
            Navigate("home", true);
            await Task.Delay(300);
            await HideRootSwitchAsync();
        }
        catch (Exception ex)
        {
            AppLog.Warn("app", "切换游戏目录失败：" + ex.Message);
            RootSwitchStatus.Text = "切换失败：" + ex.Message;
            await Task.Delay(1400);
            await HideRootSwitchAsync();
        }
        finally
        {
            _rootSwitching = false;
        }
    }

    private void ShowRootSwitch(string path)
    {
        RootSwitchPath.Text = path;
        RootSwitchStatus.Text = "正在写入游戏目录…";
        RootSwitchLayer.Background = Solid(ThemeManager.IsLight
            ? Color.FromArgb(0xCC, 0xEE, 0xF3, 0xF9)
            : Color.FromArgb(0xD9, 0x04, 0x08, 0x0E));
        RootSwitchLayer.Visibility = Visibility.Visible;
        RootSwitchLayer.Opacity = 0;
        RootSwitchCard.Opacity = 1;

        var fade = new Microsoft.UI.Xaml.Media.Animation.Storyboard();
        var reveal = new Microsoft.UI.Xaml.Media.Animation.DoubleAnimation
        {
            From = 0,
            To = 1,
            Duration = new Duration(TimeSpan.FromMilliseconds(260)),
            EasingFunction = new Microsoft.UI.Xaml.Media.Animation.CubicEase
            {
                EasingMode = Microsoft.UI.Xaml.Media.Animation.EasingMode.EaseOut
            }
        };
        Microsoft.UI.Xaml.Media.Animation.Storyboard.SetTarget(reveal, RootSwitchLayer);
        Microsoft.UI.Xaml.Media.Animation.Storyboard.SetTargetProperty(reveal, "Opacity");
        fade.Children.Add(reveal);
        fade.Begin();
        Motion.Enter(RootSwitchCard, 40, 0.96f, 16);

        _rootSwitchSpin?.Stop();
        var spin = new Microsoft.UI.Xaml.Media.Animation.Storyboard();
        var rotate = new Microsoft.UI.Xaml.Media.Animation.DoubleAnimation
        {
            From = 0,
            To = 360,
            Duration = new Duration(TimeSpan.FromMilliseconds(900)),
            RepeatBehavior = Microsoft.UI.Xaml.Media.Animation.RepeatBehavior.Forever
        };
        Microsoft.UI.Xaml.Media.Animation.Storyboard.SetTarget(rotate, RootSwitchRotate);
        Microsoft.UI.Xaml.Media.Animation.Storyboard.SetTargetProperty(rotate, "Angle");
        spin.Children.Add(rotate);
        _rootSwitchSpin = spin;
        spin.Begin();
    }

    private async Task HideRootSwitchAsync()
    {
        if (RootSwitchLayer.Visibility != Visibility.Visible) return;
        var fade = new Microsoft.UI.Xaml.Media.Animation.Storyboard();
        var hide = new Microsoft.UI.Xaml.Media.Animation.DoubleAnimation
        {
            From = RootSwitchLayer.Opacity,
            To = 0,
            Duration = new Duration(TimeSpan.FromMilliseconds(240)),
            EasingFunction = new Microsoft.UI.Xaml.Media.Animation.CubicEase
            {
                EasingMode = Microsoft.UI.Xaml.Media.Animation.EasingMode.EaseIn
            }
        };
        Microsoft.UI.Xaml.Media.Animation.Storyboard.SetTarget(hide, RootSwitchLayer);
        Microsoft.UI.Xaml.Media.Animation.Storyboard.SetTargetProperty(hide, "Opacity");
        fade.Children.Add(hide);
        fade.Begin();
        await Task.Delay(250);
        RootSwitchLayer.Visibility = Visibility.Collapsed;
        _rootSwitchSpin?.Stop();
        _rootSwitchSpin = null;
    }

    private Microsoft.UI.Xaml.Media.Animation.Storyboard? _navFade;
    private Microsoft.UI.Dispatching.DispatcherQueueTimer? _navGuard;

    private void BeginStageFade()
    {
        _navFade?.Stop();
        var fade = new Microsoft.UI.Xaml.Media.Animation.Storyboard();
        _navFade = fade;
        AddFade(fade, Backdrop, 300);
        AddFade(fade, ContentFrame, 300);
        fade.Completed += (_, _) => RestoreStage();
        fade.Begin();
        _navGuard?.Stop();
        _navGuard ??= DispatcherQueue.CreateTimer();
        _navGuard.Interval = TimeSpan.FromMilliseconds(700);
        _navGuard.IsRepeating = false;
        _navGuard.Tick += (_, _) => RestoreStage();
        _navGuard.Start();
    }

    private void RestoreStage()
    {
        _navGuard?.Stop();
        if (_navFade is not null)
        {
            _navFade.Stop();
            _navFade = null;
        }
        Backdrop.Opacity = 1;
        ContentFrame.Opacity = 1;
    }

    private static void AddFade(Microsoft.UI.Xaml.Media.Animation.Storyboard storyboard, Microsoft.UI.Xaml.UIElement target, int milliseconds)
    {
        var animation = new Microsoft.UI.Xaml.Media.Animation.DoubleAnimation
        {
            From = 0.15,
            To = 1,
            Duration = new Microsoft.UI.Xaml.Duration(TimeSpan.FromMilliseconds(milliseconds)),
            EasingFunction = new Microsoft.UI.Xaml.Media.Animation.CubicEase
            {
                EasingMode = Microsoft.UI.Xaml.Media.Animation.EasingMode.EaseOut
            }
        };
        Microsoft.UI.Xaml.Media.Animation.Storyboard.SetTarget(animation, target);
        Microsoft.UI.Xaml.Media.Animation.Storyboard.SetTargetProperty(animation, "Opacity");
        storyboard.Children.Add(animation);
    }

    private void UpdateNav()
    {
        foreach (var button in NavButtons)
        {
            var on = (button.Tag as string) == _active;
            button.Background = on
                ? (Brush)Application.Current.Resources["BrushNavSelected"]
                : new SolidColorBrush(Windows.UI.Color.FromArgb(0, 0, 0, 0));
            button.BorderBrush = on ? (Brush)Application.Current.Resources["BrushNavSelectedBorder"] : null;
            button.BorderThickness = on ? new Thickness(1) : new Thickness(0);
            var foreground = (Brush)Application.Current.Resources[on ? "BrushMint" : "BrushTextSecondary"];
            button.Foreground = foreground;
            ApplyNavIconBrush(button, foreground);
        }
    }

    private async void Account_Click(object sender, RoutedEventArgs e)
    {
        var flyout = new MenuFlyout { Placement = Microsoft.UI.Xaml.Controls.Primitives.FlyoutPlacementMode.BottomEdgeAlignedRight };
        var accounts = LumenRuntime.Accounts.PublicAccounts();
        if (accounts.Count == 0)
        {
            flyout.Items.Add(new MenuFlyoutItem { Text = "尚未添加账户", IsEnabled = false });
        }
        foreach (var account in accounts)
        {
            var item = new MenuFlyoutItem { Text = account.IsSelected ? account.Name + "   \u2713" : account.Name, Tag = account.Subtitle };
            item.Icon = IconFor(account);
            item.Click += (_, _) => { LumenRuntime.Accounts.Select(account.Id); RefreshAccount(); };
            flyout.Items.Add(item);
        }
        flyout.Items.Add(new MenuFlyoutSeparator());
        var offline = new MenuFlyoutItem { Text = "创建离线账户", Icon = new FontIcon { Glyph = "\uE77B" } };
        offline.Click += async (_, _) => await CreateOfflineAsync();
        flyout.Items.Add(offline);
        var ms = new MenuFlyoutItem { Text = "Microsoft 登录（未绑定）", Icon = new FontIcon { Glyph = "\uE77B" } };
        ms.Click += async (_, _) =>
        {
            try
            {
                var code = await Views.Dialogs.MicrosoftLoginWindow.ShowAsync();
                if (string.IsNullOrWhiteSpace(code)) return;
                var busy = Views.Dialogs.GlassModal.Show("正在完成 Microsoft 登录", "正在换取 Minecraft 档案，请稍候。", 420);
                busy.HideButtons();
                busy.SetCloseAllowed(false);
                try
                {
                    var result = await LumenRuntime.Microsoft.CompleteLoginAsync(code);
                    busy.Close();
                    RefreshAccount();
                    await Views.Dialogs.MessageDialog.ShowAsync(XamlRoot, "Microsoft 已登录",
                        result.Name + " 已添加，皮肤与名称将用于启动。");
                }
                catch
                {
                    busy.Close();
                    throw;
                }
            }
            catch (Exception ex)
            {
                await Views.Dialogs.MessageDialog.ShowAsync(XamlRoot, "Microsoft 登录失败", ex.Message);
            }
        };
        flyout.Items.Add(ms);
        var nl = new MenuFlyoutItem { Text = "NovaLink 登录", Icon = new FontIcon { Glyph = "\uE8D7" } };
        nl.Click += async (_, _) =>
        {
            var account = await Views.Dialogs.NovalinkLoginDialog.ShowAsync(XamlRoot);
            if (account is not null) RefreshAccount();
        };
        flyout.Items.Add(nl);
        var selected = LumenRuntime.Accounts.Selected;
        if (selected is { Type: "novalink", Microsoft: not null })
        {
            var unbind = new MenuFlyoutItem { Text = "解绑 Microsoft", Icon = new FontIcon { Glyph = "\uE711" } };
            unbind.Click += async (_, _) => await UnbindAsync(selected);
            flyout.Items.Add(unbind);
        }
        flyout.Items.Add(new MenuFlyoutSeparator());
        var manage = new MenuFlyoutItem { Text = "管理账户", Icon = new FontIcon { Glyph = "\uE713" } };
        manage.Click += (_, _) => Navigate("settings");
        flyout.Items.Add(manage);
        flyout.ShowAt(AccountButton);
        await Task.CompletedTask;
    }

    private static IconElement IconFor(AccountPublic account)
    {
        if (!string.IsNullOrWhiteSpace(account.AvatarUrl))
        {
            try
            {
                return new IconSourceElement { IconSource = new ImageIconSource { ImageSource = new BitmapImage(new Uri(account.AvatarUrl)) } };
            }
            catch { }
        }
        return new FontIcon { Glyph = "\uE77B" };
    }

    private async Task CreateOfflineAsync()
    {
        var name = await Views.Dialogs.OfflineAccountDialog.ShowAsync(XamlRoot);
        if (string.IsNullOrWhiteSpace(name)) return;
        try
        {
            await LumenRuntime.Accounts.CreateOfflineAsync(name);
            RefreshAccount();
        }
        catch (Exception ex)
        {
            await ShowAsync("创建失败", ex.Message);
        }
    }

    private async Task UnbindAsync(AccountRecord account)
    {
        try
        {
            await LumenRuntime.Accounts.UnbindMicrosoftAsync(account);
            RefreshAccount();
        }
        catch (Exception ex)
        {
            await ShowAsync("解绑失败", ex.Message);
        }
    }

    private async Task ComingSoonAsync(string title)
        => await ShowAsync(title, "该模块尚未接入当前版本。");

    private Task ShowAsync(string title, string message)
        => Views.Dialogs.MessageDialog.ShowAsync(XamlRoot, title, message);

    private async void SearchBox_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key != VirtualKey.Enter) return;
        e.Handled = true;
        var query = SearchBox.Text.Trim();
        if (query.Length == 0) { Navigate("versions"); return; }
        IReadOnlyList<InstanceSummary> all;
        try { all = await LumenRuntime.Instances.ScanAsync(); }
        catch (Exception ex) { await ShowAsync("搜索失败", ex.Message); return; }
        var matches = all.Where(x => x.Name.Contains(query, StringComparison.OrdinalIgnoreCase)
                || x.Kernel.Contains(query, StringComparison.OrdinalIgnoreCase)
                || x.Loader.Contains(query, StringComparison.OrdinalIgnoreCase)
                || x.Id.Contains(query, StringComparison.OrdinalIgnoreCase))
            .Take(8).ToList();
        if (matches.Count == 0)
        {
            await ShowAsync("没有匹配结果", "没有找到与 “" + query + "” 相关的本地实例。");
            return;
        }
        if (matches.Count == 1)
        {
            OpenInstance(matches[0].Id);
            return;
        }
        var modal = GlassModal.Show("搜索结果", "找到 " + matches.Count + " 个实例，选择一个打开。", 440);
        modal.HideButtons();
        foreach (var item in matches)
        {
            var button = new Button
            {
                HorizontalAlignment = HorizontalAlignment.Stretch,
                HorizontalContentAlignment = HorizontalAlignment.Left,
                Padding = new Thickness(14, 10, 14, 10),
                CornerRadius = new CornerRadius(12),
                Background = new SolidColorBrush(Windows.UI.Color.FromArgb(12, 255, 255, 255)),
                BorderBrush = new SolidColorBrush(Windows.UI.Color.FromArgb(22, 255, 255, 255)),
                BorderThickness = new Thickness(1)
            };
            var text = new StackPanel { Spacing = 2 };
            text.Children.Add(new TextBlock { Text = item.Name, FontSize = 13.5, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold });
            text.Children.Add(new TextBlock { Text = item.Loader + " · 内核 " + item.Kernel, FontSize = 11, Opacity = 0.66 });
            button.Content = text;
            button.Click += (_, _) => { modal.Close(); OpenInstance(item.Id); };
            modal.Body.Children.Add(button);
        }
    }

    private void OpenInstance(string id)
    {
        Navigate("versions");
        _ = DispatcherQueue.TryEnqueue(async () =>
        {
            await Task.Delay(220);
            if (ContentFrame.Content is VersionsPage page) page.FocusInstance(id);
        });
    }

    private void Activity_Click(object sender, RoutedEventArgs e) => ProgressCenter.OpenOverlay();

    private async void Bell_Click(object sender, RoutedEventArgs e)
    {
        if (_pluginNotices.Count == 0)
        {
            await ShowAsync("通知", "暂时没有新的通知。");
            return;
        }

        var recent = _pluginNotices.AsEnumerable().Reverse().Take(12).ToList();
        var lines = recent.Select(note =>
            "· " + note.Title + (string.IsNullOrWhiteSpace(note.Message) ? "" : "：" + note.Message));
        await ShowAsync("通知", string.Join(Environment.NewLine + Environment.NewLine, lines));
    }

    /// <summary>插件/宿主通知：写日志并浮出气泡；非 UI 线程自动切回 UI 线程。</summary>
    private void OnPluginNotice(string title, string message, string level)
    {
        if (!DispatcherQueue.HasThreadAccess)
        {
            DispatcherQueue.TryEnqueue(() => OnPluginNotice(title, message, level));
            return;
        }

        try
        {
            _pluginNotices.Add((title ?? "", message ?? "", level ?? "info"));
            if (_pluginNotices.Count > 30) _pluginNotices.RemoveRange(0, _pluginNotices.Count - 30);
            _ = ShowPluginToastAsync(title ?? "", message ?? "", level ?? "info");
        }
        catch (Exception ex)
        {
            AppLog.Warn("plugin-ui", "登记插件通知失败：" + ex.Message);
        }
    }

    private async Task ShowPluginToastAsync(string title, string message, string level)
    {
        var token = ++_toastToken;
        try
        {
            PluginToastTitle.Text = string.IsNullOrWhiteSpace(title) ? "插件通知" : title;
            PluginToastBody.Text = message;
            PluginToastIcon.Glyph = level.ToLowerInvariant() switch
            {
                "warn" or "warning" => "",
                "error" => "",
                _ => ""
            };
            PluginToastIcon.Foreground = (Brush)Application.Current.Resources[
                level.Equals("error", StringComparison.OrdinalIgnoreCase) ? "BrushDanger" : "BrushMint"];

            PluginToast.Visibility = Visibility.Visible;
            PluginToast.Opacity = 0;
            for (var step = 1; step <= 5; step++)
            {
                if (token != _toastToken) return;
                PluginToast.Opacity = step / 5.0;
                await Task.Delay(22);
            }

            await Task.Delay(3800);
            if (token != _toastToken) return;

            for (var step = 5; step >= 0; step--)
            {
                if (token != _toastToken) return;
                PluginToast.Opacity = step / 5.0;
                await Task.Delay(22);
            }

            if (token == _toastToken) PluginToast.Visibility = Visibility.Collapsed;
        }
        catch (Exception ex)
        {
            AppLog.Warn("plugin-ui", "显示插件通知失败：" + ex.Message);
        }
    }

    private void GameStatusChip_Tapped(object sender, TappedRoutedEventArgs e) => ProgressCenter.OpenOverlay();

    private void ProgressDock_Tapped(object sender, TappedRoutedEventArgs e) => ProgressCenter.OpenOverlay();

    private void GameStop_Click(object sender, RoutedEventArgs e)
    {
        if (LumenRuntime.Launch.State.Phase is not (GamePhase.Preparing or GamePhase.Starting or GamePhase.Running)) return;
        LumenRuntime.Launch.Kill();
    }

    private void ApplyGameState(GameState state)
    {
        var wasActive = _lastGamePhase is GamePhase.Preparing or GamePhase.Starting or GamePhase.Running;
        _lastGamePhase = state.Phase;
        var active = state.Phase is GamePhase.Preparing or GamePhase.Starting or GamePhase.Running;
        GameStatusChip.Visibility = active ? Visibility.Visible : Visibility.Collapsed;
        GameStopButton.Visibility = state.Phase == GamePhase.Running
            ? Visibility.Visible
            : Visibility.Collapsed;
        if (active)
        {
            GameStatusText.Text = state.Phase switch
            {
                GamePhase.Preparing => "正在启动",
                GamePhase.Starting => "正在启动",
                _ => "游戏运行中"
            };
            GameInstanceText.Text = state.InstanceName;
        }

        UpdateTopBar();
        if (ContentFrame.Content is HomePage home) home.UpdatePlayState(state);
        if (ContentFrame.Content is VersionsPage versions) versions.UpdatePlayState(state);

        // 游戏刚退出：重新扫一次实例，让「上次启动 / 上次游玩」和"按最近启动排序"立刻生效。
        if (wasActive && state.Phase is GamePhase.Exited or GamePhase.Crashed)
        {
            if (ContentFrame.Content is VersionsPage page) page.Reload();
            else if (ContentFrame.Content is HomePage hero) hero.Reload();
        }
    }

    private void RefreshProgressDock()
    {
        var tasks = ProgressCenter.Snapshot();
        var task = tasks.FirstOrDefault(t => t.Status is ProgressStatus.Active or ProgressStatus.Stopping)
                   ?? tasks.FirstOrDefault();
        if (task is null)
        {
            ProgressDock.Visibility = Visibility.Collapsed;
            return;
        }
        if (task.Status is not (ProgressStatus.Active or ProgressStatus.Stopping))
        {
            var age = DateTimeOffset.Now - (task.FinishedAt ?? DateTimeOffset.Now);
            if (age.TotalSeconds > 1.5)
            {
                ProgressDock.Visibility = Visibility.Collapsed;
                return;
            }
            if (_dockTimer is null)
            {
                _dockTimer = DispatcherQueue.CreateTimer();
                _dockTimer.Interval = TimeSpan.FromSeconds(1.5);
                _dockTimer.IsRepeating = false;
                _dockTimer.Tick += (_, _) => { _dockTimer = null; RefreshProgressDock(); };
                _dockTimer.Start();
            }
        }

        ProgressDock.Visibility = Visibility.Visible;
        ProgressDockTitle.Text = task.Title;
        ProgressDockIcon.Glyph = task.Kind == "launch" ? "\uE768" : "\uE896";
        ProgressDockPercent.Text = task.Indeterminate ? "···" : task.PercentValue + "%";
        ProgressDockBar.IsIndeterminate = task.Indeterminate;
        ProgressDockBar.Value = task.Indeterminate ? 0 : task.PercentValue;
        ProgressDockStatus.Text = task.StatusText + (string.IsNullOrEmpty(task.SpeedText) ? "" : " · " + task.SpeedText);
        ProgressDockMessage.Text = task.Message;
        var more = Math.Max(0, tasks.Count(t => t.Status is ProgressStatus.Active or ProgressStatus.Stopping) - 1);
        ProgressDockMore.Visibility = more > 0 ? Visibility.Visible : Visibility.Collapsed;
        ProgressDockMoreText.Text = "+" + more;
    }
}