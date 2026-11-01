using System;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.Graphics;
using Windows.UI;

namespace NovaLink.Lumen.Startup;

/// <summary>玩家在恢复对话框里能做的三件事。</summary>
public enum RecoveryChoice
{
    /// <summary>重新启动一次（不改变任何参数）。</summary>
    Retry,

    /// <summary>以安全模式重启（禁用全部插件）。</summary>
    SafeMode,

    /// <summary>退出启动器。</summary>
    Exit
}

/// <summary>
/// 启动失败时的恢复对话框。刻意不显示异常类型、堆栈与文件路径——
/// 玩家只看到"出了什么问题 + 可以怎么办"，细节全部进日志。
/// </summary>
public static class RecoveryDialog
{
    private const int DialogWidth = 560;
    private const int DialogHeight = 366;

    /// <summary>显示恢复对话框；即使 WinUI 本身不可用也会退回系统消息框。</summary>
    public static async Task<RecoveryChoice> ShowAsync(string title, string message, string? hint = null)
    {
        try
        {
            return await ShowWindowAsync(title, message, hint);
        }
        catch (Exception ex)
        {
            BootLog.Error("恢复对话框创建失败，退回系统消息框。", ex);
            return ShowNativeFallback(title, message, hint);
        }
    }

    private static Task<RecoveryChoice> ShowWindowAsync(string title, string message, string? hint)
    {
        var completion = new TaskCompletionSource<RecoveryChoice>(TaskCreationOptions.RunContinuationsAsynchronously);
        var window = new Window { Title = "NovaLink Lumen" };

        var panel = new StackPanel { Spacing = 12, Padding = new Thickness(30, 26, 30, 24) };

        panel.Children.Add(new TextBlock
        {
            Text = title,
            FontSize = 20,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            Foreground = new SolidColorBrush(Color.FromArgb(255, 234, 240, 250)),
            TextWrapping = TextWrapping.Wrap
        });

        panel.Children.Add(new TextBlock
        {
            Text = message,
            FontSize = 13.5,
            LineHeight = 21,
            Foreground = new SolidColorBrush(Color.FromArgb(255, 176, 188, 206)),
            TextWrapping = TextWrapping.Wrap
        });

        if (!string.IsNullOrWhiteSpace(hint))
        {
            panel.Children.Add(new TextBlock
            {
                Text = hint,
                FontSize = 12.5,
                LineHeight = 19,
                Foreground = new SolidColorBrush(Color.FromArgb(255, 138, 150, 168)),
                TextWrapping = TextWrapping.Wrap
            });
        }

        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 10,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 10, 0, 0)
        };

        var retry = CreateButton("重试", true);
        retry.Click += (_, _) => completion.TrySetResult(RecoveryChoice.Retry);

        var safe = CreateButton("以安全模式启动", false);
        safe.Click += (_, _) => completion.TrySetResult(RecoveryChoice.SafeMode);

        var exit = CreateButton("退出", false);
        exit.Click += (_, _) => completion.TrySetResult(RecoveryChoice.Exit);

        buttons.Children.Add(OpenLogsButton());
        buttons.Children.Add(retry);
        buttons.Children.Add(safe);
        buttons.Children.Add(exit);
        panel.Children.Add(buttons);

        window.Content = new Grid
        {
            RequestedTheme = ElementTheme.Dark,
            Background = new SolidColorBrush(Color.FromArgb(255, 15, 18, 24)),
            Children = { panel }
        };

        window.Closed += (_, _) => completion.TrySetResult(RecoveryChoice.Exit);
        window.Activate();
        TryResize(window);

        return completion.Task;
    }

    private static Button CreateButton(string text, bool primary)
    {
        var button = new Button
        {
            Content = text,
            Height = 40,
            MinWidth = 96,
            Padding = new Thickness(18, 0, 18, 0),
            CornerRadius = new CornerRadius(10),
            Background = new SolidColorBrush(primary
                ? Color.FromArgb(255, 92, 214, 178)
                : Color.FromArgb(255, 40, 46, 58)),
            Foreground = new SolidColorBrush(primary
                ? Color.FromArgb(255, 12, 22, 20)
                : Color.FromArgb(255, 206, 216, 230)),
            BorderThickness = new Thickness(0)
        };
        return button;
    }

    private static HyperlinkButton OpenLogsButton()
    {
        var link = new HyperlinkButton
        {
            Content = "打开日志文件夹",
            Height = 40,
            Padding = new Thickness(10, 0, 10, 0)
        };
        link.Click += (_, _) =>
        {
            try
            {
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(BootLog.LogDirectory)
                {
                    UseShellExecute = true
                });
            }
            catch (Exception ex)
            {
                BootLog.Error("打开日志目录失败。", ex);
            }
        };
        return link;
    }

    private static void TryResize(Window window)
    {
        try
        {
            var appWindow = window.AppWindow;
            appWindow.Resize(new SizeInt32(DialogWidth, DialogHeight));
            var area = DisplayArea.GetFromWindowId(appWindow.Id, DisplayAreaFallback.Primary);
            var work = area.WorkArea;
            appWindow.Move(new PointInt32(
                work.X + Math.Max(0, (work.Width - DialogWidth) / 2),
                work.Y + Math.Max(0, (work.Height - DialogHeight) / 2)));
        }
        catch (Exception ex)
        {
            BootLog.Warn("恢复对话框尺寸调整失败：" + ex.Message);
        }
    }

    private static RecoveryChoice ShowNativeFallback(string title, string message, string? hint)
    {
        const uint mbYesNoCancel = 0x00000003;
        const uint mbIconError = 0x00000010;
        const uint mbTopMost = 0x00040000;
        const uint mbSetForeground = 0x00010000;

        var text = title + "\r\n\r\n" + message;
        if (!string.IsNullOrWhiteSpace(hint)) text += "\r\n\r\n" + hint;
        text += "\r\n\r\n【是】重试　【否】以安全模式启动　【取消】退出";

        int result;
        try
        {
            result = MessageBoxW(IntPtr.Zero, text, "NovaLink Lumen",
                mbYesNoCancel | mbIconError | mbTopMost | mbSetForeground);
        }
        catch
        {
            return RecoveryChoice.Exit;
        }

        return result switch
        {
            6 => RecoveryChoice.Retry,   // IDYES
            7 => RecoveryChoice.SafeMode, // IDNO
            _ => RecoveryChoice.Exit
        };
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = false)]
    private static extern int MessageBoxW(IntPtr hWnd, string text, string caption, uint type);
}
