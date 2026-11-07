using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using NovaLink.Lumen.Core.Models;
using NovaLink.Lumen.Controls;
using NovaLink.Lumen.Services;
using NovaLink.Lumen.Services.Storage;
using NovaLink.Lumen.Services.Updates;
using NovaLink.Lumen.Startup;
using NovaLink.Lumen.Views.Dialogs;
using Windows.Storage;
using Windows.Storage.Pickers;
using Windows.UI;

namespace NovaLink.Lumen.Views.Pages;

public sealed partial class SettingsPage : Page
{
    private string _javaPath = "";
    private bool _ready;
    private bool _syncIsolation;
    private bool _syncAccent;
    private bool _syncMusic;
    private bool _glassScanRunning;
    private int _glassScanToken;

    public SettingsPage()
    {
        InitializeComponent();
        Loaded += SettingsPage_Loaded;
        Unloaded += SettingsPage_Unloaded;
        _ready = true;
    }

    private async void SettingsPage_Loaded(object sender, RoutedEventArgs e)
    {
        LumenRuntime.Accounts.AccountsChanged += Accounts_Changed;
        _javaPath = LumenRuntime.Settings.Settings.JavaPath;
        RootText.Text = LumenRuntime.Settings.Settings.GameRoot;
        // 版本号跟通道走：正式版 "1.5.0"，测试版 "1.5.0 Preview"。
        VersionText.Text = "NovaLink Lumen " + LauncherChannelInfo.DisplayVersion;
        DataPathText.Text = LumenRuntime.Paths.Root;
        StorageText.Text = "数据目录\n" + LumenRuntime.Paths.Root;
        MemorySlider.Value = Math.Clamp(LumenRuntime.Settings.Settings.Memory <= 0 ? 4096 : LumenRuntime.Settings.Settings.Memory, 1024, 32768);
        JavaArgsBox.Text = LumenRuntime.Settings.Settings.JavaArgs;
        SyncGlassControls();
        SyncTheme();
        SyncAccent();
        SyncBackgroundMusic();
        SyncDownloadThreads();
        SyncIsolation();
        // 实验性功能：插件默认关闭，开关初值与「插件」分类的显隐都在这里定一次。
        SyncExperimental();
        UpdateMemoryLabel();
        ShowPanel("accounts");
        ReloadAccounts();
        await ReloadJavaAsync(false);
        UpdatePerfText();
        RefreshBackupStatus();
    }

    private void SettingsPage_Unloaded(object sender, RoutedEventArgs e)
        => LumenRuntime.Accounts.AccountsChanged -= Accounts_Changed;

    private void Accounts_Changed(object? sender, EventArgs e) => ReloadAccounts();

    // 扫描动画的分步文案：检测本身几十毫秒，这里按 4 步放慢到约 1.4 秒，让用户看清在测什么。
    private static readonly string[] GlassScanSteps =
    {
        "正在检测处理器…",
        "正在检测显卡…",
        "正在检测内存…",
        "正在计算挡位…"
    };

    private async void GlassPreset_Click(object sender, RoutedEventArgs e)
    {
        if (!_ready || sender is not Button button || button.Tag is not string tag) return;
        await SelectGlassPresetAsync(GlassPresets.Parse(tag));
    }

    private async void GlassRescan_Click(object sender, RoutedEventArgs e)
    {
        if (!_ready) return;
        await SelectGlassPresetAsync(GlassPreset.Auto);
    }

    private async Task SelectGlassPresetAsync(GlassPreset preset)
    {
        // 手动挡位立刻生效，顺手掐掉还在跑的扫描（token 变化后旧扫描回来什么也不做）。
        if (preset != GlassPreset.Auto)
        {
            _glassScanToken++;
            _glassScanRunning = false;
            GlassScanRing.IsActive = false;
            GlassScanPanel.Visibility = Visibility.Collapsed;
            GlassRescanButton.IsEnabled = true;
            ApplyGlassPreset(preset);
            return;
        }

        await RunGlassScanAsync();
    }

    /// <summary>把挡位同时写到玻璃、设置与胶囊上；auto 用当前检测结果，其余用固定档。</summary>
    private void ApplyGlassPreset(GlassPreset preset)
    {
        var quality = LumenRuntime.ApplyGlassProfile(preset, LumenRuntime.Hardware);
        ApplyGlassQualityToBackdrop(quality);
        LumenRuntime.Settings.Settings.GlassQuality = GlassPresets.ToSetting(preset);
        LumenRuntime.Settings.Save();
        RenderGlassPreset(preset);
        ShowGlassHint(preset);
    }

    private void ApplyGlassQualityToBackdrop(GlassQuality quality)
    {
        if (ShellPage.Current?.GlassBackdrop is { } backdrop) backdrop.Glass.Quality = quality;
    }

    /// <summary>
    /// 选「自动」或点「重新检测」：后台线程读一次硬件（不到 300ms），前台按分步文案滚动约 1.4 秒，
    /// 完成后应用推荐挡位、把结果 chip 摆出来，并把设置写成 auto（下次启动接着自动检测）。
    /// </summary>
    private async Task RunGlassScanAsync()
    {
        if (_glassScanRunning) return;
        var token = ++_glassScanToken;
        _glassScanRunning = true;

        RenderGlassPreset(GlassPreset.Auto);
        GlassResultChip.Visibility = Visibility.Collapsed;
        GlassScanPanel.Visibility = Visibility.Visible;
        GlassScanDetail.Text = "只读取本地配置，大约 1 秒。";
        GlassScanRing.IsActive = true;
        GlassRescanButton.IsEnabled = false;

        HardwareReport? report = null;
        try
        {
            var detect = Task.Run(() => HardwareProfile.Detect());
            for (var i = 0; i < GlassScanSteps.Length; i++)
            {
                if (token != _glassScanToken) return;
                GlassScanStep.Text = GlassScanSteps[i];
                await Task.WhenAll(detect, Task.Delay(i == 0 ? 380 : 340));
            }

            report = await detect;
        }
        catch
        {
            // 检测失败不弹错误：按平衡档继续，界面上只说「没成功」。
            report = null;
        }

        if (token != _glassScanToken) return;

        _glassScanRunning = false;
        GlassScanRing.IsActive = false;
        GlassScanPanel.Visibility = Visibility.Collapsed;
        GlassRescanButton.IsEnabled = true;

        var quality = LumenRuntime.ApplyDetectedHardware(report);
        ApplyGlassQualityToBackdrop(quality);
        LumenRuntime.Settings.Settings.GlassQuality = GlassPresets.ToSetting(GlassPreset.Auto);
        LumenRuntime.Settings.Save();

        GlassResultText.Text = report is null
            ? "自动检测没成功，已按平衡档运行"
            : "已选择：" + report.TierLabel + "（" + report.Summary + "）";
        GlassResultChip.Visibility = Visibility.Visible;
        GlassQualityHint.Text = report is null
            ? "自动 · 下次启动会再试一次，也可以手动选一个挡位"
            : "自动 · 每次启动都会重新检测；点左侧挡位可以固定下来";
        UpdatePerfText();
    }

    /// <summary>胶囊选中态：选中用强调色底 + 深色字，未选中透明底 + 次级字。</summary>
    private void RenderGlassPreset(GlassPreset preset)
    {
        var accent = (Brush)Application.Current.Resources["BrushAccent"];
        var onAccent = (Brush)Application.Current.Resources["BrushOnAccent"];
        var idle = (Brush)Application.Current.Resources["BrushTextSecondary"];
        var clear = new SolidColorBrush(Microsoft.UI.Colors.Transparent);
        var buttons = new (Button Button, GlassPreset Preset)[]
        {
            (GlassPresetAuto, GlassPreset.Auto),
            (GlassPresetHigh, GlassPreset.High),
            (GlassPresetBalanced, GlassPreset.Balanced),
            (GlassPresetPerformance, GlassPreset.Performance),
            (GlassPresetOff, GlassPreset.Off)
        };
        foreach (var (button, value) in buttons)
        {
            var selected = value == preset;
            button.Background = selected ? accent : clear;
            button.Foreground = selected ? onAccent : idle;
            button.FontWeight = selected ? Microsoft.UI.Text.FontWeights.SemiBold : Microsoft.UI.Text.FontWeights.Normal;
        }
    }

    private void ShowGlassHint(GlassPreset preset)
    {
        if (GlassQualityHint is null) return;
        GlassQualityHint.Text = preset switch
        {
            GlassPreset.Auto => "自动 · 启动时按当前硬件选择挡位，也可以随时重新检测",
            GlassPreset.High => "质量 · 全采样，边缘高光与扫光最细腻",
            GlassPreset.Balanced => "平衡 · 0.70 采样，画面与占用兼顾",
            GlassPreset.Performance => "性能 · 0.55 采样，优先保证帧率",
            _ => "已关闭 · 只保留背景星空"
        };
        if (preset != GlassPreset.Auto) GlassResultChip.Visibility = Visibility.Collapsed;
    }

    private void UpdatePerfText()
    {
        if (PerfText is null) return;
        PerfText.Text = LumenRuntime.Hardware is { } report
            ? "硬件：" + report.Summary + " · 当前 " + report.TierLabel + " · 目标 60 FPS"
            : "自适应渲染 · 未做硬件检测 · 目标 60 FPS";
    }

    private void GlassSlider_Changed(object sender, RangeBaseValueChangedEventArgs e)
    {
        if (!_ready) return;
        if (sender is not Slider slider || slider.Tag is not string field) return;
        if (ShellPage.Current?.GlassBackdrop is not { } backdrop) return;
        var glass = backdrop.Glass;
        var value = Math.Round(e.NewValue, 2);
        switch (field)
        {
            case "FresnelFactor":
                glass.FresnelFactor = value;
                FresnelValue.Text = value.ToString("0.00");
                break;
            case "GlareFactor":
                glass.GlareFactor = value;
                GlareValue.Text = value.ToString("0.00");
                break;
        }
    }

    private void SyncTheme()
    {
        var light = ThemeManager.IsLight;
        ThemeCombo.SelectedIndex = light ? 1 : 0;
        if (ThemeStatusText is not null)
            ThemeStatusText.Text = light ? "浅色 · 日光玻璃" : "深色 · 液态玻璃";
        if (ThemeDot is not null)
            ThemeDot.Fill = (Brush)Application.Current.Resources[light ? "BrushViolet" : "BrushMint"];
    }

    private void Theme_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (!_ready) return;
        if (ThemeCombo.SelectedItem is not ComboBoxItem item || item.Tag is not string tag) return;
        ThemeManager.Apply(tag, LumenRuntime.Settings.Settings.Accent);
        LumenRuntime.Settings.Settings.Theme = tag;
        LumenRuntime.Settings.Save();
        SyncTheme();
    }

    private void SyncAccent()
    {
        _syncAccent = true;
        try
        {
            if (AccentPicker is not null)
                AccentPicker.Color = ThemeManager.Accent;
            if (AccentStatusText is not null)
                AccentStatusText.Text = AccentName(ThemeManager.Accent);
        }
        finally
        {
            _syncAccent = false;
        }
    }

    private void AccentPreset_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button button || button.Tag is not string hex) return;
        if (!TryParseHex(hex, out var color)) return;
        ApplyAccent(color);
    }

    private void AccentPicker_Loaded(object sender, RoutedEventArgs e)
    {
        _syncAccent = true;
        try
        {
            AccentPicker.Color = ThemeManager.Accent;
        }
        finally
        {
            _syncAccent = false;
        }
    }

    private void AccentPicker_ColorChanged(ColorPicker sender, ColorChangedEventArgs args)
    {
        if (_syncAccent || !_ready) return;
        ApplyAccent(args.NewColor);
    }

    private void ApplyAccent(Color color)
    {
        var hex = $"#{color.R:X2}{color.G:X2}{color.B:X2}";
        ThemeManager.ApplyAccent(hex);
        LumenRuntime.Settings.Settings.Accent = hex;
        LumenRuntime.Settings.Save();
        SyncAccent();
    }

    private static string AccentName(Color color)
    {
        var hex = $"#{color.R:X2}{color.G:X2}{color.B:X2}";
        var name = hex switch
        {
            "#7CEBD6" => "薄荷",
            "#61C8FF" => "天青",
            "#A996FF" => "星紫",
            "#FF8FB8" => "霞粉",
            "#FFC46B" => "暖金",
            "#A8E063" => "青柠",
            _ => "自定义"
        };
        return name + " · " + hex;
    }

    private static bool TryParseHex(string text, out Color color)
    {
        color = default;
        var value = text.Trim().TrimStart('#');
        if (value.Length != 6 && value.Length != 8) return false;
        if (!uint.TryParse(value, System.Globalization.NumberStyles.HexNumber, null, out var raw)) return false;
        if (value.Length == 6)
            color = Color.FromArgb(0xFF, (byte)((raw >> 16) & 0xFF), (byte)((raw >> 8) & 0xFF), (byte)(raw & 0xFF));
        else
            color = Color.FromArgb((byte)((raw >> 24) & 0xFF), (byte)((raw >> 16) & 0xFF), (byte)((raw >> 8) & 0xFF), (byte)(raw & 0xFF));
        return true;
    }


    private void SyncBackgroundMusic()
    {
        var s = LumenRuntime.Settings.Settings;
        if (BackgroundStatusText is not null)
        {
            BackgroundStatusText.Text = s.BackgroundKind switch
            {
                "image" => "图片 · " + Path.GetFileName(s.BackgroundPath),
                "video" => "视频 · " + Path.GetFileName(s.BackgroundPath),
                _ => "默认 · 深色方块"
            };
        }
        if (BackgroundHint is not null)
            BackgroundHint.Text = s.BackgroundKind == "default"
                ? "图片和视频都只保存在本地，不会上传。"
                : "当前背景会在启动时自动恢复；切换主题不会覆盖它。";

        _syncMusic = true;
        try
        {
            if (MusicModeCombo is not null)
                MusicModeCombo.SelectedIndex = s.MusicMode switch { "one" => 0, "shuffle" => 2, _ => 1 };
            if (MusicVolumeSlider is not null)
                MusicVolumeSlider.Value = Math.Clamp(s.MusicVolume, 0, 1);
            if (MusicVolumeText is not null)
                MusicVolumeText.Text = (int)Math.Round(Math.Clamp(s.MusicVolume, 0, 1) * 100) + "%";
            if (MusicHint is not null)
                MusicHint.Text = s.MusicTracks.Count == 0 ? "未导入音乐" : s.MusicTracks.Count + " 首 · " + Controls.BackgroundMusicService.ModeLabel(s.MusicMode);
            if (MusicRememberSwitch is not null)
                MusicRememberSwitch.IsOn = s.MusicRememberPosition;
            if (MusicAutoplaySwitch is not null)
                MusicAutoplaySwitch.IsOn = s.MusicAutoplay;
            RebuildMusicList();
        }
        finally
        {
            _syncMusic = false;
        }
    }

    private void RebuildMusicList()
    {
        if (MusicListPanel is null) return;
        MusicListPanel.Children.Clear();
        var tracks = LumenRuntime.Settings.Settings.MusicTracks;
        if (tracks.Count == 0)
        {
            MusicListPanel.Children.Add(new TextBlock
            {
                Text = "还没有导入音乐。",
                FontSize = 11.5,
                Foreground = (Brush)Application.Current.Resources["BrushTextTertiary"]
            });
            return;
        }
        for (var i = 0; i < tracks.Count; i++)
        {
            var index = i;
            var path = tracks[i];
            var row = new Grid { ColumnSpacing = 8, Padding = new Thickness(10, 6, 8, 6) };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            row.Children.Add(new TextBlock
            {
                Text = Path.GetFileNameWithoutExtension(path),
                FontSize = 12,
                Foreground = (Brush)Application.Current.Resources["BrushTextSecondary"],
                TextTrimming = TextTrimming.CharacterEllipsis,
                VerticalAlignment = VerticalAlignment.Center
            });
            var play = new Button
            {
                Content = "播放",
                Height = 28,
                Padding = new Thickness(10, 0, 10, 0),
                Style = (Style)Application.Current.Resources["GhostButton"]
            };
            play.Click += (_, _) =>
            {
                ShellPage.Current?.Music.PlayIndex(index);
            };
            Grid.SetColumn(play, 1);
            row.Children.Add(play);
            var remove = new Button
            {
                Content = "移除",
                Height = 28,
                Padding = new Thickness(10, 0, 10, 0),
                Style = (Style)Application.Current.Resources["GhostButton"]
            };
            remove.Click += (_, _) =>
            {
                var list = LumenRuntime.Settings.Settings.MusicTracks;
                if (index >= 0 && index < list.Count) list.RemoveAt(index);
                LumenRuntime.Settings.Save();
                ShellPage.Current?.ApplyBackgroundAndMusic();
                SyncBackgroundMusic();
            };
            Grid.SetColumn(remove, 2);
            row.Children.Add(remove);
            MusicListPanel.Children.Add(new Border
            {
                CornerRadius = new CornerRadius(9),
                Background = (Brush)Application.Current.Resources["BrushFillSubtle"],
                BorderBrush = (Brush)Application.Current.Resources["BrushBorderSubtle"],
                BorderThickness = new Thickness(1),
                Child = row
            });
        }
    }

    private async void BackgroundImage_Click(object sender, RoutedEventArgs e)
        => await PickBackgroundAsync(false);

    private async void BackgroundVideo_Click(object sender, RoutedEventArgs e)
        => await PickBackgroundAsync(true);

    private async Task PickBackgroundAsync(bool video)
    {
        if (MainWindow.Current is null) return;
        try
        {
            var picker = new FileOpenPicker { SuggestedStartLocation = PickerLocationId.PicturesLibrary };
            if (video)
            {
                picker.FileTypeFilter.Add(".mp4");
                picker.FileTypeFilter.Add(".webm");
                picker.FileTypeFilter.Add(".mov");
                picker.FileTypeFilter.Add(".mkv");
            }
            else
            {
                picker.FileTypeFilter.Add(".png");
                picker.FileTypeFilter.Add(".jpg");
                picker.FileTypeFilter.Add(".jpeg");
                picker.FileTypeFilter.Add(".webp");
                picker.FileTypeFilter.Add(".bmp");
            }
            WinRT.Interop.InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(MainWindow.Current));
            var file = await picker.PickSingleFileAsync();
            if (file is null) return;
            var s = LumenRuntime.Settings.Settings;
            var result = await BackgroundEditorDialog.ShowAsync(XamlRoot, file.Path, video,
                video && s.BackgroundVideoSound, s.BackgroundScale, s.BackgroundOffsetX, s.BackgroundOffsetY, s.BackgroundDim);
            if (result is null) return;
            s.BackgroundKind = video ? "video" : "image";
            s.BackgroundPath = file.Path;
            s.BackgroundScale = result.Scale;
            s.BackgroundOffsetX = result.OffsetX;
            s.BackgroundOffsetY = result.OffsetY;
            s.BackgroundDim = result.Dim;
            s.BackgroundVideoSound = result.VideoSound;
            LumenRuntime.Settings.Save();
            await Task.Delay(340);
            ShellPage.Current?.ApplyBackgroundAndMusic();
            SyncBackgroundMusic();
        }
        catch (Exception ex)
        {
            await MessageDialog.ShowAsync(XamlRoot, "无法设置背景", ex.Message);
        }
    }

    private void BackgroundDefault_Click(object sender, RoutedEventArgs e)
    {
        var s = LumenRuntime.Settings.Settings;
        s.BackgroundKind = "default";
        s.BackgroundPath = "";
        LumenRuntime.Settings.Save();
        ShellPage.Current?.ApplyBackgroundAndMusic();
        SyncBackgroundMusic();
    }

    private async void ImportMusic_Click(object sender, RoutedEventArgs e)
    {
        if (MainWindow.Current is null) return;
        try
        {
            var picker = new FileOpenPicker { SuggestedStartLocation = PickerLocationId.MusicLibrary };
            foreach (var ext in new[] { ".mp3", ".wav", ".flac", ".m4a", ".ogg", ".aac", ".wma" })
                picker.FileTypeFilter.Add(ext);
            WinRT.Interop.InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(MainWindow.Current));
            var files = await picker.PickMultipleFilesAsync();
            if (files is null || files.Count == 0) return;
            var list = LumenRuntime.Settings.Settings.MusicTracks;
            foreach (var file in files)
            {
                if (!list.Any(p => string.Equals(p, file.Path, StringComparison.OrdinalIgnoreCase)))
                    list.Add(file.Path);
            }
            LumenRuntime.Settings.Save();
            ShellPage.Current?.ApplyBackgroundAndMusic();
            if (ShellPage.Current is { } shell && shell.Music.HasTracks && !shell.Music.IsPlaying)
                shell.Music.PlayIndex(0);
            SyncBackgroundMusic();
        }
        catch (Exception ex)
        {
            await MessageDialog.ShowAsync(XamlRoot, "无法导入音乐", ex.Message);
        }
    }

    private void MusicMode_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_syncMusic || !_ready) return;
        if (MusicModeCombo.SelectedItem is not ComboBoxItem item || item.Tag is not string mode) return;
        LumenRuntime.Settings.Settings.MusicMode = mode;
        LumenRuntime.Settings.Save();
        ShellPage.Current?.Music.SetMode(mode);
        if (MusicHint is not null)
            MusicHint.Text = LumenRuntime.Settings.Settings.MusicTracks.Count + " 首 · " + Controls.BackgroundMusicService.ModeLabel(mode);
    }

    private void MusicVolume_Changed(object sender, RangeBaseValueChangedEventArgs e)
    {
        if (_syncMusic || !_ready) return;
        var volume = Math.Clamp(e.NewValue, 0, 1);
        LumenRuntime.Settings.Settings.MusicVolume = volume;
        LumenRuntime.Settings.Save();
        if (ShellPage.Current is { } shell) shell.Music.Volume = volume;
        if (MusicVolumeText is not null) MusicVolumeText.Text = (int)Math.Round(volume * 100) + "%";
    }

    private void MusicRemember_Toggled(object sender, RoutedEventArgs e)
    {
        if (_syncMusic || !_ready) return;
        LumenRuntime.Settings.Settings.MusicRememberPosition = MusicRememberSwitch.IsOn;
        LumenRuntime.Settings.Save();
    }

    private void MusicAutoplay_Toggled(object sender, RoutedEventArgs e)
    {
        if (_syncMusic || !_ready) return;
        LumenRuntime.Settings.Settings.MusicAutoplay = MusicAutoplaySwitch.IsOn;
        LumenRuntime.Settings.Save();
    }

    private void MusicClear_Click(object sender, RoutedEventArgs e)
    {
        LumenRuntime.Settings.Settings.MusicTracks.Clear();
        LumenRuntime.Settings.Save();
        ShellPage.Current?.ApplyBackgroundAndMusic();
        SyncBackgroundMusic();
    }

    private void SyncDownloadThreads()
    {
        var threads = Math.Clamp(LumenRuntime.Settings.Settings.DownloadThreads, 2, 8);
        DownloadThreadsCombo.SelectedIndex = threads <= 2 ? 0 : threads <= 4 ? 1 : threads <= 6 ? 2 : 3;
        DownloadThreadsHint.Text = "并行分片 · 当前 " + threads + " 线程";
    }

    private void DownloadThreads_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (!_ready) return;
        if (DownloadThreadsCombo.SelectedItem is not ComboBoxItem item || item.Tag is not string tag) return;
        if (!int.TryParse(tag, out var threads)) return;
        threads = Math.Clamp(threads, 2, 8);
        LumenRuntime.Settings.Settings.DownloadThreads = threads;
        LumenRuntime.Modrinth.MaxParallelChunks = threads;
        LumenRuntime.Settings.Save();
        if (DownloadThreadsHint is not null) DownloadThreadsHint.Text = "并行分片 · 当前 " + threads + " 线程";
    }

    private void SyncGlassControls()
    {
        // 玻璃参数回显：挡位以设置里的字符串为准；auto 时直接用启动链算好的检测结果，
        // 打开设置页不再重新扫一遍硬件，只把已有的结论摆出来。
        if (ShellPage.Current?.GlassBackdrop is { } backdrop)
        {
            FresnelValue.Text = backdrop.Glass.FresnelFactor.ToString("0.00");
            GlareValue.Text = backdrop.Glass.GlareFactor.ToString("0.00");
        }

        var preset = GlassPresets.Parse(LumenRuntime.Settings.Settings.GlassQuality);
        RenderGlassPreset(preset);
        ApplyGlassQualityToBackdrop(LumenRuntime.ApplyGlassProfile(preset, LumenRuntime.Hardware));
        ShowGlassHint(preset);

        if (preset == GlassPreset.Auto && LumenRuntime.Hardware is { } report)
        {
            GlassResultText.Text = "已选择：" + report.TierLabel + "（" + report.Summary + "）";
            GlassResultChip.Visibility = Visibility.Visible;
        }
        UpdatePerfText();
    }

    private void Category_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button button && button.Tag is string tag) ShowPanel(tag);
    }

    private void SyncIsolation()
    {
        _syncIsolation = true;
        DefaultIsolationSwitch.IsOn = LumenRuntime.Settings.Settings.DefaultIsolation;
        _syncIsolation = false;
    }

    private void DefaultIsolation_Toggled(object sender, RoutedEventArgs e)
    {
        if (_syncIsolation || !_ready) return;
        LumenRuntime.Settings.Settings.DefaultIsolation = DefaultIsolationSwitch.IsOn;
        LumenRuntime.Settings.Save();
    }

    private void ShowPanel(string tag)
    {
        // 实验性功能关闭时「插件」页不存在；任何指向它的调用（含重启前残留的偏好）都落到「实验性」页。
        if (!LumenRuntime.PluginsEnabled && string.Equals(tag, "plugins", StringComparison.Ordinal))
            tag = "experimental";

        SettingsScroll.ChangeView(null, 0, null, true);
        var active = tag switch
        {
            "game" => (FrameworkElement)GamePanel,
            "plugins" => PluginsPanel,
            "appearance" => AppearancePanel,
            "experimental" => ExperimentalPanel,
            "about" => AboutPanel,
            _ => AccountsPanel
        };
        AccountsPanel.Visibility = active == AccountsPanel ? Visibility.Visible : Visibility.Collapsed;
        GamePanel.Visibility = active == GamePanel ? Visibility.Visible : Visibility.Collapsed;
        PluginsPanel.Visibility = active == PluginsPanel ? Visibility.Visible : Visibility.Collapsed;
        AppearancePanel.Visibility = active == AppearancePanel ? Visibility.Visible : Visibility.Collapsed;
        ExperimentalPanel.Visibility = active == ExperimentalPanel ? Visibility.Visible : Visibility.Collapsed;
        AboutPanel.Visibility = active == AboutPanel ? Visibility.Visible : Visibility.Collapsed;
        // Re-parent only the active panel. Keeping collapsed siblings in the same Grid leaves
        // stale composition tiles behind the glass cards, which reads as a ghost of the old page.
        SettingsPanelHost.Children.Clear();
        SettingsPanelHost.Children.Add(active);
        SettingsScroll.UpdateLayout();
        active.UpdateLayout();
        Motion.Enter(active, 0, 0.99f);
        DispatcherQueue.TryEnqueue(() =>
        {
            SettingsScroll.UpdateLayout();
            active.InvalidateMeasure();
        });
        foreach (var (button, key) in new[]
                 {
                     (CategoryAccounts, "accounts"), (CategoryGame, "game"),
                     (CategoryPlugins, "plugins"),
                     (CategoryAppearance, "appearance"), (CategoryExperimental, "experimental"),
                     (CategoryAbout, "about")
                 })
        {
            var on = tag == key;
            button.Background = on
                ? (Brush)Application.Current.Resources["BrushAccentSoft"]
                : new SolidColorBrush(Windows.UI.Color.FromArgb(0, 0, 0, 0));
            button.Foreground = (Brush)Application.Current.Resources[on ? "BrushText" : "BrushTextSecondary"];
        }

        if (tag == "plugins") ReloadPlugins();
    }

    private void ReloadAccounts()
    {
        AccountList.Children.Clear();
        var accounts = LumenRuntime.Accounts.PublicAccounts();
        AccountCountText.Text = accounts.Count == 0 ? "没有账户" : accounts.Count + " 个账户";
        EmptyAccounts.Visibility = accounts.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        foreach (var account in accounts)
            AccountList.Children.Add(BuildAccountRow(account));
    }

    private UIElement BuildAccountRow(AccountPublic account)
    {
        var card = new Border
        {
            Background = account.IsSelected
                ? (Brush)Application.Current.Resources["BrushAccentSoft"]
                : new SolidColorBrush(Windows.UI.Color.FromArgb(12, 255, 255, 255)),
            BorderBrush = account.IsSelected
                ? (Brush)Application.Current.Resources["BrushMint"]
                : new SolidColorBrush(Windows.UI.Color.FromArgb(25, 255, 255, 255)),
            BorderThickness = new Thickness(account.IsSelected ? 1.4 : 1),
            CornerRadius = new CornerRadius(16),
            Padding = new Thickness(12, 10, 10, 10)
        };

        var grid = new Grid { ColumnSpacing = 12 };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        grid.Children.Add(Avatar(account));

        var info = new StackPanel { Spacing = 3, VerticalAlignment = VerticalAlignment.Center };
        var nameRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        nameRow.Children.Add(new TextBlock { Text = account.Name, FontSize = 14.5, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold });
        nameRow.Children.Add(new Border
        {
            Style = (Style)Application.Current.Resources["Pill"],
            Padding = new Thickness(8, 2, 8, 2),
            VerticalAlignment = VerticalAlignment.Center,
            Child = new TextBlock { Text = account.Type, FontSize = 10.5, Foreground = (Brush)Application.Current.Resources["BrushTextSecondary"] }
        });
        if (account.IsSelected)
            nameRow.Children.Add(new Border
            {
                Style = (Style)Application.Current.Resources["Pill"],
                Background = (Brush)Application.Current.Resources["BrushAccentSoft"],
                Padding = new Thickness(8, 2, 8, 2),
                Child = new TextBlock { Text = "启动账户", FontSize = 10.5, Foreground = (Brush)Application.Current.Resources["BrushMint"] }
            });
        info.Children.Add(nameRow);
        info.Children.Add(new TextBlock
        {
            Text = account.Subtitle,
            FontSize = 11.5,
            Foreground = (Brush)Application.Current.Resources["BrushTextTertiary"],
            TextTrimming = TextTrimming.CharacterEllipsis
        });
        Grid.SetColumn(info, 1);
        grid.Children.Add(info);

        var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, VerticalAlignment = VerticalAlignment.Center };
        if (!account.IsSelected)
        {
            var select = SmallButton("设为启动");
            select.Click += (_, _) => { LumenRuntime.Accounts.Select(account.Id); ReloadAccounts(); };
            actions.Children.Add(select);
        }
        var rename = SmallButton("重命名");
        rename.Click += async (_, _) => await RenameAccountAsync(account.Id);
        actions.Children.Add(rename);
        // 皮肤入口：离线账户套用正版/本地皮肤，正版账户把本地皮肤传上去。
        var skinLabel = SkinButtonLabel(account);
        if (skinLabel.Length > 0)
        {
            var skin = SmallButton(skinLabel);
            skin.Click += async (_, _) =>
            {
                if (await SkinDialog.ShowAsync(XamlRoot, account.Id)) ReloadAccounts();
            };
            actions.Children.Add(skin);
        }
        if (account.Type == "NovaLink · Microsoft" || account.Type == "NovaLink")
        {
            var bind = SmallButton(account.HasMicrosoft ? "解绑 Microsoft" : "绑定 Microsoft");
            bind.Click += async (_, _) => await ToggleBindAsync(account);
            actions.Children.Add(bind);
        }
        var remove = SmallButton("删除");
        remove.Foreground = (Brush)Application.Current.Resources["BrushDanger"];
        remove.Click += async (_, _) => await RemoveAccountAsync(account.Id);
        actions.Children.Add(remove);
        Grid.SetColumn(actions, 2);
        grid.Children.Add(actions);

        card.Child = grid;
        return card;
    }

    private static Button SmallButton(string text) => new()
    {
        Content = text,
        Style = (Style)Application.Current.Resources["GhostButton"],
        Height = 32,
        // 最小宽度 + 左右留 14 + 字号 12.5：这几个按钮原来 46x30、字号 11.5，
        // 两个汉字只有 23px 宽，浅色下看起来糊成一团。
        MinWidth = 64,
        Padding = new Thickness(14, 0, 14, 0),
        FontSize = 12.5,
        HorizontalContentAlignment = HorizontalAlignment.Center
    };

    /// <summary>账户卡上的皮肤按钮文案；返回空串表示这个账户不支持换皮肤。</summary>
    private static string SkinButtonLabel(AccountPublic account)
    {
        // 离线账户：套用正版/本地皮肤。
        if (account.Type == "离线账户") return "皮肤";
        // Microsoft 正版与绑定了 Microsoft 的 NovaLink 账户：把本地皮肤上传到正版账号。
        if (account.Type == "Microsoft" || (account.Type.StartsWith("NovaLink", StringComparison.Ordinal) && account.HasMicrosoft))
            return "上传到正版";
        return "";
    }

    private static UIElement Avatar(AccountPublic account)
    {
        var host = new Grid { Width = 42, Height = 42 };
        var fallback = new Border
        {
            CornerRadius = new CornerRadius(12),
            Background = (Brush)Application.Current.Resources["BrushAccentSoft"],
            BorderBrush = new SolidColorBrush(Windows.UI.Color.FromArgb(28, 255, 255, 255)),
            BorderThickness = new Thickness(1),
            Child = new TextBlock
            {
                Text = string.IsNullOrWhiteSpace(account.Initial) ? "N" : account.Initial,
                FontSize = 15,
                FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            }
        };
        host.Children.Add(fallback);
        if (!string.IsNullOrWhiteSpace(account.AvatarUrl))
        {
            try
            {
                var image = new Image
                {
                    Width = 42,
                    Height = 42,
                    Stretch = Stretch.UniformToFill,
                    Source = new BitmapImage(new Uri(account.AvatarUrl))
                };
                image.ImageFailed += (_, _) => image.Visibility = Visibility.Collapsed;
                var clip = new Border { CornerRadius = new CornerRadius(12), Child = image };
                host.Children.Add(clip);
            }
            catch { }
        }
        return host;
    }

    private async Task RenameAccountAsync(string id)
    {
        var account = LumenRuntime.Settings.Find(id);
        if (account is null) return;
        var name = await TextPromptDialog.ShowAsync(XamlRoot, "重命名账户", "账户名称", account.Name, 32);
        if (string.IsNullOrWhiteSpace(name)) return;
        try
        {
            LumenRuntime.Accounts.Rename(account, name);
            ReloadAccounts();
        }
        catch (Exception ex)
        {
            await MessageDialog.ShowAsync(XamlRoot, "重命名失败", ex.Message);
        }
    }

    private async Task RemoveAccountAsync(string id)
    {
        var account = LumenRuntime.Settings.Find(id);
        if (account is null) return;
        var modal = GlassModal.Show("删除账户", "将移除 " + account.Name + " 的本地登录信息，不会影响游戏存档。", 420);
        var confirmed = false;
        modal.PrimaryText = "删除";
        modal.SecondaryText = "取消";
        modal.PrimaryInvoked = async () => { confirmed = true; modal.Close(); await Task.CompletedTask; };
        modal.SecondaryInvoked = async () => { modal.Close(); await Task.CompletedTask; };
        await modal.WaitAsync();
        if (!confirmed) return;
        LumenRuntime.Accounts.Remove(account);
        ReloadAccounts();
    }

    private async Task ToggleBindAsync(AccountPublic publicAccount)
    {
        var account = LumenRuntime.Settings.Find(publicAccount.Id);
        if (account is null) return;
        try
        {
            if (account.Microsoft is not null)
            {
                await LumenRuntime.Accounts.UnbindMicrosoftAsync(account);
                ReloadAccounts();
                return;
            }
            var code = await MicrosoftLoginWindow.ShowAsync();
            if (string.IsNullOrWhiteSpace(code)) return;
            await LumenRuntime.Microsoft.CompleteLoginAsync(code);
            var microsoft = LumenRuntime.Accounts.Selected;
            if (microsoft is null || microsoft.Type != "microsoft") throw new InvalidOperationException("没有取得本地 Microsoft 账户");
            await LumenRuntime.Accounts.BindMicrosoftAsync(account, microsoft);
            LumenRuntime.Accounts.Select(account.Id);
            ReloadAccounts();
            await MessageDialog.ShowAsync(XamlRoot, "绑定成功", "NovaLink 账户现在使用 " + microsoft.Name + " 的 Minecraft 档案。");
        }
        catch (Exception ex)
        {
            await MessageDialog.ShowAsync(XamlRoot, "绑定失败", ex.Message);
        }
    }

    private async void AddOffline_Click(object sender, RoutedEventArgs e)
    {
        var name = await OfflineAccountDialog.ShowAsync(XamlRoot);
        if (string.IsNullOrWhiteSpace(name)) return;
        try
        {
            await LumenRuntime.Accounts.CreateOfflineAsync(name);
            ReloadAccounts();
        }
        catch (Exception ex) { await MessageDialog.ShowAsync(XamlRoot, "创建失败", ex.Message); }
    }

    private async void AddMicrosoft_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var code = await MicrosoftLoginWindow.ShowAsync();
            if (string.IsNullOrWhiteSpace(code)) return;
            var account = await LumenRuntime.Microsoft.CompleteLoginAsync(code);
            ReloadAccounts();
            await MessageDialog.ShowAsync(XamlRoot, "Microsoft 已登录", account.Name + " 已添加。");
        }
        catch (Exception ex) { await MessageDialog.ShowAsync(XamlRoot, "Microsoft 登录失败", ex.Message); }
    }

    private async void AddLittleSkin_Click(object sender, RoutedEventArgs e)
    {
        // 登录成功后账户已写入并设为启动账户，这里只需要刷新列表。
        var account = await LittleSkinLoginDialog.ShowAsync(XamlRoot);
        if (account is null) return;
        ReloadAccounts();
        await MessageDialog.ShowAsync(XamlRoot, "LittleSkin 已登录",
            account.Name + " 已添加为启动账户，皮肤与头像已保存到本地。");
    }

    private async void AddNovalink_Click(object sender, RoutedEventArgs e)
    {
        var account = await NovalinkLoginDialog.ShowAsync(XamlRoot);
        if (account is not null) ReloadAccounts();
    }

    private async void ChangeRoot_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var picker = new FolderPicker();
            picker.FileTypeFilter.Add("*");
            var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(MainWindow.Current);
            WinRT.Interop.InitializeWithWindow.Initialize(picker, hwnd);
            var folder = await picker.PickSingleFolderAsync();
            if (folder is null) return;
            RootText.Text = folder.Path;
            if (ShellPage.Current is { } shell)
                await shell.SwitchGameRootAsync(folder.Path);
            else
            {
                LumenRuntime.Settings.Settings.GameRoot = folder.Path;
                LumenRuntime.Settings.Save();
                await LumenRuntime.Instances.ScanAsync();
            }
        }
        catch (Exception ex) { await MessageDialog.ShowAsync(XamlRoot, "切换失败", ex.Message); }
    }

    private void OpenRoot_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var path = LumenRuntime.Settings.Settings.GameRoot;
            if (string.IsNullOrWhiteSpace(path)) return;
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("explorer.exe", "\"" + path + "\"") { UseShellExecute = true });
        }
        catch { }
    }

    private async Task ReloadJavaAsync(bool force)
    {
        JavaList.Children.Clear();
        JavaHint.Text = "正在检测 Java…";
        var runtimes = await LumenRuntime.Java.DetectAsync(force);
        JavaHint.Text = runtimes.Count == 0 ? "没有找到 Java。启动时会按需自动准备。" : "已找到 " + runtimes.Count + " 个 Java 运行时，点击选择启动时优先使用的一个。";
        var current = _javaPath;
        foreach (var java in runtimes)
        {
            var selected = string.Equals(java.Path, current, StringComparison.OrdinalIgnoreCase);
            var button = new Button
            {
                HorizontalAlignment = HorizontalAlignment.Stretch,
                HorizontalContentAlignment = HorizontalAlignment.Left,
                Background = selected ? (Brush)Application.Current.Resources["BrushAccentSoft"] : new SolidColorBrush(Windows.UI.Color.FromArgb(10, 255, 255, 255)),
                BorderBrush = selected ? (Brush)Application.Current.Resources["BrushMint"] : new SolidColorBrush(Windows.UI.Color.FromArgb(20, 255, 255, 255)),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(14),
                Padding = new Thickness(14, 10, 14, 10)
            };
            var stack = new StackPanel { Spacing = 3 };
            stack.Children.Add(new TextBlock { Text = "Java " + java.Major + "  ·  " + java.Version, FontSize = 13.5, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold });
            stack.Children.Add(new TextBlock { Text = java.Path, FontSize = 11, Foreground = (Brush)Application.Current.Resources["BrushTextTertiary"], TextTrimming = TextTrimming.CharacterEllipsis });
            button.Content = stack;
            button.Click += (_, _) =>
            {
                _javaPath = java.Path;
                LumenRuntime.Settings.Settings.JavaPath = java.Path;
                LumenRuntime.Settings.Save();
                _ = ReloadJavaAsync(false);
            };
            JavaList.Children.Add(button);
        }
    }

    private async void InstallJava_Click(object sender, RoutedEventArgs e)
    {
        AppLog.Info("java", "点击自动安装 Java");
        var modal = GlassModal.Show("准备 Java 21", "将从 Adoptium 下载 Eclipse Temurin JDK，并自动写入启动设置。", 460);
        modal.HideButtons();
        modal.SetCloseAllowed(false);
        var bar = new ProgressBar
        {
            Minimum = 0,
            Maximum = 100,
            Value = 0,
            Height = 6,
            Margin = new Thickness(0, 4, 0, 0),
            Foreground = (Brush)Application.Current.Resources["BrushMint"]
        };
        var stage = new TextBlock { Text = "正在读取版本信息…", FontSize = 12.5, TextWrapping = TextWrapping.Wrap, Foreground = (Brush)Application.Current.Resources["BrushTextSecondary"] };
        modal.Body.Children.Add(bar);
        modal.Body.Children.Add(stage);
        var progress = new Progress<DownloadProgress>(p =>
        {
            bar.Value = Math.Clamp(p.Percent, 0, 100);
            var text = p.Stage;
            if (p.Total > 0) text += " · " + Math.Round(p.Percent) + "%";
            if (p.BytesPerSecond > 0) text += " · " + (p.BytesPerSecond / 1048576.0).ToString("0.0") + " MB/s";
            if (!string.IsNullOrWhiteSpace(p.Message)) text += " · " + p.Message;
            stage.Text = text;
        });
        try
        {
            var runtime = await Task.Run(() => LumenRuntime.JavaInstaller.EnsureAsync(21, progress));
            modal.Close();
            if (runtime is not null)
            {
                _javaPath = runtime.Path;
                await ReloadJavaAsync(true);
                await MessageDialog.ShowAsync(XamlRoot, "Java 已就绪", "Java " + runtime.Major + " · " + runtime.Version + "\n" + runtime.Path);
            }
            else
            {
                await MessageDialog.ShowAsync(XamlRoot, "Java 未就绪", "没有找到可用的 Java，也没有完成自动安装。");
            }
        }
        catch (Exception ex)
        {
            AppLog.Error("java", "自动安装 Java 失败", ex);
            modal.Close();
            await MessageDialog.ShowAsync(XamlRoot, "Java 安装失败", string.IsNullOrWhiteSpace(ex.Message) ? ex.GetType().Name : ex.Message);
        }
    }

    private async void DetectJava_Click(object sender, RoutedEventArgs e) => await ReloadJavaAsync(true);

    private void MemorySlider_ValueChanged(object sender, Microsoft.UI.Xaml.Controls.Primitives.RangeBaseValueChangedEventArgs e)
        => UpdateMemoryLabel();

    private void UpdateMemoryLabel()
    {
        if (MemoryValue is null) return;
        var value = (int)Math.Round(MemorySlider.Value / 512.0) * 512;
        MemoryValue.Text = value + " MB";
    }

    private async void SaveRuntime_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var settings = LumenRuntime.Settings.Settings;
            settings.Memory = (int)Math.Round(MemorySlider.Value / 512.0) * 512;
            settings.JavaArgs = JavaArgsBox.Text.Trim();
            LumenRuntime.Settings.Save();
            await MessageDialog.ShowAsync(XamlRoot, "已保存", "内存与 JVM 设置将在下次启动时生效。");
        }
        catch (Exception ex) { await MessageDialog.ShowAsync(XamlRoot, "保存失败", ex.Message); }
    }

    private async void CheckUpdate_Click(object sender, RoutedEventArgs e)
        => await UpdateDialog.CheckAsync(false);

    private async void GitHub_Click(object sender, RoutedEventArgs e)
        => await Windows.System.Launcher.LaunchUriAsync(new Uri("https://github.com/Eric8844/novalink-lumen-releases"));

    /// <summary>刷新「数据备份与恢复」卡片：只显示可用的升级前备份，绝不暴露内部异常文本。</summary>
    private void RefreshBackupStatus()
    {
        try
        {
            if (BackupStatusText is null || RestoreBackupButton is null) return;

            var backups = UpgradeGuard.BuildRestorePlan(LumenRuntime.Paths);
            if (backups.Count == 0)
            {
                BackupStatusText.Text = "当前没有可恢复的升级前备份。";
                RestoreBackupButton.IsEnabled = false;
                return;
            }

            var latest = backups[0];
            var version = ReadBackupSourceVersion(latest);
            BackupStatusText.Text = version is null
                ? "可恢复备份 " + backups.Count + " 份 · 最新一份在 " + latest
                : "可恢复备份 " + backups.Count + " 份 · 最新一份来自 V" + version + "（" + latest + "）";
            RestoreBackupButton.IsEnabled = true;
        }
        catch
        {
            if (BackupStatusText is not null) BackupStatusText.Text = "暂时无法读取备份列表。";
            if (RestoreBackupButton is not null) RestoreBackupButton.IsEnabled = false;
        }
    }

    private static string? ReadBackupSourceVersion(string backupRoot)
    {
        try
        {
            var marker = Path.Combine(backupRoot, UpgradeGuard.BackupInfoFileName);
            if (!File.Exists(marker)) return null;
            using var document = JsonDocument.Parse(File.ReadAllText(marker));
            if (document.RootElement.ValueKind != JsonValueKind.Object) return null;
            return document.RootElement.TryGetProperty("sourceVersion", out var value)
                ? value.GetString()
                : null;
        }
        catch
        {
            return null;
        }
    }

    private async void RestoreBackup_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var backups = UpgradeGuard.BuildRestorePlan(LumenRuntime.Paths);
            if (backups.Count == 0)
            {
                RefreshBackupStatus();
                await MessageDialog.ShowAsync(XamlRoot, "没有可恢复的备份", "启动器还没有做过升级前备份。升级到新版本时会自动备份。");
                return;
            }

            var latest = backups[0];
            var modal = GlassModal.Show("从备份恢复数据",
                "会先把现在的数据整体改名保留（不会删除），再把 " + Path.GetFileName(latest) +
                " 里的数据换回来。启动器随后会重启一次。", 480);
            var confirmed = false;
            modal.PrimaryText = "恢复并重启";
            modal.SecondaryText = "取消";
            modal.PrimaryInvoked = async () => { confirmed = true; modal.Close(); await Task.CompletedTask; };
            modal.SecondaryInvoked = async () => { modal.Close(); await Task.CompletedTask; };
            await modal.WaitAsync();
            if (!confirmed) return;

            // 恢复必须在服务初始化之前完成，所以只能带着参数重启，不能在本进程里做。
            var options = StartupOptions.Current;
            if (!LauncherRestart.Restart(options.BuildArguments(options.SafeMode, restoreDataBackup: true)))
            {
                await MessageDialog.ShowAsync(XamlRoot, "无法重启启动器", "请手动关闭后重新打开启动器再试。");
                return;
            }
            Application.Current.Exit();
        }
        catch
        {
            await MessageDialog.ShowAsync(XamlRoot, "恢复失败", "暂时无法从备份恢复。当前数据没有被删除，可以稍后再试。");
        }
    }

    private void OpenProgressHistory_Click(object sender, RoutedEventArgs e) => ProgressCenter.OpenOverlay();

    private void OpenData_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("explorer.exe", "\"" + LumenRuntime.Paths.Root + "\"") { UseShellExecute = true });
        }
        catch { }
    }
}