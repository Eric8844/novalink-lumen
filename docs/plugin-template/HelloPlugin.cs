using System;
using NovaLink.Lumen.Core.Plugins;

namespace Hello.World;

/// <summary>
/// 插件入口。宿主会在独立 AssemblyLoadContext 里加载本程序集，实例化这个类型，
/// 然后调用 <see cref="Initialize"/>。
/// </summary>
public sealed class HelloPlugin : ILumenPlugin
{
    private IPluginContext? _ctx;

    public void Initialize(IPluginContext context)
    {
        _ctx = context;
        context.Log.Info("Hello World 插件已加载。");

        // 注入左侧导航栏。页面工厂在用户真正点进来时才执行，避免启动时构造 UI。
        context.Ui.AddNavigation(new PluginNavItem(
            Tag: "plugin:hello.world",
            Title: "示例插件",
            PageFactory: () => new MainPage(context),
            Order: 600,
            Glyph: "\uE8F1"));

        // 订阅启动事件：可以做"每次启动检查一次更新"这类事情。
        context.Subscribe(PluginEvent.AppStartup, _ => context.Log.Info("启动事件已收到。"));
    }

    public void Shutdown()
    {
        _ctx?.Log.Info("Hello World 插件已卸载。");
        _ctx = null;
    }
}
