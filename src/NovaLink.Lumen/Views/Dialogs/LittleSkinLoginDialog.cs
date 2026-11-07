using System;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using NovaLink.Lumen.Core.Models;
using NovaLink.Lumen.Services;

namespace NovaLink.Lumen.Views.Dialogs;

/// <summary>
/// LittleSkin（Yggdrasil 皮肤站）登录弹窗：邮箱/用户名 + 密码。
/// 网络与协议细节都在 AccountService / LittleSkinAuthService 里，这里只负责界面与中文错误提示。
/// </summary>
public static class LittleSkinLoginDialog
{
    public static async Task<AccountPublic?> ShowAsync(XamlRoot root)
    {
        var modal = GlassModal.Show("登录 LittleSkin", "LittleSkin 是外置皮肤站。登录后可以用自己的角色和皮肤启动游戏。", 480);

        // 和「创建离线账户」等单行输入框保持同一套几何：固定 42 高、13 左右内边距、垂直居中。
        // 不要套 FieldBox 样式——那个样式带 Padding=14,10 与 MinHeight=44，和 Height=42 打架会把占位文字顶到上边缘。
        // 不要写死 Height：WinUI 的文本框按「内容 + Padding」决定内容区，
        // 固定高度会把占位文字压在顶部；这里用 MinHeight + 上下对称内边距，文字自然落在正中。
        var name = new TextBox
        {
            PlaceholderText = "LittleSkin 邮箱或用户名",
            MinHeight = 44,
            CornerRadius = new CornerRadius(12),
            Background = ThemeManager.Brush("BrushField", ThemeManager.ControlFill),
            BorderBrush = ThemeManager.Brush("BrushFieldBorder", ThemeManager.ControlBorder),
            Foreground = ThemeManager.BrushText,
            PlaceholderForeground = ThemeManager.BrushTextTertiary,
            BorderThickness = new Thickness(1),
            Padding = new Thickness(13, 11, 13, 11),
            VerticalContentAlignment = VerticalAlignment.Center
        };
        var password = new PasswordBox
        {
            PlaceholderText = "密码",
            MinHeight = 44,
            Background = ThemeManager.Brush("BrushField", ThemeManager.ControlFill),
            BorderBrush = ThemeManager.Brush("BrushFieldBorder", ThemeManager.ControlBorder),
            Foreground = ThemeManager.BrushText,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(12),
            Padding = new Thickness(13, 11, 13, 11),
            VerticalContentAlignment = VerticalAlignment.Center
        };
        var hint = new TextBlock
        {
            Text = "还没有账户？先到 littleskin.cn 注册，并在网站里创建一个 Minecraft 角色，再回来登录。",
            FontSize = 11.5,
            Foreground = ThemeManager.BrushTextTertiary,
            TextWrapping = TextWrapping.Wrap
        };
        modal.Body.Children.Add(name);
        modal.Body.Children.Add(password);
        modal.Body.Children.Add(hint);
        modal.PrimaryText = "登录";
        modal.SecondaryText = "去注册";

        // 回车直接提交，登录框的手感和其它表单一致。
        password.KeyDown += (_, e) =>
        {
            if (e.Key != Windows.System.VirtualKey.Enter) return;
            e.Handled = true;
            _ = SubmitAsync();
        };

        AccountPublic? result = null;
        async Task SubmitAsync()
        {
            var user = name.Text.Trim();
            var pass = password.Password;
            if (user.Length == 0)
            {
                ShowError("请填写 LittleSkin 邮箱或用户名。");
                name.Focus(FocusState.Programmatic);
                return;
            }
            if (pass.Length == 0)
            {
                ShowError("请填写密码。");
                password.Focus(FocusState.Programmatic);
                return;
            }

            modal.SetBusy(true);
            hint.Foreground = ThemeManager.BrushTextTertiary;
            hint.Text = "正在登录 LittleSkin…";
            try
            {
                result = await LumenRuntime.Accounts.LoginLittleSkinAsync(user, pass);
                modal.Close();
            }
            catch (Exception ex)
            {
                // 登录服务抛出的消息已经是中文人话，这里不再拼接异常类型或堆栈。
                ShowError(Human(ex));
                AppLog.Warn("account", "LittleSkin 登录失败：" + ex.Message);
            }
            finally
            {
                modal.SetBusy(false);
            }
        }

        void ShowError(string message)
        {
            hint.Text = message;
            hint.Foreground = ThemeManager.BrushDanger;
        }

        modal.PrimaryInvoked = SubmitAsync;
        modal.SecondaryInvoked = async () =>
        {
            try
            {
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("https://littleskin.cn") { UseShellExecute = true });
            }
            catch (Exception ex)
            {
                AppLog.Warn("account", "打开 LittleSkin 官网失败：" + ex.Message);
            }
            await Task.CompletedTask;
        };

        modal.Body.Loaded += (_, _) =>
        {
            try { name.Focus(FocusState.Programmatic); } catch { }
        };

        await modal.WaitAsync();
        return result;
    }

    private static string Human(Exception ex)
    {
        var message = ex.Message ?? "";
        return string.IsNullOrWhiteSpace(message) ? "LittleSkin 登录失败，请稍后再试。" : message;
    }
}
