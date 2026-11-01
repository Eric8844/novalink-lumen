using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Hosting;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using Microsoft.UI.Xaml.Media.Imaging;
using Rectangle = Microsoft.UI.Xaml.Shapes.Rectangle;
using NovaLink.Lumen.Controls;
using NovaLink.Lumen.Core.Models;
using NovaLink.Lumen.Services;
using NovaLink.Lumen.Services.Modrinth;
using NovaLink.Lumen.Views.Dialogs;
using Windows.Storage.Pickers;
using Windows.UI;

namespace NovaLink.Lumen.Views.Pages;

public sealed partial class LibraryPage : Page
{
    private readonly List<InstanceSummary> _instances = new();
    private readonly HashSet<string> _selected = new(StringComparer.OrdinalIgnoreCase);
    private InstanceSummary? _current;
    private string _kind = "mod";
    private LibrarySnapshot? _snapshot;
    private LibraryOverview? _overview;
    private bool _bulkGuard;
    private LoadingState? _loading;
    private int _renderVersion;
    private FileSystemWatcher? _watcher;
    private DispatcherQueueTimer? _watchDebounce;

    public LibraryPage()
    {
        InitializeComponent();
        _loading = LoadingState.Attach(LoadingHost, "正在读取库…");
        _loading.Hide();
        Loaded += LibraryPage_Loaded;
        Unloaded += LibraryPage_Unloaded;
    }

    private void LibraryPage_Unloaded(object sender, RoutedEventArgs e)
    {
        // 页面离开时取消排队中的渲染，并停掉加载动画，别让它在后台继续跑。
        _renderVersion++;
        _loading?.Detach();
        _watcher?.Dispose();
        _watcher = null;
        _watchDebounce?.Stop();
    }

    private async void LibraryPage_Loaded(object sender, RoutedEventArgs e)
    {
        try
        {
            ShowBusy("正在读取实例…");
            _instances.Clear();
            // 扫描在后台线程完成，UI 线程只负责显示，背景动画不会停。
            _instances.AddRange(await Task.Run(async () => await LumenRuntime.Instances.ScanAsync()));
            InstanceCombo.Items.Clear();
            foreach (var instance in _instances)
            {
                InstanceCombo.Items.Add(new ComboBoxItem
                {
                    Content = instance.Name + "  ·  " + instance.Kernel,
                    Tag = instance
                });
            }
            if (_instances.Count == 0)
            {
                HideBusy();
                ShowEmpty("还没有可管理的实例");
                return;
            }
            var activeId = LumenRuntime.Settings.Settings.SelectedInstanceId;
            var index = _instances.FindIndex(x => string.Equals(x.Id, activeId, StringComparison.OrdinalIgnoreCase));
            InstanceCombo.SelectedIndex = index >= 0 ? index : 0;
            SelectTab("mod");
            await ReloadAsync();
            _ = LoadOverviewAsync();
        }
        catch (Exception ex)
        {
            AppLog.Warn("library", "读取库失败：" + ex.Message);
            HideBusy();
            ShowEmpty("读取库失败：" + ex.Message);
        }
    }

    private void ShowBusy(string message)
    {
        _loading?.SetMessage(message);
        _loading?.Show();
        EmptyPanel.Visibility = Visibility.Collapsed;
    }

    private void HideBusy() => _loading?.Hide();

    private string KindLabel => _kind switch
    {
        "world" => "世界",
        "resourcepack" => "资源包",
        "shader" => "光影",
        _ => "模组"
    };

    private async Task LoadOverviewAsync()
    {
        try
        {
            var instances = _instances.ToList();
            OverviewText.Text = "统计中…";
            _overview = await Task.Run(() => LumenRuntime.Library.Overview(instances));
            OverviewText.Text = _overview.Instances + " 个实例 · " + _overview.Mods + " 模组 · " + _overview.Worlds + " 世界 · "
                + (_overview.ResourcePacks + _overview.ShaderPacks) + " 资源 · 占用 " + FormatSize(_overview.TotalBytes);
        }
        catch (Exception ex)
        {
            AppLog.Warn("library", "统计库失败：" + ex.Message);
        }
    }

    private async void Instance_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (InstanceCombo.SelectedItem is ComboBoxItem { Tag: InstanceSummary instance })
        {
            _current = instance;
            _selected.Clear();
            await ReloadAsync();
        }
    }

    private async void Refresh_Click(object sender, RoutedEventArgs e) => await ReloadAsync();

    private async void Tab_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string kind }) return;
        SelectTab(kind);
        _selected.Clear();
        await RenderAsync(++_renderVersion);
    }

    private void SelectTab(string kind)
    {
        _kind = kind;
        foreach (var (button, key) in new[] { (TabMods, "mod"), (TabWorlds, "world"), (TabResources, "resourcepack"), (TabShaders, "shader") })
        {
            var active = key == kind;
            // 选中态用主题色 + Motion.HoverInMs 过渡：浅色下也能一眼看出当前页签。
            FadeBrush(button, true, active
                ? ThemeColor("BrushSelected", Alpha(ThemeManager.Accent, 0x1A))
                : Color.FromArgb(0, 0, 0, 0), Motion.HoverInMs);
            FadeBrush(button, false, active
                ? ThemeColor("BrushNavSelectedBorder", Alpha(ThemeManager.Accent, 0x46))
                : ThemeColor("BrushStroke", Alpha(ThemeManager.Text, 0x14)), Motion.HoverInMs);
            button.Foreground = active
                ? (Brush)Application.Current.Resources["BrushMint"]
                : (Brush)Application.Current.Resources["BrushTextSecondary"];
        }
        ListTitle.Text = kind switch
        {
            "world" => "世界",
            "resourcepack" => "资源包",
            "shader" => "光影",
            _ => "模组"
        };
        BatchUpdateButton.Visibility = kind == "mod" ? Visibility.Visible : Visibility.Collapsed;
        BatchEnableButton.Visibility = kind == "mod" ? Visibility.Visible : Visibility.Collapsed;
        BatchDisableButton.Visibility = kind == "mod" ? Visibility.Visible : Visibility.Collapsed;
    }

    private async Task ReloadAsync()
    {
        if (_current is null) { HideBusy(); return; }
        var current = _current;
        var version = ++_renderVersion;
        ShowBusy("正在扫描" + KindLabel + "…");
        try
        {
            // 磁盘扫描（读 jar 元数据、算 SHA1）全部放在后台线程。
            var snapshot = await Task.Run(() => LumenRuntime.Library.Scan(current));
            if (version != _renderVersion) return;
            _snapshot = snapshot;
            await RenderAsync(version);
        }
        catch (Exception ex)
        {
            AppLog.Warn("library", "扫描内容失败：" + ex.Message);
            if (version == _renderVersion) ShowEmpty("扫描失败：" + ex.Message);
        }
        finally
        {
            if (version == _renderVersion) HideBusy();
        }
    }

    private IReadOnlyList<LibraryItem> CurrentItems()
    {
        if (_snapshot is null) return Array.Empty<LibraryItem>();
        return _kind switch
        {
            "world" => _snapshot.Worlds,
            "resourcepack" => _snapshot.ResourcePacks.OrderBy(x => x.Title, StringComparer.CurrentCulture).ToList(),
            "shader" => _snapshot.ShaderPacks.OrderBy(x => x.Title, StringComparer.CurrentCulture).ToList(),
            _ => _snapshot.Mods
        };
    }

    /// <summary>
    /// 分批把列表画出来：每批之间让出一帧，UI 线程不会被长时间占住，
    /// 背景的极光/方格动画在处理大列表时也能继续动。
    /// </summary>
    private async Task RenderAsync(int version)
    {
        if (_current is null || _snapshot is null) return;
        var items = CurrentItems();
        var folder = NovaLink.Lumen.Services.Minecraft.LibraryService.FolderFor(_current, _kind);
        PathText.Text = folder;
        WatchFolder(folder);
        CountText.Text = items.Count + " 项";
        ItemList.Children.Clear();
        EmptyPanel.Visibility = items.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        ItemList.Visibility = items.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
        // 内容开始上屏就收起加载层：否则会出现「转圈还在 + 列表已经出现」两层叠在一起。
        HideBusy();
        EmptyText.Text = _kind switch
        {
            "world" => "还没有世界",
            "resourcepack" => "还没有资源包",
            "shader" => "还没有光影",
            _ => "还没有模组"
        };
        _selected.RemoveWhere(path => items.All(item => !string.Equals(item.Path, path, StringComparison.OrdinalIgnoreCase)));

        const int batchSize = 8;
        for (var i = 0; i < items.Count; i += batchSize)
        {
            if (version != _renderVersion) return;
            var end = Math.Min(items.Count, i + batchSize);
            for (var j = i; j < end; j++)
            {
                var row = BuildRow(items[j]);
                ItemList.Children.Add(row);
                Motion.RowIn(row, j - i);
            }
            if (end < items.Count)
                await UiYield.NextFrameAsync();
        }

        if (version != _renderVersion) return;
        UpdateSelection();
    }

    /// <summary>库目录被外部改动（资源管理器删除/游戏写入）时自动重扫，600ms 防抖。</summary>
    private void WatchFolder(string folder)
    {
        try
        {
            if (_watcher is not null && string.Equals(_watcher.Path, folder, StringComparison.OrdinalIgnoreCase))
                return;
            _watcher?.Dispose();
            _watcher = null;
            if (!Directory.Exists(folder)) return;
            var watcher = new FileSystemWatcher(folder)
            {
                IncludeSubdirectories = true,
                NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName,
                InternalBufferSize = 32 * 1024,
                EnableRaisingEvents = true
            };
            watcher.Created += (_, _) => QueueWatchRefresh();
            watcher.Deleted += (_, _) => QueueWatchRefresh();
            watcher.Renamed += (_, _) => QueueWatchRefresh();
            _watcher = watcher;
        }
        catch (Exception)
        {
            // 目录不可监听（权限/网络盘）时静默放弃，手动刷新仍然可用。
        }
    }

    private void QueueWatchRefresh()
    {
        DispatcherQueue.TryEnqueue(() =>
        {
            if (_watchDebounce is null)
            {
                _watchDebounce = DispatcherQueue.CreateTimer();
                _watchDebounce.IsRepeating = false;
                _watchDebounce.Interval = TimeSpan.FromMilliseconds(600);
                _watchDebounce.Tick += (_, _) => _ = ReloadAsync();
            }
            // 每次事件都重新计时，避免批量删除时整目录被反复扫描。
            _watchDebounce.Start();
        });
    }

    private static Color ThemeColor(string key, Color fallback) =>
        Application.Current.Resources.TryGetValue(key, out var value) && value is SolidColorBrush brush
            ? brush.Color
            : fallback;

    private static Color Alpha(Color color, byte alpha) => Color.FromArgb(alpha, color.R, color.G, color.B);

    private static Color DangerColor => ThemeColor("BrushDanger", ThemeManager.IsLight
        ? Color.FromArgb(0xFF, 0xD1, 0x3F, 0x62)
        : Color.FromArgb(0xFF, 0xFF, 0x6B, 0x8A));

    /// <summary>换色时用 Motion.HoverInMs 过渡，选中/未选中的切换不再硬跳。</summary>
    private static void FadeBrush(Control target, bool background, Color to, int durationMs)
    {
        var previous = (background ? target.Background : target.BorderBrush) as SolidColorBrush;
        var brush = new SolidColorBrush(to);
        if (background) target.Background = brush; else target.BorderBrush = brush;
        AnimateBrush(brush, previous?.Color, to, durationMs);
    }

    private static void FadeBrush(Border target, bool background, Color to, int durationMs)
    {
        var previous = (background ? target.Background : target.BorderBrush) as SolidColorBrush;
        var brush = new SolidColorBrush(to);
        if (background) target.Background = brush; else target.BorderBrush = brush;
        AnimateBrush(brush, previous?.Color, to, durationMs);
    }

    private static void AnimateBrush(SolidColorBrush brush, Color? from, Color to, int durationMs)
    {
        if (from is null || durationMs <= 0 || from.Value == to) return;
        try
        {
            var animation = new ColorAnimation
            {
                From = from.Value,
                To = to,
                Duration = new Duration(TimeSpan.FromMilliseconds(durationMs)),
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
            // 动画失败时颜色已经落地为目标色，不影响可用性。
        }
    }

    private Border BuildRow(LibraryItem item)
    {
        var card = new Border
        {
            // 玻璃行卡片：填充取玻璃淡层，描边走主题实色，浅色主题下边界才立得住。
            Background = (Brush)Application.Current.Resources["BrushGlassFaint"],
            BorderBrush = ThemeManager.BrushCardBorder,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(14),
            Padding = new Thickness(12, 10, 14, 10)
        };
        var grid = new Grid { ColumnSpacing = 11 };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var check = new CheckBox
        {
            Tag = item,
            IsChecked = _selected.Contains(item.Path),
            MinWidth = 0,
            VerticalAlignment = VerticalAlignment.Center
        };
        check.Checked += (_, _) => SetSelected(item, true);
        check.Unchecked += (_, _) => SetSelected(item, false);
        Grid.SetColumn(check, 0);
        grid.Children.Add(check);

        var mark = BuildMark(item);
        Grid.SetColumn(mark, 1);
        grid.Children.Add(mark);

        var text = new StackPanel { Spacing = 2, VerticalAlignment = VerticalAlignment.Center };
        var titleRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        titleRow.Children.Add(new TextBlock
        {
            Text = item.Title,
            FontSize = 13.5,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            TextTrimming = TextTrimming.CharacterEllipsis,
            MaxWidth = 520
        });
        if (!item.Enabled)
            titleRow.Children.Add(PillTag("已禁用", DangerColor));
        if (!string.IsNullOrWhiteSpace(item.Loader))
            titleRow.Children.Add(PillTag(item.Loader!, ThemeColor("BrushMint", ThemeManager.Accent)));
        text.Children.Add(titleRow);
        text.Children.Add(new TextBlock
        {
            Text = FormatSize(item.Size) + "  ·  " + FormatTime(item.Modified) + (string.IsNullOrWhiteSpace(item.Subtitle) ? "" : "  ·  " + item.Subtitle),
            FontSize = 11,
            Foreground = (Brush)Application.Current.Resources["BrushTextTertiary"],
            TextTrimming = TextTrimming.CharacterEllipsis
        });
        if (!string.IsNullOrWhiteSpace(item.Description))
        {
            text.Children.Add(new TextBlock
            {
                Text = item.Description,
                FontSize = 11.5,
                Foreground = (Brush)Application.Current.Resources["BrushTextSecondary"],
                TextWrapping = TextWrapping.Wrap,
                MaxLines = 2,
                TextTrimming = TextTrimming.CharacterEllipsis,
                Margin = new Thickness(0, 2, 0, 0)
            });
        }
        else if (!string.IsNullOrWhiteSpace(item.Authors))
        {
            text.Children.Add(new TextBlock
            {
                Text = "作者 " + item.Authors,
                FontSize = 11,
                Foreground = (Brush)Application.Current.Resources["BrushTextTertiary"]
            });
        }
        Grid.SetColumn(text, 2);
        grid.Children.Add(text);

        var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, VerticalAlignment = VerticalAlignment.Center };
        var details = SmallButton("详情");
        details.Click += async (_, _) => await ShowDetailsAsync(item);
        actions.Children.Add(details);
        if (item.Kind == "mod")
        {
            var toggle = SmallButton(item.Enabled ? "禁用" : "启用");
            toggle.Click += async (_, _) =>
            {
                try
                {
                    if (!LumenRuntime.Library.SetEnabled(item, !item.Enabled))
                        await MessageDialog.ShowAsync(XamlRoot, "无法切换", "目标文件已存在或源文件已消失。");
                }
                catch (Exception ex)
                {
                    await MessageDialog.ShowAsync(XamlRoot, "无法切换", ex.Message);
                }
                await ReloadAsync();
            };
            actions.Children.Add(toggle);
        }
        var open = SmallButton("打开");
        open.Click += (_, _) => OpenItem(item);
        actions.Children.Add(open);
        var remove = SmallButton("删除", true);
        remove.Click += async (_, _) => await DeleteAsync(item);
        actions.Children.Add(remove);
        Grid.SetColumn(actions, 3);
        grid.Children.Add(actions);

        // 顶边 1.5px 液态高光，和主容器同一配方；不参与命中测试。
        var shell = new Grid();
        shell.Children.Add(grid);
        shell.Children.Add(new Rectangle
        {
            Height = 1.5,
            RadiusX = 13,
            RadiusY = 13,
            Margin = new Thickness(1, 0, 1, 0),
            VerticalAlignment = VerticalAlignment.Top,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            Fill = (Brush)Application.Current.Resources["BrushGlassHighlight"],
            IsHitTestVisible = false
        });
        card.Child = shell;
        card.Tag = "no-motion";
        Motion.Hover(card, 1.003f, 1.0f);
        return card;
    }

    private static Border BuildMark(LibraryItem item)
    {
        var host = new Border
        {
            Width = 42,
            Height = 42,
            CornerRadius = new CornerRadius(11),
            Background = ThemeManager.Brush("BrushAccentSoft", Alpha(ThemeManager.Accent, 0x14)),
            VerticalAlignment = VerticalAlignment.Center
        };
        if (!string.IsNullOrWhiteSpace(item.IconPath) && File.Exists(item.IconPath))
        {
            try
            {
                host.Background = new ImageBrush
                {
                    // 图标只显示 42px，按 2 倍解码就够了：整包图标解码是进库时卡顿的大头。
                    ImageSource = new BitmapImage(new Uri("file:///" + item.IconPath!.Replace('\\', '/')))
                    {
                        DecodePixelWidth = 84,
                        DecodePixelType = DecodePixelType.Logical
                    },
                    Stretch = Stretch.UniformToFill
                };
                return host;
            }
            catch { }
        }
        var letter = item.Kind switch
        {
            "world" => "W",
            "resourcepack" => "R",
            "shader" => "S",
            _ => string.IsNullOrWhiteSpace(item.Title) ? "M" : item.Title[..1].ToUpperInvariant()
        };
        host.Child = new TextBlock
        {
            Text = letter,
            FontSize = 15,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Foreground = (Brush)Application.Current.Resources["BrushMint"]
        };
        return host;
    }

    private static Border PillTag(string text, Color color) => new()
    {
        Padding = new Thickness(7, 2, 7, 2),
        CornerRadius = new CornerRadius(6),
        Background = new SolidColorBrush(Color.FromArgb(22, color.R, color.G, color.B)),
        Child = new TextBlock { Text = text, FontSize = 10, Foreground = new SolidColorBrush(color) }
    };

    private void SetSelected(LibraryItem item, bool selected)
    {
        if (selected) _selected.Add(item.Path); else _selected.Remove(item.Path);
        UpdateSelection();
    }

    private void UpdateSelection()
    {
        var items = CurrentItems();
        _bulkGuard = true;
        SelectAllCheck.IsChecked = items.Count > 0 && items.All(item => _selected.Contains(item.Path));
        SelectAllCheck.IsEnabled = items.Count > 0;
        _bulkGuard = false;
        SelectionText.Text = _selected.Count == 0 ? "全选" : "已选 " + _selected.Count + " 项";
        var has = _selected.Count > 0;
        BatchEnableButton.IsEnabled = has && _kind == "mod";
        BatchDisableButton.IsEnabled = has && _kind == "mod";
        BatchUpdateButton.IsEnabled = has && _kind == "mod";
        BatchDeleteButton.IsEnabled = has;
    }

    private async void SelectAll_Changed(object sender, RoutedEventArgs e)
    {
        if (_bulkGuard) return;
        var check = SelectAllCheck.IsChecked == true;
        _selected.Clear();
        if (check)
            foreach (var item in CurrentItems()) _selected.Add(item.Path);
        // Rebuild checkboxes without rescanning disk.
        await RenderAsync(++_renderVersion);
    }

    private List<LibraryItem> SelectedItems() => CurrentItems().Where(item => _selected.Contains(item.Path)).ToList();

    private async Task EnsureSelectionAsync()
    {
        if (_selected.Count == 0)
            await MessageDialog.ShowAsync(XamlRoot, "还没有选择", "先勾选要操作的内容。");
    }

    private async void BatchEnable_Click(object sender, RoutedEventArgs e)
    {
        await EnsureSelectionAsync();
        var count = LumenRuntime.Library.SetEnabled(SelectedItems(), true);
        await ReloadAsync();
        if (_selected.Count > 0) await MessageDialog.ShowAsync(XamlRoot, "已启用", "已启用 " + count + " 个模组。");
    }

    private async void BatchDisable_Click(object sender, RoutedEventArgs e)
    {
        await EnsureSelectionAsync();
        var count = LumenRuntime.Library.SetEnabled(SelectedItems(), false);
        await ReloadAsync();
        if (_selected.Count > 0) await MessageDialog.ShowAsync(XamlRoot, "已禁用", "已禁用 " + count + " 个模组。");
    }

    private async void BatchUpdate_Click(object sender, RoutedEventArgs e)
    {
        await EnsureSelectionAsync();
        var mods = SelectedItems();
        if (mods.Count > 0) await RunUpdatesAsync(mods);
    }

    private async void BatchDelete_Click(object sender, RoutedEventArgs e)
    {
        await EnsureSelectionAsync();
        var items = SelectedItems();
        if (items.Count == 0) return;
        var modal = GlassModal.Show("删除 " + items.Count + " 项内容", "文件会被永久删除，无法撤销。", 480);
        modal.PrimaryText = "删除";
        modal.PrimaryInvoked = async () =>
        {
            var count = 0;
            foreach (var item in items)
            {
                try { LumenRuntime.Library.Delete(item); count++; }
                catch (Exception ex) { AppLog.Warn("library", "删除失败：" + ex.Message); }
            }
            _selected.Clear();
            modal.Close();
            await ReloadAsync();
            await MessageDialog.ShowAsync(XamlRoot, "删除完成", "已删除 " + count + " 项内容。");
        };
        await modal.WaitAsync();
    }

    private async void Import_Click(object sender, RoutedEventArgs e)
    {
        if (_current is null) return;
        if (_kind == "world")
        {
            await MessageDialog.ShowAsync(XamlRoot, "导入世界", "请把世界文件夹直接放入 saves 目录，或在实例页打开游戏目录。");
            return;
        }
        var picker = new FileOpenPicker();
        WinRT.Interop.InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(MainWindow.Current));
        picker.ViewMode = PickerViewMode.List;
        picker.SuggestedStartLocation = PickerLocationId.Downloads;
        if (_kind == "mod") picker.FileTypeFilter.Add(".jar");
        else { picker.FileTypeFilter.Add(".zip"); }
        var files = await picker.PickMultipleFilesAsync();
        if (files.Count == 0) return;
        var failed = 0;
        foreach (var file in files)
        {
            try { LumenRuntime.Library.Import(file.Path, _current, _kind); }
            catch (Exception ex) { failed++; AppLog.Warn("library", "导入失败：" + ex.Message); }
        }
        await ReloadAsync();
        var message = "已导入 " + (files.Count - failed) + " 个文件。";
        if (failed > 0) message += "\n" + failed + " 个文件导入失败。";
        await MessageDialog.ShowAsync(XamlRoot, "导入完成", message);
    }

    private async Task ShowDetailsAsync(LibraryItem item)
    {
        if (item.Kind == "mod")
        {
            ModrinthVersion? version = null;
            GlassModal? busy = null;
            try
            {
                if (!string.IsNullOrWhiteSpace(item.Sha1))
                {
                    busy = GlassModal.Show("识别模组", "正在向 Modrinth 匹配文件指纹…", 430);
                    busy.HideButtons();
                    busy.SetBusy(true);
                    version = await LumenRuntime.Modrinth.LookupByHashAsync(item.Sha1);
                }
            }
            catch (Exception ex)
            {
                AppLog.Warn("library", "Modrinth 指纹查询失败：" + ex.Message);
            }
            finally
            {
                busy?.Close();
            }

            ModrinthProject? project = null;
            if (version is not null && !string.IsNullOrWhiteSpace(version.ProjectId))
            {
                try { project = await LumenRuntime.Modrinth.ProjectAsync(version.ProjectId); }
                catch (Exception ex) { AppLog.Warn("library", "读取 Modrinth 项目失败：" + ex.Message); }
            }
            project ??= await FindProjectAsync(item);
            if (project is not null)
            {
                await ProjectDetailDialog.ShowAsync(XamlRoot, project, _current, item.Version, true);
                return;
            }
        }
        await ShowLocalDetailsAsync(item);
    }

    private async Task<ModrinthProject?> FindProjectAsync(LibraryItem item)
    {
        try
        {
            var query = SearchQuery(item);
            if (query.Length < 3) return null;
            var result = await LumenRuntime.Modrinth.SearchAsync(query, "mod", "", "", 0, 10);
            var key = NormalizeKey(query);
            var hit = result.Hits.FirstOrDefault(h => NormalizeKey(h.Title) == key)
                ?? result.Hits.FirstOrDefault(h => NormalizeKey(h.Title).Contains(key) || key.Contains(NormalizeKey(h.Title)));
            if (hit is null || string.IsNullOrWhiteSpace(hit.ProjectId)) return null;
            return await LumenRuntime.Modrinth.ProjectAsync(hit.ProjectId);
        }
        catch (Exception ex)
        {
            AppLog.Warn("library", "Modrinth 搜索匹配失败：" + ex.Message);
            return null;
        }
    }

    private static string SearchQuery(LibraryItem item)
    {
        var name = Path.GetFileNameWithoutExtension(item.Name);
        name = Regex.Replace(name ?? "", @"\[[^\]]*\]", " ");
        name = Regex.Replace(name, @"(?i)(fabric|forge|neoforge|quilt)[-_]?\d.*$", " ");
        name = Regex.Replace(name, @"[-_ ]?v?\d+(\.\d+)+.*$", " ");
        return Regex.Replace(name, @"[-_]+", " ").Trim();
    }

    private static string NormalizeKey(string? value)
        => Regex.Replace((value ?? "").ToLowerInvariant(), "[^a-z0-9]", "");

    private async Task ShowLocalDetailsAsync(LibraryItem item)
    {
        var modal = GlassModal.Show(item.Title, item.Subtitle, 560);
        var details = new StackPanel { Spacing = 8 };
        void Line(string label, string? value)
        {
            if (string.IsNullOrWhiteSpace(value)) return;
            var row = new Grid { ColumnSpacing = 14 };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(94) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.Children.Add(new TextBlock { Text = label, FontSize = 11.5, Foreground = (Brush)Application.Current.Resources["BrushTextTertiary"] });
            var body = new TextBlock { Text = value, FontSize = 12, TextWrapping = TextWrapping.Wrap, Foreground = (Brush)Application.Current.Resources["BrushTextSecondary"] };
            Grid.SetColumn(body, 1);
            row.Children.Add(body);
            details.Children.Add(row);
        }
        Line("文件", item.Name);
        Line("版本", item.Version);
        Line("加载器", item.Loader);
        Line("作者", item.Authors);
        Line("格式", item.PackFormat > 0 ? "pack_format " + item.PackFormat : null);
        Line("大小", FormatSize(item.Size));
        Line("修改", FormatTime(item.Modified));
        Line("SHA1", item.Sha1);
        Line("路径", item.Path);
        if (!string.IsNullOrWhiteSpace(item.Description))
        {
            details.Children.Add(new TextBlock
            {
                Text = item.Description,
                FontSize = 12.5,
                TextWrapping = TextWrapping.Wrap,
                Foreground = (Brush)Application.Current.Resources["BrushTextSecondary"],
                Margin = new Thickness(0, 6, 0, 0)
            });
        }
        modal.Body.Children.Add(new ScrollViewer
        {
            MaxHeight = 300,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            Content = details
        });
        modal.PrimaryText = "打开目录";
        modal.PrimaryInvoked = () => { OpenItem(item); return Task.CompletedTask; };
        modal.SecondaryText = "关闭";
        await modal.WaitAsync();
    }

    private async void CheckUpdates_Click(object sender, RoutedEventArgs e)
    {
        if (_current is null) return;
        if (_kind != "mod")
        {
            SelectTab("mod");
            await RenderAsync(++_renderVersion);
        }
        var mods = CurrentItems().ToList();
        if (mods.Count == 0)
        {
            await MessageDialog.ShowAsync(XamlRoot, "没有模组", "当前实例还没有安装任何模组。");
            return;
        }
        await RunUpdatesAsync(mods);
    }

    private async Task RunUpdatesAsync(IReadOnlyList<LibraryItem> mods)
    {
        if (_current is null) return;
        var busy = GlassModal.Show("检查模组更新", "正在向 Modrinth 查询最新版本…", 560);
        busy.HideButtons();
        busy.SetCloseAllowed(false);
        var status = new TextBlock
        {
            Text = "正在计算 " + mods.Count + " 个文件的指纹…",
            FontSize = 12,
            TextWrapping = TextWrapping.Wrap,
            Foreground = (Brush)Application.Current.Resources["BrushTextSecondary"]
        };
        var bar = new ProgressBar { IsIndeterminate = true, Height = 4, CornerRadius = new CornerRadius(2) };
        busy.Body.Children.Add(status);
        busy.Body.Children.Add(bar);

        IReadOnlyList<ModrinthService.ModFileUpdate> updates;
        try
        {
            updates = await LumenRuntime.Modrinth.CheckUpdatesAsync(mods, _current.Kernel, LoaderTag(_current.Loader));
        }
        catch (Exception ex)
        {
            busy.Close();
            AppLog.Warn("library", "检查模组更新失败：" + ex.Message);
            await MessageDialog.ShowAsync(XamlRoot, "检查更新失败", ex.Message);
            return;
        }
        busy.Close();

        if (updates.Count == 0)
        {
            await MessageDialog.ShowAsync(XamlRoot, "已是最新", "已检查 " + mods.Count + " 个模组，没有发现可更新版本。");
            return;
        }

        var confirm = GlassModal.Show("发现 " + updates.Count + " 个更新", "更新会替换原模组文件，世界与配置不受影响。", 560);
        var list = new StackPanel { Spacing = 9 };
        foreach (var update in updates.Take(10))
        {
            var row = new StackPanel { Spacing = 2 };
            row.Children.Add(new TextBlock
            {
                Text = update.Item.Title,
                FontSize = 13,
                FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                TextTrimming = TextTrimming.CharacterEllipsis
            });
            row.Children.Add(new TextBlock
            {
                Text = update.Version.VersionNumber + "  ·  " + update.FileName,
                FontSize = 11,
                Foreground = (Brush)Application.Current.Resources["BrushTextTertiary"],
                TextTrimming = TextTrimming.CharacterEllipsis
            });
            list.Children.Add(row);
        }
        if (updates.Count > 10)
            list.Children.Add(new TextBlock { Text = "以及另外 " + (updates.Count - 10) + " 个模组", FontSize = 11, Foreground = (Brush)Application.Current.Resources["BrushTextTertiary"] });
        confirm.Body.Children.Add(list);
        confirm.PrimaryText = "更新全部";
        confirm.SecondaryText = "稍后";
        confirm.PrimaryInvoked = async () =>
        {
            confirm.HideButtons();
            confirm.SetCloseAllowed(false);
            var updateStatus = new TextBlock
            {
                Text = "准备更新…",
                FontSize = 12,
                TextWrapping = TextWrapping.Wrap,
                Foreground = (Brush)Application.Current.Resources["BrushTextSecondary"]
            };
            var updateBar = new ProgressBar { Height = 4, CornerRadius = new CornerRadius(2), Minimum = 0, Maximum = 100 };
            confirm.Body.Children.Add(updateStatus);
            confirm.Body.Children.Add(updateBar);
            IProgress<DownloadProgress> progress = new Progress<DownloadProgress>(p =>
            {
                updateBar.Value = Math.Clamp(p.Percent, 0, 100);
                updateStatus.Text = p.Message;
            });
            var done = 0;
            var failed = 0;
            foreach (var update in updates)
            {
                try
                {
                    progress.Report(new DownloadProgress("update", 0, 0, 0, 0, update.Item.Title));
                    await LumenRuntime.Modrinth.ApplyUpdateAsync(update, progress);
                    done++;
                }
                catch (Exception ex)
                {
                    failed++;
                    AppLog.Warn("library", "更新 " + update.Item.Title + " 失败：" + ex.Message);
                }
            }
            confirm.Close();
            await ReloadAsync();
            var message = "已更新 " + done + " 个模组。";
            if (failed > 0) message += "\n" + failed + " 个模组更新失败，已保留原文件，请稍后重试。";
            await MessageDialog.ShowAsync(XamlRoot, "更新完成", message);
        };
        await confirm.WaitAsync();
    }

    private static string LoaderTag(string loader) => loader switch
    {
        "Fabric" => "fabric",
        "Quilt" => "quilt",
        "Forge" => "forge",
        "NeoForge" => "neoforge",
        _ => ""
    };

    private static Button SmallButton(string label, bool danger = false)
    {
        var button = new Button
        {
            Content = label,
            Tag = "no-motion",
            Height = 30,
            Padding = new Thickness(11, 0, 11, 0),
            FontSize = 11.5,
            CornerRadius = new CornerRadius(8),
            Background = ThemeManager.Brush("BrushFillSubtle", Alpha(ThemeManager.Text, 0x0A)),
            BorderBrush = ThemeManager.BrushCardBorder,
            BorderThickness = new Thickness(1)
        };
        if (danger)
            button.Foreground = ThemeManager.BrushDanger;
        return button;
    }

    private async Task DeleteAsync(LibraryItem item)
    {
        var modal = GlassModal.Show("删除内容", item.Title, 460);
        modal.Body.Children.Add(new TextBlock
        {
            Text = "此操作会永久删除文件，无法撤销。",
            TextWrapping = TextWrapping.Wrap,
            Foreground = (Brush)Application.Current.Resources["BrushTextSecondary"]
        });
        modal.PrimaryText = "删除";
        modal.PrimaryInvoked = async () =>
        {
            try
            {
                LumenRuntime.Library.Delete(item);
                modal.Close();
                await ReloadAsync();
            }
            catch (Exception ex)
            {
                await MessageDialog.ShowAsync(XamlRoot, "删除失败", ex.Message);
            }
        };
        await modal.WaitAsync();
    }

    private void OpenItem(LibraryItem item)
    {
        try
        {
            var args = item.IsDirectory ? "\"" + item.Path + "\"" : "/select,\"" + item.Path + "\"";
            Process.Start(new ProcessStartInfo("explorer.exe", args) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            AppLog.Warn("library", "打开失败：" + ex.Message);
        }
    }

    private void OpenFolder_Click(object sender, RoutedEventArgs e)
    {
        if (_current is null) return;
        try
        {
            Process.Start(new ProcessStartInfo("explorer.exe", NovaLink.Lumen.Services.Minecraft.LibraryService.FolderFor(_current, _kind)) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            AppLog.Warn("library", "打开目录失败：" + ex.Message);
        }
    }

    private void ShowEmpty(string message)
    {
        ItemList.Visibility = Visibility.Collapsed;
        EmptyPanel.Visibility = Visibility.Visible;
        EmptyText.Text = message;
    }

    private static string FormatSize(long bytes)
    {
        if (bytes <= 0) return "0 B";
        string[] units = { "B", "KB", "MB", "GB" };
        double value = bytes;
        var index = 0;
        while (value >= 1024 && index < units.Length - 1) { value /= 1024; index++; }
        return value.ToString(value >= 100 ? "0" : "0.0", CultureInfo.InvariantCulture) + " " + units[index];
    }

    private static string FormatTime(long unixMs)
    {
        if (unixMs <= 0) return "未知时间";
        var time = DateTimeOffset.FromUnixTimeMilliseconds(unixMs).ToLocalTime();
        return time.Year == DateTimeOffset.Now.Year ? time.ToString("MM-dd HH:mm") : time.ToString("yyyy-MM-dd");
    }
}