using System;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;
using NovaLink.Lumen.Plugins;
using NovaLink.Lumen.Services;

namespace NovaLink.Lumen.Views.Pages;

/// <summary>
/// 插件页面的宿主容器：主导航里每个 <c>plugin:</c> 入口都导航到这里，
/// 页面参数是导航项的 tag（例如 <c>plugin:hello.world</c>）。
///
/// 插件页面的实际控件由 <see cref="PluginUiService.CreatePage"/> 创建：
/// 它会先把插件自带的 .pri 挂到宿主，再调用插件自己的工厂方法。
/// 任何失败都不把异常丢给玩家——这里只显示一句人话 + 「重试」「返回首页」。
/// </summary>
public sealed partial class PluginPageHost : Page
{
    private object? _pluginPage;
    private string? _tag;

    public PluginPageHost()
    {
        InitializeComponent();
    }

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        Render(e.Parameter as string);
    }

    protected override void OnNavigatedFrom(NavigationEventArgs e)
    {
        base.OnNavigatedFrom(e);
        DisposePluginPage();
    }

    private void Render(string? tag)
    {
        _tag = tag;
        DisposePluginPage();

        if (string.IsNullOrWhiteSpace(tag))
        {
            ShowFallback("没有找到要打开的插件页面。");
            return;
        }

        PluginTitle.Text = PluginUiService.Instance.TitleFor(tag);
        PluginCaption.Text = "由插件提供的页面";

        try
        {
            var page = PluginUiService.Instance.CreatePage(tag, out var hint);
            if (page is null)
            {
                ShowFallback(hint);
                return;
            }

            if (page is not UIElement element)
            {
                ShowFallback("这个插件没有提供可以显示的页面。");
                return;
            }

            _pluginPage = page;
            HostContent.Content = element;
            HostContent.Visibility = Visibility.Visible;
            FallbackPanel.Visibility = Visibility.Collapsed;
        }
        catch (Exception ex)
        {
            AppLog.Error("plugin-ui", "插件页面渲染失败：" + tag, ex);
            ShowFallback("插件页面加载失败，可以点【重试】再试一次；如果一直失败，请在设置里停用这个插件。");
        }
    }

    private void ShowFallback(string message)
    {
        HostContent.Content = null;
        HostContent.Visibility = Visibility.Collapsed;
        FallbackMessage.Text = string.IsNullOrWhiteSpace(message)
            ? "插件页面加载失败，可以点【重试】再试一次。"
            : message;
        FallbackPanel.Visibility = Visibility.Visible;
    }

    private void DisposePluginPage()
    {
        var page = _pluginPage;
        _pluginPage = null;
        HostContent.Content = null;

        if (page is null) return;

        try
        {
            if (page is IDisposable disposable) disposable.Dispose();
        }
        catch (Exception ex)
        {
            AppLog.Warn("plugin-ui", "释放插件页面时出错：" + ex.Message);
        }
    }

    private void Reload_Click(object sender, RoutedEventArgs e) => Render(_tag);

    private void Retry_Click(object sender, RoutedEventArgs e) => Render(_tag);

    private void Home_Click(object sender, RoutedEventArgs e)
    {
        if (ShellPage.Current is { } shell) shell.Navigate("home", true);
    }
}
