using System;
using System.Diagnostics;
using System.IO;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using NovaLink.Lumen.Plugins;
using NovaLink.Lumen.Services;
using NovaLink.Lumen.Services.Plugins;
using NovaLink.Lumen.Services.Plugins.Dev;
using NovaLink.Lumen.Startup;
using NovaLink.Lumen.Views.Dialogs;
using Windows.Storage.Pickers;

namespace NovaLink.Lumen.Views.Pages;

/// <summary>
/// 设置页「插件」卡片里的开发者工具：选插件源码目录 → 打包成 .lumenplugin → 安装到插件目录。
/// 只在开发模式面板里出现；所有用户可见文案都是中文，构建原始输出只放进折叠的日志区。
/// </summary>
public sealed partial class SettingsPage
{
    private const string DevBuildLogArea = "plugins";
    private const int MaxDevBuildLogChars = 40000;
    private bool _devBuilding;

    /// <summary>打包产物目录：&lt;数据目录&gt;\plugin-builds。</summary>
    private static string DevBuildOutputDirectory()
        => Path.Combine(LumenRuntime.Paths.Root, "plugin-builds");

    private async void DevBuild_Click(object sender, RoutedEventArgs e)
    {
        if (_devBuilding) return;

        try
        {
            var picker = new FolderPicker { SuggestedStartLocation = PickerLocationId.DocumentsLibrary };
            // FolderPicker 要求 FileTypeFilter 非空，否则 PickSingleFolderAsync 会直接抛出。
            picker.FileTypeFilter.Add("*");

            var window = MainWindow.Current;
            if (window is null)
            {
                SetDevBuildStatus("启动器窗口还没准备好，请稍后再试。", true);
                return;
            }

            WinRT.Interop.InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(window));
            var folder = await picker.PickSingleFolderAsync();
            if (folder is null) return;

            await BuildAndInstallAsync(folder.Path);
        }
        catch (Exception ex)
        {
            AppLog.Error(DevBuildLogArea, "打开插件源码目录选择器失败。", ex);
            SetDevBuildStatus("无法打开目录选择器，请稍后再试。", true);
        }
    }

    private async Task BuildAndInstallAsync(string sourceDirectory)
    {
        _devBuilding = true;
        DevBuildButton.IsEnabled = false;
        DevBuildLogPanel.Visibility = Visibility.Collapsed;
        DevBuildLog.Text = "";
        SetDevBuildStatus("正在打包…（首次编译可能要 1~2 分钟）");

        try
        {
            var outputDirectory = DevBuildOutputDirectory();
            Directory.CreateDirectory(outputDirectory);

            var progress = new Progress<string>(line =>
            {
                var text = Shorten(line, 160);
                if (text.Length == 0) return;
                DispatcherQueue.TryEnqueue(() =>
                    SetDevBuildStatus(text));
            });

            AppLog.Info(DevBuildLogArea, "开始从源码目录打包插件：" + sourceDirectory);
            var result = await PluginDevBuilder.BuildAsync(sourceDirectory, outputDirectory, progress);

            if (!result.Ok)
            {
                ShowDevBuildLog(result.BuildLog);
                FinishBuildResult(false);
                SetDevBuildStatus(Shorten(result.Message, 200), true);
                await MessageDialog.ShowAsync(XamlRoot, "打包失败",
                    Shorten(result.Message, 200) + "（构建输出已显示在设置页下方）");
                return;
            }

            if (string.IsNullOrWhiteSpace(result.PackagePath) || !File.Exists(result.PackagePath))
            {
                ShowDevBuildLog(result.BuildLog);
                FinishBuildResult(true);
                SetDevBuildStatus("打包完成，但没有找到生成的插件包文件。", true);
                await MessageDialog.ShowAsync(XamlRoot, "打包结果不完整",
                    "构建器没有返回可安装的 .lumenplugin 文件，请查看构建输出或日志。");
                return;
            }

            AppLog.Info(DevBuildLogArea,
                "插件打包完成：" + result.PackagePath + "（用时 " + Math.Round(result.Duration.TotalSeconds, 1) + " 秒）");

            await InstallBuiltPackageAsync(result);
        }
        catch (Exception ex)
        {
            FinishBuildResult(false);
            AppLog.Error(DevBuildLogArea, "从源码目录打包插件失败。", ex);
            SetDevBuildStatus("打包时发生意外错误，请查看构建输出或日志。", true);
        }
        finally
        {
            _devBuilding = false;
            DevBuildButton.IsEnabled = true;
        }
    }

    /// <summary>把打包结果安装到插件目录；同名插件会先问用户是否删除旧版本。</summary>
    private async Task InstallBuiltPackageAsync(DevBuildResult result)
    {
        var install = PluginPackageInstaller.Install(result.PackagePath!, PluginsRootPath());
        if (!install.Ok && install.Message.Contains("已经安装过插件", StringComparison.Ordinal))
        {
            var replace = await ConfirmReplacePluginAsync();
            if (!replace)
            {
                FinishBuildResult(true);
                SetDevBuildStatus("已打包，但未安装：" + Shorten(install.Message, 160), true);
                return;
            }

            var pluginId = string.IsNullOrWhiteSpace(result.PluginId) ? install.PluginId : result.PluginId;
            if (string.IsNullOrWhiteSpace(pluginId))
            {
                FinishBuildResult(true);
                SetDevBuildStatus("已打包，但无法确定旧插件的目录名，请手动删除旧版本后重试。", true);
                return;
            }

            var replaced = true;
            try
            {
                var oldDirectory = PluginDirectoryFor(pluginId!);
                if (Directory.Exists(oldDirectory))
                    Directory.Delete(oldDirectory, true);
            }
            catch (Exception ex)
            {
                // 插件正在运行，DLL 被当前进程占用，删不掉 —— 这不是错误，
                // 改成把新包排进队列，下次启动（插件还没加载）时由启动器自己替换。
                replaced = false;
                AppLog.Info(DevBuildLogArea, "旧版本插件目录当前被占用，改为重启后替换：" + ex.Message);
            }

            if (!replaced)
            {
                install = PluginPackageInstaller.QueueReplace(result.PackagePath!, PluginsRootPath());
                if (!install.Ok)
                {
                    FinishBuildResult(true);
                    SetDevBuildStatus("已打包，但没法替换正在运行的插件：" + Shorten(install.Message, 160), true);
                    await MessageDialog.ShowAsync(XamlRoot, "无法替换插件", install.Message);
                    return;
                }

                var (queuedName, queuedVersion) = DescribeInstalledPlugin(install.PluginId);
                FinishBuildResult(true);
                SetDevBuildStatus("已打包，重启后替换：" + queuedName +
                                  (queuedVersion.Length == 0 ? "" : " " + queuedVersion) +
                                  "\n" + result.PackagePath, success: true);
                AppLog.Info(DevBuildLogArea, "开发者工具已把插件新版本排进替换队列：" + install.PluginId);
                await PromptRestartAsync(queuedName, "新版本已经排进队列，重启启动器时会自动替换成新版本。");
                return;
            }

            install = PluginPackageInstaller.Install(result.PackagePath!, PluginsRootPath());
        }

        if (!install.Ok)
        {
            FinishBuildResult(true);
            SetDevBuildStatus("已打包，但安装失败：" + Shorten(install.Message, 160), true);
            await MessageDialog.ShowAsync(XamlRoot, "无法安装插件", install.Message);
            return;
        }

        var (name, version) = DescribeInstalledPlugin(install.PluginId);
        FinishBuildResult(true);
        SetDevBuildStatus("已打包并安装：" + name + (version.Length == 0 ? "" : " " + version) +
                          "\n" + result.PackagePath, success: true);
        AppLog.Info(DevBuildLogArea, "开发者工具已安装插件：" + install.PluginId);
        await PromptRestartAsync(name);
    }

    private async Task<bool> ConfirmReplacePluginAsync()
    {
        var modal = GlassModal.Show("插件已存在", "插件目录里已经有同名插件，要删除旧版本并重新安装吗？", 460);
        var confirmed = false;
        modal.PrimaryText = "删除并重装";
        modal.SecondaryText = "取消";
        modal.PrimaryInvoked = async () => { confirmed = true; modal.Close(); await Task.CompletedTask; };
        modal.SecondaryInvoked = async () => { modal.Close(); await Task.CompletedTask; };
        await modal.WaitAsync();
        return confirmed;
    }

    private async Task PromptRestartAsync(string pluginName, string? body = null)
    {
        var modal = GlassModal.Show("已安装到启动器",
            body ?? "已安装插件「" + pluginName + "」。重启启动器后生效。", 460);
        var restart = false;
        modal.PrimaryText = "立即重启";
        modal.SecondaryText = "稍后";
        modal.PrimaryInvoked = async () => { restart = true; modal.Close(); await Task.CompletedTask; };
        modal.SecondaryInvoked = async () => { modal.Close(); await Task.CompletedTask; };
        await modal.WaitAsync();
        if (!restart) return;

        try
        {
            if (LauncherRestart.Restart(StartupOptions.Current.BuildArguments(false)))
            {
                AppLog.Info(DevBuildLogArea, "安装插件后正在重启启动器。");
                Application.Current.Exit();
                return;
            }
        }
        catch (Exception ex)
        {
            AppLog.Error(DevBuildLogArea, "安装插件后重启启动器失败。", ex);
        }

        await MessageDialog.ShowAsync(XamlRoot, "无法重启启动器",
            "插件已经安装好了，请手动关闭并重新打开启动器。");
    }

    /// <summary>按 id 计算已安装插件目录；id 不合法时抛出中文异常。</summary>
    private static string PluginDirectoryFor(string id)
    {
        if (!PluginPaths.IsValidId(id))
            throw new ArgumentException("插件 id 不合法：" + id, nameof(id));
        return Path.Combine(PluginsRootPath(), id);
    }

    /// <summary>读取刚安装插件的清单，拿到展示名称与版本；读不到时退回插件 id。</summary>
    private static (string Name, string Version) DescribeInstalledPlugin(string? pluginId)
    {
        var name = string.IsNullOrWhiteSpace(pluginId) ? "插件" : pluginId!;
        var version = "";
        try
        {
            if (string.IsNullOrWhiteSpace(pluginId)) return (name, version);
            var manifest = PluginManifestReader.Load(PluginDirectoryFor(pluginId!)).Manifest;
            if (manifest is null) return (name, version);

            if (!string.IsNullOrWhiteSpace(manifest.Name)) name = manifest.Name;
            version = manifest.Version?.Trim() ?? "";
            if (version.Length > 0 && !version.StartsWith("v", StringComparison.OrdinalIgnoreCase))
                version = "v" + version;
        }
        catch (Exception ex)
        {
            AppLog.Warn(DevBuildLogArea, "读取刚安装插件的清单失败：" + ex.Message);
        }

        return (name, version);
    }

    private async void DevBuildOpen_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var directory = DevBuildOutputDirectory();
            if (!Directory.Exists(directory))
            {
                await MessageDialog.ShowAsync(XamlRoot, "还没有构建产物",
                    "请先点「选择源码目录并打包…」打包一次，之后就能在这里打开产物目录。");
                return;
            }

            Process.Start(new ProcessStartInfo("explorer.exe", "\"" + directory + "\"") { UseShellExecute = true });
            AppLog.Info(DevBuildLogArea, "打开插件打包产物目录：" + directory);
        }
        catch (Exception ex)
        {
            AppLog.Error(DevBuildLogArea, "打开插件打包产物目录失败。", ex);
            await MessageDialog.ShowAsync(XamlRoot, "无法打开产物目录",
                "请手动在资源管理器里打开：" + DevBuildOutputDirectory());
        }
    }

    private void SetDevBuildStatus(string text, bool danger = false, bool success = false)
    {
        if (DevBuildStatus is null) return;
        DevBuildStatus.Text = text;
        DevBuildStatus.Foreground = ResourceBrush(success ? "BrushMint" : danger ? "BrushDanger" : "BrushTextSecondary");
        DevBuildStatus.Visibility = Visibility.Visible;
    }

    private void ShowDevBuildLog(string? log)
    {
        var text = log ?? "";
        if (text.Length > MaxDevBuildLogChars)
            text = text[..MaxDevBuildLogChars] + "\n……（输出过长，已截断）";
        DevBuildLog.Text = text.Length == 0 ? "（构建器没有输出内容）" : text;
        DevBuildLogPanel.Visibility = Visibility.Visible;
    }

    /// <summary>构建结束：刷新「打开产物目录」按钮；成功后隐藏旧日志，避免把上次失败输出留在界面上。</summary>
    private void FinishBuildResult(bool success)
    {
        if (success) DevBuildLogPanel.Visibility = Visibility.Collapsed;
        DevBuildOpenButton.IsEnabled = Directory.Exists(DevBuildOutputDirectory());
    }
}
