using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using NovaLink.Lumen.Controls;
using NovaLink.Lumen.Core.Models;
using NovaLink.Lumen.Services;
using NovaLink.Lumen.Views.Dialogs;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Windows.UI;

namespace NovaLink.Lumen.Views.Pages;

public sealed partial class DiscoverPage
{
    private static readonly HashSet<string> AprilFools = new(StringComparer.OrdinalIgnoreCase)
    {
        "15w14a", "1.RV-Pre1", "3D Shareware v1.34", "20w14infinite",
        "22w13oneblockatatime", "23w13a_or_b", "24w14potato", "25w14craftmine"
    };

    private static readonly (string Key, string Label)[] VanillaCategories =
    {
        ("", "全部"), ("release", "正式版"), ("snapshot", "快照版"),
        ("old", "远古版"), ("fool", "愚人节版")
    };

    private IReadOnlyList<ManifestVersion> _vanillaManifest = [];
    private bool _vanillaInstalling;

    public void ShowVanilla()
    {
        _type = "vanilla";
        _category = "";
        _loader = "";
        if (!_ready) return;
        BuildCategories();
        UpdateTypeButtons();
        _ = ReloadAsync();
    }

    private void BuildVanillaCategories()
    {
        CategoryRow.Children.Clear();
        foreach (var (key, label) in VanillaCategories)
        {
            var button = new Button
            {
                Content = label,
                Tag = key,
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

    private async Task ReloadVanillaAsync()
    {
        _searchCts?.Cancel();
        _searchCts = null;
        var query = SearchBox.Text.Trim();
        _query = query;
        _category = string.IsNullOrWhiteSpace(_category) ? "" : _category;
        ShowSkeleton(query.Length > 0 ? "正在查找版本「" + query + "」…" : "正在读取 Mojang 版本清单…");
        EmptyPanel.Visibility = Visibility.Collapsed;
        LoadMoreButton.Visibility = Visibility.Collapsed;
        CountText.Text = "读取中";
        StatusText.Text = "";

        try
        {
            // 清单请求和 JSON 解析放到后台线程，UI 线程只负责搭版本行。
            var manifest = await Task.Run(() => LumenRuntime.Manifest.GetAsync());
            _vanillaManifest = manifest;
            await HoldSkeletonAsync();
            HideBusy();
            await RenderVanillaAsync();
        }
        catch (Exception ex)
        {
            ResultsPanel.Children.Clear();
            StatusText.Text = "版本清单读取失败：" + ex.Message;
            EmptyText.Text = "无法读取 Mojang 版本清单";
            EmptyPanel.Visibility = Visibility.Visible;
            AppLog.Warn("discover", "原版版本清单失败：" + ex.Message);
        }
        finally
        {
            HideBusy();
        }
    }

    /// <summary>
    /// 渲染原版版本列表。目录探测与分组放到后台线程算，版本行分批搭建（每批 6 行让出一帧），
    /// 每次渲染自增 <see cref="_renderToken"/>，过期的一轮在下一个检查点直接退出。
    /// </summary>
    private async Task RenderVanillaAsync()
    {
        var token = ++_renderToken;
        ResultsPanel.Children.Clear();
        HideBusy();
        var query = _query.Trim();
        var root = LumenRuntime.Settings.Settings.GameRoot;

        // 逐版本探测 versions 目录是同步 IO，放后台线程做完再回 UI 线程。
        var (installed, latestRelease, items) = await Task.Run(() =>
        {
            var installedSet = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var item in _vanillaManifest)
            {
                try
                {
                    if (Directory.Exists(Path.Combine(root, "versions", item.Id))) installedSet.Add(item.Id);
                }
                catch { }
            }

            var latest = _vanillaManifest
                .Where(v => v.Type == "release")
                .OrderByDescending(v => v.ReleaseTime)
                .Select(v => v.Id)
                .FirstOrDefault();

            var filtered = _vanillaManifest
                .Where(v => query.Length == 0 || v.Id.Contains(query, StringComparison.OrdinalIgnoreCase))
                .ToList();

            return (installedSet, latest, filtered);
        });
        if (token != _renderToken) return;

        var groups = items
            .GroupBy(VanillaGroupKey)
            .OrderBy(g => VanillaGroupRank(g.Key))
            .ToList();

        if (!string.IsNullOrWhiteSpace(_category))
            groups = groups.Where(g => string.Equals(g.Key, _category, StringComparison.OrdinalIgnoreCase)).ToList();

        var total = items.Count;
        _total = total;
        CountText.Text = total == 0 ? "没有匹配版本" : total.ToString("N0") + " 个版本 · 原版游戏";
        DisplayText.Text = total == 0 ? "" : "共 " + total.ToString("N0") + " 个 Mojang 版本 · 按类型折叠";
        SourceText.Text = "MOJANG · 版本清单";
        FooterHint.Text = "版本由 Mojang 官方清单提供 · 安装前会按设置决定是否版本隔离";
        StatusText.Text = "目标目录：" + ShortPath(root);

        if (total == 0)
        {
            EmptyText.Text = query.Length > 0 ? "没有找到版本“" + query + "”" : "这个分类暂时没有版本";
            EmptyPanel.Visibility = Visibility.Visible;
            return;
        }

        var body = new StackPanel { Spacing = 8 };
        var wrapper = new Border
        {
            Style = (Style)Application.Current.Resources["GlassCard"],
            Padding = new Thickness(18, 16, 18, 16)
        };
        wrapper.Child = GlassSurface(body);

        var first = true;
        foreach (var group in groups)
        {
            if (token != _renderToken) return;
            var versions = group
                .OrderByDescending(v => v.ReleaseTime)
                .ToList();
            var expander = new Expander
            {
                Header = VanillaGroupHeader(VanillaGroupLabel(group.Key), versions.Count, group.Key),
                IsExpanded = first || query.Length > 0,
                HorizontalAlignment = HorizontalAlignment.Stretch,
                HorizontalContentAlignment = HorizontalAlignment.Stretch,
                Margin = new Thickness(0, 0, 0, 4)
            };
            first = false;

            var rows = new StackPanel { Spacing = 4, Margin = new Thickness(2, 4, 2, 6) };
            var initial = Math.Min(12, versions.Count);
            for (var i = 0; i < initial; i++)
            {
                rows.Children.Add(BuildVanillaRow(versions[i], installed.Contains(versions[i].Id), versions[i].Id == latestRelease));
                if ((i + 1) % 6 == 0)
                {
                    await UiYield.NextFrameAsync();
                    if (token != _renderToken) return;
                }
            }

            if (versions.Count > initial)
            {
                var more = new Button
                {
                    Content = "展开其余 " + (versions.Count - initial) + " 个版本",
                    Style = (Style)Application.Current.Resources["GhostButton"],
                    Height = 32,
                    Padding = new Thickness(18, 0, 18, 0),
                    HorizontalAlignment = HorizontalAlignment.Left,
                    Margin = new Thickness(0, 6, 0, 0),
                    Tag = versions.Skip(initial).ToList()
                };
                more.Click += (_, _) =>
                {
                    var index = 0;
                    foreach (var version in (IReadOnlyList<ManifestVersion>)more.Tag)
                    {
                        var added = BuildVanillaRow(version, installed.Contains(version.Id), version.Id == latestRelease);
                        rows.Children.Insert(rows.Children.Count - 1, added);
                        Motion.RowIn(added, index++);
                    }
                    more.Visibility = Visibility.Collapsed;
                };
                rows.Children.Add(more);
            }

            expander.Content = rows;
            body.Children.Add(expander);
            // 挂进视觉树之后再让每行入场，避免动画落在未上屏的元素上。
            var stagger = 0;
            foreach (var child in rows.Children)
                if (child is FrameworkElement rowElement) Motion.RowIn(rowElement, stagger++);
            await UiYield.NextFrameAsync();
        }

        if (token != _renderToken) return;
        ResultsPanel.Children.Add(wrapper);
        EmptyPanel.Visibility = Visibility.Collapsed;
    }

    private Border BuildVanillaRow(ManifestVersion version, bool installed, bool latest)
    {
        var row = new Border
        {
            // 走 GlassRow 样式：行自带液态玻璃底（BrushGlassFaint + BrushBorderSubtle 1px + 圆角 14），
            // 会被背景层的透镜收集器接住。之前这里手写 fill/border 不是玻璃样式，所以整列版本行没有玻璃。
            Style = (Style)Application.Current.Resources["GlassRow"],
            Padding = new Thickness(12, 10, 12, 10),
            Tag = "no-motion"
        };

        var grid = new Grid { ColumnSpacing = 12 };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var icon = new Border
        {
            Width = 38,
            Height = 38,
            CornerRadius = new CornerRadius(12),
            Background = new LinearGradientBrush
            {
                StartPoint = new Windows.Foundation.Point(0, 0),
                EndPoint = new Windows.Foundation.Point(1, 1),
                GradientStops =
                {
                    // accent 由主题决定（浅色下是更深的青绿），半透明叠出玻璃反光。
                    new GradientStop { Color = WithAlpha(ThemeManager.Accent, 0x55), Offset = 0 },
                    new GradientStop { Color = WithAlpha(ThemeManager.IsLight ? ThemeManager.Accent : Color.FromArgb(0xFF, 0xA9, 0x96, 0xFF), 0x33), Offset = 1 }
                }
            },
            Child = new FontIcon { Glyph = "\uE7FC", FontSize = 16, Foreground = (Brush)Application.Current.Resources["BrushMint"] }
        };
        grid.Children.Add(icon);

        var text = new StackPanel { Spacing = 3, VerticalAlignment = VerticalAlignment.Center };
        var title = new TextBlock
        {
            Text = version.Id,
            FontSize = 15.5,
            FontWeight = FontWeights.SemiBold,
            Foreground = (Brush)Application.Current.Resources["BrushText"],
            TextTrimming = TextTrimming.CharacterEllipsis
        };
        var sub = new TextBlock
        {
            Text = version.Type switch
            {
                "release" => "正式版 · " + version.ReleaseTime.ToLocalTime().ToString("yyyy-MM-dd"),
                "snapshot" => "快照版 · " + version.ReleaseTime.ToLocalTime().ToString("yyyy-MM-dd"),
                "old_beta" => "远古 Beta · " + version.ReleaseTime.ToLocalTime().ToString("yyyy-MM-dd"),
                "old_alpha" => "远古 Alpha · " + version.ReleaseTime.ToLocalTime().ToString("yyyy-MM-dd"),
                _ => version.ReleaseTime.ToLocalTime().ToString("yyyy-MM-dd")
            },
            FontSize = 10.5,
            Foreground = (Brush)Application.Current.Resources["BrushTextTertiary"]
        };
        text.Children.Add(title);
        text.Children.Add(sub);
        Grid.SetColumn(text, 1);
        grid.Children.Add(text);

        var badges = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 6,
            VerticalAlignment = VerticalAlignment.Center
        };
        if (latest) badges.Children.Add(VanillaPill("最新正式版", ThemeManager.Accent));
        if (installed) badges.Children.Add(VanillaPill("已安装", InstalledTint));
        Grid.SetColumn(badges, 2);
        grid.Children.Add(badges);

        var install = new Button
        {
            Content = installed ? "重新安装" : "安装",
            Style = (Style)Application.Current.Resources["GhostButton"],
            Height = 32,
            Padding = new Thickness(18, 0, 18, 0),
            VerticalAlignment = VerticalAlignment.Center,
            Tag = version.Id,
            IsEnabled = !_vanillaInstalling
        };
        AutomationProperties.SetName(install, "安装原版 " + version.Id);
        install.Click += InstallVanilla_Click;
        Grid.SetColumn(install, 3);
        grid.Children.Add(install);

        row.Child = grid;
        return row;
    }

    private static Border VanillaPill(string text, Color color) => new()
    {
        Background = new SolidColorBrush(Color.FromArgb(0x22, color.R, color.G, color.B)),
        BorderBrush = new SolidColorBrush(Color.FromArgb(0x40, color.R, color.G, color.B)),
        BorderThickness = new Thickness(1),
        CornerRadius = new CornerRadius(7),
        Padding = new Thickness(8, 2, 8, 2),
        VerticalAlignment = VerticalAlignment.Center,
        Child = new TextBlock { Text = text, FontSize = 9, Foreground = new SolidColorBrush(color) }
    };

    private static FrameworkElement VanillaGroupHeader(string title, int count, string key)
    {
        var grid = new Grid { ColumnSpacing = 10 };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var stack = new StackPanel { Spacing = 2 };
        stack.Children.Add(new TextBlock
        {
            Text = title,
            FontSize = 13.5,
            FontWeight = FontWeights.SemiBold,
            Foreground = (Brush)Application.Current.Resources["BrushText"]
        });
        stack.Children.Add(new TextBlock
        {
            Text = key switch
            {
                "release" => "Mojang 正式发布版本",
                "snapshot" => "开发中的预览快照",
                "old" => "Beta 与 Alpha 时代的旧版本",
                "fool" => "愚人节特别版本",
                _ => "全部原版版本"
            },
            FontSize = 9.5,
            Foreground = (Brush)Application.Current.Resources["BrushTextTertiary"]
        });
        grid.Children.Add(stack);
        var countPill = VanillaPill(count + " 个版本", ThemeManager.TextSecondary);
        Grid.SetColumn(countPill, 1);
        grid.Children.Add(countPill);
        return grid;
    }

    private async void InstallVanilla_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button button || _vanillaInstalling) return;
        var id = button.Tag?.ToString();
        var version = _vanillaManifest.FirstOrDefault(v => string.Equals(v.Id, id, StringComparison.OrdinalIgnoreCase));
        if (version is null) return;

        _vanillaInstalling = true;
        try
        {
            var installed = await VanillaInstallSheet.ShowAsync(XamlRoot, version);
            if (installed)
            {
                _instances = (await LumenRuntime.Instances.ScanAsync()).ToList();
                await RenderVanillaAsync();
            }
        }
        catch (Exception ex)
        {
            AppLog.Error("install", "打开版本安装设置失败", ex);
            try
            {
                await MessageDialog.ShowAsync(XamlRoot, "无法打开安装设置",
                    string.IsNullOrWhiteSpace(ex.Message) ? ex.GetType().Name : ex.Message);
            }
            catch (Exception dialogEx)
            {
                AppLog.Error("install", "无法显示安装设置错误提示", dialogEx);
            }
        }
        finally
        {
            _vanillaInstalling = false;
            await RenderVanillaAsync();
        }
    }

    private static string VanillaGroupKey(ManifestVersion version)
    {
        if (AprilFools.Contains(version.Id)) return "fool";
        return version.Type switch
        {
            "release" => "release",
            "snapshot" => "snapshot",
            "old_beta" or "old_alpha" => "old",
            _ => "snapshot"
        };
    }

    private static int VanillaGroupRank(string key) => key switch
    {
        "release" => 0,
        "snapshot" => 1,
        "old" => 2,
        "fool" => 3,
        _ => 4
    };

    private static string VanillaGroupLabel(string key) => key switch
    {
        "release" => "正式版",
        "snapshot" => "快照版",
        "old" => "远古版",
        "fool" => "愚人节版",
        _ => "其他版本"
    };

    /// <summary>"已安装"标签的颜色：浅色下用更深的绿、深色下用浅绿，保证 9px 文字两种主题都可读。</summary>
    private static Color InstalledTint => ThemeManager.IsLight
        ? Color.FromArgb(0xFF, 0x1F, 0x8A, 0x5F)
        : Color.FromArgb(0xFF, 0x9B, 0xE8, 0xB8);

    /// <summary>给主题色叠一层 alpha，做半透明玻璃底/描边。</summary>
    private static Color WithAlpha(Color color, byte alpha) => Color.FromArgb(alpha, color.R, color.G, color.B);

    private static string ShortPath(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return "";
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return !string.IsNullOrWhiteSpace(home) && path.StartsWith(home, StringComparison.OrdinalIgnoreCase)
            ? "~" + path[home.Length..]
            : path;
    }
}
