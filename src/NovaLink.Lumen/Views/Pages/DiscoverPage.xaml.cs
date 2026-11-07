using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using Microsoft.UI.Xaml.Media.Imaging;
using NovaLink.Lumen.Controls;
using NovaLink.Lumen.Core.Models;
using NovaLink.Lumen.Services;
using NovaLink.Lumen.Services.Modrinth;
using NovaLink.Lumen.Views.Dialogs;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Windows.Storage.Pickers;
using Windows.UI;

namespace NovaLink.Lumen.Views.Pages;

public sealed partial class DiscoverPage : Page
{
    private const int PageSize = 18;

    private static readonly Dictionary<string, (string Label, string Slug)[]> CategorySets = new()
    {
        ["mod"] = new[]
        {
            ("全部", ""), ("优化", "optimization"), ("科技", "technology"), ("魔法", "magic"),
            ("冒险", "adventure"), ("装饰", "decoration"), ("前置库", "library"), ("世界生成", "worldgen"),
            ("存储", "storage"), ("装备", "equipment"), ("生物", "mobs"), ("实用", "utility")
        },
        ["resourcepack"] = new[]
        {
            ("全部", ""), ("写实", "realistic"), ("现代", "modern"), ("中世纪", "medieval"),
            ("主题", "themed"), ("字体", "fonts"), ("16x", "16x"), ("32x", "32x")
        },
        ["shader"] = new[]
        {
            ("全部", ""), ("写实", "realistic"), ("奇幻", "fantasy"), ("卡通", "cartoon"),
            ("氛围", "atmospheric"), ("原版风", "vanilla"), ("低配", "low-end")
        },
        ["modpack"] = new[]
        {
            ("全部", ""), ("冒险", "adventure"), ("科技", "technology"), ("魔法", "magic"),
            ("整合", "kitchen-sink"), ("任务", "quests"), ("轻量", "lightweight"), ("优化", "optimization")
        }
    };

    private string _type = "mod";
    private string _query = "";
    private string _gameVersion = "";
    private string _loader = "";
    private string _category = "";
    private List<InstanceSummary> _instances = new();
    private readonly List<ModrinthProject> _items = new();
    private CancellationTokenSource? _searchCts;
    private long _total;
    private bool _ready;
    private bool _loading;
    private bool _orbsStarted;
    private long _skeletonAt;
    private LoadingState? _busy;
    private long _renderToken;

    public DiscoverPage()
    {
        InitializeComponent();
        // 实验性功能「启动器插件」关闭时，插件商店整栏都不出现，页面固定在「内容」。
        if (!LumenRuntime.PluginsEnabled) SegPlugins.Visibility = Visibility.Collapsed;
        // 两条分段栏都在这里装好药丸指示器，之后切换只负责把药丸滑过去。
        SegmentedSwitch.Enable(SegBar);
        SegmentedSwitch.Select(SegBar, 0, animate: false);
        SegmentedSwitch.Enable(TypeSegBar);
        SegmentedSwitch.Select(TypeSegBar, TypeIndex(_type), animate: false);
        _busy = LoadingState.Attach(PageRoot, "正在读取内容…");
        _busy.Hide();
        Loaded += async (_, _) => await InitAsync();
        Loaded += (_, _) => StartAmbient();
        Unloaded += OnPageUnloaded;
    }

    /// <summary>页面卸载：摘掉加载层并让正在分批搭建的卡片立即作废，避免动画计时器空转。</summary>
    private void OnPageUnloaded(object sender, RoutedEventArgs e)
    {
        _renderToken++;
        _busy?.Detach();
        _busy = null;
    }

    /// <summary>显示整页加载层（只做提示，不拦截点击）。</summary>
    private void ShowBusy(string message)
    {
        if (_busy is null) return;
        _busy.SetMessage(message);
        _busy.Show();
    }

    /// <summary>收起整页加载层。</summary>
    private void HideBusy() => _busy?.Hide();

    private void StartAmbient()
    {
        if (_orbsStarted) return;
        _orbsStarted = true;
        Motion.Float(DiscoverOrbA, 7, 8.4, 0);
        Motion.Float(DiscoverOrbB, 5, 6.6, 420);
        Motion.Float(DiscoverOrbC, 4, 5.4, 860);
    }

    private async Task InitAsync()
    {
        if (_ready) return;
        ShowSkeleton("正在连接内容库…");
        try
        {
            var manifest = await LumenRuntime.Manifest.GetAsync();
            var releases = manifest.Where(v => v.Type == "release")
                .OrderByDescending(v => v.ReleaseTime)
                .Select(v => v.Id).Distinct().ToList();
            VersionCombo.Items.Add(new ComboBoxItem { Content = "全部版本", Tag = "" });
            foreach (var id in releases) VersionCombo.Items.Add(new ComboBoxItem { Content = id, Tag = id });
            VersionCombo.SelectedIndex = 0;

            LoaderCombo.Items.Add(new ComboBoxItem { Content = "全部加载器", Tag = "" });
            LoaderCombo.Items.Add(new ComboBoxItem { Content = "Fabric", Tag = "fabric" });
            LoaderCombo.Items.Add(new ComboBoxItem { Content = "Forge", Tag = "forge" });
            LoaderCombo.Items.Add(new ComboBoxItem { Content = "NeoForge", Tag = "neoforge" });
            LoaderCombo.Items.Add(new ComboBoxItem { Content = "Quilt", Tag = "quilt" });
            LoaderCombo.SelectedIndex = 0;

            _instances = (await LumenRuntime.Instances.ScanAsync()).ToList();
            foreach (var item in _instances)
                InstanceCombo.Items.Add(new ComboBoxItem { Content = item.Name + " · " + item.Kernel, Tag = item.Id });
            if (_instances.Count > 0)
            {
                var activeId = LumenRuntime.Settings.Settings.SelectedInstanceId;
                var index = _instances.FindIndex(x => string.Equals(x.Id, activeId, StringComparison.OrdinalIgnoreCase));
                InstanceCombo.SelectedIndex = index >= 0 ? index : 0;
            }
            else InstanceCombo.PlaceholderText = "未安装实例";

            _ready = true;
            BuildCategories();
            UpdateTypeButtons();
            await ReloadAsync();
        }
        catch (Exception ex)
        {
            ResultsPanel.Children.Clear();
            StatusText.Text = "读取内容库失败：" + ex.Message;
            AppLog.Warn("discover", "初始化失败：" + ex);
        }
    }

    private void BuildCategories()
    {
        if (_type == "vanilla")
        {
            BuildVanillaCategories();
            return;
        }
        CategoryRow.Children.Clear();
        var set = CategorySets.TryGetValue(_type, out var value) ? value : CategorySets["mod"];
        foreach (var (label, slug) in set)
        {
            var button = new Button
            {
                Content = label,
                Tag = slug,
                Height = 32,
                Padding = new Thickness(15, 0, 15, 0),
                CornerRadius = new CornerRadius(9),
                FontSize = 11.5,
                BorderThickness = new Thickness(0)
            };
            button.Click += Category_Click;
            Motion.Hover(button, 1.012f, 1.2f);
            CategoryRow.Children.Add(button);
        }
        UpdateCategoryButtons();
    }

    private void UpdateCategoryButtons()
    {
        foreach (var child in CategoryRow.Children)
        {
            if (child is not Button button) continue;
            var active = string.Equals(button.Tag?.ToString() ?? "", _category, StringComparison.OrdinalIgnoreCase);
            // 选中/未选中都用主题 brush：深色下是薄荷色调，浅色下自动变成更深的青绿 + 淡灰底，
            // 不再用写死的 0x2A7CEBD6 / 0x12FFFFFF（浅色下几乎看不见）。
            button.Background = active ? Res("BrushSelected") : Res("BrushFillSubtle");
            button.Foreground = active ? Res("BrushMint") : Res("BrushTextSecondary");
        }
    }

    private async void Category_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button button) return;
        _category = button.Tag?.ToString() ?? "";
        UpdateCategoryButtons();
        await ReloadAsync();
    }

    private static string LoaderTag(string loader) => loader.ToLowerInvariant() switch
    {
        "fabric" => "fabric",
        "forge" => "forge",
        "neoforge" => "neoforge",
        "quilt" => "quilt",
        _ => ""
    };

    private async void Search_Click(object sender, RoutedEventArgs e) => await ReloadAsync();

    private async void SearchBox_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key != Windows.System.VirtualKey.Enter) return;
        e.Handled = true;
        await ReloadAsync();
    }

    private async void Filter_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (!_ready) return;
        if (VersionCombo.SelectedItem is ComboBoxItem version) _gameVersion = version.Tag?.ToString() ?? "";
        if (LoaderCombo.SelectedItem is ComboBoxItem loader) _loader = loader.Tag?.ToString() ?? "";
        await ReloadAsync();
    }

    private async void Type_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button button) return;
        _type = button.Tag?.ToString() ?? "mod";
        _category = "";
        _loader = "";
        if (LoaderCombo.Items.Count > 0) LoaderCombo.SelectedIndex = 0;
        BuildCategories();
        UpdateTypeButtons();
        await ReloadAsync();
    }

    /// <summary>内容类型在分段栏里的下标，顺序必须和 XAML 里 TypeVanilla…TypeModpack 一致。</summary>
    private static int TypeIndex(string type) => (type ?? "").ToLowerInvariant() switch
    {
        "vanilla" => 0,
        "resourcepack" => 2,
        "shader" => 3,
        "modpack" => 4,
        _ => 1
    };

    private void UpdateTypeButtons()
    {
        // 选中态交给药丸指示器（薄荷色文字 + 100% 不透明度），这里只负责把药丸滑到当前类型。
        // 不再改字重：字重变化会让按钮宽度抖动，药丸就会在动画途中被重新对齐而跳一下。
        SegmentedSwitch.Select(TypeSegBar, TypeIndex(_type));

        var content = _type is "mod" or "resourcepack" or "shader" or "modpack";
        VersionCombo.Visibility = content ? Visibility.Visible : Visibility.Collapsed;
        InstanceCombo.Visibility = content ? Visibility.Visible : Visibility.Collapsed;
        FilterDivider.Visibility = content ? Visibility.Visible : Visibility.Collapsed;
        LoaderCombo.Visibility = _type is "mod" or "modpack" ? Visibility.Visible : Visibility.Collapsed;
        SourceText.Text = content ? "MODRINTH · 实时目录" : "MOJANG · 版本清单";
        SearchBox.PlaceholderText = content
            ? "搜索模组、作者或中文关键词（如 钠、苹果皮）"
            : "搜索版本号（如 26.3、1.21.1）";
        FooterHint.Text = content
            ? "内容由 Modrinth 提供 · 下载前会询问保存位置 · 需要网络连接"
            : "版本由 Mojang 官方清单提供 · 安装前会按设置决定是否版本隔离";
        HeroSubtitle.Text = content
            ? "模组、资源包、光影与整合包。先浏览全部内容，需要时再按版本或加载器筛选。"
            : "Mojang 原版游戏都在这里。按正式版、快照版、远古版与愚人节版折叠浏览。";
    }

    private async Task ReloadAsync()
    {
        if (!_ready) return;
        _searchCts?.Cancel();
        var cts = new CancellationTokenSource();
        _searchCts = cts;
        _loading = true;
        var query = SearchBox.Text.Trim();
        _query = query;
        _items.Clear();

        if (_type == "vanilla")
        {
            await ReloadVanillaAsync();
            return;
        }

        ShowSkeleton(query.Length > 0 ? "正在搜索「" + query + "」…" : "正在读取 Modrinth 资源…");
        EmptyPanel.Visibility = Visibility.Collapsed;
        LoadMoreButton.Visibility = Visibility.Collapsed;
        CountText.Text = "读取中";

        try
        {
            // 网络请求与 JSON 解析都放在后台线程，UI 线程只负责把结果变成卡片。
            var result = await Task.Run(() => LumenRuntime.Modrinth.SearchAsync(
                query, _type, _gameVersion, _loader, 0, PageSize,
                query.Length == 0 ? "downloads" : "relevance", _category, cts.Token), cts.Token);
            if (cts.IsCancellationRequested) return;
            _total = result.TotalHits;
            _items.AddRange(result.Hits);
            CountText.Text = _total.ToString("N0") + " 个结果 · " + TypeName(_type);
            StatusText.Text = "";
            await HoldSkeletonAsync();
            if (cts.IsCancellationRequested) return;
            HideBusy();
            await RenderItemsAsync(_items, cts.Token);
            UpdateFooter();
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            if (cts.IsCancellationRequested) return;
            ResultsPanel.Children.Clear();
            StatusText.Text = "搜索失败：" + ex.Message;
            EmptyText.Text = "搜索暂时不可用";
            EmptyPanel.Visibility = Visibility.Visible;
            AppLog.Warn("discover", "搜索失败：" + ex);
        }
        finally
        {
            if (ReferenceEquals(_searchCts, cts))
            {
                _loading = false;
                _searchCts = null;
                HideBusy();
            }
        }
    }

    private async void LoadMore_Click(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        _loading = true;
        LoadMoreButton.IsEnabled = false;
        var startIndex = _items.Count;
        // 等待期间在列表末尾补一行骨架（页面中央不再出现转圈）。
        var pending = BuildSkeletonRow();
        ResultsPanel.Children.Add(pending);
        try
        {
            var skip = _items.Count;
            var result = await Task.Run(() => LumenRuntime.Modrinth.SearchAsync(
                _query, _type, _gameVersion, _loader, skip, PageSize,
                _query.Length == 0 ? "downloads" : "relevance", _category));
            _total = result.TotalHits;
            foreach (var item in result.Hits)
            {
                var key = string.IsNullOrWhiteSpace(item.ProjectId) ? item.Slug : item.ProjectId;
                if (_items.Any(x => string.Equals(string.IsNullOrWhiteSpace(x.ProjectId) ? x.Slug : x.ProjectId, key, StringComparison.OrdinalIgnoreCase))) continue;
                _items.Add(item);
            }
            ResultsPanel.Children.Remove(pending);
            await RenderItemsAsync(_items, CancellationToken.None, startIndex);
            UpdateFooter();
        }
        catch (Exception ex)
        {
            ResultsPanel.Children.Remove(pending);
            StatusText.Text = "加载更多失败：" + ex.Message;
        }
        finally
        {
            _loading = false;
            LoadMoreButton.IsEnabled = true;
            HideBusy();
        }
    }

    private void UpdateFooter()
    {
        DisplayText.Text = _items.Count == 0 ? "" : "已显示 " + _items.Count + " / " + _total.ToString("N0");
        LoadMoreButton.Visibility = _items.Count > 0 && _items.Count < _total ? Visibility.Visible : Visibility.Collapsed;
        EmptyPanel.Visibility = _items.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        if (_items.Count == 0) EmptyText.Text = _query.Length > 0 ? "没有找到“" + _query + "”" : "这个分类暂时没有内容";
    }

    private async void Retry_Click(object sender, RoutedEventArgs e) => await ReloadAsync();

    private static string TypeName(string type) => type switch
    {
        "resourcepack" => "资源包",
        "shader" => "光影",
        "modpack" => "整合包",
        "vanilla" => "原版游戏",
        _ => "模组"
    };

    /// <summary>
    /// 结果区按最终排版铺骨架（模组/资源包是卡片网格，原版游戏是版本行列表），
    /// 不再用页面正中那枚转圈：整页只有一套加载反馈，骨架和真实内容尺寸一致，数据到位时不会跳。
    /// </summary>
    private void ShowSkeleton(string status)
    {
        _skeletonAt = Stopwatch.GetTimestamp();
        ResultsPanel.Children.Clear();
        StatusText.Text = status;
        if (_type == "vanilla")
        {
            ResultsPanel.Children.Add(new Border
            {
                Style = (Style)Application.Current.Resources["GlassCard"],
                Padding = new Thickness(18, 16, 18, 14),
                Child = Skeleton.VersionList()
            });
            return;
        }
        ResultsPanel.Children.Add(BuildSkeletonRow());
        ResultsPanel.Children.Add(BuildSkeletonRow());
    }

    private static Grid BuildSkeletonRow()
    {
        var row = new Grid { ColumnSpacing = 14, Height = 228 };
        for (var c = 0; c < 3; c++)
        {
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            var card = Skeleton.Card();
            Grid.SetColumn(card, c);
            row.Children.Add(card);
        }
        return row;
    }

    private async Task HoldSkeletonAsync()
    {
        if (_skeletonAt == 0) return;
        var elapsed = Stopwatch.GetElapsedTime(_skeletonAt);
        _skeletonAt = 0;
        var remain = TimeSpan.FromMilliseconds(480) - elapsed;
        if (remain > TimeSpan.Zero) await Task.Delay(remain);
    }

    /// <summary>
    /// 分批搭建卡片：每批 3 行（9 张）后让出一帧，避免一次性建上百张卡片把 UI 线程堵死。
    /// 每次渲染自增 <see cref="_renderToken"/>，过期的一轮会在下一个检查点直接退出。
    /// </summary>
    private async Task RenderItemsAsync(IReadOnlyList<ModrinthProject> items, CancellationToken ct, int startIndex = 0)
    {
        var token = ++_renderToken;
        var from = Math.Max(0, startIndex);
        if (from == 0) ResultsPanel.Children.Clear();
        HideBusy();
        // 追加模式：从已渲染的行之后接着往下建，不再清空整张表。
        // 之前每次都 Clear 会让内容高度瞬间归零，ScrollViewer 的偏移被夹回 0，
        // 用户看到的就是「点加载更多后自动滚回顶部」。
        for (var rowIndex = from / 3; rowIndex * 3 < items.Count; rowIndex++)
        {
            if (token != _renderToken || ct.IsCancellationRequested) return;
            var i = rowIndex * 3;
            var row = BuildRow(items, i);
            if (rowIndex < ResultsPanel.Children.Count)
            {
                ResultsPanel.Children.RemoveAt(rowIndex);
                ResultsPanel.Children.Insert(rowIndex, row);
            }
            else
            {
                ResultsPanel.Children.Add(row);
            }
            // 先上屏再入场：行只有在挂进视觉树后动画才会真正跑。
            for (var c = 0; c < row.Children.Count; c++)
                if (row.Children[c] is FrameworkElement cardElement) Motion.RowIn(cardElement, i + c);
            await UiYield.NextFrameAsync();
        }
    }

    private Grid BuildRow(IReadOnlyList<ModrinthProject> items, int index)
    {
        var row = new Grid { ColumnSpacing = 14, Height = 228 };
        for (var c = 0; c < 3; c++)
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        for (var c = 0; c < 3 && index + c < items.Count; c++)
        {
            var card = BuildCard(items[index + c]);
            Grid.SetColumn(card, c);
            row.Children.Add(card);
        }
        return row;
    }

    private Border BuildCard(ModrinthProject project)
    {
        // 玻璃配方：GlassCard 样式给 BrushGlassCard 底 + BrushGlassRim 描边，
        // 不再覆盖成 BrushCardTint（浅色下那块 #F4FFFFFF 看起来就是一张白纸）。
        var card = new Border
        {
            Style = (Style)Application.Current.Resources["GlassCard"],
            Padding = new Thickness(18, 16, 18, 14),
            Tag = project
        };
        card.Tapped += async (_, e) => { e.Handled = true; await ShowDetailAsync(project); };
        Motion.Hover(card, 1.008f, 2f);

        var root = new Grid();
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        var head = new Grid { ColumnSpacing = 11 };
        head.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        head.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        Border iconBox = new()
        {
            Width = 44, Height = 44, CornerRadius = new CornerRadius(13),
            Background = (Brush)Application.Current.Resources["BrushFillSoft"],
            Child = new TextBlock
            {
                Text = string.IsNullOrWhiteSpace(project.Title) ? "?" : project.Title[..1].ToUpperInvariant(),
                FontSize = 18, FontWeight = FontWeights.SemiBold,
                HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center
            }
        };
        if (!string.IsNullOrWhiteSpace(project.IconUrl))
        {
            var image = new Image { Stretch = Stretch.UniformToFill };
            try { image.Source = new BitmapImage(new Uri(project.IconUrl)); } catch { }
            image.ImageFailed += (_, _) => image.Visibility = Visibility.Collapsed;
            iconBox = new Border
            {
                Width = 44, Height = 44, CornerRadius = new CornerRadius(13), Child = image,
                Background = (Brush)Application.Current.Resources["BrushFillSoft"]
            };
        }
        head.Children.Add(iconBox);

        var titles = new StackPanel { Spacing = 3, VerticalAlignment = VerticalAlignment.Center };
        var nameLine = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
        nameLine.Children.Add(new TextBlock
        {
            Text = project.Title, FontSize = 14.5, FontWeight = FontWeights.SemiBold,
            TextTrimming = TextTrimming.CharacterEllipsis, MaxWidth = 160
        });
        var zhBadge = new TextBlock
        {
            Text = string.IsNullOrWhiteSpace(project.ZhTitle) ? "" : "（" + project.ZhTitle + "）",
            FontSize = 11, Foreground = (Brush)Application.Current.Resources["BrushTextTertiary"],
            VerticalAlignment = VerticalAlignment.Center,
            Visibility = string.IsNullOrWhiteSpace(project.ZhTitle) ? Visibility.Collapsed : Visibility.Visible
        };
        nameLine.Children.Add(zhBadge);
        titles.Children.Add(nameLine);
        titles.Children.Add(new TextBlock
        {
            Text = "by " + (string.IsNullOrWhiteSpace(project.Author) ? "未知作者" : project.Author),
            FontSize = 10.5, Foreground = (Brush)Application.Current.Resources["BrushTextTertiary"]
        });
        Grid.SetColumn(titles, 1);
        head.Children.Add(titles);
        root.Children.Add(head);

        var desc = new TextBlock
        {
            Text = string.IsNullOrWhiteSpace(project.Description) ? "暂无简介" : project.Description,
            FontSize = 11.5, LineHeight = 18, Margin = new Thickness(0, 11, 0, 0),
            Foreground = (Brush)Application.Current.Resources["BrushTextSecondary"],
            TextWrapping = TextWrapping.Wrap, MaxLines = 3, TextTrimming = TextTrimming.CharacterEllipsis,
            VerticalAlignment = VerticalAlignment.Top
        };
        Grid.SetRow(desc, 1);
        root.Children.Add(desc);
        _ = LocalizeAsync(project, zhBadge, desc);

        var foot = new Grid { Margin = new Thickness(0, 10, 0, 0) };
        foot.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        foot.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        foot.Children.Add(new TextBlock
        {
            Text = FormatCount(project.Downloads) + " 下载 · " + FormatCount(project.Follows) + " 关注",
            FontSize = 10, Foreground = (Brush)Application.Current.Resources["BrushTextTertiary"],
            VerticalAlignment = VerticalAlignment.Center
        });

        var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
        var brief = new Button
        {
            Content = "简介", Height = 30, Padding = new Thickness(13, 0, 13, 0),
            CornerRadius = new CornerRadius(9), FontSize = 11,
            Background = (Brush)Application.Current.Resources["BrushFillSubtle"],
            Foreground = (Brush)Application.Current.Resources["BrushTextSecondary"],
            BorderThickness = new Thickness(0)
        };
        brief.Click += async (_, _) => await ShowDetailAsync(project);
        actions.Children.Add(brief);

        var install = new Button
        {
            Content = "安装", Height = 30, Padding = new Thickness(15, 0, 15, 0),
            CornerRadius = new CornerRadius(9), FontSize = 11.5,
            // 主按钮观感：accent 渐变底 + BrushOnAccent 深色字，浅色模式下同样可读。
            Background = Res("BrushAccent"),
            Foreground = Res("BrushOnAccent"),
            BorderThickness = new Thickness(0)
        };
        Motion.Hover(install, 1.03f, 1.2f);
        install.Click += async (_, _) => await ShowDetailAsync(project, true);
        actions.Children.Add(install);
        Grid.SetColumn(actions, 1);
        foot.Children.Add(actions);
        Grid.SetRow(foot, 2);
        root.Children.Add(foot);

        card.Child = GlassSurface(root);
        return card;
    }

    /// <summary>卡片统一外壳：直接返回内容容器（用户要求去掉顶部高光横线）。</summary>
    private static FrameworkElement GlassSurface(FrameworkElement content) => content;

    private static async Task LocalizeAsync(ModrinthProject project, TextBlock badge, TextBlock description)
    {
        try
        {
            var zhTitle = project.ZhTitle;
            if (string.IsNullOrWhiteSpace(zhTitle))
                zhTitle = await LumenRuntime.Modrinth.TranslateToChineseAsync(project.Title);
            if (!string.IsNullOrWhiteSpace(zhTitle) && !string.Equals(zhTitle, project.Title, StringComparison.OrdinalIgnoreCase))
            {
                badge.Text = "（" + zhTitle + "）";
                badge.Visibility = Visibility.Visible;
            }
        }
        catch { }
        try
        {
            if (!string.IsNullOrWhiteSpace(project.Description) && !ModrinthService.HasChinese(project.Description))
            {
                var zh = await LumenRuntime.Modrinth.TranslateToChineseAsync(project.Description);
                if (!string.IsNullOrWhiteSpace(zh) && !string.Equals(zh, project.Description, StringComparison.OrdinalIgnoreCase))
                    description.Text = zh;
            }
        }
        catch { }
    }

    private static string FormatCount(long value) => value switch
    {
        >= 100_000_000 => (value / 100_000_000.0).ToString("0.#") + "亿",
        >= 10_000 => (value / 10_000.0).ToString("0.#") + "万",
        _ => value.ToString("N0")
    };

    private async Task ShowDetailAsync(ModrinthProject project, bool install = false)
    {
        try
        {
            var detail = await LumenRuntime.Modrinth.ProjectAsync(project.Slug);
            if (string.IsNullOrWhiteSpace(detail.Author)) detail.Author = project.Author;
            if (string.IsNullOrWhiteSpace(detail.IconUrl)) detail.IconUrl = project.IconUrl;
            if (string.IsNullOrWhiteSpace(detail.Title)) detail.Title = project.Title;
            if (string.IsNullOrWhiteSpace(detail.Description)) detail.Description = project.Description;
            var instance = SelectedInstance();
            await ProjectDetailDialog.ShowAsync(XamlRoot, detail, instance);
            _ = install;
        }
        catch (Exception ex)
        {
            await MessageDialog.ShowAsync(XamlRoot, "读取项目失败", ex.Message);
        }
    }

    private InstanceSummary? SelectedInstance()
    {
        var id = (InstanceCombo.SelectedItem as ComboBoxItem)?.Tag?.ToString();
        return _instances.FirstOrDefault(x => x.Id == id)
            ?? _instances.FirstOrDefault(x => x.Id == LumenRuntime.Settings.Settings.SelectedInstanceId)
            ?? _instances.FirstOrDefault();
    }

    private async void ImportModpack_Click(object sender, RoutedEventArgs e)
    {
        var picker = new FileOpenPicker();
        picker.FileTypeFilter.Add(".mrpack");
        picker.FileTypeFilter.Add(".zip");
        WinRT.Interop.InitializeWithWindow.Initialize(picker,
            WinRT.Interop.WindowNative.GetWindowHandle(MainWindow.Current));
        var file = await picker.PickSingleFileAsync();
        if (file is null) return;

        var modal = GlassModal.Show("导入整合包", "正在解析 " + file.Name, 520);
        modal.HideButtons();
        modal.SetCloseAllowed(false);
        var bar = new ProgressBar { Minimum = 0, Maximum = 100, Height = 6, Foreground = (Brush)Application.Current.Resources["BrushMint"] };
        var line = new TextBlock
        {
            Text = "准备安装…", FontSize = 12.5, TextWrapping = TextWrapping.Wrap,
            Foreground = (Brush)Application.Current.Resources["BrushTextSecondary"]
        };
        modal.Body.Children.Add(bar);
        modal.Body.Children.Add(line);
        try
        {
            var progress = new Progress<DownloadProgress>(p =>
            {
                bar.Value = Math.Clamp(p.Percent, 0, 100);
                line.Text = p.Stage + (string.IsNullOrWhiteSpace(p.Message) ? "" : " · " + p.Message) +
                            " · " + p.Percent.ToString("0") + "%" +
                            (p.BytesPerSecond > 0 ? " · " + (p.BytesPerSecond / 1048576.0).ToString("0.0") + " MB/s" : "");
            });
            var instance = await Task.Run(() => LumenRuntime.Modpacks.InstallAsync(file.Path, progress));
            _instances = (await LumenRuntime.Instances.ScanAsync()).ToList();
            InstanceCombo.Items.Clear();
            foreach (var item in _instances)
                InstanceCombo.Items.Add(new ComboBoxItem { Content = item.Name + " · " + item.Kernel, Tag = item.Id });
            if (_instances.Count > 0) InstanceCombo.SelectedIndex = 0;
            modal.Close();
            await MessageDialog.ShowAsync(XamlRoot, "导入完成", instance.Name + " 已创建，包含 " + instance.Mods + " 个模组。");
            await ReloadAsync();
        }
        catch (Exception ex)
        {
            AppLog.Warn("discover", "导入整合包失败：" + ex);
            modal.Close();
            await MessageDialog.ShowAsync(XamlRoot, "导入失败", string.IsNullOrWhiteSpace(ex.Message) ? ex.GetType().Name : ex.Message);
        }
    }

    private void OpenLibrary_Click(object sender, RoutedEventArgs e) => ShellPage.Current?.Navigate("library");

    /// <summary>按主题 key 取实时画刷（浅色/深色切换后自动跟随，绝不缓存冻结颜色）。</summary>
    private static Brush Res(string key) => ThemeManager.Brush(key, ThemeManager.Text);

    /// <summary>分段控件切换内容区时的淡入时长：统一落在 180–260ms 区间。</summary>
    private const int SegmentFadeMs = 220;

    /// <summary>淡入一个刚显示出来的内容区，避免分段切换时硬闪。</summary>
    private static void FadeIn(FrameworkElement element, int durationMs = SegmentFadeMs)
    {
        if (element is null) return;
        element.Opacity = 0;
        var story = new Storyboard();
        var fade = new DoubleAnimation
        {
            From = 0,
            To = 1,
            Duration = new Duration(TimeSpan.FromMilliseconds(durationMs)),
            EnableDependentAnimation = true,
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
        };
        Storyboard.SetTarget(fade, element);
        Storyboard.SetTargetProperty(fade, "Opacity");
        story.Children.Add(fade);
        story.Begin();
    }
}