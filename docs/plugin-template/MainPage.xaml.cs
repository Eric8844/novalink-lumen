using System;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using NovaLink.Lumen.Core.Plugins;

namespace Hello.World;

/// <summary>插件自带的页面。注意：这里用的是插件自己程序集里的类型，宿主通过 .pri 解析。</summary>
public sealed partial class MainPage : UserControl
{
    private readonly IPluginContext _ctx;

    public MainPage(IPluginContext context)
    {
        _ctx = context;
        InitializeComponent();
    }

    private async void OnPingClick(object sender, RoutedEventArgs e)
    {
        try
        {
            StatusText.Text = "正在请求…";
            var result = await _ctx.Host.HttpGetAsync("https://api.github.com/repos/Eric8844/novalink-lumen-releases");
            StatusText.Text = result.IsSuccess
                ? "宿主 API 正常（HTTP " + result.StatusCode + "）"
                : "宿主拒绝了请求（HTTP " + result.StatusCode + "）";
        }
        catch (Exception ex)
        {
            // 插件自己的异常必须自己兜住：宿主只会禁用它，不会让整个启动器崩。
            _ctx.Log.Error("请求失败", ex);
            StatusText.Text = "请求失败，详情见插件日志。";
        }
    }
}
