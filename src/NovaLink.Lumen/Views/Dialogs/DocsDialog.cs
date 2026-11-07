using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Documents;
using Microsoft.UI.Xaml.Media;
using NovaLink.Lumen.Controls;
using NovaLink.Lumen.Services;
using Windows.ApplicationModel.DataTransfer;
using Windows.UI;

namespace NovaLink.Lumen.Views.Dialogs;

/// <summary>插件文档的两个页签。</summary>
public enum DocsTab
{
    Usage,
    Development
}

/// <summary>
/// 插件文档弹窗：正文来自内嵌的 Markdown（使用文档 / 开发文档），这里只负责解析后的渲染。
/// 代码块是独立专栏：语言标签 + 复制按钮 + 横向滚动，正文可以全选复制。
/// </summary>
public static class DocsDialog
{
    /// <summary>行内标记：**粗体**、代码片段、[文字](链接)。代码片段用 \\u0060 写成反引号。</summary>
    private static readonly Regex InlinePattern = new(
        "\\*\\*(.+?)\\*\\*|\u0060([^\u0060]+)\u0060|\\[([^\\]]+)\\]\\(([^)]+)\\)",
        RegexOptions.Compiled);

    public static GlassModal Show(DocsTab tab = DocsTab.Usage)
    {
        if (MainWindow.Current is null)
            throw new InvalidOperationException("主窗口尚未准备好。");

        var modal = GlassModal.Show(
            "插件文档",
            "「使用文档」讲怎么装、怎么用；「开发文档」讲怎么写一个插件、怎么上架。",
            820);
        modal.HideSecondary();
        modal.PrimaryText = "关闭";

        var content = new StackPanel { Spacing = 11 };

        var tabs = new Grid { ColumnSpacing = 8 };
        tabs.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        tabs.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        tabs.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        var usageButton = CreateTabButton("使用文档");
        var devButton = CreateTabButton("开发文档");
        Grid.SetColumn(devButton, 1);
        tabs.Children.Add(usageButton);
        tabs.Children.Add(devButton);

        var scroll = new ScrollViewer
        {
            Content = content,
            MaxHeight = 460,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            Padding = new Thickness(0, 0, 12, 0)
        };

        var body = new StackPanel { Spacing = 12 };
        body.Children.Add(tabs);
        body.Children.Add(scroll);
        modal.Body.Children.Add(body);

        void Select(DocsTab next)
        {
            ApplyTabState(usageButton, next == DocsTab.Usage);
            ApplyTabState(devButton, next == DocsTab.Development);
            Fill(content, next == DocsTab.Usage ? DocsContent.Usage : DocsContent.Development);
            ResetScroll(scroll);
        }

        usageButton.Click += (_, _) => Select(DocsTab.Usage);
        devButton.Click += (_, _) => Select(DocsTab.Development);
        Motion.Hover(usageButton);
        Motion.Hover(devButton);
        scroll.Loaded += (_, _) => ResetScroll(scroll);

        Select(tab);
        return modal;
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
        button.Background = selected
            ? AccentTint(0x33)
            : new SolidColorBrush(Color.FromArgb(0, 0, 0, 0));
        button.BorderBrush = selected
            ? Res("BrushMint", ThemeManager.Accent)
            : Res("BrushBorderSubtle", ThemeManager.CardBorder);
        button.Foreground = selected
            ? Res("BrushText", ThemeManager.Text)
            : Res("BrushTextSecondary", ThemeManager.TextSecondary);
    }

    private static void ResetScroll(ScrollViewer scroll)
    {
        scroll.UpdateLayout();
        scroll.ChangeView(null, 0, null, true);
        scroll.DispatcherQueue?.TryEnqueue(DispatcherQueuePriority.Low, () =>
        {
            scroll.UpdateLayout();
            scroll.ChangeView(null, 0, null, true);
        });
    }

    private static void Fill(StackPanel content, IReadOnlyList<DocBlock> blocks)
    {
        content.Children.Clear();
        var index = 0;
        foreach (var block in blocks)
        {
            // 文档的一级标题就是弹窗标题，跳过避免重复。
            if (block.Kind == DocBlockKind.Heading1) continue;

            var element = BuildBlock(block);
            if (element is null) continue;
            content.Children.Add(element);
            if (element is FrameworkElement row) Motion.RowIn(row, index++);
        }
    }

    private static FrameworkElement? BuildBlock(DocBlock block)
    {
        switch (block.Kind)
        {
            case DocBlockKind.Heading2:
                return new TextBlock
                {
                    Text = StripInline(block.Text),
                    FontSize = 16,
                    FontWeight = FontWeights.SemiBold,
                    Foreground = Res("BrushText", ThemeManager.Text),
                    TextWrapping = TextWrapping.Wrap,
                    Margin = new Thickness(0, 14, 0, 2)
                };

            case DocBlockKind.Heading3:
                return new TextBlock
                {
                    Text = StripInline(block.Text),
                    FontSize = 13.5,
                    FontWeight = FontWeights.SemiBold,
                    Foreground = Res("BrushText", ThemeManager.Text),
                    TextWrapping = TextWrapping.Wrap,
                    Margin = new Thickness(0, 8, 0, 0)
                };

            case DocBlockKind.Bullet:
                return BuildBullet(block.Text, "·");

            case DocBlockKind.Numbered:
                return BuildBullet(block.Text, string.Empty);

            case DocBlockKind.Code:
                return BuildCodeBlock(block);

            case DocBlockKind.Note:
                return BuildNote(block.Text);

            case DocBlockKind.Table:
                return BuildTable(block.Text);

            case DocBlockKind.Rule:
                return new Border
                {
                    Height = 1,
                    Background = Res("BrushBorderSubtle", ThemeManager.CardBorder),
                    Margin = new Thickness(0, 6, 0, 6)
                };

            case DocBlockKind.Paragraph:
                var paragraph = new TextBlock
                {
                    FontSize = 12.5,
                    LineHeight = 20,
                    TextWrapping = TextWrapping.Wrap,
                    Foreground = Res("BrushTextSecondary", ThemeManager.TextSecondary),
                    IsTextSelectionEnabled = true
                };
                SetInline(paragraph, block.Text);
                return paragraph;

            default:
                return null;
        }
    }

    private static FrameworkElement BuildBullet(string text, string marker)
    {
        var grid = new Grid { ColumnSpacing = 8, Margin = new Thickness(8, 0, 0, 0) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        if (marker.Length > 0)
        {
            var bullet = new TextBlock
            {
                Text = marker,
                FontSize = 12.5,
                Foreground = Res("BrushTextTertiary", ThemeManager.TextTertiary),
                VerticalAlignment = VerticalAlignment.Top
            };
            grid.Children.Add(bullet);
        }

        var content = new TextBlock
        {
            FontSize = 12.5,
            LineHeight = 20,
            TextWrapping = TextWrapping.Wrap,
            Foreground = Res("BrushTextSecondary", ThemeManager.TextSecondary),
            IsTextSelectionEnabled = true
        };
        SetInline(content, text);
        Grid.SetColumn(content, 1);
        grid.Children.Add(content);
        return grid;
    }

    private static FrameworkElement BuildCodeBlock(DocBlock block)
    {
        var code = new TextBlock
        {
            Text = block.Text,
            FontFamily = new FontFamily("Consolas"),
            FontSize = 12,
            Foreground = Res("BrushText", ThemeManager.Text),
            TextWrapping = TextWrapping.NoWrap,
            IsTextSelectionEnabled = true
        };

        var scroller = new ScrollViewer
        {
            Content = code,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
            VerticalScrollBarVisibility = ScrollBarVisibility.Disabled,
            HorizontalScrollMode = ScrollMode.Enabled,
            VerticalScrollMode = ScrollMode.Disabled,
            Padding = new Thickness(0, 2, 0, 2)
        };

        var language = new TextBlock
        {
            Text = LanguageLabel(block.Language),
            FontSize = 11,
            Foreground = Res("BrushTextTertiary", ThemeManager.TextTertiary),
            VerticalAlignment = VerticalAlignment.Center
        };

        var copy = new Button
        {
            Content = "复制",
            FontSize = 11.5,
            Height = 26,
            Padding = new Thickness(12, 0, 12, 0),
            CornerRadius = new CornerRadius(13),
            BorderThickness = new Thickness(1),
            Background = new SolidColorBrush(Color.FromArgb(0, 0, 0, 0)),
            BorderBrush = Res("BrushBorderSubtle", ThemeManager.CardBorder),
            Foreground = Res("BrushTextSecondary", ThemeManager.TextSecondary),
            VerticalAlignment = VerticalAlignment.Center
        };
        Motion.Hover(copy);
        copy.Click += (_, _) => CopyCode(code.Text, copy);

        var header = new Grid { ColumnSpacing = 8 };
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        header.Children.Add(language);
        Grid.SetColumn(copy, 2);
        header.Children.Add(copy);

        var panel = new StackPanel { Spacing = 6 };
        panel.Children.Add(header);
        panel.Children.Add(scroller);

        return new Border
        {
            Background = new SolidColorBrush(ThemeManager.ControlFill),
            BorderBrush = Res("BrushBorderSubtle", ThemeManager.CardBorder),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(10),
            Padding = new Thickness(12, 10, 12, 10),
            Child = panel
        };
    }

    private static FrameworkElement BuildNote(string text)
    {
        var body = new TextBlock
        {
            FontSize = 12.5,
            LineHeight = 20,
            TextWrapping = TextWrapping.Wrap,
            Foreground = Res("BrushText", ThemeManager.Text),
            IsTextSelectionEnabled = true
        };
        SetInline(body, text);

        return new Border
        {
            Background = AccentTint(0x22),
            BorderBrush = Res("BrushMint", ThemeManager.Accent),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(12),
            Padding = new Thickness(14, 11, 14, 11),
            Child = body
        };
    }

    private static FrameworkElement BuildTable(string text)
    {
        var rows = text.Split('\n');
        var panel = new StackPanel { Spacing = 6 };

        for (var i = 0; i < rows.Length; i++)
        {
            var cells = rows[i].Split('\t');
            for (var c = 0; c < cells.Length; c++) cells[c] = StripInline(cells[c]);

            var line = new TextBlock
            {
                Text = string.Join("  ·  ", cells),
                FontSize = 12,
                LineHeight = 19,
                TextWrapping = TextWrapping.Wrap,
                Foreground = i == 0
                    ? Res("BrushText", ThemeManager.Text)
                    : Res("BrushTextSecondary", ThemeManager.TextSecondary),
                FontWeight = i == 0 ? FontWeights.SemiBold : FontWeights.Normal,
                IsTextSelectionEnabled = true
            };
            panel.Children.Add(line);

            if (i == 0 && rows.Length > 1)
            {
                panel.Children.Add(new Border
                {
                    Height = 1,
                    Background = Res("BrushBorderSubtle", ThemeManager.CardBorder),
                    Margin = new Thickness(0, 2, 0, 2)
                });
            }
        }

        return new Border
        {
            Background = new SolidColorBrush(ThemeManager.ControlFill),
            BorderBrush = Res("BrushBorderSubtle", ThemeManager.CardBorder),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(10),
            Padding = new Thickness(12, 10, 12, 10),
            Child = panel
        };
    }

    private static void CopyCode(string text, Button button)
    {
        try
        {
            var package = new DataPackage { RequestedOperation = DataPackageOperation.Copy };
            package.SetText(text);
            Clipboard.SetContent(package);
            button.Content = "已复制";

            var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1.4) };
            timer.Tick += (_, _) =>
            {
                timer.Stop();
                button.Content = "复制";
            };
            timer.Start();
        }
        catch (Exception ex)
        {
            AppLog.Warn("docs", "复制代码失败：" + ex.Message);
            button.Content = "复制失败";
        }
    }

    private static string LanguageLabel(string language) => language.ToLowerInvariant() switch
    {
        "json" => "JSON",
        "csharp" or "cs" => "C#",
        "xml" => "XML",
        "javascript" or "js" => "JavaScript",
        "powershell" or "ps1" => "PowerShell",
        "text" or "" => "文本",
        _ => language
    };

    private static void SetInline(TextBlock textBlock, string text)
    {
        var position = 0;
        foreach (Match match in InlinePattern.Matches(text))
        {
            if (match.Index > position)
                textBlock.Inlines.Add(new Run { Text = text[position..match.Index] });

            if (match.Groups[1].Success)
            {
                textBlock.Inlines.Add(new Run
                {
                    Text = match.Groups[1].Value,
                    FontWeight = FontWeights.SemiBold
                });
            }
            else if (match.Groups[2].Success)
            {
                textBlock.Inlines.Add(new Run
                {
                    Text = match.Groups[2].Value,
                    FontFamily = new FontFamily("Consolas"),
                    FontSize = 11.5
                });
            }
            else if (match.Groups[3].Success)
            {
                textBlock.Inlines.Add(new Run
                {
                    Text = match.Groups[3].Value,
                    Foreground = Res("BrushMint", ThemeManager.Accent)
                });
            }

            position = match.Index + match.Length;
        }

        if (position < text.Length)
            textBlock.Inlines.Add(new Run { Text = text[position..] });
    }

    private static string StripInline(string text) => InlinePattern.Replace(text, match =>
        match.Groups[1].Success ? match.Groups[1].Value
        : match.Groups[2].Success ? match.Groups[2].Value
        : match.Groups[3].Value);

    private static Brush AccentTint(byte alpha)
    {
        var accent = ThemeManager.Accent;
        return new SolidColorBrush(Color.FromArgb(alpha, accent.R, accent.G, accent.B));
    }

    private static Brush Res(string key, Color fallback)
    {
        try
        {
            if (Application.Current.Resources.TryGetValue(key, out var value) && value is Brush brush)
                return brush;
        }
        catch (Exception ex)
        {
            AppLog.Warn("docs", "读取主题画刷失败：" + key + " " + ex.Message);
        }
        return new SolidColorBrush(fallback);
    }
}
