using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using System;
using System.Threading.Tasks;
using Windows.UI;

namespace NovaLink.Lumen.Views.Dialogs;

public static class TextPromptDialog
{
    public static async Task<string?> ShowAsync(XamlRoot root, string title, string placeholder, string initial, int maxLength)
    {
        var box = new TextBox
        {
            Text = initial,
            PlaceholderText = placeholder,
            MaxLength = maxLength,
            SelectionStart = initial.Length,
            Height = 42,
            CornerRadius = new CornerRadius(12),
            Background = new SolidColorBrush(Color.FromArgb(0x12, 0xFF, 0xFF, 0xFF)),
            BorderBrush = new SolidColorBrush(Color.FromArgb(0x24, 0xFF, 0xFF, 0xFF)),
            Foreground = new SolidColorBrush(Color.FromArgb(0xFF, 0xF3, 0xF7, 0xFE))
        };
        var modal = GlassModal.Show(title, null, 430);
        modal.Body.Children.Add(box);
        modal.PrimaryText = "保存";
        modal.SecondaryText = "取消";
        string? result = null;
        modal.PrimaryInvoked = () =>
        {
            result = box.Text.Trim();
            modal.Close();
            return Task.CompletedTask;
        };
        modal.Body.Loaded += (_, _) => box.Focus(FocusState.Programmatic);
        await modal.WaitAsync();
        return result;
    }
}
