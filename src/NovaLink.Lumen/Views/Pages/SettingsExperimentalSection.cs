using System;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using NovaLink.Lumen.Services;
using NovaLink.Lumen.Startup;
using NovaLink.Lumen.Views.Dialogs;

namespace NovaLink.Lumen.Views.Pages;

/// <summary>
/// 设置页「实验性」分类：目前只有「启动器插件」一个开关。
/// 开关只写设置文件，真正生效要等下次启动 —— <see cref="LumenRuntime.PluginsEnabled"/> 在启动链里只判定一次，
/// 所以这里改完就提示重启，不改当前进程已经建好的插件系统。
/// </summary>
public sealed partial class SettingsPage
{
    private bool _syncExperimental;

    /// <summary>把开关初值、分类显隐与状态说明同步到当前设置。页面加载时调用一次。</summary>
    private void SyncExperimental()
    {
        _syncExperimental = true;
        ExperimentalPluginsSwitch.IsOn = LumenRuntime.Settings.Settings.ExperimentalPlugins;
        _syncExperimental = false;

        // 实验性功能关闭时「插件」分类整体不出现（分类按钮与面板都不给入口）。
        CategoryPlugins.Visibility = LumenRuntime.PluginsEnabled ? Visibility.Visible : Visibility.Collapsed;
        ExperimentalRestartBanner.Visibility = Visibility.Collapsed;
        UpdateExperimentalStateText();
    }

    private void ExperimentalPlugins_Toggled(object sender, RoutedEventArgs e)
    {
        if (_syncExperimental || !_ready) return;

        var enabled = ExperimentalPluginsSwitch.IsOn;
        LumenRuntime.Settings.Settings.ExperimentalPlugins = enabled;
        LumenRuntime.Settings.Save();
        AppLog.Info("plugins", "实验性功能「启动器插件」已" + (enabled ? "开启" : "关闭") + "，重启启动器后生效。");

        ExperimentalRestartBanner.Visibility = Visibility.Visible;
        UpdateExperimentalStateText();
    }

    private async void ExperimentalRestart_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            // 不强制安全模式：插件本来就默认关闭，用户主动重启只为了让新开关生效。
            if (LauncherRestart.Restart(StartupOptions.Current.BuildArguments(forceSafeMode: false)))
            {
                AppLog.Info("plugins", "实验性功能设置已保存，正在重启启动器。");
                Application.Current.Exit();
                return;
            }

            await MessageDialog.ShowAsync(XamlRoot, "无法重启启动器",
                "设置已经保存，请手动关闭启动器再重新打开。");
        }
        catch (Exception ex)
        {
            AppLog.Warn("plugins", "重启启动器失败：" + ex.Message);
            await MessageDialog.ShowAsync(XamlRoot, "无法重启启动器",
                "设置已经保存，请手动关闭启动器再重新打开。");
        }
    }

    /// <summary>说明「设置里存的值」和「这次启动实际生效的状态」的差别，避免用户以为开关没保存。</summary>
    private void UpdateExperimentalStateText()
    {
        var running = LumenRuntime.PluginsEnabled;
        var saved = LumenRuntime.Settings.Settings.ExperimentalPlugins;

        if (saved == running)
        {
            ExperimentalPluginsStateText.Text = running
                ? "当前这次启动已开启插件：设置里的「插件」页与发现页的「启动器插件」商店都可以用了。"
                : "当前这次启动没有加载插件：「插件」页与发现页的「启动器插件」商店都不显示，已安装的插件文件与设置仍保留在插件目录里。";
            return;
        }

        ExperimentalPluginsStateText.Text = saved
            ? "已保存为开启，但这次启动还没加载插件：重启启动器后才会出现「插件」页与插件商店。"
            : "已保存为关闭，但这次启动仍在加载插件：重启启动器后插件才会全部停用。";
    }
}
