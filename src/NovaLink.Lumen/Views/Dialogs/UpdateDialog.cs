using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using NovaLink.Lumen.Core.Models;
using NovaLink.Lumen.Services;
using NovaLink.Lumen.Services.Updates;
using System;
using System.Threading.Tasks;

namespace NovaLink.Lumen.Views.Dialogs;

public static class UpdateDialog
{
    public static async Task CheckAsync(bool silent)
    {
        LauncherUpdate update;
        try
        {
            update = await LumenRuntime.Updates.CheckAsync();
        }
        catch (Exception ex)
        {
            AppLog.Warn("update", "检查更新失败：" + ex.Message);
            if (!silent) await MessageDialog.ShowAsync(MainWindow.Current!.Content.XamlRoot, "检查更新失败", "无法连接 GitHub Releases，请稍后再试。");
            return;
        }

        if (!update.Available)
        {
            if (!silent)
                await MessageDialog.ShowAsync(MainWindow.Current!.Content.XamlRoot, "已是最新版本",
                    "当前版本 " + update.Current + " 已经是最新。后续版本会通过同一入口推送。");
            return;
        }

        if (string.IsNullOrWhiteSpace(update.DownloadUrl))
        {
            if (!silent)
                await MessageDialog.ShowAsync(MainWindow.Current!.Content.XamlRoot, "发现新版本 " + update.Latest,
                    "已发布 " + update.Latest + "，但暂未上传 Windows 安装包。请稍后重试。");
            return;
        }

        var modal = GlassModal.Show("发现新版本 " + update.Latest, "当前 " + update.Current + " · GitHub Releases", 560);
        var notes = new TextBlock
        {
            Text = Trim(update.Notes),
            FontSize = 12,
            LineHeight = 20,
            TextWrapping = TextWrapping.Wrap,
            MaxHeight = 240,
            Foreground = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["BrushTextSecondary"]
        };
        modal.Body.Children.Add(new ScrollViewer { Content = notes, MaxHeight = 240, VerticalScrollBarVisibility = ScrollBarVisibility.Auto });
        modal.PrimaryText = "下载并安装";
        modal.SecondaryText = "稍后";
        modal.PrimaryInvoked = async () =>
        {
            modal.Close();
            await DownloadAndInstallAsync(update);
        };
    }

    private static async Task DownloadAndInstallAsync(LauncherUpdate update)
    {
        var progressModal = GlassModal.Show("正在下载更新", "下载完成后会自动关闭启动器并安装。", 480);
        progressModal.HideButtons();
        progressModal.SetCloseAllowed(false);
        var bar = new ProgressBar { Height = 4, CornerRadius = new CornerRadius(2), Minimum = 0, Maximum = 100 };
        var status = new TextBlock { FontSize = 11.5, Text = "准备下载…", Foreground = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["BrushTextTertiary"] };
        progressModal.Body.Children.Add(bar);
        progressModal.Body.Children.Add(status);

        try
        {
            var progress = new Progress<DownloadProgress>(p =>
            {
                bar.Value = Math.Clamp(p.Percent, 0, 100);
                status.Text = p.Message + (p.BytesPerSecond > 0 ? "  ·  " + (p.BytesPerSecond / 1048576.0).ToString("F1") + " MB/s" : "");
            });
            var path = await LumenRuntime.Updates.DownloadAsync(update, progress);
            progressModal.Close();
            try { System.IO.File.WriteAllText(System.IO.Path.Combine(LumenRuntime.Paths.Root, "update-complete.txt"), update.Latest); } catch { }
            UpdateService.RunInstaller(path);
            Application.Current.Exit();
        }
        catch (Exception ex)
        {
            progressModal.Close();
            AppLog.Warn("update", "下载更新失败：" + ex.Message);
            await MessageDialog.ShowAsync(MainWindow.Current!.Content.XamlRoot, "更新失败", ex.Message);
        }
    }

    private static string Trim(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return "包含稳定性与界面改进。";
        var value = text.Replace("\r", "");
        return value.Length > 900 ? value[..900] + "…" : value;
    }
}
