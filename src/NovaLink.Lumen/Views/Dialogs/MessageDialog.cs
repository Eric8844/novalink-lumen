using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using System.Threading.Tasks;

namespace NovaLink.Lumen.Views.Dialogs;

public static class MessageDialog
{
    public static async Task ShowAsync(XamlRoot root, string title, string message)
    {
        var modal = GlassModal.Show(title, null, 430);
        modal.Body.Children.Add(new TextBlock
        {
            Text = message,
            TextWrapping = TextWrapping.Wrap,
            FontSize = 13,
            LineHeight = 21,
            Foreground = (Brush)Application.Current.Resources["BrushTextSecondary"]
        });
        modal.PrimaryText = "知道了";
        modal.HideSecondary();
        await modal.WaitAsync();
    }
}
