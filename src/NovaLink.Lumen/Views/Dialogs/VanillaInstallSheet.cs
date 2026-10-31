using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using NovaLink.Lumen.Controls;
using NovaLink.Lumen.Core.Models;
using NovaLink.Lumen.Services;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Windows.UI;

namespace NovaLink.Lumen.Views.Dialogs;

/// <summary>Settings sheet shown before a Mojang version is downloaded.</summary>
public static class VanillaInstallSheet
{
    private static readonly (string Label, string Value)[] Loaders =
    {
        ("原版", "原版"), ("Fabric", "Fabric"), ("Quilt", "Quilt"),
        ("Forge", "Forge"), ("NeoForge", "NeoForge")
    };

    public static async Task<bool> ShowAsync(XamlRoot root, ManifestVersion version)
    {
        var modal = GlassModal.Show("安装 " + version.Id,
            "选择加载器和安装选项，确认后才会开始下载。", 600);

        var header = new Grid { ColumnSpacing = 14 };
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        var icon = new Border
        {
            Width = 56,
            Height = 56,
            CornerRadius = new CornerRadius(17),
            Background = new LinearGradientBrush
            {
                StartPoint = new Windows.Foundation.Point(0, 0),
                EndPoint = new Windows.Foundation.Point(1, 1),
                GradientStops =
                {
                    new GradientStop { Color = Color.FromArgb(0x66, 0x7C, 0xEB, 0xD6), Offset = 0 },
                    new GradientStop { Color = Color.FromArgb(0x44, 0xA9, 0x96, 0xFF), Offset = 1 }
                }
            },
            Child = new FontIcon { Glyph = "\uE7FC", FontSize = 24, Foreground = (Brush)Application.Current.Resources["BrushMint"] }
        };
        header.Children.Add(icon);

        var heading = new StackPanel { Spacing = 3, VerticalAlignment = VerticalAlignment.Center };
        heading.Children.Add(new TextBlock
        {
            Text = version.Id,
            FontSize = 25,
            FontWeight = FontWeights.SemiBold,
            Foreground = (Brush)Application.Current.Resources["BrushText"]
        });
        var typeName = version.Type switch
        {
            "release" => "正式版",
            "snapshot" => "快照版",
            "old_beta" => "远古 Beta",
            "old_alpha" => "远古 Alpha",
            _ => version.Type
        };
        heading.Children.Add(new TextBlock
        {
            Text = typeName + " · " + version.ReleaseTime.ToLocalTime().ToString("yyyy-MM-dd") + " · Mojang 官方原版",
            FontSize = 10.5,
            Foreground = (Brush)Application.Current.Resources["BrushTextTertiary"]
        });
        Grid.SetColumn(heading, 1);
        header.Children.Add(heading);
        modal.Body.Children.Add(header);

        modal.Body.Children.Add(SectionLabel("模组加载器"));
        var loaderRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
        modal.Body.Children.Add(loaderRow);

        var loaderVersionCombo = new ComboBox
        {
            Header = "加载器版本",
            HorizontalAlignment = HorizontalAlignment.Stretch,
            MaxDropDownHeight = 280,
            MinHeight = 42,
            CornerRadius = new CornerRadius(12),
            Background = (Brush)Application.Current.Resources["BrushField"],
            BorderBrush = (Brush)Application.Current.Resources["BrushFieldBorder"],
            Foreground = (Brush)Application.Current.Resources["BrushText"],
            Visibility = Visibility.Collapsed
        };
        modal.Body.Children.Add(loaderVersionCombo);

        var nameBox = new TextBox
        {
            Header = "显示名称（可选）",
            PlaceholderText = "例如：26.3 生存",
            MaxLength = 40,
            MinHeight = 42,
            CornerRadius = new CornerRadius(12),
            Background = (Brush)Application.Current.Resources["BrushField"],
            BorderBrush = (Brush)Application.Current.Resources["BrushFieldBorder"],
            Foreground = (Brush)Application.Current.Resources["BrushText"]
        };
        modal.Body.Children.Add(nameBox);

        var isolation = new ToggleSwitch
        {
            Header = "版本隔离",
            OnContent = "开",
            OffContent = "关",
            IsOn = LumenRuntime.Settings.Settings.DefaultIsolation
        };
        modal.Body.Children.Add(isolation);
        modal.Body.Children.Add(Caption("开启后，模组、存档和资源包会保存在该版本自己的文件夹里。"));

        var fabricApi = new ToggleSwitch
        {
            Header = "同时安装 Fabric API",
            OnContent = "安装",
            OffContent = "不安装",
            IsOn = true,
            Visibility = Visibility.Collapsed
        };
        modal.Body.Children.Add(fabricApi);

        var fabricVersionCombo = new ComboBox
        {
            Header = "Fabric API 版本",
            HorizontalAlignment = HorizontalAlignment.Stretch,
            MaxDropDownHeight = 300,
            MinHeight = 42,
            CornerRadius = new CornerRadius(12),
            Background = (Brush)Application.Current.Resources["BrushField"],
            BorderBrush = (Brush)Application.Current.Resources["BrushFieldBorder"],
            Foreground = (Brush)Application.Current.Resources["BrushText"],
            IsEnabled = false,
            Visibility = Visibility.Collapsed
        };
        modal.Body.Children.Add(fabricVersionCombo);

        var fabricHint = Caption("Fabric API 会从 Modrinth 下载所选兼容版本，放入该实例的 mods 文件夹。");
        fabricHint.Visibility = Visibility.Collapsed;
        modal.Body.Children.Add(fabricHint);

        var loader = "原版";
        var buttons = new List<(Button Button, string Value)>();
        var loaderCts = new CancellationTokenSource();
        var fabricCts = new CancellationTokenSource();
        var fabricLoadedFor = "";
        var choosing = false;

        void ApplyLoaderVisuals()
        {
            foreach (var item in buttons)
            {
                var active = item.Value == loader;
                item.Button.Background = active
                    ? new SolidColorBrush(Color.FromArgb(0x2A, 0x7C, 0xEB, 0xD6))
                    : (Brush)Application.Current.Resources["BrushFillSubtle"];
                item.Button.Foreground = active
                    ? (Brush)Application.Current.Resources["BrushMint"]
                    : (Brush)Application.Current.Resources["BrushTextSecondary"];
                item.Button.FontWeight = active ? FontWeights.SemiBold : FontWeights.Normal;
            }
        }

        async Task LoadLoaderVersionsAsync()
        {
            loaderCts.Cancel();
            loaderCts = new CancellationTokenSource();
            var token = loaderCts.Token;
            loaderVersionCombo.Items.Clear();
            if (loader == "原版")
            {
                loaderVersionCombo.Visibility = Visibility.Collapsed;
                return;
            }
            loaderVersionCombo.Visibility = Visibility.Visible;
            loaderVersionCombo.IsEnabled = false;
            loaderVersionCombo.Header = "正在读取 " + loader + " 版本…";
            loaderVersionCombo.Items.Add(new ComboBoxItem { Content = "最新稳定版", Tag = "" });
            loaderVersionCombo.SelectedIndex = 0;
            try
            {
                var versions = await LumenRuntime.Loaders.VersionsAsync(loader, version.Id, token);
                if (token.IsCancellationRequested || loader == "原版") return;
                loaderVersionCombo.Items.Clear();
                loaderVersionCombo.Items.Add(new ComboBoxItem { Content = "最新稳定版", Tag = "" });
                foreach (var item in versions.Take(40))
                    loaderVersionCombo.Items.Add(new ComboBoxItem { Content = item, Tag = item });
                loaderVersionCombo.SelectedIndex = 0;
                loaderVersionCombo.Header = loader + " 版本";
                loaderVersionCombo.IsEnabled = true;
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                if (token.IsCancellationRequested) return;
                loaderVersionCombo.Header = loader + " 版本（将使用最新稳定版）";
                loaderVersionCombo.IsEnabled = true;
                AppLog.Warn("install", "读取加载器版本失败：" + ex.Message);
            }
        }

        async Task LoadFabricApiVersionsAsync(bool force = false)
        {
            if (loader != "Fabric" || !fabricApi.IsOn)
            {
                fabricVersionCombo.Visibility = Visibility.Collapsed;
                return;
            }

            fabricVersionCombo.Visibility = Visibility.Visible;
            if (!force && fabricLoadedFor == version.Id && fabricVersionCombo.Items.Count > 0) return;

            fabricCts.Cancel();
            fabricCts = new CancellationTokenSource();
            var token = fabricCts.Token;
            fabricVersionCombo.Items.Clear();
            fabricVersionCombo.SelectedItem = null;
            fabricVersionCombo.IsEnabled = false;
            fabricVersionCombo.Header = "Fabric API 版本（正在读取…）";
            fabricHint.Text = "正在从 Modrinth 读取适配 " + version.Id + " 的 Fabric API 版本。";
            try
            {
                var project = await LumenRuntime.Modrinth.ProjectAsync("fabric-api");
                var versions = await LumenRuntime.Modrinth.VersionsAsync(project.ProjectId, version.Id, "fabric", token);
                if (token.IsCancellationRequested || loader != "Fabric" || !fabricApi.IsOn) return;

                var ordered = versions
                    .OrderByDescending(v => v.DatePublished ?? DateTimeOffset.MinValue)
                    .ToList();
                fabricVersionCombo.Items.Clear();
                fabricVersionCombo.Items.Add(new ComboBoxItem
                {
                    Content = "自动 · 优先最新正式版",
                    Tag = null
                });

                ModrinthVersion? recommended = ordered.FirstOrDefault(v => v.VersionType == "release")
                    ?? ordered.FirstOrDefault();
                ComboBoxItem? selectedItem = null;
                foreach (var item in ordered)
                {
                    var type = item.VersionType switch
                    {
                        "release" => "正式版",
                        "beta" => "测试版",
                        "alpha" => "内测版",
                        _ => item.VersionType
                    };
                    var date = item.DatePublished?.ToLocalTime().ToString("yyyy-MM-dd") ?? "";
                    var entry = new ComboBoxItem
                    {
                        Content = item.VersionNumber + " · " + type
                            + (string.IsNullOrWhiteSpace(date) ? "" : " · " + date)
                            + (ReferenceEquals(item, recommended) ? " · 推荐" : ""),
                        Tag = item
                    };
                    fabricVersionCombo.Items.Add(entry);
                    if (ReferenceEquals(item, recommended)) selectedItem = entry;
                }

                fabricVersionCombo.SelectedItem = selectedItem
                    ?? fabricVersionCombo.Items.FirstOrDefault();
                fabricVersionCombo.IsEnabled = ordered.Count > 0;
                fabricVersionCombo.Header = "Fabric API 版本";
                fabricLoadedFor = version.Id;
                fabricHint.Text = ordered.Count == 0
                    ? "没有找到适配 " + version.Id + " 的 Fabric API 版本；安装将继续，但不含 Fabric API。"
                    : "共 " + ordered.Count + " 个适配 " + version.Id + " 的版本，默认推荐 "
                        + (recommended?.VersionNumber ?? "最新版") + "；可在这里切换到其他版本。";
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                if (token.IsCancellationRequested) return;
                fabricVersionCombo.Header = "Fabric API 版本（将自动选择推荐版）";
                fabricVersionCombo.IsEnabled = false;
                fabricHint.Text = "读取 Fabric API 版本失败：" + ex.Message + "。安装时会自动选择推荐版本。";
                AppLog.Warn("install", "读取 Fabric API 版本失败：" + ex.Message);
            }
        }

        void SetLoader(string value)
        {
            loader = value;
            ApplyLoaderVisuals();
            var fabric = value == "Fabric";
            fabricApi.Visibility = fabric ? Visibility.Visible : Visibility.Collapsed;
            fabricHint.Visibility = fabric ? Visibility.Visible : Visibility.Collapsed;
            if (!fabric)
            {
                fabricVersionCombo.Visibility = Visibility.Collapsed;
                fabricApi.IsOn = true;
            }
            _ = LoadLoaderVersionsAsync();
            _ = LoadFabricApiVersionsAsync();
        }

        foreach (var (label, value) in Loaders)
        {
            var button = new Button
            {
                Content = label,
                Style = (Style)Application.Current.Resources["GhostButton"],
                Height = 37,
                Padding = new Thickness(13, 0, 13, 0),
                CornerRadius = new CornerRadius(11),
                FontSize = 11.5,
                Tag = value
            };
            button.Click += (_, _) => SetLoader(value);
            buttons.Add((button, value));
            loaderRow.Children.Add(button);
        }
        ApplyLoaderVisuals();
        fabricApi.Toggled += (_, _) =>
        {
            if (loader != "Fabric") return;
            if (!fabricApi.IsOn)
            {
                fabricVersionCombo.Visibility = Visibility.Collapsed;
                return;
            }
            _ = LoadFabricApiVersionsAsync();
        };

        var chosenLoader = "原版";
        var chosenLoaderVersion = "";
        var chosenName = "";
        var chosenIsolation = isolation.IsOn;
        var chosenFabricApi = false;
        ModrinthVersion? chosenFabricApiVersion = null;
        var confirmed = false;

        modal.PrimaryText = "开始安装";
        modal.SecondaryText = "取消";
        modal.PrimaryInvoked = () =>
        {
            if (choosing) return Task.CompletedTask;
            choosing = true;
            chosenLoader = loader;
            chosenLoaderVersion = (loaderVersionCombo.SelectedItem as ComboBoxItem)?.Tag as string ?? "";
            chosenName = nameBox.Text.Trim();
            chosenIsolation = isolation.IsOn;
            chosenFabricApi = loader == "Fabric" && fabricApi.IsOn;
            chosenFabricApiVersion = chosenFabricApi
                ? (fabricVersionCombo.SelectedItem as ComboBoxItem)?.Tag as ModrinthVersion
                : null;
            confirmed = true;
            modal.Close();
            return Task.CompletedTask;
        };

        await modal.WaitAsync();
        loaderCts.Cancel();
        fabricCts.Cancel();
        if (!confirmed) return false;

        return await InstallAsync(root, version, chosenLoader, chosenLoaderVersion,
            chosenName, chosenIsolation, chosenFabricApi, chosenFabricApiVersion);
    }

    private static async Task<bool> InstallAsync(XamlRoot root, ManifestVersion version,
        string loader, string loaderVersion, string displayName, bool isolated, bool fabricApi,
        ModrinthVersion? fabricApiVersion)
    {
        var title = version.Id + (loader == "原版" ? "" : " · " + loader);
        var taskId = "vanilla:" + version.Id + ":" + loader;
        ProgressCenter.Start(taskId, "安装 " + title, "Mojang 原版游戏", "install", "准备中", "正在准备下载", false);
        ProgressCenter.OpenOverlay();

        var reporter = new Progress<DownloadProgress>(p =>
        {
            ProgressCenter.Update(taskId,
                phase: string.IsNullOrWhiteSpace(p.Stage) ? "下载中" : p.Stage,
                percent: p.Percent,
                message: string.IsNullOrWhiteSpace(p.Message) ? null : p.Message,
                bytes: p.Received);
        });

        try
        {
            InstanceSummary result;
            if (loader == "原版")
            {
                result = await Task.Run(() => LumenRuntime.Installer.InstallVanillaAsync(
                    version.Id, displayName, isolated, reporter));
            }
            else
            {
                result = await Task.Run(() => LumenRuntime.Loaders.InstallAsync(
                    loader, version.Id, string.IsNullOrWhiteSpace(loaderVersion) ? null : loaderVersion,
                    displayName, isolated, reporter));
            }

            string? fabricWarning = null;
            if (fabricApi)
            {
                try
                {
                    ProgressCenter.Update(taskId, phase: "安装 Fabric API", indeterminate: true,
                        message: "正在准备 Fabric API " + (fabricApiVersion?.VersionNumber ?? "推荐版本"));
                    var project = await LumenRuntime.Modrinth.ProjectAsync("fabric-api");
                    ModrinthVersion? pick = fabricApiVersion;
                    if (pick is null)
                    {
                        var versions = await LumenRuntime.Modrinth.VersionsAsync(project.ProjectId, version.Id, "fabric");
                        pick = versions
                            .OrderByDescending(v => v.DatePublished ?? DateTimeOffset.MinValue)
                            .FirstOrDefault(v => v.VersionType == "release")
                            ?? versions.OrderByDescending(v => v.DatePublished ?? DateTimeOffset.MinValue).FirstOrDefault();
                    }
                    var file = pick?.Files.FirstOrDefault(f => f.Primary) ?? pick?.Files.FirstOrDefault();
                    if (pick is null || file is null)
                        throw new InvalidOperationException("没有找到适配 " + version.Id + " 的 Fabric API 版本。");
                    ProgressCenter.Update(taskId, phase: "安装 Fabric API", indeterminate: true,
                        message: "正在下载 Fabric API " + pick.VersionNumber);
                    var mods = Path.Combine(result.GameDir, "mods");
                    Directory.CreateDirectory(mods);
                    var target = Path.Combine(mods, file.Filename);
                    foreach (var existing in Directory.EnumerateFiles(mods, "fabric-api-*.jar"))
                    {
                        if (string.Equals(Path.GetFileName(existing), file.Filename, StringComparison.OrdinalIgnoreCase))
                            continue;
                        try
                        {
                            File.Delete(existing);
                            AppLog.Info("install", "替换旧 Fabric API：" + Path.GetFileName(existing));
                        }
                        catch (Exception cleanupEx)
                        {
                            AppLog.Warn("install", "无法清理旧 Fabric API：" + cleanupEx.Message);
                        }
                    }
                    await LumenRuntime.Modrinth.InstallToPathAsync(file, target, reporter);
                }
                catch (Exception ex)
                {
                    fabricWarning = ex.Message;
                    AppLog.Warn("install", "Fabric API 安装失败：" + ex.Message);
                }
            }

            ProgressCenter.Finish(taskId, result.Name + " 已安装");
            ProgressCenter.CloseOverlay();
            await Task.Delay(90);
            var message = result.Name + " 已准备就绪，可以在版本页启动。";
            if (fabricWarning is not null)
                message += "\n\nFabric API 安装失败：" + fabricWarning;
            try
            {
                await MessageDialog.ShowAsync(root, "安装完成", message);
            }
            catch (Exception dialogEx)
            {
                AppLog.Error("install", "无法显示安装完成提示", dialogEx);
            }
            return true;
        }
        catch (Exception ex)
        {
            ProgressCenter.Finish(taskId, "安装失败：" + ex.Message, failed: true);
            ProgressCenter.CloseOverlay();
            await Task.Delay(90);
            AppLog.Error("install", "版本安装失败", ex);
            try
            {
                await MessageDialog.ShowAsync(root, "安装失败",
                    string.IsNullOrWhiteSpace(ex.Message) ? ex.GetType().Name : ex.Message);
            }
            catch (Exception dialogEx)
            {
                AppLog.Error("install", "无法显示安装失败提示", dialogEx);
            }
            return false;
        }
    }

    private static TextBlock SectionLabel(string text) => new()
    {
        Text = text,
        FontSize = 11.5,
        FontWeight = FontWeights.SemiBold,
        Foreground = (Brush)Application.Current.Resources["BrushTextSecondary"],
        Margin = new Thickness(0, 2, 0, 0)
    };

    private static TextBlock Caption(string text) => new()
    {
        Text = text,
        FontSize = 10.5,
        TextWrapping = TextWrapping.Wrap,
        Foreground = (Brush)Application.Current.Resources["BrushTextTertiary"]
    };
}
