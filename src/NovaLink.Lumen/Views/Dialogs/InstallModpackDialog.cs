using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using NovaLink.Lumen.Services;
using Windows.Storage.Pickers;

namespace NovaLink.Lumen.Views.Dialogs;

public static class InstallModpackDialog
{
    public static async Task PickAndInstallAsync(XamlRoot root)
    {
        var picker = new FileOpenPicker();
        var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(MainWindow.Current);
        WinRT.Interop.InitializeWithWindow.Initialize(picker, hwnd);
        picker.FileTypeFilter.Add(".mrpack");
        picker.FileTypeFilter.Add(".zip");
        picker.SuggestedStartLocation = PickerLocationId.Downloads;
        var file = await picker.PickSingleFileAsync();
        if (file is null) return;
        await InstallAsync(root, file.Path);
    }

    public static async Task InstallAsync(XamlRoot root, string path)
    {
        if (GlassModal.Current is not null) return;
        var modal = GlassModal.Show("导入整合包", "支持 Modrinth .mrpack，自动准备加载器与 overrides。", 540);
        modal.HideSecondary();
        modal.HidePrimary();
        modal.SetCloseAllowed(false);
        var bar = new ProgressBar { Minimum = 0, Maximum = 100, Height = 6, IsIndeterminate = true };
        var stage = new TextBlock { Text = "正在读取整合包", Style = (Style)Application.Current.Resources["TextBody"] };
        var message = new TextBlock { Text = Path.GetFileName(path), Foreground = (Brush)Application.Current.Resources["BrushTextTertiary"], FontSize = 12, TextWrapping = TextWrapping.Wrap };
        modal.Body.Children.Add(bar);
        modal.Body.Children.Add(stage);
        modal.Body.Children.Add(message);

        var progress = new Progress<Core.Models.DownloadProgress>(p =>
        {
            bar.IsIndeterminate = false;
            bar.Value = Math.Clamp(p.Percent, 0, 100);
            stage.Text = p.Stage;
            message.Text = p.Message + (p.BytesPerSecond > 0 ? " · " + (p.BytesPerSecond / 1048576.0).ToString("0.0") + " MB/s" : "");
        });
        try
        {
            var result = await Task.Run(() => LumenRuntime.Modpacks.InstallAsync(path, progress));
            modal.Close();
            await MessageDialog.ShowAsync(root, "整合包安装完成", result.Name + " 已准备就绪。\n加载器：" + result.Loader + " · 内核 " + result.Kernel + "\n整合包文件位于独立目录，可在版本页启动。");
        }
        catch (Exception ex)
        {
            modal.Close();
            AppLog.Error("modpack", "整合包安装失败", ex);
            await MessageDialog.ShowAsync(root, "整合包安装失败", string.IsNullOrWhiteSpace(ex.Message) ? ex.GetType().Name : ex.Message);
        }
    }
}