using System;
using System.IO;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Documents;
using Microsoft.UI.Xaml.Media;
using NovaLink.Lumen.Controls;
using NovaLink.Lumen.Views;
using Windows.UI;

namespace NovaLink.Lumen.Views.Dialogs;

/// <summary>First-run welcome and update-complete screens, rebuilt from the Electron welcome layout.</summary>
public static class StartupDialog
{
    private const string WelcomeMarker = "welcome-glass-v2.done";

    public static bool ShowWelcomeIfNeeded(ShellPage shell)
    {
        var marker = Path.Combine(LumenRuntime.Paths.Root, WelcomeMarker);
        if (File.Exists(marker)) return false;

        var modal = GlassModal.Show("", null, 560);
        modal.HideHeader();
        modal.HideButtons();
        var body = new StackPanel { Spacing = 0, MaxWidth = 520 };

        var logoRow = new Grid { ColumnSpacing = 14 };
        logoRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        logoRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        var logo = new Border
        {
            Width = 64,
            Height = 64,
            CornerRadius = new CornerRadius(19),
            Background = new SolidColorBrush(Color.FromArgb(0x24, 0x7C, 0xEB, 0xD6)),
            BorderBrush = new SolidColorBrush(Color.FromArgb(0x2E, 0xFF, 0xFF, 0xFF)),
            BorderThickness = new Thickness(1),
            Child = new Image
            {
                Source = new Microsoft.UI.Xaml.Media.Imaging.BitmapImage(new Uri("ms-appx:///Assets/Images/lumen-logo.svg")),
                Stretch = Stretch.Uniform
            }
        };
        logoRow.Children.Add(logo);
        var logoCopy = new StackPanel { Spacing = 2, VerticalAlignment = VerticalAlignment.Center };
        logoCopy.Children.Add(new TextBlock
        {
            Text = "欢迎来到 Lumen",
            FontSize = 25,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            CharacterSpacing = -45,
            Foreground = Brush("BrushText")
        });
        logoCopy.Children.Add(new TextBlock
        {
            Text = "把每一个世界，折射成光。",
            FontSize = 11,
            Foreground = Brush("BrushTextSecondary")
        });
        Grid.SetColumn(logoCopy, 1);
        logoRow.Children.Add(logoCopy);
        body.Children.Add(logoRow);

        var copy = new TextBlock
        {
            Margin = new Thickness(0, 20, 0, 18),
            FontSize = 12,
            LineHeight = 20.5,
            TextWrapping = TextWrapping.Wrap,
            Foreground = Brush("BrushTextSecondary")
        };
        copy.Inlines.Add(new Run { Text = "Lumen 是 " });
        copy.Inlines.Add(new Run { Text = "NovaLink", FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, Foreground = Brush("BrushText") });
        copy.Inlines.Add(new Run { Text = " 系列的 Minecraft Java Edition 启动器。先创建一个本地身份，开始离线游玩；也可以连接 Microsoft 账户，同步正版皮肤与 Realms。" });
        body.Children.Add(copy);

        var local = Option("使用本地账户", "无需网络 · 3–16 位字母、数字或下划线", "\uE77B");
        local.Click += (_, _) =>
        {
            Done(marker);
            modal.Close();
            shell.Navigate("settings");
        };
        body.Children.Add(local);
        body.Children.Add(new Border { Height = 10 });
        var microsoft = Option("登录 Microsoft 账户", "正版档案 · 皮肤与披风 · Realms", "\uE8D7");
        microsoft.Click += (_, _) =>
        {
            Done(marker);
            modal.Close();
            shell.Navigate("settings");
        };
        body.Children.Add(microsoft);

        var note = new Border
        {
            Margin = new Thickness(0, 14, 0, 0),
            Padding = new Thickness(12, 10, 12, 10),
            CornerRadius = new CornerRadius(11),
            Background = new SolidColorBrush(Color.FromArgb(0x10, 0xFF, 0xFF, 0xFF)),
            BorderBrush = new SolidColorBrush(Color.FromArgb(0x1E, 0xFF, 0xFF, 0xFF)),
            BorderThickness = new Thickness(1)
        };
        var noteRow = new Grid { ColumnSpacing = 9 };
        noteRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        noteRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        noteRow.Children.Add(new FontIcon
        {
            Glyph = "\uE72E",
            FontSize = 13,
            Foreground = Brush("BrushMint"),
            VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(0, 2, 0, 0)
        });
        var noteText = new TextBlock
        {
            Text = "账户令牌仅保存在本机，并使用 Windows DPAPI 加密。Lumen 不会上传你的游戏数据。",
            FontSize = 10.5,
            LineHeight = 17,
            TextWrapping = TextWrapping.Wrap,
            Foreground = Brush("BrushTextTertiary")
        };
        Grid.SetColumn(noteText, 1);
        noteRow.Children.Add(noteText);
        note.Child = noteRow;
        body.Children.Add(note);

        var skip = new Button
        {
            Content = "稍后设置",
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 16, 0, 0),
            Height = 34,
            Padding = new Thickness(12, 0, 12, 0),
            CornerRadius = new CornerRadius(10),
            Background = new SolidColorBrush(Color.FromArgb(0, 0, 0, 0)),
            BorderThickness = new Thickness(0),
            Foreground = Brush("BrushTextSecondary"),
            FontSize = 12
        };
        skip.Click += (_, _) =>
        {
            Done(marker);
            modal.Close();
        };
        body.Children.Add(skip);

        modal.Body.Children.Add(body);
        return true;
    }

    public static void ShowUpdateComplete(string version)
    {
        var modal = GlassModal.Show("", null, 520);
        modal.HideHeader();
        modal.HideButtons();
        var body = new StackPanel
        {
            HorizontalAlignment = HorizontalAlignment.Center,
            Spacing = 0,
            MinWidth = 420
        };

        body.Children.Add(new Border
        {
            Width = 66,
            Height = 66,
            CornerRadius = new CornerRadius(20),
            HorizontalAlignment = HorizontalAlignment.Center,
            Background = new SolidColorBrush(Color.FromArgb(0x18, 0x7C, 0xEB, 0xD6)),
            BorderBrush = new SolidColorBrush(Color.FromArgb(0x3D, 0x8D, 0xEE, 0xFF)),
            BorderThickness = new Thickness(1),
            Child = new FontIcon
            {
                Glyph = "\uE734",
                FontSize = 26,
                Foreground = new SolidColorBrush(Color.FromArgb(0xFF, 0xA9, 0xF4, 0xFF)),
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            }
        });
        body.Children.Add(new TextBlock
        {
            Text = "一 切 就 绪",
            Margin = new Thickness(0, 18, 0, 0),
            FontSize = 9,
            CharacterSpacing = 180,
            Foreground = new SolidColorBrush(Color.FromArgb(0xA0, 0xA2, 0xE6, 0xFC)),
            HorizontalAlignment = HorizontalAlignment.Center
        });
        body.Children.Add(new TextBlock
        {
            Text = "更新完毕。",
            Margin = new Thickness(0, 8, 0, 0),
            FontSize = 34,
            FontWeight = Microsoft.UI.Text.FontWeights.Medium,
            CharacterSpacing = -35,
            Foreground = Brush("BrushText"),
            HorizontalAlignment = HorizontalAlignment.Center
        });
        body.Children.Add(new TextBlock
        {
            Text = "NovaLink Lumen " + version + " 已就绪，继续你的旅程。",
            Margin = new Thickness(0, 7, 0, 0),
            FontSize = 11,
            Foreground = new SolidColorBrush(Color.FromArgb(0x9E, 0xBE, 0xDB, 0xEB)),
            HorizontalAlignment = HorizontalAlignment.Center
        });
        var dots = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 5,
            Margin = new Thickness(0, 22, 0, 4),
            HorizontalAlignment = HorizontalAlignment.Center
        };
        for (var i = 0; i < 3; i++)
            dots.Children.Add(new Microsoft.UI.Xaml.Shapes.Ellipse
            {
                Width = 5,
                Height = 5,
                Fill = new SolidColorBrush(i switch
                {
                    1 => Color.FromArgb(0x8C, 0xB4, 0xB4, 0xFF),
                    2 => Color.FromArgb(0x73, 0xA0, 0xF5, 0xCD),
                    _ => Color.FromArgb(0x8C, 0x84, 0xE6, 0xFF)
                })
            });
        body.Children.Add(dots);
        body.Children.Add(new Button
        {
            Content = "开启新的 Lumen Minecraft 之旅",
            Margin = new Thickness(0, 16, 0, 0),
            Height = 40,
            Padding = new Thickness(20, 0, 20, 0),
            CornerRadius = new CornerRadius(12),
            HorizontalAlignment = HorizontalAlignment.Center,
            Background = new LinearGradientBrush
            {
                StartPoint = new Windows.Foundation.Point(0, 0),
                EndPoint = new Windows.Foundation.Point(1, 1),
                GradientStops =
                {
                    new GradientStop { Color = Color.FromArgb(0xFF, 0x7C, 0xEB, 0xD6), Offset = 0 },
                    new GradientStop { Color = Color.FromArgb(0xFF, 0xA9, 0x96, 0xFF), Offset = 1 }
                }
            },
            Foreground = new SolidColorBrush(Color.FromArgb(0xFF, 0x06, 0x10, 0x1C)),
            BorderThickness = new Thickness(0),
            FontSize = 12
        });
        ((Button)body.Children[^1]).Click += (_, _) => modal.Close();
        modal.Body.Children.Add(body);
    }

    private static Button Option(string title, string detail, string glyph)
    {
        var button = new Button
        {
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
            Height = 68,
            Padding = new Thickness(14, 0, 14, 0),
            CornerRadius = new CornerRadius(14),
            Background = new SolidColorBrush(Color.FromArgb(0x0A, 0xFF, 0xFF, 0xFF)),
            BorderBrush = new SolidColorBrush(Color.FromArgb(0x22, 0xFF, 0xFF, 0xFF)),
            BorderThickness = new Thickness(1)
        };
        var grid = new Grid { ColumnSpacing = 12 };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var icon = new Border
        {
            Width = 38,
            Height = 38,
            CornerRadius = new CornerRadius(11),
            Background = new SolidColorBrush(Color.FromArgb(0x18, 0x7C, 0xEB, 0xD6)),
            BorderBrush = new SolidColorBrush(Color.FromArgb(0x2E, 0x7C, 0xEB, 0xD6)),
            BorderThickness = new Thickness(1),
            Child = new FontIcon { Glyph = glyph, FontSize = 15, Foreground = Brush("BrushMint") }
        };
        grid.Children.Add(icon);
        var copy = new StackPanel { Spacing = 2, VerticalAlignment = VerticalAlignment.Center };
        copy.Children.Add(new TextBlock { Text = title, FontSize = 12.5, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, Foreground = Brush("BrushText") });
        copy.Children.Add(new TextBlock { Text = detail, FontSize = 9.5, Foreground = Brush("BrushTextTertiary") });
        Grid.SetColumn(copy, 1);
        grid.Children.Add(copy);
        var arrow = new FontIcon { Glyph = "\uE76C", FontSize = 11, Foreground = Brush("BrushTextTertiary"), VerticalAlignment = VerticalAlignment.Center };
        Grid.SetColumn(arrow, 2);
        grid.Children.Add(arrow);
        button.Content = grid;
        Motion.AttachButtons(button);
        return button;
    }

    private static void Done(string marker)
    {
        try { File.WriteAllText(marker, DateTimeOffset.Now.ToString("O")); } catch { }
    }

    private static Brush Brush(string key) => (Brush)Application.Current.Resources[key];
}
