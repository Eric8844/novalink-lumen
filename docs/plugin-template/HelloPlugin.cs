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

        // 主题令牌：Name/IsLight/Colors 是宿主当前配色，切换深浅主题时会触发 Theme.Changed。
        // 不要在插件里写死 #RRGGBB —— 浅色主题下会看不清。
        context.Log.Info("Hello World 插件已加载。主题：" + context.Theme.Name
            + "（" + (context.Theme.IsLight ? "浅色" : "深色") + "），可用颜色 "
            + context.Theme.Colors.Count + " 个。");

        // 注入左侧导航栏。页面工厂在用户真正点进来时才执行，避免启动时构造 UI。
        // 页面里演示了宿主玻璃组件：context.Ui.CreateGlassCard / CreateGlassButton / ApplyGlassCard。
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
