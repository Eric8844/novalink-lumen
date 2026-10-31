using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Hosting;
using Microsoft.UI.Xaml.Media;
using System;
using System.Numerics;
using System.Threading.Tasks;
using Windows.Graphics;
using Windows.UI;

namespace NovaLink.Lumen;

public sealed partial class MainWindow : Window
{
    public static new MainWindow? Current { get; private set; }

    public Grid ModalRoot => ModalLayer;

    private bool _started;

    public MainWindow()
    {
        InitializeComponent();
        Current = this;
        ApplyTheme(ThemeManager.IsLight);
        Title = "NovaLink Lumen";
        ExtendsContentIntoTitleBar = true;
        SetTitleBar(DragRegion);
        AppWindow.SetIcon(System.IO.Path.Combine(AppContext.BaseDirectory, "Assets", "AppIcon.ico"));

        if (AppWindowTitleBar.IsCustomizationSupported())
        {
            var bar = AppWindow.TitleBar;
            bar.ButtonBackgroundColor = Colors.Transparent;
            bar.ButtonInactiveBackgroundColor = Colors.Transparent;
            bar.ButtonForegroundColor = Color.FromArgb(255, 220, 230, 244);
            bar.ButtonInactiveForegroundColor = Color.FromArgb(255, 110, 122, 142);
            bar.ButtonHoverBackgroundColor = Color.FromArgb(40, 255, 255, 255);
            bar.ButtonPressedBackgroundColor = Color.FromArgb(70, 255, 255, 255);
        }

        CenterOnScreen(1460, 920);
        Activated += MainWindow_Activated;
        Closed += (_, _) => { if (ReferenceEquals(Current, this)) Current = null; };
    }

    public void ApplyTheme(bool light)
    {
        RootLayout.RequestedTheme = light ? ElementTheme.Light : ElementTheme.Dark;
        RootLayout.Background = new SolidColorBrush(ThemeManager.Background);
        SplashMint.Opacity = light ? 0.42 : 1.0;
        SplashViolet.Opacity = light ? 0.36 : 1.0;
        if (AppWindowTitleBar.IsCustomizationSupported())
        {
            var bar = AppWindow.TitleBar;
            bar.ButtonBackgroundColor = Colors.Transparent;
            bar.ButtonInactiveBackgroundColor = Colors.Transparent;
            bar.ButtonForegroundColor = light ? Color.FromArgb(255, 31, 45, 60) : Color.FromArgb(255, 220, 230, 244);
            bar.ButtonInactiveForegroundColor = light ? Color.FromArgb(255, 128, 143, 160) : Color.FromArgb(255, 110, 122, 142);
            bar.ButtonHoverBackgroundColor = light ? Color.FromArgb(24, 20, 40, 60) : Color.FromArgb(40, 255, 255, 255);
            bar.ButtonPressedBackgroundColor = light ? Color.FromArgb(40, 20, 40, 60) : Color.FromArgb(70, 255, 255, 255);
        }
        Shell?.ApplyTheme(light);
    }

    private async void MainWindow_Activated(object sender, WindowActivatedEventArgs args)
    {
        if (_started) return;
        _started = true;
        await Task.Delay(650);
        SplashStatus.Text = "正在读取实例与账户…";
        await Task.Delay(520);
        var visual = ElementCompositionPreview.GetElementVisual(SplashLayer);
        var fade = visual.Compositor.CreateScalarKeyFrameAnimation();
        fade.InsertKeyFrame(1f, 0f);
        fade.Duration = TimeSpan.FromMilliseconds(420);
        visual.StartAnimation("Opacity", fade);
        await Task.Delay(430);
        SplashLayer.Visibility = Visibility.Collapsed;
        if (!ShowUpdateCompleteIfNeeded()) ShowWelcomeIfNeeded();
        _ = AutoCheckAsync();
    }

    private async Task AutoCheckAsync()
    {
        await Task.Delay(TimeSpan.FromSeconds(6));
        if (Views.Dialogs.GlassModal.Current is null)
            await Views.Dialogs.UpdateDialog.CheckAsync(true);
    }

    private bool ShowUpdateCompleteIfNeeded()
    {
        try
        {
            var marker = System.IO.Path.Combine(LumenRuntime.Paths.Root, "update-complete.txt");
            if (!System.IO.File.Exists(marker)) return false;
            var version = System.IO.File.ReadAllText(marker).Trim();
            System.IO.File.Delete(marker);
            Views.Dialogs.StartupDialog.ShowUpdateComplete(version);
            return true;
        }
        catch { return false; }
    }

    private void ShowWelcomeIfNeeded()
    {
        try { Views.Dialogs.StartupDialog.ShowWelcomeIfNeeded(Shell); }
        catch { }
    }

    private void CenterOnScreen(int width, int height)
    {
        var area = DisplayArea.GetFromWindowId(AppWindow.Id, DisplayAreaFallback.Primary);
        var work = area.WorkArea;
        var w = Math.Min(width, work.Width - 40);
        var h = Math.Min(height, work.Height - 40);
        AppWindow.MoveAndResize(new RectInt32(
            work.X + (work.Width - w) / 2,
            work.Y + (work.Height - h) / 2,
            w, h));
    }
}