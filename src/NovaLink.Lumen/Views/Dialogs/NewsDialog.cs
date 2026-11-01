using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using NovaLink.Lumen.Controls;
using NovaLink.Lumen.Core.Models;
using NovaLink.Lumen.Services;
using System;
using System.Threading.Tasks;
using Windows.System;

namespace NovaLink.Lumen.Views.Dialogs;

/// <summary>Lumen 快讯详情：站内正文阅读；只有存在合法原网页链接时才显示「在浏览器打开」。</summary>
public static class NewsDialog
{
    public static async Task ShowAsync(XamlRoot root, NewsItem item)
    {
        var modal = GlassModal.Show(item.Category, FriendlyDate(item.Date), 620);
        var body = new StackPanel { Spacing = 14 };

        if (!string.IsNullOrWhiteSpace(item.ImageUrl))
        {
            // 配图完整显示：不固定高度、不用 UniformToFill 裁切，按原比例缩放，超高时才由 MaxHeight 限制。
            var photo = new Image
            {
                Stretch = Stretch.Uniform,
                MaxHeight = 240,
                MaxWidth = 520,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            };
            AutomationProperties.SetAutomationId(photo, "NewsImage");

            // 配图外壳也走液态玻璃配方：磨砂淡底 + 玻璃描边 + 顶部高光。
            var frameGlass = new Grid();
            frameGlass.Children.Add(photo);
            frameGlass.Children.Add(GlassModal.TopHighlight(16));
            var frame = new Border
            {
                CornerRadius = new CornerRadius(16),
                // 贴着图片本身，不要拉成一条空白玻璃横条。
                HorizontalAlignment = HorizontalAlignment.Center,
                Background = ThemeManager.Brush("BrushGlassFaint", ThemeManager.CardSurface),
                BorderBrush = ThemeManager.Brush("BrushGlassRim", ThemeManager.CardBorder),
                BorderThickness = new Thickness(1),
                Child = frameGlass
            };
            AutomationProperties.SetAutomationId(frame, "NewsImageFrame");
            AutomationProperties.SetName(frame, "快讯配图");
            ToolTipService.SetToolTip(frame, "点击放大查看");
            // Border 在 WinUI 里是密封类型、改不了鼠标指针，就用悬停压暗 + 文字提示表达可点击。
            frame.PointerEntered += (_, _) => frame.Opacity = 0.9;
            frame.PointerExited += (_, _) => frame.Opacity = 1;
            frame.Tapped += (_, _) => ImageViewer.Show(modal, item.ImageUrl);

            // 图片加载失败时维持原有兜底：不弹错误、也不留一块空白，直接把这张图收起来。
            photo.ImageFailed += (_, _) =>
            {
                AppLog.Warn("news", "快讯配图加载失败，已隐藏该图片");
                frame.Visibility = Visibility.Collapsed;
            };
            try { photo.Source = new BitmapImage(new Uri(item.ImageUrl)); }
            catch { frame.Visibility = Visibility.Collapsed; }

            body.Children.Add(frame);
        }

        var meta = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        meta.Children.Add(new TextBlock
        {
            Text = item.Category.ToUpperInvariant(),
            FontSize = 10,
            CharacterSpacing = 80,
            Foreground = ThemeManager.BrushAccent
        });
        if (!string.IsNullOrWhiteSpace(item.Version))
        {
            meta.Children.Add(new TextBlock
            {
                Text = "· " + item.Version,
                FontSize = 10,
                Foreground = ThemeManager.BrushTextTertiary
            });
        }
        body.Children.Add(meta);

        body.Children.Add(new TextBlock
        {
            Text = item.Title,
            FontSize = 25,
            FontWeight = FontWeights.SemiBold,
            TextWrapping = TextWrapping.Wrap,
            Foreground = ThemeManager.BrushText
        });

        // 正文状态行：读取中 / 读取失败的提示都写在这里，避免再开第二个弹窗。
        var status = new TextBlock
        {
            FontSize = 11.5,
            TextWrapping = TextWrapping.Wrap,
            Foreground = ThemeManager.BrushTextTertiary,
            Visibility = Visibility.Collapsed
        };
        // 正文区：先放摘要，站内正文读回来后替换成全文。
        var article = new TextBlock
        {
            Text = item.ShortText ?? "",
            FontSize = 13.5,
            LineHeight = 23,
            TextWrapping = TextWrapping.Wrap,
            IsTextSelectionEnabled = true,
            Foreground = ThemeManager.BrushTextSecondary
        };
        // 正文卡片：液态玻璃底 + 玻璃描边 + 顶部高光，和弹窗壳保持同一套质感。
        var articleGrid = new Grid { RowSpacing = 8 };
        articleGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        articleGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        articleGrid.Children.Add(status);
        Grid.SetRow(article, 1);
        articleGrid.Children.Add(article);
        var articleGlass = new Grid();
        articleGlass.Children.Add(articleGrid);
        articleGlass.Children.Add(GlassModal.TopHighlight(16));
        body.Children.Add(new Border
        {
            Padding = new Thickness(16, 14, 16, 14),
            CornerRadius = new CornerRadius(16),
            Background = ThemeManager.Brush("BrushGlassFaint", ThemeManager.CardSurface),
            BorderBrush = ThemeManager.Brush("BrushGlassRim", ThemeManager.CardBorder),
            BorderThickness = new Thickness(1),
            Child = articleGlass
        });

        // 内联提示：只在「没能唤起浏览器」时出现，绝不改成新弹窗。
        var hint = new TextBlock
        {
            FontSize = 11.5,
            TextWrapping = TextWrapping.Wrap,
            IsTextSelectionEnabled = true,
            Foreground = ThemeManager.BrushTextTertiary,
            Visibility = Visibility.Collapsed
        };
        body.Children.Add(hint);

        body.Children.Add(new TextBlock
        {
            Text = "内容来自 Minecraft 官方更新日志",
            FontSize = 10.5,
            Foreground = ThemeManager.BrushTextTertiary
        });

        // 整个正文区一起滚动：图片 + 标题 + 全文都在同一个滚动容器里，
        // 这样无论正文多长，弹窗都不会超出窗口、底部的「关闭」按钮始终可见。
        var bodyScroll = new ScrollViewer
        {
            MaxHeight = BodyMaxHeight(root),
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            Content = body
        };
        modal.Body.Children.Add(bodyScroll);
        modal.PrimaryText = "关闭";
        modal.PrimaryInvoked = () => { modal.Close(); return Task.CompletedTask; };

        var link = ResolveHttpLink(item.Link);
        if (link is { } target)
        {
            modal.SecondaryText = "在浏览器打开";
            modal.SecondaryInvoked = async () =>
            {
                bool opened;
                try { opened = await Launcher.LaunchUriAsync(target); }
                catch (Exception ex)
                {
                    AppLog.Warn("news", "唤起浏览器失败：" + ex.Message);
                    opened = false;
                }
                if (opened) { modal.Close(); return; }
                hint.Text = "没能唤起浏览器，可以手动复制链接：" + target.AbsoluteUri;
                hint.Visibility = Visibility.Visible;
            };
        }
        else
        {
            // 没有可用链接（官方更新日志条目只有 contentPath）：收起按钮，改为站内读正文。
            modal.HideSecondary();
            status.Text = "正在读取正文…";
            status.Visibility = Visibility.Visible;
            _ = LoadArticleAsync();
        }

        await modal.WaitAsync();

        // 站内读正文：后台加载，成功替换全文，失败给出提示并保留摘要；弹窗关闭后不再回写 UI。
        async Task LoadArticleAsync()
        {
            string? text;
            try { text = await LumenRuntime.News.GetArticleTextAsync(item); }
            catch (Exception ex)
            {
                AppLog.Warn("news", "读取快讯正文失败：" + ex.Message);
                text = null;
            }
            if (modal.IsClosed) return;
            if (!string.IsNullOrWhiteSpace(text))
            {
                article.Text = text;
                status.Visibility = Visibility.Collapsed;
            }
            else
            {
                status.Text = "这条快讯暂时只有摘要，稍后再试或到官网查看。";
                status.Visibility = Visibility.Visible;
            }
        }
    }

    /// <summary>正文滚动区的高度：跟随窗口高度变化，留出标题栏、页脚按钮与边距，避免弹窗超出窗口。</summary>
    private static double BodyMaxHeight(XamlRoot root)
    {
        var available = root.Size.Height - 300;
        if (double.IsNaN(available) || available < 200) available = 360;
        return Math.Min(available, 460);
    }

    /// <summary>只接受绝对的 http/https 链接；其余（空、相对路径、ms-appx 等）一律视为没有链接。</summary>
    private static Uri? ResolveHttpLink(string? link)
    {
        if (string.IsNullOrWhiteSpace(link)) return null;
        if (!Uri.TryCreate(link, UriKind.Absolute, out var uri)) return null;
        return uri.Scheme.Equals(Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase)
            || uri.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)
            ? uri
            : null;
    }

    private static string FriendlyDate(string date)
    {
        return DateTimeOffset.TryParse(date, out var parsed)
            ? parsed.ToLocalTime().ToString("yyyy年M月d日")
            : date;
    }
}
