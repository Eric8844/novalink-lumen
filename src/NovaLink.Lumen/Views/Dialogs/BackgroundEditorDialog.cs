using System;
using System.IO;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.Foundation;
using Windows.Media.Core;
using Windows.Media.Playback;

namespace NovaLink.Lumen.Views.Dialogs;

public sealed record BackgroundEditResult(double Scale, double OffsetX, double OffsetY, double Dim, bool VideoSound);

/// <summary>Preview + crop editor shown before a custom image or video becomes the launcher backdrop.</summary>
public static class BackgroundEditorDialog
{
    public static async Task<BackgroundEditResult?> ShowAsync(
        XamlRoot root, string path, bool isVideo, bool videoSound,
        double scale, double offsetX, double offsetY, double dim)
    {
        var modal = GlassModal.Show(isVideo ? "调整视频背景" : "调整图片背景",
            "拖动滑块决定裁切范围，预览就是启动器中的真实效果。", 680);

        var previewHost = new Grid { Height = 306, CornerRadius = new CornerRadius(16), Background = new SolidColorBrush(Windows.UI.Color.FromArgb(0xFF, 0x05, 0x0A, 0x12)) };
        var content = new Grid();
        previewHost.Children.Add(content);
        var clip = new RectangleGeometry();
        previewHost.SizeChanged += (_, e) => clip.Rect = new Rect(0, 0, e.NewSize.Width, e.NewSize.Height);
        previewHost.Clip = clip;

        Image? image = null;
        MediaPlayerElement? video = null;
        MediaPlayer? player = null;
        CompositeTransform transform = new() { CenterX = 340, CenterY = 153 };

        if (isVideo)
        {
            player = new MediaPlayer { IsLoopingEnabled = true, IsMuted = !videoSound, AutoPlay = true };
            try { player.Source = MediaSource.CreateFromUri(new Uri(path)); } catch { }
            video = new MediaPlayerElement
            {
                Stretch = Stretch.UniformToFill,
                AreTransportControlsEnabled = false,
                RenderTransformOrigin = new Point(0.5, 0.5),
                RenderTransform = transform
            };
            video.SetMediaPlayer(player);
            content.Children.Add(video);
        }
        else
        {
            image = new Image
            {
                Stretch = Stretch.UniformToFill,
                RenderTransformOrigin = new Point(0.5, 0.5),
                RenderTransform = transform
            };
            try { image.Source = new BitmapImage(new Uri(path)); } catch { }
            content.Children.Add(image);
        }

        modal.Body.Children.Add(previewHost);

        var scaleText = new TextBlock { Text = scale.ToString("0.00") + "×", FontSize = 11, Foreground = (Brush)Application.Current.Resources["BrushTextTertiary"] };
        var offsetXText = new TextBlock { Text = offsetX.ToString("0.00"), FontSize = 11, Foreground = (Brush)Application.Current.Resources["BrushTextTertiary"] };
        var offsetYText = new TextBlock { Text = offsetY.ToString("0.00"), FontSize = 11, Foreground = (Brush)Application.Current.Resources["BrushTextTertiary"] };
        var dimText = new TextBlock { Text = dim.ToString("0.00"), FontSize = 11, Foreground = (Brush)Application.Current.Resources["BrushTextTertiary"] };

        var scaleSlider = Slider(1.0, 2.6, 0.01, scale);
        var offsetXSlider = Slider(-1, 1, 0.01, offsetX);
        var offsetYSlider = Slider(-1, 1, 0.01, offsetY);
        var dimSlider = Slider(0, 0.7, 0.01, dim);

        void Refresh()
        {
            transform.ScaleX = scaleSlider.Value;
            transform.ScaleY = scaleSlider.Value;
            var width = previewHost.ActualWidth <= 0 ? 640 : previewHost.ActualWidth;
            var height = previewHost.ActualHeight <= 0 ? 306 : previewHost.ActualHeight;
            transform.TranslateX = offsetXSlider.Value * width * 0.42;
            transform.TranslateY = offsetYSlider.Value * height * 0.42;
            scaleText.Text = scaleSlider.Value.ToString("0.00") + "×";
            offsetXText.Text = offsetXSlider.Value.ToString("0.00");
            offsetYText.Text = offsetYSlider.Value.ToString("0.00");
            dimText.Text = dimSlider.Value.ToString("0.00");
        }

        scaleSlider.ValueChanged += (_, _) => Refresh();
        offsetXSlider.ValueChanged += (_, _) => Refresh();
        offsetYSlider.ValueChanged += (_, _) => Refresh();
        dimSlider.ValueChanged += (_, _) => Refresh();

        var controls = new StackPanel { Spacing = 6 };
        controls.Children.Add(ControlRow("缩放", scaleSlider, scaleText));
        controls.Children.Add(ControlRow("水平位置", offsetXSlider, offsetXText));
        controls.Children.Add(ControlRow("垂直位置", offsetYSlider, offsetYText));
        controls.Children.Add(ControlRow("压暗", dimSlider, dimText));

        ToggleSwitch? audioToggle = null;
        if (isVideo)
        {
            audioToggle = new ToggleSwitch
            {
                Header = "视频原声",
                OnContent = "播放",
                OffContent = "静音",
                IsOn = videoSound,
                MinWidth = 120
            };
            audioToggle.Toggled += (_, _) => { if (player is not null) player.IsMuted = !audioToggle.IsOn; };
            var audioRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 14, VerticalAlignment = VerticalAlignment.Center };
            audioRow.Children.Add(audioToggle);
            audioRow.Children.Add(new TextBlock
            {
                Text = "视频声音会与背景音乐同时播放，可随时静音。",
                FontSize = 11,
                Foreground = (Brush)Application.Current.Resources["BrushTextTertiary"],
                VerticalAlignment = VerticalAlignment.Center,
                TextWrapping = TextWrapping.Wrap
            });
            controls.Children.Add(audioRow);
        }

        modal.Body.Children.Add(controls);
        modal.Body.Children.Add(new TextBlock
        {
            Text = "背景只在启动器内显示，不会写入游戏目录。",
            FontSize = 10.5,
            Foreground = (Brush)Application.Current.Resources["BrushTextTertiary"]
        });

        BackgroundEditResult? result = null;
        modal.PrimaryText = "应用背景";
        modal.SecondaryText = "取消";
        modal.PrimaryInvoked = () =>
        {
            result = new BackgroundEditResult(scaleSlider.Value, offsetXSlider.Value, offsetYSlider.Value, dimSlider.Value, audioToggle?.IsOn ?? false);
            modal.Close();
            return Task.CompletedTask;
        };
        modal.SecondaryInvoked = () => { modal.Close(); return Task.CompletedTask; };

        try
        {
            await modal.WaitAsync();
        }
        finally
        {
            if (video is not null)
            {
                video.Visibility = Visibility.Collapsed;
                try { video.SetMediaPlayer(null); } catch { }
            }
            if (player is not null)
            {
                try { player.Pause(); player.Source = null; } catch { }
                try { player.Dispose(); } catch { }
            }
            // 给解码器线程一点时间释放，避免紧接着创建背景播放器时原生崩溃。
            await Task.Delay(260);
        }
        return result;
    }

    private static Slider Slider(double min, double max, double step, double value) => new()
    {
        Minimum = min,
        Maximum = max,
        StepFrequency = step,
        Value = Math.Clamp(value, min, max),
        MinWidth = 360
    };

    private static Grid ControlRow(string label, Slider slider, TextBlock value)
    {
        var row = new Grid { ColumnSpacing = 14, Margin = new Thickness(0, 2, 0, 2) };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(86) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(52) });
        row.Children.Add(new TextBlock
        {
            Text = label,
            FontSize = 12,
            Foreground = (Brush)Application.Current.Resources["BrushTextSecondary"],
            VerticalAlignment = VerticalAlignment.Center
        });
        Grid.SetColumn(slider, 1);
        row.Children.Add(slider);
        Grid.SetColumn(value, 2);
        value.HorizontalAlignment = HorizontalAlignment.Right;
        value.VerticalAlignment = VerticalAlignment.Center;
        row.Children.Add(value);
        return row;
    }
}
