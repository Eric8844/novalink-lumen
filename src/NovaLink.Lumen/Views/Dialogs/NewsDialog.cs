using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using NovaLink.Lumen.Core.Models;
using System;
using System.Threading.Tasks;
using Windows.System;

namespace NovaLink.Lumen.Views.Dialogs;

/// <summary>Lumen 快讯详情：站内阅读摘要，并保留跳转原网页的入口。</summary>
public static class NewsDialog
{
    public static async Task ShowAsync(XamlRoot root, NewsItem item)
    {
        var modal = GlassModal.Show(item.Category, FriendlyDate(item.Date), 620);
        var body = new StackPanel { Spacing = 14 };

        if (!string.IsNullOrWhiteSpace(item.ImageUrl))
        {
            var image = new Image { Stretch = Stretch.UniformToFill, Height = 206 };
            try { image.Source = new BitmapImage(new Uri(item.ImageUrl)); } catch { }
            body.Children.Add(new Border
            {
                Height = 206,
                CornerRadius = new CornerRadius(16),
                Background = (Brush)Application.Current.Resources["BrushGlassFaint"],
                Child = image
            });
        }

        var meta = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        meta.Children.Add(new TextBlock
        {
            Text = item.Category.ToUpperInvariant(),
            FontSize = 10,
            CharacterSpacing = 80,
            Foreground = (Brush)Application.Current.Resources["BrushMint"]
        });
        if (!string.IsNullOrWhiteSpace(item.Version))
        {
            meta.Children.Add(new TextBlock
            {
                Text = "· " + item.Version,
                FontSize = 10,
                Foreground = (Brush)Application.Current.Resources["BrushTextTertiary"]
            });
        }
        body.Children.Add(meta);

        body.Children.Add(new TextBlock
        {
            Text = item.Title,
            FontSize = 25,
            FontWeight = FontWeights.SemiBold,
            TextWrapping = TextWrapping.Wrap
        });

        var text = string.IsNullOrWhiteSpace(item.ShortText)
            ? "这条快讯暂时没有更多摘要，可以在浏览器中打开原网页查看完整内容。"
            : item.ShortText;
        body.Children.Add(new ScrollViewer
        {
            MaxHeight = 330,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            Content = new TextBlock
            {
                Text = text,
                FontSize = 13.5,
                LineHeight = 23,
                TextWrapping = TextWrapping.Wrap,
                Foreground = (Brush)Application.Current.Resources["BrushTextSecondary"]
            }
        });

        body.Children.Add(new TextBlock
        {
            Text = "内容来自 Minecraft 官方更新日志",
            FontSize = 10.5,
            Foreground = (Brush)Application.Current.Resources["BrushTextTertiary"]
        });

        modal.Body.Children.Add(body);
        modal.SecondaryText = "在浏览器打开";
        modal.PrimaryText = "关闭";
        modal.PrimaryInvoked = () => { modal.Close(); return Task.CompletedTask; };
        modal.SecondaryInvoked = async () =>
        {
            if (Uri.TryCreate(item.Link, UriKind.Absolute, out var uri))
            {
                await Launcher.LaunchUriAsync(uri);
                modal.Close();
            }
            else
            {
                await MessageDialog.ShowAsync(root, "无法打开", "这条快讯没有可用的原网页链接。");
            }
        };

        await modal.WaitAsync();
    }

    private static string FriendlyDate(string date)
    {
        return DateTimeOffset.TryParse(date, out var parsed)
            ? parsed.ToLocalTime().ToString("yyyy年M月d日")
            : date;
    }
}
