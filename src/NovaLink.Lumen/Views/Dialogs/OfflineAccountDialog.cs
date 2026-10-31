using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using System.Threading.Tasks;
using Windows.UI;

namespace NovaLink.Lumen.Views.Dialogs;

public static class OfflineAccountDialog
{
    public static async Task<string?> ShowAsync(XamlRoot root)
    {
        var name = new TextBox
        {
            PlaceholderText = "3–16 位字母、数字或下划线",
            MaxLength = 16,
            Height = 42,
            CornerRadius = new CornerRadius(12),
            Background = new SolidColorBrush(Color.FromArgb(0x12, 0xFF, 0xFF, 0xFF)),
            BorderBrush = new SolidColorBrush(Color.FromArgb(0x24, 0xFF, 0xFF, 0xFF)),
            Foreground = new SolidColorBrush(Color.FromArgb(0xFF, 0xF3, 0xF7, 0xFE))
        };
        var modal = GlassModal.Show("创建离线账户", "离线账户不需要联网，用户名会用于生成稳定的离线 UUID。", 440);
        modal.Body.Children.Add(name);
        modal.Body.Children.Add(new TextBlock
        {
            Text = "支持英文、数字与下划线，创建后仍可在账户管理中重命名。",
            FontSize = 11,
            Foreground = (Brush)Application.Current.Resources["BrushTextTertiary"]
        });
        modal.PrimaryText = "创建";
        modal.SecondaryText = "取消";
        string? result = null;
        modal.PrimaryInvoked = () =>
        {
            var value = name.Text.Trim();
            if (value.Length < 3)
            {
                name.Header = "用户名至少需要 3 个字符";
                return Task.CompletedTask;
            }
            result = value;
            modal.Close();
            return Task.CompletedTask;
        };
        modal.Body.Loaded += (_, _) => name.Focus(FocusState.Programmatic);
        await modal.WaitAsync();
        return result;
    }
}
