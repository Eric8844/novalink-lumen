using Microsoft.UI.Xaml;
using NovaLink.Lumen.Services;
using System;

namespace NovaLink.Lumen;

public partial class App : Application
{
    private Window? _window;

    public App()
    {
        InitializeComponent();
        UnhandledException += (_, e) =>
        {
            try { AppLog.Error("app", "未处理异常", e.Exception); } catch { }
        };
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        LumenRuntime.Initialize();
        ThemeManager.Initialize(LumenRuntime.Settings.Settings.Theme, LumenRuntime.Settings.Settings.Accent);
        _window = new MainWindow();
        _window.Activate();
    }
}
