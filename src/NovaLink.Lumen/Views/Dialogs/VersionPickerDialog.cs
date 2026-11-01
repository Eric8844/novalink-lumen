using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using NovaLink.Lumen.Controls;
using NovaLink.Lumen.Core.Models;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Windows.UI;

namespace NovaLink.Lumen.Views.Dialogs;

/// <summary>All-version browser grouped by game version and loader.</summary>
public static class VersionPickerDialog
{
    // 全部走主题画刷（Theme.xaml 资源），浅色/深色自动跟随；不要再写死深色，否则浅色下正文几乎看不见。
    private static Brush Text => ThemeManager.BrushText;
    private static Brush Muted => ThemeManager.BrushTextSecondary;
    private static Brush Faint => ThemeManager.BrushTextTertiary;
    private static Brush Mint => ThemeManager.BrushAccent;
    private static Brush Amber => ThemeManager.Brush("BrushWarn",
        ThemeManager.IsLight ? Color.FromArgb(0xFF, 0xB2, 0x6A, 0x00) : Color.FromArgb(0xFF, 0xF5, 0xC2, 0x6B));
    private static Brush Rose => ThemeManager.Brush("BrushDanger",
        ThemeManager.IsLight ? Color.FromArgb(0xFF, 0xD1, 0x3F, 0x62) : Color.FromArgb(0xFF, 0xFF, 0x6B, 0x8A));
    private static Brush FillSubtle => ThemeManager.Brush("BrushFillSubtle", ThemeManager.ControlFill);
    private static Brush FieldEdge => ThemeManager.Brush("BrushFieldBorder", ThemeManager.ControlBorder);
    private static Brush RowFill => ThemeManager.Brush("BrushGlassFaint", ThemeManager.ControlFill);
    private static Brush RowEdge => ThemeManager.Brush("BrushBorderSubtle", ThemeManager.ControlBorder);
    private static Brush RowSelected => ThemeManager.Brush("BrushSelected", ThemeManager.ControlFill);

    public static bool IsCompatible(ModrinthVersion version, InstanceSummary? instance, string projectType)
    {
        if (instance is null) return true;
        if (!string.IsNullOrWhiteSpace(instance.Kernel) &&
            !version.GameVersions.Contains(instance.Kernel, StringComparer.OrdinalIgnoreCase)) return false;
        if (projectType == "mod")
        {
            var loader = ProjectDetailDialog.LoaderTag(instance.Loader);
            if (!string.IsNullOrWhiteSpace(loader) &&
                !version.Loaders.Any(l => string.Equals(l, loader, StringComparison.OrdinalIgnoreCase))) return false;
        }
        return true;
    }

    public static async Task<ModrinthVersion?> ShowAsync(GlassModal modal, IReadOnlyList<ModrinthVersion> versions,
        InstanceSummary? instance, string projectType, ModrinthVersion? selected, string? installedVersion,
        Func<Task<IReadOnlyList<ModrinthVersion>>>? loader = null)
    {
        var tcs = new TaskCompletionSource<ModrinthVersion?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var closed = false;

        // 遮罩跟着主题走：浅色下铺浅灰纱，深色下铺暗纱；写死近黑色会让浅色模式整块变黑。
        var dim = ThemeManager.Dim;
        var overlay = new Grid
        {
            Background = new SolidColorBrush(Color.FromArgb(
                ThemeManager.IsLight ? (byte)0xB4 : (byte)0xE6, dim.R, dim.G, dim.B))
        };

        var card = new Border
        {
            Width = 660,
            MaxHeight = 640,
            Margin = new Thickness(36),
            Background = GlassModal.GlassLiquidBrush(),
            BorderBrush = ThemeManager.Brush("BrushGlassRim", ThemeManager.CardBorder),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(22),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            RequestedTheme = ThemeManager.IsLight ? ElementTheme.Light : ElementTheme.Dark
        };
        card.PointerPressed += (_, e) => e.Handled = true;

        var layout = new Grid { Padding = new Thickness(26, 22, 26, 20), RowSpacing = 14 };
        layout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        layout.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        layout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        var header = new Grid();
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var heading = new StackPanel { Spacing = 5 };
        heading.Children.Add(new TextBlock
        {
            Text = "选择版本",
            FontSize = 21,
            FontWeight = FontWeights.SemiBold,
            Foreground = Text
        });
        var subtitle = new TextBlock
        {
            Text = loader is not null
                ? "正在读取全部版本 · 按游戏版本与加载器折叠分组"
                : "全部 " + versions.Count + " 个版本 · 按游戏版本与加载器折叠分组",
            FontSize = 11.5,
            Foreground = Muted
        };
        heading.Children.Add(subtitle);
        header.Children.Add(heading);

        var close = new Button
        {
            Content = new FontIcon { Glyph = "\uE711", FontSize = 12 },
            Width = 34,
            Height = 34,
            Padding = new Thickness(0),
            CornerRadius = new CornerRadius(17),
            Background = FillSubtle,
            BorderThickness = new Thickness(0),
            Foreground = Muted,
            VerticalAlignment = VerticalAlignment.Top
        };
        Grid.SetColumn(close, 1);
        header.Children.Add(close);
        layout.Children.Add(header);

        var list = new StackPanel { Spacing = 6 };
        var scroll = new ScrollViewer
        {
            Content = list,
            MaxHeight = 468,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            Padding = new Thickness(0, 0, 6, 0)
        };
        Grid.SetRow(scroll, 1);
        layout.Children.Add(scroll);

        var footer = new Grid();
        footer.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        footer.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        footer.Children.Add(new TextBlock
        {
            Text = "点击任意版本立即应用，文件列表会在主界面刷新。",
            FontSize = 10.5,
            Foreground = Faint,
            VerticalAlignment = VerticalAlignment.Center,
            TextWrapping = TextWrapping.Wrap
        });
        var cancel = new Button
        {
            Content = "取消",
            Height = 34,
            MinWidth = 78,
            Padding = new Thickness(18, 0, 18, 0),
            CornerRadius = new CornerRadius(10),
            Background = FillSubtle,
            BorderBrush = FieldEdge,
            BorderThickness = new Thickness(1),
            Foreground = Muted
        };
        Grid.SetColumn(cancel, 1);
        footer.Children.Add(cancel);
        Grid.SetRow(footer, 2);
        layout.Children.Add(footer);

        // 与 GlassModal 一致：玻璃底 + 1.5px 顶部高光，圆角 22。
        var glassRoot = new Grid();
        glassRoot.Children.Add(layout);
        glassRoot.Children.Add(GlassModal.TopHighlight(22));
        card.Child = glassRoot;
        overlay.Children.Add(card);

        void Finish(ModrinthVersion? value)
        {
            if (closed) return;
            closed = true;
            modal.ClearOverlay();
            tcs.TrySetResult(value);
        }

        overlay.PointerPressed += (_, e) =>
        {
            if (ReferenceEquals(e.OriginalSource, overlay)) Finish(null);
        };
        close.Click += (_, _) => Finish(null);
        cancel.Click += (_, _) => Finish(null);
        modal.ShowOverlay(overlay);

        var currentKernel = instance?.Kernel ?? "";

        void ShowSkeleton()
        {
            list.Children.Clear();
            list.Children.Add(Skeleton.VersionList());
        }

        void ShowMessage(string text)
        {
            list.Children.Clear();
            list.Children.Add(new TextBlock
            {
                Text = text,
                FontSize = 11.5,
                Foreground = Muted,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(2, 12, 2, 12)
            });
        }

        void RenderVersions(IReadOnlyList<ModrinthVersion> source)
        {
            list.Children.Clear();
            var gameGroups = source
                .GroupBy(v => string.IsNullOrWhiteSpace(v.GameVersions.FirstOrDefault())
                    ? "未标注游戏版本"
                    : v.GameVersions[0])
                .OrderBy(g => string.Equals(g.Key, currentKernel, StringComparison.OrdinalIgnoreCase) ? 0 : 1)
                .ThenByDescending(g => g.Key, Comparer<string>.Create(CompareGameVersions))
                .ToList();

            for (var groupIndex = 0; groupIndex < gameGroups.Count; groupIndex++)
            {
                var gameGroup = gameGroups[groupIndex];
                var gameExpander = new Expander
                {
                    Header = GroupHeader(gameGroup.Key, gameGroup.Count() + " 个版本"),
                    IsExpanded = groupIndex == 0,
                    HorizontalAlignment = HorizontalAlignment.Stretch,
                    HorizontalContentAlignment = HorizontalAlignment.Stretch,
                    Margin = new Thickness(0, 0, 0, 5)
                };

                var loaderHost = new StackPanel { Spacing = 4, Margin = new Thickness(12, 2, 0, 4) };
                var loaderGroups = gameGroup
                    .GroupBy(v => PrimaryLoader(v))
                    .OrderBy(g => LoaderRank(g.Key))
                    .ThenBy(g => g.Key, StringComparer.OrdinalIgnoreCase)
                    .ToList();
                foreach (var loaderGroup in loaderGroups)
                {
                    var loaderExpander = new Expander
                    {
                        Header = GroupHeader(loaderGroup.Key, loaderGroup.Count() + " 个文件版本"),
                        IsExpanded = true,
                        HorizontalAlignment = HorizontalAlignment.Stretch,
                        HorizontalContentAlignment = HorizontalAlignment.Stretch,
                        Margin = new Thickness(0, 0, 0, 3)
                    };
                    var rows = new StackPanel { Spacing = 2, Margin = new Thickness(4, 2, 4, 2) };
                    foreach (var version in loaderGroup
                                 .OrderBy(v => VersionTypeRank(v.VersionType))
                                 .ThenByDescending(v => v.DatePublished))
                    {
                        rows.Children.Add(BuildRow(version, selected, installedVersion, instance, projectType, Finish));
                    }
                    loaderExpander.Content = rows;
                    loaderHost.Children.Add(loaderExpander);
                }
                gameExpander.Content = loaderHost;
                list.Children.Add(gameExpander);
            }
        }

        async Task LoadVersionsAsync()
        {
            var started = Stopwatch.GetTimestamp();
            ShowSkeleton();
            try
            {
                var loaded = await loader!();
                var remain = TimeSpan.FromMilliseconds(460) - Stopwatch.GetElapsedTime(started);
                if (remain > TimeSpan.Zero) await Task.Delay(remain);
                if (closed) return;
                subtitle.Text = "全部 " + loaded.Count + " 个版本 · 按游戏版本与加载器折叠分组";
                if (loaded.Count == 0)
                {
                    ShowMessage("这个项目暂时没有可下载版本。");
                    return;
                }
                RenderVersions(loaded);
            }
            catch (Exception ex)
            {
                if (closed) return;
                subtitle.Text = "读取版本失败";
                ShowMessage("读取版本失败：" + ex.Message);
            }
        }

        if (loader is not null)
        {
            _ = LoadVersionsAsync();
        }
        else if (versions.Count > 0)
        {
            RenderVersions(versions);
        }
        else
        {
            ShowMessage("这个项目暂时没有可下载版本。");
        }

        return await tcs.Task;
    }

    private static FrameworkElement BuildRow(ModrinthVersion version, ModrinthVersion? selected,
        string? installedVersion, InstanceSummary? instance, string projectType, Action<ModrinthVersion?> finish)
    {
        var isSelected = selected is not null && string.Equals(selected.Id, version.Id, StringComparison.OrdinalIgnoreCase);
        var compatible = IsCompatible(version, instance, projectType);
        var row = new Button
        {
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
            Padding = new Thickness(11, 8, 11, 8),
            CornerRadius = new CornerRadius(11),
            Background = isSelected ? RowSelected : RowFill,
            BorderBrush = isSelected ? Mint : RowEdge,
            BorderThickness = new Thickness(1),
            Margin = new Thickness(0, 1, 0, 1)
        };

        var layout = new Grid { ColumnSpacing = 10 };
        layout.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        layout.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var copy = new StackPanel { Spacing = 4 };
        var titleLine = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
        titleLine.Children.Add(new TextBlock
        {
            Text = string.IsNullOrWhiteSpace(version.VersionNumber) ? version.Name : version.VersionNumber,
            FontSize = 13,
            FontWeight = FontWeights.SemiBold,
            Foreground = Text,
            TextTrimming = TextTrimming.CharacterEllipsis
        });
        titleLine.Children.Add(Pill(DisplayType(version.VersionType), TypeBrush(version.VersionType)));
        if (!string.IsNullOrWhiteSpace(installedVersion) &&
            string.Equals(installedVersion, version.VersionNumber, StringComparison.OrdinalIgnoreCase))
            titleLine.Children.Add(Pill("已安装", Mint));
        if (!compatible) titleLine.Children.Add(Pill("与当前实例不匹配", Amber));
        copy.Children.Add(titleLine);

        var details = new List<string>();
        if (version.DatePublished is not null) details.Add(version.DatePublished.Value.ToLocalTime().ToString("yyyy-MM-dd"));
        if (version.GameVersions.Count > 0) details.Add("游戏 " + string.Join(" / ", version.GameVersions.Take(4)));
        if (version.Loaders.Count > 0) details.Add(string.Join(" / ", version.Loaders.Select(DisplayLoader).Distinct()));
        details.Add(version.Files.Count + " 个文件");
        copy.Children.Add(new TextBlock
        {
            Text = string.Join(" · ", details),
            FontSize = 10,
            Foreground = Faint,
            TextTrimming = TextTrimming.CharacterEllipsis
        });
        layout.Children.Add(copy);

        var chevron = new FontIcon
        {
            Glyph = "\uE76C",
            FontSize = 11,
            Foreground = isSelected ? Mint : Muted,
            VerticalAlignment = VerticalAlignment.Center
        };
        Grid.SetColumn(chevron, 1);
        layout.Children.Add(chevron);
        row.Content = layout;
        row.Click += (_, _) => finish(version);
        return row;
    }

    private static FrameworkElement GroupHeader(string title, string subtitle)
    {
        var stack = new StackPanel { Spacing = 2 };
        stack.Children.Add(new TextBlock
        {
            Text = title,
            FontSize = 12.5,
            FontWeight = FontWeights.SemiBold,
            Foreground = Text
        });
        stack.Children.Add(new TextBlock
        {
            Text = subtitle,
            FontSize = 9.5,
            Foreground = Faint
        });
        stack.Children.Add(new Border
        {
            Height = 1,
            MinWidth = 220,
            Margin = new Thickness(0, 4, 0, 0),
            Background = RowEdge
        });
        return stack;
    }

    private static Border Pill(string text, Brush accent) => new()
    {
        Background = FillSubtle,
        BorderBrush = FieldEdge,
        BorderThickness = new Thickness(1),
        CornerRadius = new CornerRadius(6),
        Padding = new Thickness(6, 1, 6, 1),
        VerticalAlignment = VerticalAlignment.Center,
        Child = new TextBlock
        {
            Text = text,
            FontSize = 8.5,
            Foreground = accent
        }
    };

    private static int CompareGameVersions(string? left, string? right)
    {
        if (string.Equals(left, right, StringComparison.OrdinalIgnoreCase)) return 0;
        if (string.IsNullOrWhiteSpace(left)) return 1;
        if (string.IsNullOrWhiteSpace(right)) return -1;
        var lowerLeft = left.ToLowerInvariant();
        var lowerRight = right.ToLowerInvariant();
        if (lowerLeft.StartsWith(lowerRight, StringComparison.Ordinal) && lowerLeft.Length > lowerRight.Length)
        {
            var next = lowerLeft[lowerRight.Length];
            if (next == '-' || next == '+' || next == ' ') return -1;
        }
        if (lowerRight.StartsWith(lowerLeft, StringComparison.Ordinal) && lowerRight.Length > lowerLeft.Length)
        {
            var next = lowerRight[lowerLeft.Length];
            if (next == '-' || next == '+' || next == ' ') return 1;
        }
        var a = Numbers(left);
        var b = Numbers(right);
        for (var i = 0; i < Math.Max(a.Count, b.Count); i++)
        {
            var av = i < a.Count ? a[i] : -1;
            var bv = i < b.Count ? b[i] : -1;
            if (av != bv) return av.CompareTo(bv);
        }
        return string.Compare(left, right, StringComparison.OrdinalIgnoreCase);
    }

    private static List<int> Numbers(string value)
    {
        var numbers = new List<int>();
        foreach (var piece in Regex.Split(value, "[^0-9]+"))
            if (int.TryParse(piece, out var number)) numbers.Add(number);
        return numbers;
    }

    private static string PrimaryLoader(ModrinthVersion version)
    {
        var raw = version.Loaders.FirstOrDefault(l => !string.IsNullOrWhiteSpace(l));
        return string.IsNullOrWhiteSpace(raw) ? "通用" : DisplayLoader(raw);
    }

    private static string DisplayLoader(string loader) => loader.ToLowerInvariant() switch
    {
        "fabric" => "Fabric",
        "quilt" => "Quilt",
        "forge" => "Forge",
        "neoforge" => "NeoForge",
        "liteloader" => "LiteLoader",
        "minecraft" => "原版",
        _ => loader
    };

    private static int LoaderRank(string loader) => loader switch
    {
        "Fabric" => 0,
        "Quilt" => 1,
        "Forge" => 2,
        "NeoForge" => 3,
        "原版" => 4,
        _ => 5
    };

    private static string DisplayType(string type) => string.IsNullOrWhiteSpace(type) ? "release" : type;

    private static int VersionTypeRank(string type) => DisplayType(type).ToLowerInvariant() switch
    {
        "release" => 0,
        "beta" => 1,
        "alpha" => 2,
        _ => 3
    };

    private static Brush TypeBrush(string type) => DisplayType(type).ToLowerInvariant() switch
    {
        "release" => Mint,
        "beta" => Amber,
        "alpha" => Rose,
        _ => Muted
    };
}
