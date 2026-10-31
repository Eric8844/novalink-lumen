using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using System;
using Windows.Foundation;
using Windows.UI;

namespace NovaLink.Lumen.Controls;

/// <summary>Lightweight shimmer placeholders used while Modrinth content is loading.</summary>
public static class Skeleton
{
    private static readonly Color BoneColor = Color.FromArgb(0x14, 0xFF, 0xFF, 0xFF);
    private static readonly Color BoneStrong = Color.FromArgb(0x20, 0xFF, 0xFF, 0xFF);

    public static Border Bone(double width, double height, double radius = 7, int delayMs = 0,
        bool strong = false)
    {
        var shine = new Border
        {
            Width = 150,
            HorizontalAlignment = HorizontalAlignment.Left,
            Background = new LinearGradientBrush
            {
                StartPoint = new Point(0, 0.5),
                EndPoint = new Point(1, 0.5),
                GradientStops =
                {
                    new GradientStop { Color = Color.FromArgb(0x00, 0xFF, 0xFF, 0xFF), Offset = 0 },
                    new GradientStop { Color = Color.FromArgb(0x2E, 0xFF, 0xFF, 0xFF), Offset = 0.45 },
                    new GradientStop { Color = Color.FromArgb(0x00, 0xFF, 0xFF, 0xFF), Offset = 1 }
                }
            },
            RenderTransform = new TranslateTransform { X = -180 }
        };

        var shell = new Border
        {
            Width = width,
            Height = height,
            CornerRadius = new CornerRadius(radius),
            Background = new SolidColorBrush(strong ? BoneStrong : BoneColor),
            Child = shine,
            Clip = new RectangleGeometry { Rect = new Rect(0, 0, width, height) }
        };

        shell.Loaded += (_, _) =>
        {
            if (shine.RenderTransform is not TranslateTransform transform) return;
            var shimmer = new DoubleAnimation
            {
                From = -180,
                To = width + 180,
                Duration = new Duration(TimeSpan.FromMilliseconds(1250)),
                BeginTime = TimeSpan.FromMilliseconds(delayMs),
                RepeatBehavior = RepeatBehavior.Forever,
                EnableDependentAnimation = true
            };
            Storyboard.SetTarget(shimmer, transform);
            Storyboard.SetTargetProperty(shimmer, "X");
            var storyboard = new Storyboard();
            storyboard.Children.Add(shimmer);
            storyboard.Begin();
        };
        return shell;
    }

    public static FrameworkElement Card()
    {
        var card = new Border
        {
            Style = (Style)Application.Current.Resources["GlassCard"],
            Padding = new Thickness(18, 16, 18, 14),
            Height = 228,
            Background = new SolidColorBrush(Color.FromArgb(0x0D, 0xFF, 0xFF, 0xFF))
        };

        var root = new Grid();
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        var head = new Grid { ColumnSpacing = 11 };
        head.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        head.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        head.Children.Add(Bone(44, 44, 13, 0, true));

        var titles = new StackPanel { Spacing = 7, VerticalAlignment = VerticalAlignment.Center };
        titles.Children.Add(Bone(150, 13, 6, 70));
        titles.Children.Add(Bone(92, 9, 5, 120));
        Grid.SetColumn(titles, 1);
        head.Children.Add(titles);
        root.Children.Add(head);

        var desc = new StackPanel { Spacing = 8, Margin = new Thickness(0, 14, 0, 0) };
        desc.Children.Add(Bone(270, 10, 5, 160));
        desc.Children.Add(Bone(235, 10, 5, 210));
        desc.Children.Add(Bone(170, 10, 5, 260));
        Grid.SetRow(desc, 1);
        root.Children.Add(desc);

        var foot = new Grid();
        foot.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        foot.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        foot.Children.Add(Bone(108, 8, 4, 300));
        var button = Bone(58, 24, 8, 340, true);
        Grid.SetColumn(button, 1);
        foot.Children.Add(button);
        Grid.SetRow(foot, 2);
        root.Children.Add(foot);

        card.Child = root;
        return card;
    }

    public static FrameworkElement VersionRow()
    {
        var row = new Border
        {
            HorizontalAlignment = HorizontalAlignment.Stretch,
            Padding = new Thickness(12, 10, 12, 10),
            CornerRadius = new CornerRadius(11),
            Background = new SolidColorBrush(Color.FromArgb(0x08, 0xFF, 0xFF, 0xFF)),
            BorderBrush = new SolidColorBrush(Color.FromArgb(0x12, 0xFF, 0xFF, 0xFF)),
            BorderThickness = new Thickness(1)
        };
        var grid = new Grid { ColumnSpacing = 12 };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var copy = new StackPanel { Spacing = 8 };
        copy.Children.Add(Bone(172, 12, 5, 0));
        copy.Children.Add(Bone(228, 8, 4, 60));
        grid.Children.Add(copy);

        var tag = Bone(48, 18, 6, 90, true);
        Grid.SetColumn(tag, 1);
        tag.VerticalAlignment = VerticalAlignment.Center;
        grid.Children.Add(tag);
        row.Child = grid;
        return row;
    }

    public static FrameworkElement VersionList()
    {
        var stack = new StackPanel { Spacing = 10 };
        for (var group = 0; group < 3; group++)
        {
            var block = new StackPanel { Spacing = 6 };
            var header = new Grid();
            header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var title = Bone(110, 12, 5, group * 130);
            header.Children.Add(title);
            var count = Bone(52, 8, 4, group * 130 + 40);
            Grid.SetColumn(count, 1);
            header.Children.Add(count);
            block.Children.Add(header);
            for (var row = 0; row < 3; row++) block.Children.Add(VersionRow());
            stack.Children.Add(block);
        }
        return stack;
    }
}
