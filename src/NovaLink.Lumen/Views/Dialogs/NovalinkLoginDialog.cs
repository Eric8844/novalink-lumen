using System;
using System.Threading.Tasks;
using Microsoft.UI;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using NovaLink.Lumen.Core.Models;
using NovaLink.Lumen.Services;
using NovaLink.Lumen.Services.Http;
using Windows.UI;

namespace NovaLink.Lumen.Views.Dialogs;

public static class NovalinkLoginDialog
{
    public static async Task<AccountPublic?> ShowAsync(XamlRoot root, bool register = false)
    {
        var modal = GlassModal.Show(register ? "注册 NovaLink" : "登录 NovaLink", "NovaLink 账户可以使用论坛、动态与云端资料。", 480);
        // 几何与「创建离线账户」一致：42 高 + 13 左右内边距 + 垂直居中（FieldBox 的 Padding/MinHeight 会顶偏占位文字）。
        // 不写死 Height：靠 MinHeight + 上下对称内边距让占位文字垂直居中（固定高度会把它压在顶部）。
        var name = new TextBox
        {
            PlaceholderText = "NovaLink 用户名",
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
            Background = new SolidColorBrush(Color.FromArgb(0x12, 0xFF, 0xFF, 0xFF)),
            BorderBrush = new SolidColorBrush(Color.FromArgb(0x24, 0xFF, 0xFF, 0xFF)),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(12),
            Padding = new Thickness(13, 11, 13, 11),
            VerticalContentAlignment = VerticalAlignment.Center
        };
        var hint = new TextBlock
        {
            Text = register ? "注册后会直接登录并创建本地 NovaLink 账户。" : "还没有账户？点击下方注册。",
            FontSize = 11.5,
            Foreground = (Brush)Application.Current.Resources["BrushTextTertiary"],
            TextWrapping = TextWrapping.Wrap
        };
        modal.Body.Children.Add(name);
        modal.Body.Children.Add(password);
        modal.Body.Children.Add(hint);
        modal.PrimaryText = register ? "注册并登录" : "登录";
        modal.SecondaryText = register ? "已有账户" : "注册账户";

        AccountPublic? result = null;
        var isRegister = register;
        modal.SecondaryInvoked = async () =>
        {
            isRegister = !isRegister;
            modal.PrimaryText = isRegister ? "注册并登录" : "登录";
            modal.SecondaryText = isRegister ? "已有账户" : "注册账户";
            hint.Text = isRegister ? "注册后会直接登录并创建本地 NovaLink 账户。" : "还没有账户？点击下方注册。";
            hint.Foreground = (Brush)Application.Current.Resources["BrushTextTertiary"];
            await Task.CompletedTask;
        };

        modal.PrimaryInvoked = async () =>
        {
            var user = name.Text.Trim();
            var pass = password.Password;
            if (user.Length < 2 || pass.Length < 4)
            {
                hint.Text = "用户名至少 2 个字符，密码至少 4 个字符。";
                hint.Foreground = (Brush)Application.Current.Resources["BrushMint"];
                return;
            }
            modal.SetBusy(true);
            try
            {
                result = isRegister
                    ? await LumenRuntime.Accounts.RegisterNovaLinkAsync(user, pass)
                    : await LumenRuntime.Accounts.LoginNovaLinkAsync(user, pass);
                modal.Close();
            }
            catch (Exception ex)
            {
                hint.Text = Human(ex);
                hint.Foreground = (Brush)Application.Current.Resources["BrushMint"];
                AppLog.Warn("account", "NovaLink 登录失败：" + ex);
            }
            finally
            {
                modal.SetBusy(false);
            }
        };

        await modal.WaitAsync();
        return result;
    }

    private static string Human(Exception ex)
        => ex is ApiException api ? api.Message : string.IsNullOrWhiteSpace(ex.Message) ? ex.GetType().Name : ex.Message;
}
