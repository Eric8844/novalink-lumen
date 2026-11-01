using System.Collections.Generic;

namespace NovaLink.Lumen.Views.Dialogs;

/// <summary>文档块类型，决定弹窗里怎么渲染。</summary>
public enum DocBlockKind
{
    /// <summary>普通段落。</summary>
    Paragraph,

    /// <summary>列表项目。</summary>
    Bullet,

    /// <summary>等宽代码/路径块。</summary>
    Code,

    /// <summary>强调提示（安全提醒一类）。</summary>
    Note
}

/// <summary>一段文档内容。</summary>
public sealed record DocBlock(DocBlockKind Kind, string Text);

/// <summary>文档里的一节。</summary>
public sealed record DocSection(string Title, IReadOnlyList<DocBlock> Blocks);

/// <summary>
/// 站内文档正文（使用文档 / 开发文档）。
/// 只放面向用户与插件开发者的说明，不放仓库内部路径与构建内部细节。
/// </summary>
public static class DocsContent
{
    private static DocBlock P(string text) => new(DocBlockKind.Paragraph, text);
    private static DocBlock B(string text) => new(DocBlockKind.Bullet, text);
    private static DocBlock C(string text) => new(DocBlockKind.Code, text);
    private static DocBlock N(string text) => new(DocBlockKind.Note, text);
    private static DocSection S(string title, params DocBlock[] blocks) => new(title, blocks);

    /// <summary>使用文档：给普通玩家看。</summary>
    public static DocSection[] Usage { get; } =
    [
        S("插件是什么",
            P("插件是启动器上的扩展功能：可以在左侧导航里加一个自己的页面，也可以改写下载地址、替换界面文字或者弹出通知。"),
            P("插件由你自己安装。某个插件出问题时，只会影响它自己，启动器的其他功能照常可用。")),

        S("安装插件",
            B("在「发现 → 启动器插件」里找到要用的插件，点「安装」，装好后按提示重启启动器。"),
            B("在「设置 → 插件」点「安装插件包…」，选一个插件包文件。"),
            B("也可以点「打开插件目录」，把解压好的插件文件夹直接放进去。"),
            P("装完要重启启动器，插件才会出现在左侧导航里。")),

        S("启用与停用",
            B("「设置 → 插件」里每个插件右侧都有开关，关掉之后下次启动就不再加载它。"),
            B("开关状态会被记住，想再打开时把开关拨回来、重启启动器即可。")),

        S("安全模式",
            P("如果某个插件让启动器启动不起来，启动器会自己切到安全模式：所有插件都不加载，其余功能照常。"),
            B("退出安全模式：「设置 → 插件」→「重新启用插件并重启」。"),
            B("被隔离的插件会单独标成「已隔离」，点它右边的「清除隔离」再重启，就会重新尝试加载。"),
            B("只想临时排查是不是插件引起的问题，可以用安全模式启动一次。")),

        S("插件页面打不开怎么办",
            B("先点页面右上角的「重新加载」。"),
            B("还是不行就重启启动器；如果一直失败，在「设置 → 插件」里把这个插件停用。"),
            P("页面出错只影响这一个插件，不会影响启动器的其他功能。")),

        S("更新与卸载",
            B("有新版时，「启动器插件」里的卡片会显示「更新」，点一下就能装上。"),
            B("卸载：「设置 → 插件」→「打开插件目录」，删掉对应的文件夹，然后重启启动器。")),

        S("插件放在哪",
            P("插件本身和插件保存的数据都在启动器的数据目录里："),
            C("%LOCALAPPDATA%\\NovaLink Lumen\\LauncherData\\plugins\\        （插件本体）\n%LOCALAPPDATA%\\NovaLink Lumen\\LauncherData\\plugin-data\\     （插件保存的数据）"),
            P("升级启动器不会动这两个目录，卸载启动器也不会顺手删掉插件。")),

        S("安全提醒",
            N("插件运行在启动器进程里，能看到启动器能看到的东西，包括你的游戏账号登录信息。只安装你信任来源的插件，来源不明的不要装。"))
    ];

    /// <summary>开发文档：给写插件的人看。</summary>
    public static DocSection[] Development { get; } =
    [
        S("三类插件",
            B("类型 A｜C# + 原生界面：编译成 DLL，自带 .pri 资源，页面直接进左侧导航。"),
            B("类型 B｜脚本：写一个 .js 文件，用 Jint 运行，适合做下载源改写、文本替换、通知这类轻量扩展。"),
            B("类型 C｜底层依赖：可以带第三方 DLL，但原生库同名冲突默认拒绝加载，避免把启动器带崩。")),

        S("目录结构",
            P("一个插件就是一个文件夹，文件夹名必须和 plugin.json 里的 id 完全一致："),
            C("plugins\\yourname.helloplugin\\\n    plugin.json\n    MyPlugin.dll\n    MyPlugin.pri\n    lib\\            （私有依赖，可选）"),
            P("id 只能用小写字母、数字、点和短横线，且不能以 novalink. 或 system. 开头（这两个前缀留给官方）。")),

        S("plugin.json",
            P("最少要有下面这些字段，宿主会逐项校验："),
            C("{\n  \"id\": \"yourname.helloplugin\",\n  \"name\": \"示例插件\",\n  \"version\": \"1.0.0\",\n  \"kind\": \"assembly\",\n  \"apiVersion\": 1,\n  \"minLauncher\": \"1.4.1\",\n  \"entry\": { \"assembly\": \"MyPlugin.dll\", \"type\": \"MyPlugin.Plugin\" },\n  \"ui\": { \"nav\": [ { \"tag\": \"plugin:yourname.helloplugin\", \"title\": \"示例插件\", \"glyph\": \"\\uE8F1\", \"order\": 600 } ] },\n  \"resources\": { \"pri\": \"MyPlugin.pri\" }\n}"),
            P("minLauncher 高于用户当前的启动器版本时，商店会把插件标成不可安装，而不是装上去再崩。")),

        S("打包",
            B("源码目录里有 build.ps1 就走你的脚本；没有就用 dotnet build -c Release -p:Platform=x64 自动打包。"),
            B("「设置 → 插件」→ 开发者工具 →「选择源码目录并打包…」，产物落到源码目录的 dist\\ 下并直接安装。"),
            B("插件正在运行时也能打包：新版本会排进队列，重启启动器时整体替换旧目录。"),
            P("编译型插件要在 csproj 里关闭内嵌 XBF，才会产出自己的 .pri；目标框架建议 net10.0-windows10.0.19041.0，并把 WindowsSdkPackageVersion 提到 10.0.26100.86 以上。")),

        S("页面与导航",
            B("界面的 XAML 打进插件自己的 .pri，宿主加载 .pri、注册 XAML 元数据提供者之后才能实例化插件里的 UserControl。"),
            B("在插件的 Initialize 里登记导航项：context.Ui.AddNavigation(new PluginNavItem(tag, title, factory, order, glyph))。"),
            B("tag 必须以 plugin: 开头，才能和内置页面区分开。")),

        S("在插件里能拿到什么",
            B("context.Host：发通知、发 HTTP 请求、读启动器版本。"),
            B("context.Host.CurrentAccount：只能拿到当前账户的显示名、UUID、头像地址。令牌、刷新令牌、皮肤文件路径都不开放。"),
            B("context.Subscribe(PluginEvent.AppStartup) / (PluginEvent.AppShutdown)：跟随启动器生命周期。"),
            B("全局钩子：改写下载地址、替换界面文字、过滤通知。"),
            B("脚本插件里的 lumen 对象：log / info / warn / error、settings.get / set、hooks.on…、events.on…。")),

        S("上架到插件商店",
            B("GitHub 仓库打上 topic：novalink-lumen-plugin —— 这是唯一的扫描标识。"),
            B("仓库根目录放 plugin.json，商店靠它显示名称、版本、作者和描述。"),
            B("可选放 store.json：指定安装包文件名、下载地址、标签，不写就按 Release 默认约定推导。"),
            B("把插件包传到 Release，文件名保持 <id>.lumenplugin，商店就能直接下载安装。")),

        S("安全与凭据",
            B("插件拿不到任何令牌：宿主只给账户显示名与头像；启动器保存的凭据是加密的（enc: 开头）。"),
            B("插件自己调用解密接口会被信任边界拦下并抛出异常，错误只算这个插件失败。"),
            B("插件与启动器在同一个进程里，这套限制是尽力而为：只安装来源可信的插件。")),

        S("已知限制",
            B("不支持热卸载：改完插件文件要重启启动器才生效。"),
            B("脚本插件的回调目前只支持同步返回。"),
            B("原生库同名冲突默认拒绝加载，需要隔离的放到 v1.5 的独立进程方案。"),
            B("插件页面出错只会显示兜底页，不会带着启动器一起崩。"))
    ];
}
