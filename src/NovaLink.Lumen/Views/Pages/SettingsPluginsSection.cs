using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using NovaLink.Lumen.Controls;
using NovaLink.Lumen.Plugins;
using NovaLink.Lumen.Services;
using NovaLink.Lumen.Services.Plugins;
using NovaLink.Lumen.Startup;
using NovaLink.Lumen.Views.Dialogs;
using Windows.Storage.Pickers;

namespace NovaLink.Lumen.Views.Pages;

/// <summary>
/// 设置页「插件」卡片：安全模式横幅、插件列表与开关、插件目录、开发模式。
/// 只读写 LumenRuntime 已有的插件与设置 API，不新增宿主接口。
/// </summary>
public sealed partial class SettingsPage
{
    private bool _syncPlugins;

    private static string PluginsRootPath()
        => Path.Combine(LumenRuntime.Paths.Root, PluginPaths.PluginsFolderName);

    /// <summary>重新读取插件注册表并刷新整张卡片。切到「插件」页、点刷新、改开关后都会走这里。</summary>
    private void ReloadPlugins()
    {
        // 目录监听事件在后台线程抛，切回 UI 线程后由这里重建列表；只挂一次，页面卸载时退订。
        EnsurePluginsFolderWatch();

        _syncPlugins = true;
        try
        {
            PluginList.Children.Clear();
            SafeModeBanner.Visibility = Visibility.Collapsed;
            EmptyPlugins.Visibility = Visibility.Collapsed;
            SetPluginsHint(null);

            var registry = LumenRuntime.Plugins;
            if (registry is null)
            {
                SetPluginsHint("插件系统本次未能启用，不会加载任何插件。可以重启启动器再试，或在日志里查看详细原因。", true);
                SyncDevModeSwitch();
                return;
            }

            var safeMode = registry.SafeModeActive || !string.IsNullOrWhiteSpace(registry.InitialSafeModeReason);
            if (safeMode)
            {
                SafeModeBanner.Visibility = Visibility.Visible;
                SafeModeReasonText.Text = "原因：" + Shorten(DescribeSafeModeReason(registry), 160);
            }

            if (!registry.IsEnabled)
            {
                SetPluginsHint("插件系统未启用：没有可用的数据目录。请检查启动器的数据目录设置后重启。", true);
                SyncDevModeSwitch();
                return;
            }

            var plugins = registry.Plugins.ToList();
            EmptyPlugins.Visibility = plugins.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            if (plugins.Count == 0)
            {
                EmptyPluginsText.Text = "还没有安装插件。点右上角「安装插件包…」选一个插件包，" +
                                        "或者点下面的「打开插件目录」把插件文件夹放进去，插件会自动出现在列表里。";
            }
            else
            {
                for (var index = 0; index < plugins.Count; index++)
                {
                    var row = BuildPluginRow(plugins[index], safeMode);
                    PluginList.Children.Add(row);
                    // 行入场 + 上浮：延迟按 Motion.StaggerMs 递进，最多错开 8 行。
                    Motion.RowIn(row, index);
                }
            }

            if (safeMode)
                SetPluginsHint("安全模式下插件开关暂不可用，请先点上方「重新启用插件并重启」。", true);

            SyncDevModeSwitch();
        }
        catch (Exception ex)
        {
            AppLog.Error("plugins", "刷新插件列表失败。", ex);
            SetPluginsHint("暂时无法读取插件列表，请稍后点刷新重试。", true);
        }
        finally
        {
            _syncPlugins = false;
        }
    }

    private bool _pluginsFolderWired;

    /// <summary>
    /// 订阅「插件目录变化」事件（LumenRuntime 把 watcher 的防抖事件转发到这里），页面卸载时退订。
    /// 设置页每次切页都会重建：静态事件不退订会把整页（含可视树）钉在内存里。
    /// </summary>
    private void EnsurePluginsFolderWatch()
    {
        if (_pluginsFolderWired) return;
        _pluginsFolderWired = true;

        LumenRuntime.PluginsFolderChanged += PluginsFolder_Changed;
        Unloaded += (_, _) =>
        {
            LumenRuntime.PluginsFolderChanged -= PluginsFolder_Changed;
            _pluginsFolderWired = false;
        };
    }

    /// <summary>插件目录在启动器外部被改过：切回 UI 线程重建列表，并给一句不打扰的提示。</summary>
    private void PluginsFolder_Changed(object? sender, EventArgs e)
    {
        DispatcherQueue.TryEnqueue(() =>
        {
            if (!_ready) return;
            ReloadPlugins();
            SetPluginsHint("插件目录有变化，列表已自动更新。");
        });
    }

    /// <summary>
    /// 删除插件：二次确认 → <see cref="LumenRuntime.TryDeletePlugin"/>（写禁用偏好、撤导航、尽力删目录，失败排队）
    /// → 刷新列表并说明是"已删除"还是"重启后彻底移除"。
    /// 安全模式下也允许：删的是磁盘上的插件，不影响本次启动已经定下的加载状态。
    /// </summary>
    private async Task DeletePluginAsync(PluginDescriptor plugin)
    {
        try
        {
            var name = plugin.DisplayName;
            var modal = GlassModal.Show("删除插件",
                "确定要删除「" + name + "」吗？插件文件会被移除，它保存过的数据会保留在插件数据目录里。", 460);

            var confirmed = false;
            modal.PrimaryText = "删除";
            modal.SecondaryText = "取消";
            modal.PrimaryInvoked = async () =>
            {
                confirmed = true;
                modal.Close();
                await Task.CompletedTask;
            };
            modal.SecondaryInvoked = async () =>
            {
                modal.Close();
                await Task.CompletedTask;
            };
            await modal.WaitAsync();
            if (!confirmed) return;

            var removedNow = LumenRuntime.TryDeletePlugin(plugin.Id);
            ReloadPlugins();
            SetPluginsHint(removedNow
                ? "已删除「" + name + "」，插件文件和列表项都已移除。"
                : "已删除「" + name + "」。插件文件正被占用，重启启动器后才会彻底移除。");
        }
        catch (InvalidOperationException ex)
        {
            // 已有别的弹窗在显示：忽略这次点击即可，不必惊动用户。
            AppLog.Warn("plugins", "删除插件确认弹窗被跳过：" + ex.Message);
        }
        catch (Exception ex)
        {
            AppLog.Error("plugins", "删除插件失败：" + plugin.Id, ex);
            SetPluginsHint("删除插件失败，请稍后再试。", true);
        }
    }

    private void SyncDevModeSwitch()
    {
        var devMode = false;
        try
        {
            devMode = ReadBoolExtra("devMode") || StartupOptions.Current.DevMode;
            DevModeSwitch.IsOn = devMode;
        }
        catch
        {
            devMode = false;
            DevModeSwitch.IsOn = false;
        }

        // 开发者工具只在开发模式下出现，避免普通用户误触源码打包。
        DevToolsPanel.Visibility = devMode ? Visibility.Visible : Visibility.Collapsed;
        DevBuildOpenButton.IsEnabled = Directory.Exists(DevBuildOutputDirectory());
    }

    private Border BuildPluginRow(PluginDescriptor plugin, bool safeMode)
    {
        // 液态玻璃卡片：主题化的玻璃填充 + 玻璃描边 + 顶部 1px 高光。
        // 原来这里是写死的白色低透明度（深色专用），浅色模式下整张卡片几乎看不见。
        var card = new Border
        {
            Background = ThemeManager.BrushGlassCard,
            BorderBrush = ThemeManager.Brush("BrushGlassRim", ThemeManager.CardBorder),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(16),
            Padding = new Thickness(0)
        };

        // 卡片内容比卡片内缩 1px 的高光条：内容用 Margin 让位，高光才能贴在最上沿。
        var grid = new Grid { ColumnSpacing = 12, Margin = new Thickness(12, 10, 10, 10) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var info = new StackPanel { Spacing = 5, VerticalAlignment = VerticalAlignment.Center };

        var titleRow = new Grid { ColumnSpacing = 8 };
        titleRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        titleRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        titleRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        titleRow.Children.Add(new TextBlock
        {
            Text = plugin.DisplayName,
            Foreground = ThemeManager.BrushText,
            FontSize = 14.5,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            TextTrimming = TextTrimming.CharacterEllipsis,
            VerticalAlignment = VerticalAlignment.Center
        });

        var version = plugin.Manifest.Version?.Trim() ?? "";
        if (version.Length > 0 && !version.StartsWith("v", StringComparison.OrdinalIgnoreCase))
            version = "v" + version;
        var versionPill = Pill(version.Length == 0 ? "版本未知" : version,
            ThemeManager.BrushTextTertiary);
        versionPill.VerticalAlignment = VerticalAlignment.Center;
        Grid.SetColumn(versionPill, 1);
        titleRow.Children.Add(versionPill);

        var (stateText, stateBrush) = DescribeState(plugin);
        var statePill = Pill(stateText, stateBrush);
        statePill.VerticalAlignment = VerticalAlignment.Center;
        Grid.SetColumn(statePill, 2);
        titleRow.Children.Add(statePill);

        info.Children.Add(titleRow);

        var detail = DescribeDetail(plugin, safeMode);
        if (detail.Length > 0)
        {
            info.Children.Add(new TextBlock
            {
                Text = detail,
                FontSize = 11.5,
                Foreground = ThemeManager.BrushTextSecondary,
                TextWrapping = TextWrapping.Wrap,
                MaxLines = 2,
                TextTrimming = TextTrimming.CharacterEllipsis
            });
        }

        grid.Children.Add(info);

        var actions = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8,
            VerticalAlignment = VerticalAlignment.Center
        };

        var toggle = new ToggleSwitch
        {
            IsOn = !plugin.Quarantined && !safeMode && !IsDisabledByPreference(plugin.Id),
            OnContent = "已启用",
            OffContent = "已禁用",
            IsEnabled = !safeMode,
            VerticalAlignment = VerticalAlignment.Center
        };
        toggle.Toggled += (_, _) => PluginToggle_Toggled(plugin, toggle.IsOn);
        actions.Children.Add(toggle);

        if (plugin.Quarantined)
        {
            var clear = SmallButton("清除隔离");
            clear.Click += (_, _) => ClearPluginQuarantine(plugin);
            actions.Children.Add(clear);
        }

        // 删除入口：危险样式 + 二次确认。安全模式下同样可用（删文件不改启动链）。
        var delete = SmallButton("删除");
        delete.Foreground = ThemeManager.BrushDanger;
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetAutomationId(delete, DeleteAutomationId(plugin.Id));
        delete.Click += async (_, _) => await DeletePluginAsync(plugin);
        actions.Children.Add(delete);

        Grid.SetColumn(actions, 1);
        grid.Children.Add(actions);

        // 顶部 1px 玻璃高光：内容已经用 Margin 让位，这里贴住卡片最上沿，左右各留 1px。
        var highlight = new Border
        {
            Height = 1,
            Background = ThemeManager.Brush("BrushGlassHighlight", ThemeManager.Accent),
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(1, 0, 1, 0),
            CornerRadius = new CornerRadius(1),
            IsHitTestVisible = false
        };

        var host = new Grid();
        host.Children.Add(grid);
        host.Children.Add(highlight);
        card.Child = host;

        Motion.Hover(card);
        return card;
    }

    /// <summary>删除按钮的 AutomationId：插件 id 里的点与冒号换成下划线（UIA 里好定位）。</summary>
    private static string DeleteAutomationId(string id)
        => "PluginDelete_" + id.Replace('.', '_').Replace(':', '_');

    private static (string Text, Brush Brush) DescribeState(PluginDescriptor plugin)
    {
        if (plugin.Quarantined) return ("已隔离", ThemeManager.BrushDanger);
        return plugin.State switch
        {
            PluginState.Initialized => ("运行中", ThemeManager.BrushAccent),
            PluginState.Loaded => ("已加载", ThemeManager.Brush("BrushSky", ThemeManager.Accent)),
            PluginState.Failed => ("加载失败", ThemeManager.BrushDanger),
            PluginState.Disabled => ("已禁用", ThemeManager.BrushTextSecondary),
            _ => ("已就绪", ThemeManager.BrushTextSecondary)
        };
    }

    private static string DescribeDetail(PluginDescriptor plugin, bool safeMode)
    {
        if (plugin.Quarantined)
        {
            var times = Math.Max(plugin.FailCount, 1);
            return "连续加载失败 " + times + " 次，已自动隔离。点「清除隔离」后会在下次启动时重新尝试加载。";
        }

        if (plugin.State == PluginState.Failed)
        {
            var reason = SanitizeReason(plugin.LastError);
            return reason.Length == 0
                ? "插件加载失败，已停用。详细原因已写入日志。"
                : "插件加载失败，已停用：" + reason;
        }

        if (plugin.State == PluginState.Disabled)
        {
            if (safeMode) return "本次以安全模式启动，插件已停用。";
            var reason = SanitizeReason(plugin.LastError);
            if (reason.Length > 0) return "已停用：" + reason;
            if (plugin.DisabledByUser) return "已在插件设置里停用。";
            return "已被停用，具体原因已写入日志。";
        }

        if (plugin.State == PluginState.Discovered)
            return "已就绪，下次启动启动器时加载。";

        if (plugin.State == PluginState.Loaded)
            return "插件文件已加载，正在等待初始化。";

        var description = Shorten(plugin.Manifest.Description, 80);
        if (description.Length > 0) return description;
        var author = Shorten(plugin.Manifest.Author, 40);
        if (author.Length > 0) return "作者：" + author;
        return "";
    }

    private static string DescribeSafeModeReason(PluginRegistry registry)
    {
        var reason = registry.InitialSafeModeReason;
        if (string.IsNullOrWhiteSpace(reason)) reason = BootState.SafeModeReason;
        if (!string.IsNullOrWhiteSpace(reason)) return SanitizeReason(reason!);
        return registry.SafeModeActive
            ? "本次以安全模式启动，插件已全部停用。"
            : "上次启动没有正常完成，已自动停用所有插件。";
    }

    /// <summary>把异常文本压成一句人话：只取首行、去掉堆栈与异常类型、过滤掉没有中文的原文。</summary>
    private static string SanitizeReason(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return "";

        var text = raw.Replace("\r", "\n");
        var separator = text.IndexOf(" ---> ", StringComparison.Ordinal);
        if (separator >= 0) text = text[..separator];
        var line = text.Split('\n').Select(part => part.Trim()).FirstOrDefault(part => part.Length > 0) ?? "";

        line = Regex.Replace(line, @"\s+at\s+[\w\.\+<>\x60\[\],]+\s*\(.*$", " ");
        line = Regex.Replace(line, @"\b0x[0-9A-Fa-f]{8}\b", " ");
        line = Regex.Replace(line, @"\bHRESULT\b.*$", " ", RegexOptions.IgnoreCase);
        line = Regex.Replace(line, @"\b(System|Microsoft)\.[\w\.]+", " ");
        line = Regex.Replace(line, @"\b[A-Za-z_][\w\.]*(Exception|Error)\b\s*:?", " ");
        line = Regex.Replace(line, @"\s+", " ").Trim().Trim(':', '-', '—', '(', ')', '"', '\'');

        foreach (var prefix in new[] { "加载失败：", "加载失败:", "初始化失败：", "初始化失败:" })
        {
            if (line.StartsWith(prefix, StringComparison.Ordinal))
            {
                line = line[prefix.Length..].Trim();
                break;
            }
        }

        if (line.Length == 0) return "";
        if (!ContainsChinese(line)) return "详细原因已写入日志，可在插件目录查看。";
        if (LookLikeRawError(line)) return "详细原因已写入日志，可在插件目录查看。";
        return Shorten(line, 80);
    }

    private static bool ContainsChinese(string text)
        => text.Any(ch => ch >= '\u4e00' && ch <= '\u9fff');

    /// <summary>兜底：即使混在中文里，也不要展示原始英文报错片段。</summary>
    private static bool LookLikeRawError(string text)
        => Regex.IsMatch(text,
            @"(?i)(exception|hresult|stack trace|at\s+[A-Za-z_][\w\.]*\s*\(|could not|unable to|failed to|file or assembly|denied)");

    private static string Shorten(string? text, int max)
    {
        if (string.IsNullOrWhiteSpace(text)) return "";
        var line = text.Replace("\r", " ").Replace("\n", " ").Trim();
        line = Regex.Replace(line, @"\s+", " ");
        return line.Length <= max ? line : line[..max].TrimEnd() + "…";
    }

    private static Border Pill(string text, Brush foreground)
        => new()
        {
            Style = (Style)Application.Current.Resources["Pill"],
            Padding = new Thickness(8, 2, 8, 2),
            Child = new TextBlock
            {
                Text = text,
                FontSize = 10.5,
                Foreground = foreground,
                VerticalAlignment = VerticalAlignment.Center
            }
        };

    /// <summary>从主题资源取 Brush（设置页各 partial 共用，缺键直接抛出以便发现漏配）。</summary>
    private static Brush ResourceBrush(string key) => (Brush)Application.Current.Resources[key];

    private void PluginToggle_Toggled(PluginDescriptor plugin, bool enabled)
    {
        if (_syncPlugins || !_ready) return;
        try
        {
            SetPluginEnabledPreference(plugin.Id, enabled);
            if (enabled && plugin.Quarantined)
            {
                LumenRuntime.Plugins?.ClearQuarantine(plugin.Id);
                AppLog.Info("plugins", "用户重新启用插件并清除了隔离：" + plugin.Id);
                var name = plugin.DisplayName;
                DispatcherQueue.TryEnqueue(() =>
                {
                    if (!_ready) return;
                    ReloadPlugins();
                    SetPluginsHint("已解除「" + name + "」的隔离，下次启动会重新尝试加载。");
                });
                return;
            }

            AppLog.Info("plugins", (enabled ? "启用" : "禁用") + "插件：" + plugin.Id);
            var pluginName = plugin.DisplayName;
            DispatcherQueue.TryEnqueue(() =>
            {
                if (!_ready) return;
                ReloadPlugins();
                SetPluginsHint("已保存：" + pluginName + " 会在下次启动启动器时" + (enabled ? "启用" : "停用") + "。");
            });
        }
        catch (Exception ex)
        {
            AppLog.Error("plugins", "保存插件开关失败：" + plugin.Id, ex);
            SetPluginsHint("保存插件开关失败，请稍后再试。", true);
            DispatcherQueue.TryEnqueue(() =>
            {
                if (!_ready) return;
                ReloadPlugins();
            });
        }
    }

    private void ClearPluginQuarantine(PluginDescriptor plugin)
    {
        try
        {
            LumenRuntime.Plugins?.ClearQuarantine(plugin.Id);
            SetPluginEnabledPreference(plugin.Id, true);
            AppLog.Info("plugins", "用户清除了插件隔离：" + plugin.Id);
            ReloadPlugins();
            SetPluginsHint("已清除「" + plugin.DisplayName + "」的隔离记录，下次启动会重新尝试加载。");
        }
        catch (Exception ex)
        {
            AppLog.Error("plugins", "清除插件隔离失败：" + plugin.Id, ex);
            SetPluginsHint("暂时无法清除隔离，请稍后再试。", true);
        }
    }

    private static bool IsDisabledByPreference(string id)
    {
        try
        {
            var extra = LumenRuntime.Settings.Settings.Extra;
            if (extra is null || !extra.TryGetValue("disabledPlugins", out var element)) return false;
            if (element.ValueKind != JsonValueKind.Array) return false;
            foreach (var item in element.EnumerateArray())
            {
                if (item.ValueKind == JsonValueKind.String &&
                    string.Equals(item.GetString(), id, StringComparison.OrdinalIgnoreCase))
                    return true;
            }
            return false;
        }
        catch
        {
            return false;
        }
    }

    private static void SetPluginEnabledPreference(string id, bool enabled)
    {
        var settings = LumenRuntime.Settings.Settings;
        settings.Extra ??= new Dictionary<string, JsonElement>();

        var disabled = new List<string>();
        if (settings.Extra.TryGetValue("disabledPlugins", out var element) &&
            element.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in element.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.String) continue;
                var value = item.GetString();
                if (string.IsNullOrWhiteSpace(value)) continue;
                if (disabled.Contains(value!, StringComparer.OrdinalIgnoreCase)) continue;
                disabled.Add(value!);
            }
        }

        disabled.RemoveAll(existing => string.Equals(existing, id, StringComparison.OrdinalIgnoreCase));
        if (!enabled) disabled.Add(id);

        settings.Extra["disabledPlugins"] = JsonSerializer.SerializeToElement(disabled);
        LumenRuntime.Settings.Save();
    }

    private static bool ReadBoolExtra(string key)
    {
        try
        {
            var extra = LumenRuntime.Settings.Settings.Extra;
            if (extra is null || !extra.TryGetValue(key, out var element)) return false;
            return element.ValueKind switch
            {
                JsonValueKind.True => true,
                JsonValueKind.False => false,
                JsonValueKind.String => string.Equals(element.GetString(), "true", StringComparison.OrdinalIgnoreCase),
                _ => false
            };
        }
        catch
        {
            return false;
        }
    }

    private void SetPluginsHint(string? text, bool warn = false)
    {
        if (PluginsHintText is null) return;
        if (string.IsNullOrWhiteSpace(text))
        {
            PluginsHintText.Visibility = Visibility.Collapsed;
            return;
        }

        PluginsHintText.Text = text;
        PluginsHintText.Foreground = warn
            ? ThemeManager.BrushDanger
            : ThemeManager.BrushTextSecondary;
        PluginsHintText.Visibility = Visibility.Visible;
    }

    private async void OpenPluginsFolder_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var root = PluginsRootPath();
            Directory.CreateDirectory(root);
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("explorer.exe", "\"" + root + "\"")
            {
                UseShellExecute = true
            });
            AppLog.Info("plugins", "打开插件目录：" + root);
        }
        catch (Exception ex)
        {
            AppLog.Error("plugins", "打开插件目录失败。", ex);
            await MessageDialog.ShowAsync(XamlRoot, "无法打开插件目录",
                "请手动在资源管理器里打开：" + PluginsRootPath());
        }
    }

    private void RefreshPlugins_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var registry = LumenRuntime.Plugins;

            // 手动刷新也走同一条差集路径：新插件当场加载、被删掉的当场撤掉导航。
            if (registry is not null && registry.IsEnabled && !registry.SafeModeActive)
            {
                var result = registry.RefreshFromDisk();
                if (result.RemovedNavigationTags.Count > 0)
                    PluginUiService.Instance.RemoveNavigations(result.RemovedNavigationTags);

                ReloadPlugins();
                SetPluginsHint(result.HasChanges
                    ? "插件列表已刷新：新增 " + result.Added + " 个，移除 " + result.Removed + " 个。"
                    : "插件列表已刷新，没有发现新的变化。");
            }
            else
            {
                ReloadPlugins();
            }

            AppLog.Info("plugins", "用户手动刷新了插件列表。");
        }
        catch (Exception ex)
        {
            AppLog.Error("plugins", "刷新插件列表失败。", ex);
            SetPluginsHint("刷新插件列表失败，请稍后再试。", true);
        }
    }

/// <summary>从 .lumenplugin 文件安装插件：解压校验后放进插件目录，重启启动器生效。</summary>
    private async void InstallPlugin_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var picker = new FileOpenPicker { SuggestedStartLocation = PickerLocationId.Downloads };
            picker.FileTypeFilter.Add(".lumenplugin");
            picker.FileTypeFilter.Add(".zip");

            var window = MainWindow.Current;
            if (window is null)
            {
                SetPluginsHint("启动器窗口还没准备好，请稍后再试。", true);
                return;
            }

            WinRT.Interop.InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(window));
            var file = await picker.PickSingleFileAsync();
            if (file is null) return;

            InstallPluginButton.IsEnabled = false;
            var result = PluginPackageInstaller.Install(file.Path, PluginsRootPath());
            if (!result.Ok)
            {
                SetPluginsHint(result.Message, true);
                await MessageDialog.ShowAsync(XamlRoot, "无法安装这个插件", result.Message);
                return;
            }

            ReloadPlugins();
            SetPluginsHint(result.Message);
            await MessageDialog.ShowAsync(XamlRoot, "插件已安装", result.Message);
        }
        catch (Exception ex)
        {
            AppLog.Error("plugins", "安装插件包失败。", ex);
            SetPluginsHint("安装插件失败，请稍后再试。", true);
        }
        finally
        {
            InstallPluginButton.IsEnabled = true;
        }
    }

    /// <summary>打开站内文档帮助界面（使用文档 / 开发文档）。</summary>
    private void PluginDocs_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            DocsDialog.Show(DocsTab.Usage);
        }
        catch (InvalidOperationException ex)
        {
            // 已经有别的弹窗（或文档弹窗本身）在显示，这次点击忽略即可，不必惊动用户。
            AppLog.Warn("plugins", "打开插件文档被跳过：" + ex.Message);
        }
        catch (Exception ex)
        {
            AppLog.Error("plugins", "打开插件文档失败。", ex);
            SetPluginsHint("暂时打不开插件文档，请稍后再试。", true);
        }
    }

    private void DevMode_Toggled(object sender, RoutedEventArgs e)
    {
        if (_syncPlugins || !_ready) return;
        try
        {
            var enabled = DevModeSwitch.IsOn;
            var settings = LumenRuntime.Settings.Settings;
            settings.Extra ??= new Dictionary<string, JsonElement>();
            settings.Extra["devMode"] = JsonSerializer.SerializeToElement(enabled);
            LumenRuntime.Settings.Save();

            AppLog.Info("plugins", "开发模式偏好已" + (enabled ? "开启" : "关闭") + "。");
            // 偏好保存后立即切换开发者工具区块，不必等重新进入设置页。
            DevToolsPanel.Visibility = enabled ? Visibility.Visible : Visibility.Collapsed;
            DevBuildOpenButton.IsEnabled = enabled && Directory.Exists(DevBuildOutputDirectory());
            SetPluginsHint(enabled
                ? "开发模式已开启，开发者工具已经展开。"
                : "开发模式已关闭，开发者工具已收起。");
        }
        catch (Exception ex)
        {
            AppLog.Error("plugins", "保存开发模式偏好失败。", ex);
            SetPluginsHint("保存开发模式偏好失败，请稍后再试。", true);
        }
    }

    private async void ReenablePlugins_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var modal = GlassModal.Show("重新启用插件",
                "会清除插件安全模式的启动标记，然后重启启动器。如果插件再次导致启动失败，启动器会自动回到安全模式。",
                460);
            var confirmed = false;
            modal.PrimaryText = "清除并重启";
            modal.SecondaryText = "取消";
            modal.PrimaryInvoked = async () => { confirmed = true; modal.Close(); await Task.CompletedTask; };
            modal.SecondaryInvoked = async () => { modal.Close(); await Task.CompletedTask; };
            await modal.WaitAsync();
            if (!confirmed) return;

            // 看门狗的 MarkHealthy 只清"启动尝试次数/安全模式原因"，不再顺手洗白隔离与失败历史，
            // 所以这里必须显式解除全部隔离，否则重启后被隔离的插件依旧不会加载，按钮看起来"没生效"。
            var cleared = LumenRuntime.Plugins?.ClearAllQuarantines() ?? 0;
            if (cleared > 0)
                AppLog.Info("plugins", "重新启用插件：解除了 " + cleared + " 个插件的隔离记录。");
            LumenRuntime.Plugins?.Watchdog?.MarkHealthy();

            var options = StartupOptions.Current;
            var arguments = RemoveArgument(RemoveArgument(options.BuildArguments(false),
                StartupOptions.FlagSafeMode), StartupOptions.FlagNoPlugins);
            if (!LauncherRestart.Restart(arguments))
            {
                await MessageDialog.ShowAsync(XamlRoot, "无法重启启动器",
                    "安全模式标记已经清除，请手动关闭后重新打开启动器。");
                return;
            }

            AppLog.Info("plugins", "已清除插件安全模式标记，正在重启启动器。");
            Application.Current.Exit();
        }
        catch (Exception ex)
        {
            AppLog.Error("plugins", "重新启用插件失败。", ex);
            await MessageDialog.ShowAsync(XamlRoot, "重新启用失败",
                "暂时无法清除安全模式标记，请稍后再试。");
        }
    }

    private static string RemoveArgument(string arguments, string flag)
    {
        if (string.IsNullOrWhiteSpace(arguments)) return arguments;
        if (string.Equals(arguments.Trim(), flag, StringComparison.OrdinalIgnoreCase)) return "";

        // 按完整参数替换，避免拆散带引号的数据目录（其中可能有空格）。
        var result = arguments.Replace(" " + flag + " ", " ", StringComparison.OrdinalIgnoreCase).Trim();
        if (result.StartsWith(flag + " ", StringComparison.OrdinalIgnoreCase))
            result = result[(flag.Length + 1)..].TrimStart();
        if (result.EndsWith(" " + flag, StringComparison.OrdinalIgnoreCase))
            result = result[..^flag.Length].TrimEnd();
        return result;
    }
}
