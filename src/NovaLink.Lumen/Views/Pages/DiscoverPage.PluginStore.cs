using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using NovaLink.Lumen.Controls;
using NovaLink.Lumen.Core.Models;
using NovaLink.Lumen.Plugins;
using NovaLink.Lumen.Services;
using NovaLink.Lumen.Services.Plugins;
using NovaLink.Lumen.Services.Plugins.Store;
using NovaLink.Lumen.Startup;
using NovaLink.Lumen.Views.Dialogs;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Windows.UI;

namespace NovaLink.Lumen.Views.Pages;

/// <summary>
/// 发现页的「启动器插件」分栏：从 GitHub 上扫描带 topic 的插件仓库，展示、下载并安装。
/// 与 Modrinth 内容栏互斥显示；数据全部来自 <see cref="PluginStoreClient"/>（Services 层），这里只做 UI。
/// </summary>
public sealed partial class DiscoverPage
{
    private const string StoreLogArea = "plugin-store";
    private const int StorePageSize = 18;

    /// <summary>空商店的默认说明（GitHub 上还没有仓库打 topic 时就是正常结果，不是错误）。</summary>
    private const string StoreEmptyHint =
        "商店里还没有上架的插件，过段时间再来看看吧。";

    private bool _storeMode;
    private bool _storeLoaded;
    private bool _storeLoading;
    private bool _storeWired;
    private int _storePage = 1;
    private PluginStoreSort _storeSort = PluginStoreSort.Stars;
    private string? _storeQuery;
    private readonly List<PluginStoreEntry> _storeItems = new();
    private readonly HashSet<string> _storeInstalling = new(StringComparer.OrdinalIgnoreCase);
    private CancellationTokenSource? _storeCts;
    private bool _storeHasMore;
    private long _storeRenderToken;

    /// <summary>首次进入插件栏时补齐初始状态（分段控件选中态、排序下拉默认项、页面卸载时取消请求）。只做一次。</summary>
    private void EnsureStoreWiring()
    {
        if (_storeWired) return;
        _storeWired = true;

        Unloaded += (_, _) => _storeCts?.Cancel();

        ApplySegment(SegContent, !_storeMode);
        ApplySegment(SegPlugins, _storeMode);
        if (StoreSortCombo.SelectedIndex < 0) StoreSortCombo.SelectedIndex = 0;
    }

    /// <summary>分段按钮的选中态：选中时用导航选中的渐变与薄荷色，取消时清掉本地值回落到 GhostButton。</summary>
    private static void ApplySegment(Button button, bool active)
    {
        if (active)
        {
            button.Background = (Brush)Application.Current.Resources["BrushNavSelected"];
            button.Foreground = (Brush)Application.Current.Resources["BrushMint"];
        }
        else
        {
            button.ClearValue(Control.BackgroundProperty);
            button.ClearValue(Control.ForegroundProperty);
        }
    }

    private void SwitchStoreMode(bool store)
    {
        _storeMode = store;
        ModrinthPanel.Visibility = store ? Visibility.Collapsed : Visibility.Visible;
        PluginStorePanel.Visibility = store ? Visibility.Visible : Visibility.Collapsed;
        ApplySegment(SegContent, !store);
        ApplySegment(SegPlugins, store);
        // 分段切换给内容区一个 220ms 淡入，避免整块内容硬切造成的闪。
        FadeIn(store ? PluginStorePanel : ModrinthPanel);
    }

    private async void SegContent_Click(object sender, RoutedEventArgs e)
    {
        EnsureStoreWiring();
        SwitchStoreMode(false);
        await Task.CompletedTask;
    }

    private async void SegPlugins_Click(object sender, RoutedEventArgs e)
    {
        if (!LumenRuntime.PluginsEnabled)
        {
            // 实验性功能关闭：分段按钮本应已隐藏，这里再兜一层，保证不切栏也不请求商店。
            SwitchStoreMode(false);
            await Task.CompletedTask;
            return;
        }

        EnsureStoreWiring();
        SwitchStoreMode(true);
        if (_storeLoaded) return;

        // 首次切过来才真正去 GitHub 扫描；失败后停在空状态，用户可点「重试」。
        _storeLoaded = true;
        await LoadStoreAsync(reset: true);
    }

    private async void StoreSearch_Click(object sender, RoutedEventArgs e)
    {
        _storeQuery = StoreSearchBox.Text?.Trim();
        await LoadStoreAsync(reset: true);
    }

    private async void StoreSearchBox_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key != Windows.System.VirtualKey.Enter) return;
        e.Handled = true;
        _storeQuery = StoreSearchBox.Text?.Trim();
        await LoadStoreAsync(reset: true);
    }

    private async void StoreRefresh_Click(object sender, RoutedEventArgs e)
    {
        _storeQuery = StoreSearchBox.Text?.Trim();
        await LoadStoreAsync(reset: true, forceRefresh: true);
    }

    private async void StoreRetry_Click(object sender, RoutedEventArgs e) => await LoadStoreAsync(reset: true);

    private async void StoreSort_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (!_storeWired) return;
        var sort = (StoreSortCombo.SelectedItem as ComboBoxItem)?.Tag?.ToString() switch
        {
            "updated" => PluginStoreSort.Updated,
            "name" => PluginStoreSort.Name,
            _ => PluginStoreSort.Stars
        };
        if (sort == _storeSort) return;
        _storeSort = sort;
        if (_storeMode) await LoadStoreAsync(reset: true);
    }

    private async void StoreLoadMore_Click(object sender, RoutedEventArgs e)
    {
        if (_storeLoading || !_storeHasMore) return;
        _storePage++;
        await LoadStoreAsync(reset: false);
    }

    /// <summary>加载商店列表。reset=true 表示重新从第 1 页开始（清空已有卡片）。</summary>
    private async Task LoadStoreAsync(bool reset, bool forceRefresh = false)
    {
        // 实验性功能关闭时插件商店不存在（Store 为 null），任何入口都不该发请求。
        if (!LumenRuntime.PluginsEnabled) return;
        if (_storeLoading && !reset) return;

        var cts = new CancellationTokenSource();
        var previous = _storeCts;
        _storeCts = cts;
        previous?.Cancel();

        _storeLoading = true;
        if (reset)
        {
            _storePage = 1;
            StoreItemsPanel.Children.Clear();
            _storeItems.Clear();
            _storeHasMore = false;
            StoreLoadMoreButton.Visibility = Visibility.Collapsed;
            StoreEmptyPanel.Visibility = Visibility.Collapsed;
            StoreLoadingPanel.Visibility = Visibility.Visible;
            StoreStatusText.Visibility = Visibility.Visible;
            StoreStatusText.Text = "正在从 GitHub 读取插件商店…";
            ShowBusy("正在读取插件商店…");
        }

        try
        {
            var store = LumenRuntime.Store;
            if (store is null)
            {
                ShowStoreEmpty("商店还没准备好，请重启启动器后再试。");
                SetStoreStatus("插件商店还没有初始化。", visible: true);
                return;
            }

            // 网络请求与 GitHub 返回的 JSON 解析都放在后台线程，UI 线程只负责加卡片。
            var page = await Task.Run(() => store.SearchAsync(new PluginStoreQuery
            {
                Search = string.IsNullOrWhiteSpace(_storeQuery) ? null : _storeQuery,
                Sort = _storeSort,
                Page = _storePage,
                PageSize = StorePageSize,
                ForceRefresh = forceRefresh
            }, cts.Token), cts.Token);
            if (cts.IsCancellationRequested) return;

            if (reset)
            {
                _storeItems.Clear();
                StoreItemsPanel.Children.Clear();
            }

            var token = ++_storeRenderToken;
            var added = 0;
            HideBusy();
            foreach (var entry in page.Items)
            {
                if (token != _storeRenderToken || cts.IsCancellationRequested) return;
                if (_storeItems.Any(x => string.Equals(x.Id, entry.Id, StringComparison.OrdinalIgnoreCase))) continue;
                _storeItems.Add(entry);
                var card = BuildStoreCard(entry);
                StoreItemsPanel.Children.Add(card);
                Motion.RowIn(card, added);
                if (++added % 6 == 0)
                {
                    await UiYield.NextFrameAsync();
                }
            }

            UpdateStoreFooter(page);
            StoreEmptyPanel.Visibility = _storeItems.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            if (_storeItems.Count == 0) StoreEmptyText.Text = EmptyStoreText();

            var status = _storeItems.Count == 0
                ? (string.IsNullOrWhiteSpace(_storeQuery) ? "商店里还没有插件。" : "没有找到匹配的插件。")
                : "已加载 " + _storeItems.Count + " 个插件" + (page.FromCache ? " · 来自缓存" : "");
            if (page.Skipped > 0) status += " · 跳过 " + page.Skipped + " 个清单不完整的仓库";
            if (!string.IsNullOrWhiteSpace(page.Notice)) status += " · " + page.Notice;
            SetStoreStatus(status, visible: true);
        }
        catch (OperationCanceledException)
        {
            // 被新的搜索/切换取代，保持安静。
        }
        catch (Exception ex)
        {
            if (cts.IsCancellationRequested) return;
            AppLog.Warn(StoreLogArea, "读取插件商店失败：" + ex);
            ShowStoreEmpty(string.IsNullOrWhiteSpace(ex.Message) ? null : "具体原因：" + ex.Message);
            SetStoreStatus("商店暂时打不开，请检查网络后点重试。", visible: true);
        }
        finally
        {
            if (ReferenceEquals(_storeCts, cts))
            {
                _storeLoading = false;
                _storeCts = null;
                StoreLoadingPanel.Visibility = Visibility.Collapsed;
                HideBusy();
            }

            cts.Dispose();
        }
    }

    private void ShowStoreEmpty(string? detail)
    {
        StoreEmptyText.Text = EmptyStoreText(detail);
        StoreEmptyPanel.Visibility = Visibility.Visible;
    }

    private string EmptyStoreText(string? detail = null)
    {
        if (!string.IsNullOrWhiteSpace(detail)) return detail!;
        return string.IsNullOrWhiteSpace(_storeQuery)
            ? StoreEmptyHint
            : "没有找到「" + _storeQuery + "」。换个关键词试试，或者点「刷新」看看最新上架的插件。";
    }

    private void SetStoreStatus(string text, bool visible)
    {
        StoreStatusText.Text = text;
        StoreStatusText.Visibility = visible && !string.IsNullOrWhiteSpace(text) ? Visibility.Visible : Visibility.Collapsed;
    }

    private void UpdateStoreFooter(PluginStorePage page)
    {
        var total = page.TotalCount;
        _storeHasMore = page.Items.Count >= StorePageSize && (total <= 0 || _storeItems.Count < total);
        StoreLoadMoreButton.Visibility = _storeHasMore ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>商店卡片：名字 + 版本/类型标签 + 仓库信息 + 两行简介 + 右侧安装/更新/仓库按钮。</summary>
    private Border BuildStoreCard(PluginStoreEntry entry)
    {
        // 浅色模式下 BrushCardTint 是 #F4FFFFFF，整张卡看起来就是一块白底平块；
        // 这里回到 GlassCard 样式自带的 BrushGlassCard 渐变 + BrushGlassRim 描边，
        // 再叠一层顶部高光，才是液态玻璃。
        var card = new Border
        {
            Style = (Style)Application.Current.Resources["GlassCard"],
            Padding = new Thickness(20, 16, 20, 16)
        };
        Motion.Hover(card, 1.006f, 2f);

        var root = new Grid { ColumnSpacing = 16 };
        root.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        root.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var content = new StackPanel { Spacing = 7 };

        var nameLine = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        nameLine.Children.Add(new TextBlock
        {
            Text = string.IsNullOrWhiteSpace(entry.Name) ? entry.Id : entry.Name,
            FontSize = 15.5, FontWeight = FontWeights.SemiBold,
            VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis, MaxWidth = 420
        });
        if (!string.IsNullOrWhiteSpace(entry.Version))
            nameLine.Children.Add(BuildStorePill("v" + entry.Version.Trim().TrimStart('v', 'V')));
        nameLine.Children.Add(BuildStorePill(KindName(entry.Kind)));
        content.Children.Add(nameLine);

        var meta = (string.IsNullOrWhiteSpace(entry.RepoOwner) ? "GitHub" : entry.RepoOwner) + "/" +
                   (string.IsNullOrWhiteSpace(entry.RepoName) ? entry.Id : entry.RepoName) +
                   " · ★ " + entry.Stars.ToString("N0");
        if (entry.Updated != default) meta += " · 更新于 " + entry.Updated.ToLocalTime().ToString("yyyy-MM-dd");
        content.Children.Add(new TextBlock
        {
            Text = meta, FontSize = 10.5,
            Foreground = (Brush)Application.Current.Resources["BrushTextTertiary"]
        });

        content.Children.Add(new TextBlock
        {
            Text = string.IsNullOrWhiteSpace(entry.Description) ? "（这个插件没有写简介）" : entry.Description,
            FontSize = 12, LineHeight = 18,
            Foreground = (Brush)Application.Current.Resources["BrushTextSecondary"],
            TextWrapping = TextWrapping.Wrap, MaxLines = 2,
            TextTrimming = TextTrimming.CharacterEllipsis
        });

        if (!string.IsNullOrWhiteSpace(entry.Incompatible))
        {
            content.Children.Add(new TextBlock
            {
                Text = entry.Incompatible, FontSize = 11,
                Foreground = (Brush)Application.Current.Resources["BrushDanger"],
                TextWrapping = TextWrapping.Wrap
            });
        }

        root.Children.Add(content);

        var actions = new StackPanel
        {
            Orientation = Orientation.Vertical, Spacing = 8,
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Right
        };

        var (primaryText, primaryEnabled) = entry.Incompatible is { Length: > 0 }
            ? ("安装", false)
            : entry.UpdateAvailable ? ("更新", true)
            : entry.Installed ? ("已安装", false)
            : ("安装", true);

        var primary = new Button
        {
            Content = primaryText,
            IsEnabled = primaryEnabled,
            MinWidth = 92, Height = 32, Padding = new Thickness(16, 0, 16, 0),
            CornerRadius = new CornerRadius(10), FontSize = 12,
            // 主按钮观感：accent 渐变底 + BrushOnAccent 深色字（浅色模式同样可读）。
            Background = Res("BrushAccent"),
            Foreground = Res("BrushOnAccent"),
            BorderThickness = new Thickness(0)
        };
        Motion.Hover(primary, 1.03f, 1.2f);
        if (primaryEnabled) primary.Click += async (_, _) => await InstallFromStoreAsync(entry, primary);
        actions.Children.Add(primary);

        var repo = new Button
        {
            Content = "仓库",
            Height = 30, Padding = new Thickness(14, 0, 14, 0),
            CornerRadius = new CornerRadius(9), FontSize = 11,
            Background = (Brush)Application.Current.Resources["BrushFillSubtle"],
            Foreground = (Brush)Application.Current.Resources["BrushTextSecondary"],
            BorderThickness = new Thickness(0)
        };
        repo.Click += (_, _) => OpenStoreRepo(entry.RepoUrl);
        actions.Children.Add(repo);

        Grid.SetColumn(actions, 1);
        root.Children.Add(actions);

        card.Child = GlassSurface(root);
        return card;
    }

    private static Border BuildStorePill(string text) => new()
    {
        Style = (Style)Application.Current.Resources["Pill"],
        Padding = new Thickness(8, 3, 8, 3),
        VerticalAlignment = VerticalAlignment.Center,
        Child = new TextBlock
        {
            Text = text, FontSize = 10,
            Foreground = (Brush)Application.Current.Resources["BrushTextSecondary"]
        }
    };

    private static string KindName(string? kind) => string.Equals(kind, "script", StringComparison.OrdinalIgnoreCase)
        ? "脚本插件"
        : "原生插件";

    private static void OpenStoreRepo(string? url)
    {
        if (string.IsNullOrWhiteSpace(url)) return;
        try
        {
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            AppLog.Warn(StoreLogArea, "打开插件仓库失败：" + ex.Message);
        }
    }

    /// <summary>下载并安装。失败一律转成中文提示，绝不让异常冒到 UI 线程。</summary>
    private async Task InstallFromStoreAsync(PluginStoreEntry entry, Button button)
    {
        if (!_storeInstalling.Add(entry.Id)) return;

        var original = button.Content;
        button.IsEnabled = false;
        button.Content = "正在下载…";
        try
        {
            var store = LumenRuntime.Store;
            if (store is null)
            {
                await ShowStoreMessageAsync("安装失败", "商店还没准备好，请重启启动器后再试。");
                return;
            }

            var progress = new Progress<DownloadProgress>(p =>
            {
                button.Content = p.Percent >= 100
                    ? "正在安装…"
                    : "正在下载 " + Math.Clamp(p.Percent, 0, 100).ToString("0") + "%";
            });

            var packagePath = await store.DownloadPackageAsync(entry, progress);
            button.Content = "正在安装…";

            // 与设置页插件区同一套目录：DataRoot\plugins（见 SettingsPluginsSection.PluginsRootPath）。
            var pluginsRoot = Path.Combine(LumenRuntime.Paths.Root, PluginPaths.PluginsFolderName);
            var result = await Task.Run(() => PluginPackageInstaller.Install(packagePath, pluginsRoot));
            if (!result.Ok)
            {
                await ShowStoreMessageAsync("安装失败", result.Message);
                return;
            }

            AppLog.Info(StoreLogArea, "已从商店安装插件：" + entry.Id);
            await ShowInstalledAsync(entry);
            // 装完刷新一次，卡片上的「已安装 / 更新」状态会跟着变。
            await LoadStoreAsync(reset: true);
        }
        catch (Exception ex)
        {
            AppLog.Warn(StoreLogArea, "从商店安装插件失败：" + ex);
            await ShowStoreMessageAsync("安装失败",
                "下载或安装插件时出错了：" + (string.IsNullOrWhiteSpace(ex.Message) ? "请稍后再试。" : ex.Message));
        }
        finally
        {
            _storeInstalling.Remove(entry.Id);
            button.Content = original;
            button.IsEnabled = true;
        }
    }

    private async Task ShowInstalledAsync(PluginStoreEntry entry)
    {
        var name = string.IsNullOrWhiteSpace(entry.Name) ? entry.Id : entry.Name;
        try
        {
            var modal = GlassModal.Show("插件已安装", null, 460);
            modal.Body.Children.Add(new TextBlock
            {
                Text = "已安装插件「" + name + "」v" + entry.Version + "。重启启动器后它才会出现在左侧导航里。",
                TextWrapping = TextWrapping.Wrap, FontSize = 13, LineHeight = 21,
                Foreground = (Brush)Application.Current.Resources["BrushTextSecondary"]
            });
            modal.PrimaryText = "立即重启";
            modal.SecondaryText = "稍后";
            modal.PrimaryInvoked = () =>
            {
                modal.Close();
                RestartLauncher();
                return Task.CompletedTask;
            };
            await modal.WaitAsync();
        }
        catch (Exception ex)
        {
            AppLog.Warn(StoreLogArea, "显示安装成功提示失败：" + ex.Message);
        }
    }

    private void RestartLauncher()
    {
        if (LauncherRestart.Restart(StartupOptions.Current.BuildArguments(forceSafeMode: false)))
        {
            Application.Current.Exit();
            return;
        }

        _ = ShowStoreMessageAsync("无法自动重启", "请手动关闭并重新打开启动器，插件就会生效。");
    }

    private async Task ShowStoreMessageAsync(string title, string message)
    {
        try
        {
            await MessageDialog.ShowAsync(XamlRoot, title, message);
        }
        catch (Exception ex)
        {
            AppLog.Warn(StoreLogArea, "显示提示失败：" + ex.Message);
        }
    }
}
