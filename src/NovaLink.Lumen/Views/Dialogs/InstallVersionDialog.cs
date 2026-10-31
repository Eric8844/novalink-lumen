using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using NovaLink.Lumen.Core.Models;
using NovaLink.Lumen.Services;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Windows.UI;

namespace NovaLink.Lumen.Views.Dialogs;

public static class InstallVersionDialog
{
    public static async Task<bool> ShowAsync(XamlRoot root)
    {
        IReadOnlyList<ManifestVersion> manifest;
        try { manifest = await LumenRuntime.Manifest.GetAsync(); }
        catch (Exception ex)
        {
            await MessageDialog.ShowAsync(root, "无法读取版本清单", ex.Message);
            return false;
        }
        if (manifest.Count == 0)
        {
            await MessageDialog.ShowAsync(root, "无法读取版本清单", "请检查网络连接后重试。");
            return false;
        }

        var fieldBg = (Brush)Application.Current.Resources["BrushField"];
        var fieldBorder = (Brush)Application.Current.Resources["BrushFieldBorder"];
        var fieldText = (Brush)Application.Current.Resources["BrushText"];
        ComboBox MakeCombo() => new()
        {
            HorizontalAlignment = HorizontalAlignment.Stretch,
            MaxDropDownHeight = 300,
            Height = 42,
            CornerRadius = new CornerRadius(12),
            Background = fieldBg,
            BorderBrush = fieldBorder,
            Foreground = fieldText
        };

        var versionCombo = MakeCombo();
        var loaderCombo = MakeCombo();
        var loaderVersionCombo = MakeCombo();
        loaderVersionCombo.Visibility = Visibility.Collapsed;
        var nameBox = new TextBox
        {
            PlaceholderText = "显示名称（可选）",
            MaxLength = 40,
            Height = 42,
            CornerRadius = new CornerRadius(12),
            Background = fieldBg,
            BorderBrush = fieldBorder,
            Foreground = fieldText
        };
        var isolated = new CheckBox
        {
            Content = "使用独立游戏目录（隔离存档与模组）",
            IsChecked = LumenRuntime.Settings.Settings.DefaultIsolation
        };
        var snapshot = new ToggleSwitch { Header = "显示快照版本", IsOn = false };

        foreach (var loader in new[] { "原版", "Fabric", "Quilt", "Forge", "NeoForge" })
            loaderCombo.Items.Add(new ComboBoxItem { Content = loader, Tag = loader });
        loaderCombo.SelectedIndex = 0;

        void FillVersions()
        {
            versionCombo.Items.Clear();
            foreach (var item in manifest.Where(v => snapshot.IsOn || v.Type == "release"))
                versionCombo.Items.Add(new ComboBoxItem
                {
                    Content = item.Id + (item.Type == "release" ? "" : "  ·  " + item.Type),
                    Tag = item
                });
            if (versionCombo.Items.Count > 0) versionCombo.SelectedIndex = 0;
        }
        FillVersions();
        snapshot.Toggled += (_, _) => FillVersions();

        async Task ReloadLoaderVersionsAsync()
        {
            var loader = (loaderCombo.SelectedItem as ComboBoxItem)?.Tag as string ?? "原版";
            isolated.Visibility = Visibility.Visible;
            loaderVersionCombo.Visibility = loader == "原版" ? Visibility.Collapsed : Visibility.Visible;
            if (loader == "原版")
            {
                loaderVersionCombo.Items.Clear();
                return;
            }
            if (versionCombo.SelectedItem is not ComboBoxItem { Tag: ManifestVersion pick }) return;
            loaderVersionCombo.Items.Clear();
            loaderVersionCombo.Header = "正在读取 " + loader + " 版本…";
            loaderVersionCombo.IsEnabled = false;
            try
            {
                var versions = await LumenRuntime.Loaders.VersionsAsync(loader, pick.Id);
                loaderVersionCombo.Header = loader + " 版本";
                loaderVersionCombo.Items.Add(new ComboBoxItem { Content = "最新稳定版", Tag = "" });
                foreach (var version in versions.Take(40))
                    loaderVersionCombo.Items.Add(new ComboBoxItem { Content = version, Tag = version });
                loaderVersionCombo.SelectedIndex = 0;
                loaderVersionCombo.IsEnabled = true;
            }
            catch (Exception ex)
            {
                loaderVersionCombo.Header = "加载器版本读取失败";
                loaderVersionCombo.Items.Add(new ComboBoxItem { Content = "最新稳定版", Tag = "" });
                loaderVersionCombo.SelectedIndex = 0;
                AppLog.Warn("install", "读取加载器版本失败：" + ex.Message);
            }
        }

        loaderCombo.SelectionChanged += async (_, _) => await ReloadLoaderVersionsAsync();
        versionCombo.SelectionChanged += async (_, _) => await ReloadLoaderVersionsAsync();

        var modal = GlassModal.Show("安装新版本", "安装 Mojang 原版，或直接安装 Fabric / Quilt / Forge / NeoForge 加载器。", 500);
        modal.Body.Children.Add(snapshot);
        modal.Body.Children.Add(versionCombo);
        modal.Body.Children.Add(loaderCombo);
        modal.Body.Children.Add(loaderVersionCombo);
        modal.Body.Children.Add(nameBox);
        modal.Body.Children.Add(isolated);
        modal.PrimaryText = "开始安装";
        modal.SecondaryText = "取消";

        ManifestVersion? chosen = null;
        string loaderName = "原版";
        string loaderVersion = "";
        modal.PrimaryInvoked = () =>
        {
            if (versionCombo.SelectedItem is ComboBoxItem { Tag: ManifestVersion version })
            {
                chosen = version;
                loaderName = (loaderCombo.SelectedItem as ComboBoxItem)?.Tag as string ?? "原版";
                loaderVersion = (loaderVersionCombo.SelectedItem as ComboBoxItem)?.Tag as string ?? "";
                modal.Close();
            }
            else versionCombo.Header = "请选择要安装的版本";
            return Task.CompletedTask;
        };
        await modal.WaitAsync();
        if (chosen is null) return false;

        var title = loaderName == "原版" ? chosen.Id : chosen.Id + " · " + loaderName;
        var status = new TextBlock
        {
            Text = "准备下载 " + title + "…",
            FontSize = 12.5,
            TextWrapping = TextWrapping.Wrap,
            Foreground = (Brush)Application.Current.Resources["BrushMint"]
        };
        var bar = new ProgressBar { Minimum = 0, Maximum = 100, Value = 0, Height = 6, CornerRadius = new CornerRadius(3) };
        var stage = new TextBlock
        {
            Text = "正在连接资源服务器",
            FontSize = 11.5,
            TextWrapping = TextWrapping.Wrap,
            Foreground = (Brush)Application.Current.Resources["BrushTextSecondary"]
        };
        var progressModal = GlassModal.Show("正在安装 " + title, "下载完成后会自动校验资源文件。", 500);
        progressModal.Body.Children.Add(status);
        progressModal.Body.Children.Add(bar);
        progressModal.Body.Children.Add(stage);
        progressModal.HideButtons();
        progressModal.SetCloseAllowed(false);

        var progress = new Progress<DownloadProgress>(p =>
        {
            progressModal.Body.DispatcherQueue.TryEnqueue(() =>
            {
                bar.Value = Math.Clamp(p.Percent, 0, 100);
                var speed = p.BytesPerSecond > 0 ? " · " + (p.BytesPerSecond / 1048576.0).ToString("0.0") + " MB/s" : "";
                stage.Text = p.Message + speed;
                if (!string.IsNullOrWhiteSpace(p.Message)) status.Text = p.Message + speed;
            });
        });

        var displayName = nameBox.Text;
        var isIsolated = isolated.IsChecked == true;
        try
        {
            InstanceSummary result;
            if (loaderName == "原版")
            {
                result = await Task.Run(() => LumenRuntime.Installer.InstallVanillaAsync(
                    chosen.Id, displayName, isIsolated, progress));
            }
            else
            {
                var selectedLoader = loaderName;
                var selectedVersion = loaderVersion;
                result = await Task.Run(() => LumenRuntime.Loaders.InstallAsync(
                    selectedLoader, chosen.Id, selectedVersion, displayName, isIsolated, progress));
            }
            progressModal.Close();
            await MessageDialog.ShowAsync(root, "安装完成", result.Name + " 已准备就绪，可以在版本页启动。");
            return true;
        }
        catch (Exception ex)
        {
            progressModal.Close();
            AppLog.Error("install", "安装版本失败", ex);
            var message = string.IsNullOrWhiteSpace(ex.Message) ? ex.GetType().Name : ex.Message;
            await MessageDialog.ShowAsync(root, "安装失败", message);
            return false;
        }
    }
}