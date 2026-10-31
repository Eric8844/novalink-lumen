using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.UI;

namespace NovaLink.Lumen.Views.Dialogs;

public static class DialogTheme
{
    public static void Apply(ContentDialog dialog)
    {
        var background = new SolidColorBrush(Color.FromArgb(0xF7, 0x0B, 0x12, 0x20));
        var foreground = new SolidColorBrush(Color.FromArgb(0xFF, 0xF3, 0xF7, 0xFE));
        var border = new SolidColorBrush(Color.FromArgb(0x33, 0xFF, 0xFF, 0xFF));
        dialog.RequestedTheme = ElementTheme.Dark;
        dialog.Background = background;
        dialog.Foreground = foreground;
        dialog.BorderBrush = border;
        dialog.BorderThickness = new Thickness(1);
        dialog.Resources["ContentDialogBackground"] = background;
        dialog.Resources["ContentDialogForeground"] = foreground;
        dialog.Resources["ContentDialogBorderBrush"] = border;
        dialog.Resources["ContentDialogTopOverlay"] = new SolidColorBrush(Color.FromArgb(0, 0, 0, 0));
        dialog.Resources["ContentDialogSeparatorBorderBrush"] = new SolidColorBrush(Color.FromArgb(0x1F, 0xFF, 0xFF, 0xFF));
    }
}
