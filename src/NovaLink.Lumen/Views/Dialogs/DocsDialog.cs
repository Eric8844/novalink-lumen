using System;
using System.Collections.Generic;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using NovaLink.Lumen.Controls;
using NovaLink.Lumen.Services;
using Windows.UI;

namespace NovaLink.Lumen.Views.Dialogs;

/// <summary>文档帮助界面的分档。</summary>
public enum DocsTab
{
    /// <summary>使用文档：怎么装、怎么用。</summary>
    Usage,

    /// <summary>开发文档：怎么写一个插件、怎么上架。</summary>
    Development
}

/// <summary>
/// 站内「插件文档」弹窗：使用文档 / 开发文档两档，内容来自 <see cref="DocsContent"/>。
/// 不打开任何外部文件，免得玩家看到仓库里的 markdown 路径。
/// </summary>
public static class DocsDialog
{
    public static GlassModal Show(DocsTab tab = DocsTab.Usage)
    {
        var modal = GlassModal.Show("插件文档", "「使用文档」讲怎么装、怎么用；「开发文档」讲怎么写一个插件、怎么上架。", 820);
        modal.HideSecondary();
        modal.PrimaryText = "关闭";

        var tabs = new Grid { ColumnSpacing = 8 };
        tabs.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        tabs.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        tabs.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        var usageButton = CreateTabButton("使用文档");
        var devButton = CreateTabButton("开发文档");
        tabs.Children.Add(usageButton);
        Grid.SetColumn(devButton, 1);
        tabs.Children.Add(devButton);

        var content = new StackPanel { Spacing = 11 };
        var scroll = new ScrollViewer
        {
            Content = content,
            MaxHeight = 420,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            Padding = new Thickness(0, 0, 12, 0)
        };

        var body = new StackPanel { Spacing = 12 };
        body.Children.Add(tabs);
        body.Children.Add(scroll);
        modal.Body.Children.Add(body);

        var current = tab;
        void Select(DocsTab next)
        {
            current = next;
            ApplyTabState(usageButton, next == DocsTab.Usage);
            ApplyTabState(devButton, next == DocsTab.Development);
            FillContent(content, next);
            ResetScroll(scroll);
        }

        usageButton.Click += (_, _) => Select(DocsTab.Usage);
        devButton.Click += (_, _) => Select(DocsTab.Development);
        // 两个分档按钮是 Show 之后才建的，补挂统一的悬停动效。
        Motion.Hover(usageButton);
        Motion.Hover(devButton);
        // 首次显示时 ScrollViewer 还没布局，ChangeView 会被忽略；布局完成后再归位一次。
        scroll.Loaded += (_, _) => ResetScroll(scroll);

        Select(current);
        return modal;
    }

    // 切档后回到顶部：布局没完成时 ChangeView 会被忽略，所以先 UpdateLayout，再排到布局之后补一次。
    private static void ResetScroll(ScrollViewer scroll)
    {
        scroll.UpdateLayout();
        scroll.ChangeView(null, 0, null, true);
        scroll.DispatcherQueue?.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () =>
        {
            scroll.UpdateLayout();
            scroll.ChangeView(null, 0, null, true);
        });
    }

    private static Button CreateTabButton(string text) => new()
    {
        Content = text,
        Height = 36,
        Padding = new Thickness(20, 0, 20, 0),
        CornerRadius = new CornerRadius(12),
        FontSize = 13,
        BorderThickness = new Thickness(1)
    };

    private static void ApplyTabState(Button button, bool selected)
    {
        // 选中态用当前主题强调色的淡底（不写死 #5EE6B5），浅色模式下同样有对比。
        button.Background = selected
            ? AccentTint(0x33)
            : new SolidColorBrush(Color.FromArgb(0x00, 0x00, 0x00, 0x00));
        button.BorderBrush = selected
            ? Res("BrushMint", ThemeManager.Accent)
            : Res("BrushBorderSubtle", ThemeManager.CardBorder);
        button.Foreground = selected
            ? Res("BrushText", ThemeManager.Text)
            : Res("BrushTextSecondary", ThemeManager.TextSecondary);
    }

    private static void FillContent(Panel target, DocsTab tab)
    {
        target.Children.Clear();
        var sections = tab == DocsTab.Development ? DocsContent.Development : DocsContent.Usage;
        var index = 0;
        foreach (var section in sections)
        {
            var title = new TextBlock
            {
                Text = section.Title,
                FontSize = 14.5,
                FontWeight = FontWeights.SemiBold,
                Foreground = Res("BrushText", ThemeManager.Text),
                Margin = new Thickness(0, 4, 0, 0)
            };
            target.Children.Add(title);
            Motion.RowIn(title, index++);

            foreach (var block in section.Blocks)
            {
                var element = BuildBlock(block);
                target.Children.Add(element);
                // 逐段淡入，切换分档时有一点顺序感，不做整页闪烁。
                if (element is FrameworkElement row) Motion.RowIn(row, index++);
            }
        }
    }

    private static UIElement BuildBlock(DocBlock block)
    {
        switch (block.Kind)
        {
            case DocBlockKind.Code:
                var codeBody = new TextBlock
                {
                    Text = block.Text,
                    FontFamily = new FontFamily("Consolas"),
                    FontSize = 12,
                    TextWrapping = TextWrapping.Wrap,
                    Foreground = Res("BrushText", ThemeManager.Text)
                };
                var codeGlass = new Grid();
                codeGlass.Children.Add(codeBody);
                codeGlass.Children.Add(GlassModal.TopHighlight(10, 1));
                return new Border
                {
                    Background = new SolidColorBrush(ThemeManager.ControlFill),
                    BorderBrush = Res("BrushBorderSubtle", ThemeManager.CardBorder),
                    BorderThickness = new Thickness(1),
                    CornerRadius = new CornerRadius(10),
                    Padding = new Thickness(12, 10, 12, 10),
                    Child = codeGlass
                };

            case DocBlockKind.Note:
                var noteBody = new TextBlock
                {
                    Text = block.Text,
                    FontSize = 12.5,
                    TextWrapping = TextWrapping.Wrap,
                    Foreground = Res("BrushText", ThemeManager.Text)
                };
                var noteGlass = new Grid();
                noteGlass.Children.Add(noteBody);
                noteGlass.Children.Add(GlassModal.TopHighlight(12, 1));
                return new Border
                {
                    Background = AccentTint(0x22),
                    BorderBrush = Res("BrushMint", ThemeManager.Accent),
                    BorderThickness = new Thickness(1),
                    CornerRadius = new CornerRadius(12),
                    Padding = new Thickness(14, 11, 14, 11),
                    Child = noteGlass
                };

            case DocBlockKind.Bullet:
                return new TextBlock
                {
                    Text = "·  " + block.Text,
                    FontSize = 12.5,
                    TextWrapping = TextWrapping.Wrap,
                    Margin = new Thickness(8, 0, 0, 0),
                    Foreground = Res("BrushTextSecondary", ThemeManager.TextSecondary)
                };

            default:
                return new TextBlock
                {
                    Text = block.Text,
                    FontSize = 12.5,
                    TextWrapping = TextWrapping.Wrap,
                    Foreground = Res("BrushTextSecondary", ThemeManager.TextSecondary)
                };
        }
    }

    /// <summary>强调色的半透明淡底：选中分档、提示块都用它，跟着主题强调色走。</summary>
    private static Brush AccentTint(byte alpha)
    {
        var accent = ThemeManager.Accent;
        return new SolidColorBrush(Color.FromArgb(alpha, accent.R, accent.G, accent.B));
    }

    private static Brush Res(string key, Color fallback)
    {
        try
        {
            if (Application.Current?.Resources is { } resources && resources.TryGetValue(key, out var value) && value is Brush brush)
                return brush;
        }
        catch (Exception ex)
        {
            AppLog.Warn("docs", "读取主题画刷失败：" + key + " " + ex.Message);
        }

        return new SolidColorBrush(fallback);
    }
}
