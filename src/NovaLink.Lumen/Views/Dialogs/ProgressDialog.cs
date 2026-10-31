using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using NovaLink.Lumen.Controls;
using System;
using System.Linq;
using Windows.UI;

namespace NovaLink.Lumen.Views.Dialogs;

/// <summary>进度显示：左侧任务列表，右侧阶段、速度、耗时与详细日志。</summary>
public static class ProgressDialog
{
    private static GlassModal? _modal;
    private static StackPanel? _list;
    private static StackPanel? _detail;
    private static ScrollViewer? _logScroll;
    private static Button? _stopButton;
    private static bool _open;

    private static readonly Color Mint = Color.FromArgb(0xFF, 0x7C, 0xEB, 0xD6);
    private static readonly Color Text = Color.FromArgb(0xFF, 0xF3, 0xF7, 0xFE);
    private static readonly Color Muted = Color.FromArgb(0xFF, 0x91, 0xA0, 0xB8);
    private static readonly Color Faint = Color.FromArgb(0xFF, 0x6E, 0x7E, 0x95);

    public static void Close() => _modal?.Close();

    public static void Show()
    {
        if (_open || GlassModal.Current is { IsClosed: false }) return;
        _open = true;
        var modal = GlassModal.Show("进度显示",
            "下载、安装与启动都汇总在这里。完成后小窗会自动隐藏。", 860);
        _modal = modal;
        modal.HideButtons();
        modal.SetCloseAllowed(true);

        var layout = new Grid { ColumnSpacing = 18, Height = 470 };
        layout.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(228) });
        layout.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        _list = new StackPanel { Spacing = 7 };
        var listScroll = new ScrollViewer
        {
            Content = _list,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            Padding = new Thickness(0, 0, 4, 0)
        };
        var left = new Border
        {
            Background = new SolidColorBrush(Color.FromArgb(0x30, 0x05, 0x0A, 0x14)),
            BorderBrush = new SolidColorBrush(Color.FromArgb(0x18, 0xFF, 0xFF, 0xFF)),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(16),
            Padding = new Thickness(10, 10, 8, 10),
            Child = listScroll
        };
        layout.Children.Add(left);

        _detail = new StackPanel { Spacing = 13 };
        var detailScroll = new ScrollViewer
        {
            Content = _detail,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            Padding = new Thickness(2, 0, 6, 0)
        };
        Grid.SetColumn(detailScroll, 1);
        layout.Children.Add(detailScroll);
        modal.Body.Children.Add(layout);

        var footer = new Grid { Margin = new Thickness(0, 4, 0, 0) };
        footer.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        footer.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        footer.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var exit = new Button
        {
            Content = "退出进度显示", Height = 38, Padding = new Thickness(18, 0, 18, 0),
            CornerRadius = new CornerRadius(12),
            Background = new SolidColorBrush(Color.FromArgb(0x14, 0xFF, 0xFF, 0xFF)),
            Foreground = new SolidColorBrush(Text),
            BorderBrush = new SolidColorBrush(Color.FromArgb(0x1F, 0xFF, 0xFF, 0xFF)),
            BorderThickness = new Thickness(1)
        };
        exit.Click += (_, _) => modal.Close();
        footer.Children.Add(exit);

        var stop = new Button
        {
            Content = "停止当前进度", Height = 38, Padding = new Thickness(18, 0, 18, 0),
            CornerRadius = new CornerRadius(12),
            Background = new SolidColorBrush(Color.FromArgb(0x16, 0xFF, 0x6B, 0x6B)),
            Foreground = new SolidColorBrush(Color.FromArgb(0xFF, 0xFF, 0xB4, 0xB4)),
            BorderBrush = new SolidColorBrush(Color.FromArgb(0x30, 0xFF, 0x8A, 0x8A)),
            BorderThickness = new Thickness(1)
        };
        stop.Click += (_, _) => { ProgressCenter.StopSelected(); Refresh(); };
        Grid.SetColumn(stop, 2);
        footer.Children.Add(stop);
        _stopButton = stop;
        modal.Body.Children.Add(footer);

        ProgressCenter.Changed += OnChanged;
        modal.Closed += () =>
        {
            ProgressCenter.Changed -= OnChanged;
            _open = false;
            _modal = null;
            _list = null;
            _detail = null;
            _logScroll = null;
            _stopButton = null;
        };
        Refresh();
    }

    private static void OnChanged()
    {
        var dispatcher = _modal?.Body.DispatcherQueue;
        if (dispatcher is null) return;
        dispatcher.TryEnqueue(Refresh);
    }

    private static void Refresh()
    {
        if (!_open || _list is null || _detail is null) return;
        var tasks = ProgressCenter.Snapshot();
        _list.Children.Clear();
        foreach (var task in tasks)
        {
            var row = BuildRow(task);
            _list.Children.Add(row);
        }
        if (tasks.Count == 0)
        {
            _list.Children.Add(new TextBlock
            {
                Text = "还没有任务。\n安装版本、下载模组或启动游戏后，这里会出现进度。",
                FontSize = 11, LineHeight = 18, TextWrapping = TextWrapping.Wrap,
                Foreground = new SolidColorBrush(Faint), Margin = new Thickness(8, 16, 8, 8)
            });
        }

        _detail.Children.Clear();
        var selected = ProgressCenter.Selected;
        if (_stopButton is not null)
        {
            var live = selected?.Kind == "launch" && selected.Status == ProgressStatus.Active;
            _stopButton.Content = live ? "关闭游戏" : "停止当前进度";
            _stopButton.IsEnabled = selected is not null
                && selected.Status is ProgressStatus.Active or ProgressStatus.Stopping
                && selected.Cancelable;
        }
        if (selected is null)
        {
            _detail.Children.Add(new TextBlock
            {
                Text = "选择一个任务查看详细进度。", FontSize = 12.5,
                Foreground = new SolidColorBrush(Faint), Margin = new Thickness(4, 24, 0, 0)
            });
            return;
        }
        BuildDetail(selected);
    }

    private static Button BuildRow(ProgressTask task)
    {
        var grid = new Grid { ColumnSpacing = 9 };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(30) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var iconBox = new Border
        {
            Width = 30, Height = 30, CornerRadius = new CornerRadius(9),
            Background = new SolidColorBrush(task.Status switch
            {
                ProgressStatus.Done => Color.FromArgb(0x28, 0x7C, 0xEB, 0xD6),
                ProgressStatus.Failed => Color.FromArgb(0x28, 0xFF, 0x6B, 0x6B),
                _ => Color.FromArgb(0x1C, 0xFF, 0xFF, 0xFF)
            }),
            Child = new FontIcon
            {
                Glyph = task.Kind == "launch" ? "\uE768" : "\uE896",
                FontSize = 12,
                Foreground = new SolidColorBrush(task.Status switch
                {
                    ProgressStatus.Done => Mint,
                    ProgressStatus.Failed => Color.FromArgb(0xFF, 0xFF, 0xB4, 0xB4),
                    _ => Text
                })
            }
        };
        grid.Children.Add(iconBox);

        var copy = new StackPanel { Spacing = 2, VerticalAlignment = VerticalAlignment.Center };
        copy.Children.Add(new TextBlock
        {
            Text = task.Title, FontSize = 11.5, FontWeight = FontWeights.SemiBold,
            Foreground = new SolidColorBrush(Text), TextTrimming = TextTrimming.CharacterEllipsis
        });
        copy.Children.Add(new TextBlock
        {
            Text = task.StatusText + (task.Percent is null ? "" : " · " + task.PercentValue + "%"),
            FontSize = 9.5, Foreground = new SolidColorBrush(Faint)
        });
        Grid.SetColumn(copy, 1);
        grid.Children.Add(copy);

        var stateDot = new Ellipse
        {
            Width = 7, Height = 7, VerticalAlignment = VerticalAlignment.Center,
            Fill = new SolidColorBrush(task.Status switch
            {
                ProgressStatus.Done => Mint,
                ProgressStatus.Failed => Color.FromArgb(0xFF, 0xFF, 0x8A, 0x8A),
                ProgressStatus.Cancelled => Color.FromArgb(0xFF, 0x6E, 0x7E, 0x95),
                _ => Color.FromArgb(0xFF, 0x61, 0xC8, 0xFF)
            })
        };
        Grid.SetColumn(stateDot, 2);
        grid.Children.Add(stateDot);

        var row = new Button
        {
            Content = grid,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
            Padding = new Thickness(8, 7, 8, 7),
            CornerRadius = new CornerRadius(11),
            Background = new SolidColorBrush(ProgressCenter.Selected?.Id == task.Id
                ? Color.FromArgb(0x22, 0x7C, 0xEB, 0xD6)
                : Color.FromArgb(0x00, 0x00, 0x00, 0)),
            BorderBrush = new SolidColorBrush(ProgressCenter.Selected?.Id == task.Id
                ? Color.FromArgb(0x55, 0x7C, 0xEB, 0xD6)
                : Color.FromArgb(0x00, 0xFF, 0xFF, 0xFF)),
            BorderThickness = new Thickness(1)
        };
        row.Click += (_, _) => { ProgressCenter.Select(task.Id); Refresh(); };
        return row;
    }

    private static void BuildDetail(ProgressTask task)
    {
        var head = new Grid { ColumnSpacing = 12 };
        head.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(46) });
        head.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        head.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var iconBox = new Border
        {
            Width = 46, Height = 46, CornerRadius = new CornerRadius(14),
            Background = new SolidColorBrush(Color.FromArgb(0x20, 0x7C, 0xEB, 0xD6)),
            Child = new FontIcon
            {
                Glyph = task.Kind == "launch" ? "\uE768" : "\uE896",
                FontSize = 18, Foreground = new SolidColorBrush(Mint)
            }
        };
        head.Children.Add(iconBox);

        var copy = new StackPanel { Spacing = 3, VerticalAlignment = VerticalAlignment.Center };
        copy.Children.Add(new TextBlock
        {
            Text = task.Title, FontSize = 16, FontWeight = FontWeights.SemiBold,
            Foreground = new SolidColorBrush(Text), TextWrapping = TextWrapping.Wrap
        });
        copy.Children.Add(new TextBlock
        {
            Text = task.Subtitle, FontSize = 10.5, Foreground = new SolidColorBrush(Faint)
        });
        Grid.SetColumn(copy, 1);
        head.Children.Add(copy);

        var chip = new Border
        {
            CornerRadius = new CornerRadius(8), Padding = new Thickness(9, 3, 9, 3),
            Background = new SolidColorBrush(task.Status switch
            {
                ProgressStatus.Done => Color.FromArgb(0x30, 0x7C, 0xEB, 0xD6),
                ProgressStatus.Failed => Color.FromArgb(0x30, 0xFF, 0x6B, 0x6B),
                _ => Color.FromArgb(0x24, 0x61, 0xC8, 0xFF)
            }),
            Child = new TextBlock
            {
                Text = task.StatusText, FontSize = 9.5,
                Foreground = new SolidColorBrush(task.Status switch
                {
                    ProgressStatus.Done => Mint,
                    ProgressStatus.Failed => Color.FromArgb(0xFF, 0xFF, 0xB4, 0xB4),
                    _ => Color.FromArgb(0xFF, 0xA8, 0xDC, 0xFF)
                })
            },
            VerticalAlignment = VerticalAlignment.Center
        };
        Grid.SetColumn(chip, 2);
        head.Children.Add(chip);
        _detail!.Children.Add(head);

        var bigBar = new Grid { ColumnSpacing = 10 };
        bigBar.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        bigBar.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(52) });
        var bar = new ProgressBar
        {
            Height = 7, IsIndeterminate = task.Indeterminate,
            Value = task.Indeterminate ? 0 : task.PercentValue,
            Foreground = new SolidColorBrush(Mint),
            Background = new SolidColorBrush(Color.FromArgb(0x18, 0xFF, 0xFF, 0xFF)),
            CornerRadius = new CornerRadius(4)
        };
        bigBar.Children.Add(bar);
        var pct = new TextBlock
        {
            Text = task.Indeterminate ? "处理中" : task.PercentValue + "%",
            FontSize = 13, FontWeight = FontWeights.SemiBold,
            Foreground = new SolidColorBrush(Text), HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Center
        };
        Grid.SetColumn(pct, 1);
        bigBar.Children.Add(pct);
        _detail.Children.Add(bigBar);

        var meta = new Grid { ColumnSpacing = 8 };
        for (var i = 0; i < 5; i++)
            meta.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        AddMeta(meta, 0, "阶段", task.Phase);
        AddMeta(meta, 1, "速度", string.IsNullOrEmpty(task.SpeedText) ? "—" : task.SpeedText);
        AddMeta(meta, 2, "已用时间", task.ElapsedText);
        AddMeta(meta, 3, "已下载", task.Bytes > 0 ? ProgressTask.FormatBytes(task.Bytes) : "—");
        AddMeta(meta, 4, "任务 ID", task.Id.Length > 22 ? task.Id[..22] : task.Id, true);
        _detail.Children.Add(meta);

        _detail.Children.Add(new TextBlock
        {
            Text = task.Message, FontSize = 11.5, TextWrapping = TextWrapping.Wrap,
            Foreground = new SolidColorBrush(Color.FromArgb(0xFF, 0xC7, 0xD3, 0xE6))
        });

        var logs = task.Logs.TakeLast(80).ToList();
        var logHeader = new Grid { Margin = new Thickness(0, 4, 0, 0) };
        logHeader.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        logHeader.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        logHeader.Children.Add(new TextBlock
        {
            Text = "DETAIL LOG", FontSize = 8.5, CharacterSpacing = 180,
            Foreground = new SolidColorBrush(Faint)
        });
        var count = new TextBlock
        {
            Text = logs.Count + " lines", FontSize = 8.5, Foreground = new SolidColorBrush(Faint)
        };
        Grid.SetColumn(count, 1);
        logHeader.Children.Add(count);
        _detail.Children.Add(logHeader);

        var logText = new TextBlock
        {
            FontFamily = new FontFamily("Consolas"),
            FontSize = 9.5,
            TextWrapping = TextWrapping.Wrap,
            Foreground = new SolidColorBrush(Color.FromArgb(0xFF, 0xB7, 0xC4, 0xD8)),
            Text = logs.Count > 0 ? string.Join(Environment.NewLine, logs) : "暂无详细日志"
        };
        _logScroll = new ScrollViewer
        {
            Content = logText, Height = 150,
            Padding = new Thickness(10),
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            Background = new SolidColorBrush(Color.FromArgb(0x88, 0x03, 0x07, 0x0E)),
            BorderBrush = new SolidColorBrush(Color.FromArgb(0x18, 0xFF, 0xFF, 0xFF)),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(12)
        };
        _detail.Children.Add(_logScroll);
        _logScroll.ChangeView(null, _logScroll.ScrollableHeight, null);
    }

    private static void AddMeta(Grid grid, int column, string label, string value, bool mono = false)
    {
        var cell = new Border
        {
            Background = new SolidColorBrush(Color.FromArgb(0x14, 0xFF, 0xFF, 0xFF)),
            BorderBrush = new SolidColorBrush(Color.FromArgb(0x12, 0xFF, 0xFF, 0xFF)),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(9),
            Padding = new Thickness(8, 6, 8, 6)
        };
        var stack = new StackPanel { Spacing = 2 };
        stack.Children.Add(new TextBlock { Text = label, FontSize = 8, Foreground = new SolidColorBrush(Faint) });
        var valueText = new TextBlock
        {
            Text = string.IsNullOrWhiteSpace(value) ? "—" : value, FontSize = 10,
            Foreground = new SolidColorBrush(Text), TextTrimming = TextTrimming.CharacterEllipsis
        };
        // Assigning null to FontFamily throws 0x800F1001 inside WinUI and kills the process,
        // so only touch the property when a monospace face is requested.
        if (mono) valueText.FontFamily = new FontFamily("Consolas");
        stack.Children.Add(valueText);
        cell.Child = stack;
        Grid.SetColumn(cell, column);
        grid.Children.Add(cell);
    }
}
