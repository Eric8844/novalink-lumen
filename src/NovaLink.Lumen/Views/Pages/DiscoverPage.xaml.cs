using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
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

    public DiscoverPage()
    {
        InitializeComponent();
        Loaded += async (_, _) => await InitAsync();
        Loaded += (_, _) => StartAmbient();
    }

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
            button.Background = active
                ? new SolidColorBrush(Color.FromArgb(0x2A, 0x7C, 0xEB, 0xD6))
                : new SolidColorBrush(Color.FromArgb(0x12, 0xFF, 0xFF, 0xFF));
            button.Foreground = active
                ? (Brush)Application.Current.Resources["BrushMint"]
                : (Brush)Application.Current.Resources["BrushTextSecondary"];
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

    private void UpdateTypeButtons()
    {
        foreach (var button in new[] { TypeVanilla, TypeMod, TypeResource, TypeShader, TypeModpack })
        {
            var active = string.Equals(button.Tag?.ToString(), _type, StringComparison.OrdinalIgnoreCase);
            button.Background = active
                ? new SolidColorBrush(Color.FromArgb(0x2A, 0x7C, 0xEB, 0xD6))
                : new SolidColorBrush(Color.FromArgb(0x12, 0xFF, 0xFF, 0xFF));
            button.Foreground = active
                ? (Brush)Application.Current.Resources["BrushMint"]
                : (Brush)Application.Current.Resources["BrushTextSecondary"];
            button.FontWeight = active ? FontWeights.SemiBold : FontWeights.Normal;
        }

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

        ShowSkeleton(query.Length > 0 ? "正在搜索 " + query + " …" : "正在读取内容…");
        EmptyPanel.Visibility = Visibility.Collapsed;
        LoadMoreButton.Visibility = Visibility.Collapsed;
        CountText.Text = "读取中";

        try
        {
            var result = await LumenRuntime.Modrinth.SearchAsync(
                query, _type, _gameVersion, _loader, 0, PageSize,
                query.Length == 0 ? "downloads" : "relevance", _category, cts.Token);
            if (cts.IsCancellationRequested) return;
            _total = result.TotalHits;
            _items.AddRange(result.Hits);
            CountText.Text = _total.ToString("N0") + " 个结果 · " + TypeName(_type);
            StatusText.Text = "";
            await HoldSkeletonAsync();
            if (cts.IsCancellationRequested) return;
            RenderItems(_items);
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
            }
        }
    }

    private async void LoadMore_Click(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        _loading = true;
        LoadMoreButton.IsEnabled = false;
        try
        {
            var result = await LumenRuntime.Modrinth.SearchAsync(
                _query, _type, _gameVersion, _loader, _items.Count, PageSize,
                _query.Length == 0 ? "downloads" : "relevance", _category);
            _total = result.TotalHits;
            foreach (var item in result.Hits)
            {
                var key = string.IsNullOrWhiteSpace(item.ProjectId) ? item.Slug : item.ProjectId;
                if (_items.Any(x => string.Equals(string.IsNullOrWhiteSpace(x.ProjectId) ? x.Slug : x.ProjectId, key, StringComparison.OrdinalIgnoreCase))) continue;
                _items.Add(item);
            }
            RenderItems(_items);
            UpdateFooter();
        }
        catch (Exception ex)
        {
            StatusText.Text = "加载更多失败：" + ex.Message;
        }
        finally
        {
            _loading = false;
            LoadMoreButton.IsEnabled = true;
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

    private void ShowSkeleton(string status)
    {
        _skeletonAt = Stopwatch.GetTimestamp();
        ResultsPanel.Children.Clear();
        StatusText.Text = status;
        for (var rowIndex = 0; rowIndex < 3; rowIndex++)
        {
            var row = new Grid { ColumnSpacing = 14, Height = 228 };
            for (var c = 0; c < 3; c++)
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            for (var c = 0; c < 3; c++)
            {
                var card = Skeleton.Card();
                Grid.SetColumn(card, c);
                row.Children.Add(card);
            }
            ResultsPanel.Children.Add(row);
        }
    }

    private async Task HoldSkeletonAsync()
    {
        if (_skeletonAt == 0) return;
        var elapsed = Stopwatch.GetElapsedTime(_skeletonAt);
        _skeletonAt = 0;
        var remain = TimeSpan.FromMilliseconds(480) - elapsed;
        if (remain > TimeSpan.Zero) await Task.Delay(remain);
    }

    private void RenderItems(IReadOnlyList<ModrinthProject> items)
    {
        ResultsPanel.Children.Clear();
        for (var i = 0; i < items.Count; i += 3)
        {
            var row = new Grid { ColumnSpacing = 14, Height = 228 };
            for (var c = 0; c < 3; c++)
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            for (var c = 0; c < 3 && i + c < items.Count; c++)
            {
                var card = BuildCard(items[i + c]);
                Grid.SetColumn(card, c);
                row.Children.Add(card);
            }
            ResultsPanel.Children.Add(row);
        }
    }

    private Border BuildCard(ModrinthProject project)
    {
        var card = new Border
        {
            Style = (Style)Application.Current.Resources["GlassCard"],
            Padding = new Thickness(18, 16, 18, 14),
            Background = (Brush)Application.Current.Resources["BrushCardTint"],
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
            Background = new SolidColorBrush(Color.FromArgb(0x26, 0x7C, 0xEB, 0xD6)),
            Foreground = (Brush)Application.Current.Resources["BrushMint"],
            BorderThickness = new Thickness(0)
        };
        install.Click += async (_, _) => await ShowDetailAsync(project, true);
        actions.Children.Add(install);
        Grid.SetColumn(actions, 1);
        foot.Children.Add(actions);
        Grid.SetRow(foot, 2);
        root.Children.Add(foot);

        card.Child = root;
        return card;
    }

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
}