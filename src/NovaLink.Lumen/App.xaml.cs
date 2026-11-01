using Microsoft.UI.Xaml;
using NovaLink.Lumen.Services;
using NovaLink.Lumen.Startup;
using NovaLink.Lumen.Views.Dialogs;
using System;
using System.Threading.Tasks;

namespace NovaLink.Lumen;

public partial class App : Application
{
    private Window? _window;

    public App()
    {
        InitializeComponent();
        UnhandledException += OnUnhandledException;
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        var options = StartupOptions.FromEnvironment();
        StartupOptions.Initialize(options);
        options.ApplyDataRootEnvironment();
        BootState.Reset();
        BootLog.Configure(options.DataRoot ??
            System.IO.Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "NovaLink Lumen", "LauncherData"));

        BootLog.Info("启动参数：" + DescribeOptions(options));

        // 不阻塞 OnLaunched：恢复对话框需要消息泵，异常处理走 async 续体。
        _ = LaunchAsync(options);
    }

    private async Task LaunchAsync(StartupOptions options)
    {
        try
        {
            await LumenRuntime.InitializeAsync(options);
        }
        catch (Exception ex)
        {
            StartupFailure.Log("初始化运行时", ex);
            await HandleStartupFailureAsync(ex, options, "初始化运行时");
            return;
        }

        // --restore-data-backup：数据已经换回备份里的版本，必须重启一次才能按新数据跑。
        if (LumenRuntime.RestorePerformed)
        {
            await HandleRestoreCompletedAsync(options);
            return;
        }

        try
        {
            var settings = LumenRuntime.Settings.Settings;
            ThemeManager.Initialize(settings.Theme, settings.Accent);
            _window = new MainWindow();
            _window.Closed += OnMainWindowClosed;
            _window.Activate();
        }
        catch (Exception ex)
        {
            StartupFailure.Log("创建主窗口", ex);
            await HandleStartupFailureAsync(ex, options, "创建主窗口");
            return;
        }

        // 主窗口已经起来了：看门狗归零、插件可以开始收事件。
        LumenRuntime.MarkPluginsHealthy();
        LumenRuntime.DispatchPluginEvent(NovaLink.Lumen.Core.Plugins.PluginEvent.AppStartup);

        _ = ShowStartupNoticesAsync();
    }

    /// <summary>数据恢复完成：只给一句话 + 重启，绝不在同一次启动里继续跑 UI。</summary>
    private async Task HandleRestoreCompletedAsync(StartupOptions options)
    {
        BootLog.Info("数据恢复完成，等待用户重启。");
        var backup = LumenRuntime.Upgrade?.BackupRoot;
        var message = "已经把数据换回备份里的版本。" +
                      (string.IsNullOrWhiteSpace(backup) ? "" : " 备份位置：" + backup) +
                      " 现在需要重新启动一次。";
        try
        {
            await RecoveryDialog.ShowAsync("数据已恢复", message, "原来的数据还留着，没有删除。");
        }
        catch (Exception ex)
        {
            BootLog.Error("显示恢复完成提示失败。", ex);
        }

        if (!LauncherRestart.Restart(options.BuildArguments(forceSafeMode: options.SafeMode))) Exit();
    }

    private void OnMainWindowClosed(object sender, WindowEventArgs args)
    {
        try { LumenRuntime.ShutdownPlugins(); }
        catch (Exception ex) { BootLog.Error("关闭插件时出错。", ex); }
    }

    private async Task HandleStartupFailureAsync(Exception exception, StartupOptions options, string stage)
    {
        var failure = StartupFailure.Describe(exception);
        while (true)
        {
            var choice = await RecoveryDialog.ShowAsync(failure.Title, failure.Message, failure.Hint);

            if (choice == RecoveryChoice.Retry)
            {
                BootLog.Info("用户选择重试（阶段：" + stage + "）。");
                if (LauncherRestart.Restart(options.BuildArguments(forceSafeMode: false))) Exit();
                else await ShowRestartFailedAsync();
                return;
            }

            if (choice == RecoveryChoice.SafeMode)
            {
                BootLog.Info("用户选择以安全模式重启（阶段：" + stage + "）。");
                var safeArguments = options.BuildArguments(forceSafeMode: true);
                if (!safeArguments.Contains(StartupOptions.FlagSafeMode, StringComparison.OrdinalIgnoreCase))
                    safeArguments = (safeArguments + " " + StartupOptions.FlagSafeMode).Trim();

                if (LauncherRestart.Restart(safeArguments)) Exit();
                else await ShowRestartFailedAsync();
                return;
            }

            BootLog.Info("用户选择退出（阶段：" + stage + "）。");
            Exit();
            return;
        }
    }

    private static async Task ShowRestartFailedAsync()
    {
        await RecoveryDialog.ShowAsync(
            "无法重新启动",
            "启动器没能启动新的进程，可能需要你手动重新打开一次。",
            "日志已经记录在数据目录的 logs 文件夹里。");
    }

    /// <summary>窗口起来之后再提示：安全模式、升级备份、以及被隔离的插件。</summary>
    private async Task ShowStartupNoticesAsync()
    {
        await Task.Delay(700);

        if (StartupOptions.Current.PluginsDisabled || BootState.SafeModeReason is not null)
        {
            ShowSafeModeNotice();
            return;
        }

        if (BootState.UpgradeBackupCreated && BootState.UpgradeBackupPath is not null)
        {
            ShowBackupNotice();
        }

        if (BootState.Notices.Count > 0)
        {
            ShowPlainNotice("提示", string.Join(Environment.NewLine, BootState.Notices));
        }
    }

    private void ShowSafeModeNotice()
    {
        try
        {
            var reason = BootState.SafeModeReason ?? (StartupOptions.Current.SafeMode
                ? "你以安全模式启动了启动器。"
                : "本次启动已禁用插件。");

            var modal = GlassModal.Show("安全模式", reason + " 所有插件都没有加载，其余功能正常。");
            modal.PrimaryText = "关闭插件再试一次";
            modal.SecondaryText = "就这样用";
            modal.PrimaryInvoked = () =>
            {
                modal.Close();
                if (LauncherRestart.Restart(StartupOptions.Current.BuildArguments(forceSafeMode: false))) Exit();
                return Task.CompletedTask;
            };
            modal.SecondaryInvoked = () =>
            {
                modal.Close();
                return Task.CompletedTask;
            };
        }
        catch (Exception ex)
        {
            BootLog.Error("显示安全模式提示失败。", ex);
        }
    }

    private void ShowBackupNotice()
    {
        try
        {
            var modal = GlassModal.Show(
                "已自动备份旧数据",
                "升级前已把 " + (BootState.UpgradeBackupPath ?? "旧数据") + " 完整复制了一份备份，放在数据目录旁边。");
            modal.PrimaryText = "知道了";
            modal.HideSecondary();
            modal.PrimaryInvoked = () =>
            {
                modal.Close();
                return Task.CompletedTask;
            };
        }
        catch (Exception ex)
        {
            BootLog.Error("显示备份提示失败。", ex);
        }
    }

    private void ShowPlainNotice(string title, string message)
    {
        try
        {
            var modal = GlassModal.Show(title, message);
            modal.PrimaryText = "知道了";
            modal.HideSecondary();
            modal.PrimaryInvoked = () =>
            {
                modal.Close();
                return Task.CompletedTask;
            };
        }
        catch (Exception ex)
        {
            BootLog.Error("显示提示失败。", ex);
        }
    }

    private void OnUnhandledException(object sender, Microsoft.UI.Xaml.UnhandledExceptionEventArgs e)
    {
        try { AppLog.Error("app", "未处理异常", e.Exception); } catch { }
        BootLog.Error("未处理异常（界面线程）。", e.Exception);
    }

    private static string DescribeOptions(StartupOptions options)
    {
        var parts = new System.Collections.Generic.List<string>();
        if (options.SafeMode) parts.Add("safe-mode");
        if (options.NoPlugins) parts.Add("no-plugins");
        if (options.DevMode) parts.Add("dev-mode");
        if (options.RestoreDataBackup) parts.Add("restore-data-backup");
        if (options.BackupIncludeCache) parts.Add("backup-include-cache");
        if (options.DataRoot is not null) parts.Add("data-root=" + options.DataRoot);
        return parts.Count == 0 ? "(默认)" : string.Join(' ', parts);
    }
}
