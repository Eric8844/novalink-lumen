using System.Diagnostics;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using Microsoft.UI.Xaml.Media.Imaging;
using NovaLink.Lumen.Controls;
using NovaLink.Lumen.Core.Models;
using NovaLink.Lumen.Services;
using NovaLink.Lumen.Services.Minecraft;
using NovaLink.Lumen.Views.Dialogs;
using Windows.Storage.Pickers;
using Windows.UI;

namespace NovaLink.Lumen.Views.Pages;

public sealed partial class VersionsPage : Page
{
    private const int RenderBatchSize = 6;

    private IReadOnlyList<InstanceSummary> _instances = [];
    private InstanceSummary? _selected;
    private string? _pendingFocus;
    private bool _loading;
    private LoadingState? _busy;
    private int _renderToken;

    public VersionsPage()
    {
        InitializeComponent();
        EnsureBusy().Hide();
        Loaded += OnPageLoaded;
        Unloaded += OnPageUnloaded;
    }

    private async void OnPageLoaded(object sender, RoutedEventArgs e) => await ReloadAsync();

    private void OnPageUnloaded(object sender, RoutedEventArgs e)
    {
        // 页面离开：作废进行中的渲染，并把加载动画从视觉树上摘掉，避免它在后台一直跑。
        _renderToken++;
        _loading = false;
        _busy?.Detach();
        _busy = null;
    }

    private LoadingState EnsureBusy()
    {
        if (_busy is null || _busy.Parent is null)
            _busy = LoadingState.Attach(PageRoot, "正在读取实例…");
        return _busy;
    }

    private void ShowBusy(string message)
    {
        var busy = EnsureBusy();
        busy.SetMessage(message);
        busy.Show();
    }

    private void HideBusy() => _busy?.Hide();

    public async void Reload() => await ReloadAsync();

    public void FocusInstance(string id)
    {
        _pendingFocus = id;
        var item = _instances.FirstOrDefault(x => x.Id == id);
        if (item is not null) { Select(item); _pendingFocus = null; }
    }

    private async Task ReloadAsync()
    {
        if (_loading) return;
        _loading = true;
        var token = ++_renderToken;
        ShowBusy("正在读取实例…");
        try
        {
            // 先把加载动画画上一帧，再让后台线程去扫盘，避免进页面就卡住。
            await UiYield.NextFrameAsync();
            var previous = _selected?.Id;
            var scanned = await Task.Run(async () => await LumenRuntime.Instances.ScanAsync());
            if (token != _renderToken) return;
            _instances = scanned;
            var iconFlags = await ResolveIconAvailabilityAsync();
            if (token != _renderToken) return;
            CountText.Text = _instances.Count.ToString();
            var focus = _pendingFocus;
            var active = LumenRuntime.Settings.Settings.SelectedInstanceId;
            var target = (focus is null ? null : _instances.FirstOrDefault(x => x.Id == focus))
                ?? _instances.FirstOrDefault(x => x.Id == active)
                ?? _instances.FirstOrDefault(x => x.Id == previous) ?? _instances.FirstOrDefault();
            if (focus is not null) _pendingFocus = null;
            await RenderListAsync(target, iconFlags, token);
            if (token != _renderToken) return;
            Select(target);
            UpdatePlayState(LumenRuntime.Launch.State);
            EmptyCard.Visibility = _instances.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            DetailScroll.Visibility = _instances.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
        }
        catch (Exception ex)
        {
            AppLog.Error("versions", "扫描版本失败", ex);
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

    /// <summary>图标是否存在是文件 IO，先丢到后台线程判断。</summary>
    private async Task<bool[]> ResolveIconAvailabilityAsync()
    {
        var items = _instances;
        return await Task.Run(() =>
        {
            var flags = new bool[items.Count];
            for (var i = 0; i < items.Count; i++)
            {
                try
                {
                    flags[i] = !items[i].Icon.StartsWith("loader:", StringComparison.OrdinalIgnoreCase)
                        && File.Exists(items[i].Icon);
                }
                catch
                {
                    flags[i] = false;
                }
            }
            return flags;
        });
    }

    private async Task RenderListAsync(InstanceSummary? target, bool[] iconFlags, int token)
    {
        VersionList.Children.Clear();
        // 内容开始上屏就收起加载层，避免转圈和已渲染的行叠在一起。
        HideBusy();
        for (var index = 0; index < _instances.Count; index++)
        {
            // 分批上屏，每批之间让出一帧，背景动画就不会停住。
            if (token != _renderToken) return;
            var item = _instances[index];
            var row = BuildRow(item, item.Id == target?.Id, iconFlags[index]);
            VersionList.Children.Add(row);
            Motion.RowIn(row, index % RenderBatchSize);
            if ((index + 1) % RenderBatchSize == 0) await UiYield.NextFrameAsync();
        }
    }

    private static void ApplyRowSelection(Border row, bool selected, bool animate = false)
    {
        // 选中/未选中都取当前主题的实色：浅色下才有边界感，深色下也不会发白。
        FadeBrush(row, true,
            selected ? ThemeColor("BrushSelected", Alpha(ThemeManager.Accent, 0x2E))
                     : ThemeColor("BrushFillSubtle", Alpha(ThemeManager.Text, 0x0A)), animate);
        FadeBrush(row, false,
            selected ? ThemeColor("BrushNavSelectedBorder", Alpha(ThemeManager.Accent, 0x66))
                     : ThemeColor("BrushBorderSubtle", Alpha(ThemeManager.Text, 0x14)), animate);
    }

    /// <summary>换成目标色；animate 时从上一色渐变过去（Motion.HoverInMs），失败就直接落地。</summary>
    private static void FadeBrush(Border row, bool background, Color target, bool animate)
    {
        var previous = background
            ? (row.Background as SolidColorBrush)?.Color
            : (row.BorderBrush as SolidColorBrush)?.Color;
        var brush = new SolidColorBrush(target);
        if (background) row.Background = brush;
        else row.BorderBrush = brush;
        if (!animate || previous is null || previous.Value == target) return;
        try
        {
            var animation = new ColorAnimation
            {
                From = previous.Value,
                To = target,
                Duration = new Duration(TimeSpan.FromMilliseconds(Motion.HoverInMs)),
                FillBehavior = FillBehavior.Stop,
                EnableDependentAnimation = true,
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
            };
            Storyboard.SetTarget(animation, brush);
            Storyboard.SetTargetProperty(animation, "Color");
            var storyboard = new Storyboard();
            storyboard.Children.Add(animation);
            storyboard.Begin();
        }
        catch (Exception)
        {
            // 过渡只是附加效果，出错保留目标色。
        }
    }

    private static Color ThemeColor(string key, Color fallback)
    {
        return Application.Current.Resources.TryGetValue(key, out var value) && value is SolidColorBrush brush
            ? brush.Color
            : fallback;
    }

    private static Color Alpha(Color color, byte alpha) => Color.FromArgb(alpha, color.R, color.G, color.B);

    /// <summary>切换选中态时只改行高亮，不重建列表（否则每次点击都会重排整份列表）。</summary>
    private void UpdateSelection()
    {
        foreach (var child in VersionList.Children)
        {
            if (child is not Border row) continue;
            var selected = _selected is not null
                && row.Tag is string id
                && string.Equals(id, _selected.Id, StringComparison.OrdinalIgnoreCase);
            ApplyRowSelection(row, selected, animate: true);
        }
    }

    private Border BuildRow(InstanceSummary item, bool selected, bool hasIcon)
    {
        var row = new Border
        {
            CornerRadius = new CornerRadius(16),
            Padding = new Thickness(12, 11, 12, 11),
            BorderThickness = new Thickness(1)
        };
        ApplyRowSelection(row, selected);
        var grid = new Grid { ColumnSpacing = 11 };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(44) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var iconHost = new Border
        {
            Width = 44, Height = 44, CornerRadius = new CornerRadius(13),
            Background = ThemeManager.Brush("BrushAccentSoft", Alpha(ThemeManager.Accent, 0x26)),
            BorderBrush = ThemeManager.Brush("BrushBorderSubtle", Alpha(ThemeManager.Text, 0x24)),
            BorderThickness = new Thickness(1)
        };
        if (hasIcon)
        {
            iconHost.Background = new ImageBrush { ImageSource = new BitmapImage(new Uri(item.Icon)), Stretch = Stretch.UniformToFill };
        }
        else
        {
            iconHost.Child = new TextBlock
            {
                Text = LoaderMark(item.Loader), FontSize = 15, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
                Foreground = ThemeManager.Brush("BrushText", ThemeManager.Text)
            };
        }
        Grid.SetColumn(iconHost, 0);
        grid.Children.Add(iconHost);

        var text = new StackPanel { Spacing = 3, VerticalAlignment = VerticalAlignment.Center };
        text.Children.Add(new TextBlock
        {
            Text = item.Name, FontSize = 13.5, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            TextTrimming = TextTrimming.CharacterEllipsis, MaxWidth = 190
        });
        text.Children.Add(new TextBlock
        {
            Text = item.Loader == "Vanilla" ? "内核 " + item.Kernel : item.Loader + " " + item.LoaderVersion,
            FontSize = 10.5, Foreground = (Brush)Application.Current.Resources["BrushTextSecondary"],
            TextTrimming = TextTrimming.CharacterEllipsis, MaxWidth = 190
        });
        Grid.SetColumn(text, 1);
        grid.Children.Add(text);

        var side = new StackPanel { Spacing = 3, VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Right };
        side.Children.Add(new TextBlock
        {
            Text = item.Mods + " 模组", FontSize = 10, HorizontalAlignment = HorizontalAlignment.Right,
            Foreground = (Brush)Application.Current.Resources["BrushTextTertiary"]
        });
        side.Children.Add(new TextBlock
        {
            Text = Relative(item.LastPlayed), FontSize = 10, HorizontalAlignment = HorizontalAlignment.Right,
            Foreground = (Brush)Application.Current.Resources["BrushTextTertiary"]
        });
        Grid.SetColumn(side, 2);
        grid.Children.Add(side);

        row.Child = grid;
        row.Tag = item.Id;
        row.Tapped += (_, _) => Select(item);
        Motion.Hover(row, 1.004f, 1.2f);
        return row;
    }

    private void Select(InstanceSummary? item)
    {
        _selected = item;
        if (item is not null)
        {
            var settings = LumenRuntime.Settings.Settings;
            if (!string.Equals(settings.SelectedInstanceId, item.Id, StringComparison.OrdinalIgnoreCase))
            {
                settings.SelectedInstanceId = item.Id;
                LumenRuntime.Settings.Save();
            }
        }
        UpdateSelection();
        if (item is null)
        {
            EmptyCard.Visibility = Visibility.Visible;
            DetailScroll.Visibility = Visibility.Collapsed;
            return;
        }

        EmptyCard.Visibility = Visibility.Collapsed;
        DetailScroll.Visibility = Visibility.Visible;
        DetailName.Text = item.Name;
        DetailLoaderBadge.Text = item.Loader.ToUpperInvariant();
        DetailKernelBadge.Text = "内核 " + item.Kernel;
        var dataMode = item.Isolated ? "单独隔离" : "共享数据目录";
        DetailSub.Text = (item.Loader == "Vanilla"
            ? "原版实例 · " + item.Kernel
            : item.Loader + " " + item.LoaderVersion + " · 内核 " + item.Kernel) + " · " + dataMode;
        DetailMods.Text = item.Mods.ToString();
        DetailWorlds.Text = item.Worlds.ToString();
        DetailMemory.Text = LumenRuntime.Settings.Settings.Memory + " MB";
        DetailJava.Text = LumenRuntime.Settings.Settings.JavaPath is { Length: > 0 }
            ? Path.GetFileNameWithoutExtension(LumenRuntime.Settings.Settings.JavaPath)
            : "自动";
        InfoPath.Text = item.GameDir;
        InfoLoader.Text = item.Loader == "Vanilla" ? "原版" : item.Loader + " " + item.LoaderVersion;
        InfoLast.Text = item.LastPlayed > 0
            ? DateTimeOffset.FromUnixTimeMilliseconds(item.LastPlayed).ToLocalTime().ToString("yyyy年M月d日 HH:mm")
            : "尚未启动";
        InfoKind.Text = item.Isolated ? "单独隔离" : "共享数据目录";
    }

    private static string LoaderMark(string loader) => loader switch
    {
        "Fabric" => "F",
        "Forge" => "G",
        "NeoForge" => "N",
        "Quilt" => "Q",
        "OptiFine" => "O",
        _ => "M"
    };

    private static string Relative(long unixMs)
    {
        if (unixMs <= 0) return "未启动";
        var span = DateTimeOffset.UtcNow - DateTimeOffset.FromUnixTimeMilliseconds(unixMs);
        if (span.TotalMinutes < 1) return "刚刚";
        if (span.TotalHours < 1) return (int)span.TotalMinutes + " 分钟前";
        if (span.TotalDays < 1) return (int)span.TotalHours + " 小时前";
        if (span.TotalDays < 30) return (int)span.TotalDays + " 天前";
        return DateTimeOffset.FromUnixTimeMilliseconds(unixMs).ToLocalTime().ToString("yyyy/M/d");
    }

    private async void Refresh_Click(object sender, RoutedEventArgs e) => await ReloadAsync();

    private async void Import_Click(object sender, RoutedEventArgs e)
    {
        await InstallModpackDialog.PickAndInstallAsync(XamlRoot);
        await ReloadAsync();
    }

    private async void Play_Click(object sender, RoutedEventArgs e)
    {
        if (_selected is null) return;
        var state = LumenRuntime.Launch.State;
        if (state.Phase == GamePhase.Running)
        {
            if (string.Equals(state.InstanceId, _selected.Id, StringComparison.OrdinalIgnoreCase))
                LumenRuntime.Launch.Kill();
            else
                await MessageDialog.ShowAsync(XamlRoot, "游戏正在运行", "请先关闭当前游戏，再启动另一个实例。");
            return;
        }
        if (state.Phase is GamePhase.Preparing or GamePhase.Starting)
        {
            ProgressCenter.OpenOverlay();
            return;
        }
        await LaunchDialog.ShowAsync(XamlRoot, _selected);
    }

    public void UpdatePlayState(GameState state)
    {
        var same = _selected is not null && string.Equals(state.InstanceId, _selected.Id, StringComparison.OrdinalIgnoreCase);
        if (state.Phase == GamePhase.Running && same)
        {
            PlayLabel.Text = "停止游戏";
            PlayIcon.Glyph = "\uE71A";
        }
        else if (state.Phase is GamePhase.Preparing or GamePhase.Starting)
        {
            PlayLabel.Text = "正在启动";
            PlayIcon.Glyph = "\uE895";
        }
        else
        {
            PlayLabel.Text = "开始游戏";
            PlayIcon.Glyph = "\uE768";
        }
    }

    private void OpenFolder_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var dir = Path.Combine(LumenRuntime.Instances.GameRoot, "versions", _selected?.Id ?? "");
            if (Directory.Exists(dir)) Process.Start(new ProcessStartInfo("explorer.exe", "\"" + dir + "\"") { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            AppLog.Warn("versions", "打开目录失败：" + ex.Message);
        }
    }

    private async void Rename_Click(object sender, RoutedEventArgs e)
    {
        if (_selected is null) return;
        var name = await TextPromptDialog.ShowAsync(XamlRoot, "重命名实例", "显示名称", _selected.Name, 40);
        if (string.IsNullOrWhiteSpace(name)) return;
        var meta = LumenRuntime.Instances.GetOrCreateMeta(_selected.Id);
        meta.Name = name.Trim();
        LumenRuntime.Instances.SaveMeta();
        await ReloadAsync();
    }

    private async void Delete_Click(object sender, RoutedEventArgs e)
    {
        if (_selected is null) return;
        var target = _selected;
        var baseHint = target.Loader == "Vanilla"
            ? ""
            : " 它继承的原版内核如果不再被其他版本使用，也会一并移入回收站。";
        var modal = GlassModal.Show("删除实例",
            "将 “" + target.Name + "” 移入回收站。存档、共享资源与整合包内容不会被单独删除。" + baseHint, 470);
        modal.SecondaryText = "取消";
        modal.PrimaryText = "移入回收站";
        modal.PrimaryInvoked = async () =>
        {
            modal.SetBusy(true);
            try
            {
                await Task.Run(() => LumenRuntime.Instances.DeleteAsync(target.Id));
                modal.Close();
                _selected = null;
                await ReloadAsync();
            }
            catch (Exception ex)
            {
                modal.Close();
                await Task.Delay(80);
                try
                {
                    await MessageDialog.ShowAsync(XamlRoot, "删除失败",
                        string.IsNullOrWhiteSpace(ex.Message) ? ex.GetType().Name : ex.Message);
                }
                catch (Exception dialogEx)
                {
                    AppLog.Error("versions", "无法显示删除失败提示", dialogEx);
                }
            }
        };
    }

    private async void Icon_Click(object sender, RoutedEventArgs e)
    {
        if (_selected is null) return;
        try
        {
            var picker = new FileOpenPicker();
            picker.FileTypeFilter.Add(".png");
            picker.FileTypeFilter.Add(".jpg");
            picker.FileTypeFilter.Add(".jpeg");
            WinRT.Interop.InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(MainWindow.Current));
            var file = await picker.PickSingleFileAsync();
            if (file is null) return;
            Directory.CreateDirectory(LumenRuntime.Paths.Icons);
            var target = Path.Combine(LumenRuntime.Paths.Icons, _selected.Id + Path.GetExtension(file.Path).ToLowerInvariant());
            File.Copy(file.Path, target, true);
            var meta = LumenRuntime.Instances.GetOrCreateMeta(_selected.Id);
            meta.Icon = target;
            LumenRuntime.Instances.SaveMeta();
            await ReloadAsync();
        }
        catch (Exception ex)
        {
            AppLog.Warn("versions", "更换图标失败：" + ex.Message);
            await MessageDialog.ShowAsync(XamlRoot, "更换图标失败", ex.Message);
        }
    }

    private void Install_Click(object sender, RoutedEventArgs e)
    {
        ShellPage.Current?.OpenVanillaInstall();
    }

    private async void Loader_Click(object sender, RoutedEventArgs e)
    {
        await InstallVersionDialog.ShowAsync(XamlRoot);
        await ReloadAsync();
    }
}