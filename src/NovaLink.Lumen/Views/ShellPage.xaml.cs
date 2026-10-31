using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Microsoft.UI.Xaml.Shapes;
using NovaLink.Lumen.Controls;
using NovaLink.Lumen.Core;
using NovaLink.Lumen.Core.Models;
using NovaLink.Lumen.Services;
using NovaLink.Lumen.Services.Minecraft;
using NovaLink.Lumen.Views.Dialogs;
using NovaLink.Lumen.Views.Pages;
using System;
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
    private Microsoft.UI.Dispatching.DispatcherQueueTimer? _dockTimer;
    private bool _rootSwitching;
    private Microsoft.UI.Xaml.Media.Animation.Storyboard? _rootSwitchSpin;
    private readonly BackgroundMusicService _music;
    private MediaPlayer? _backgroundVideoPlayer;
    private Microsoft.UI.Dispatching.DispatcherQueueTimer? _musicPulseTimer;
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
                if (_backgroundVideoPlayer is { } player)
                {
                    player.Pause();
                    player.Source = null;
                    player.Dispose();
                    _backgroundVideoPlayer = null;
                }
            }
            catch { }
            _music.Dispose();
        };
        SizeChanged += (_, _) => { UpdateTopBar(); LayoutCustomBackground(); };
        foreach (var button in NavButtons)
        {
            Motion.Hover(button, 1.035f, 1f);
            button.PointerEntered += (_, _) =>
            {
                if ((button.Tag as string) == _active) return;
                button.Background = (Brush)Application.Current.Resources["BrushNavHover"];
                button.Foreground = (Brush)Application.Current.Resources["BrushText"];
                if (button.Content is Grid grid && grid.Children.Count > 0 && grid.Children[0] is StackPanel panel)
                {
                    if (panel.Children.Count > 0 && panel.Children[0] is Microsoft.UI.Xaml.Shapes.Path icon)
                        icon.Stroke = (Brush)Application.Current.Resources["BrushText"];
                    if (panel.Children.Count > 1 && panel.Children[1] is TextBlock label)
                        label.Foreground = (Brush)Application.Current.Resources["BrushText"];
                }
            };
            button.PointerExited += (_, _) => UpdateNav();
        }
        Motion.RotateOnHover(BrandLogo, 4f, 1.04f);
    }

    private Button[] NavButtons => new[] { NavHome, NavVersions, NavDiscover, NavForum, NavLibrary, NavSettings };

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
        if (!has) return;
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

    private void UpdateTopBar()
    {
        var compact = ActualWidth < 1180;
        SearchShell.Width = compact ? 286 : 372;
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
        var type = tag switch
        {
            "versions" => typeof(VersionsPage),
            "discover" => typeof(DiscoverPage),
            "forum" => typeof(ForumPage),
            "library" => typeof(LibraryPage),
            "settings" => typeof(SettingsPage),
            _ => typeof(HomePage)
        };
        if (force || ContentFrame.CurrentSourcePageType != type)
        {
            // 背景与页面用同一条时间线一起淡入，避免只有底层先出现。
            Backdrop.Opacity = 0.15;
            ContentFrame.Opacity = 0.15;
            ContentFrame.Navigate(type, null, new Microsoft.UI.Xaml.Media.Animation.SuppressNavigationTransitionInfo());
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
            button.Foreground = (Brush)Application.Current.Resources[on ? "BrushMint" : "BrushTextSecondary"];
            if (button.Content is Grid grid && grid.Children.Count > 0 && grid.Children[0] is StackPanel panel)
            {
                if (panel.Children.Count > 0 && panel.Children[0] is Microsoft.UI.Xaml.Shapes.Path icon)
                    icon.Stroke = (Brush)Application.Current.Resources[on ? "BrushMint" : "BrushTextSecondary"];
                if (panel.Children.Count > 1 && panel.Children[1] is TextBlock label)
                    label.Foreground = (Brush)Application.Current.Resources[on ? "BrushMint" : "BrushTextSecondary"];
            }
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
        => await ShowAsync("通知", "暂时没有新的通知。");

    private void GameStatusChip_Tapped(object sender, TappedRoutedEventArgs e) => ProgressCenter.OpenOverlay();

    private void ProgressDock_Tapped(object sender, TappedRoutedEventArgs e) => ProgressCenter.OpenOverlay();

    private void GameStop_Click(object sender, RoutedEventArgs e)
    {
        if (LumenRuntime.Launch.State.Phase is not (GamePhase.Preparing or GamePhase.Starting or GamePhase.Running)) return;
        LumenRuntime.Launch.Kill();
    }

    private void ApplyGameState(GameState state)
    {
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

        if (ContentFrame.Content is HomePage home) home.UpdatePlayState(state);
        if (ContentFrame.Content is VersionsPage versions) versions.UpdatePlayState(state);
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