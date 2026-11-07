# NovaLink Lumen 插件开发文档

> 插件从 v1.4.1 引入，v1.5.0 起属于**实验性功能，默认关闭**。打开方式：设置 →「实验性」→「启动器插件」开关，然后点【立即重启】。关闭时启动器不扫描、不加载插件，也不会删除插件目录与已有数据。只想临时排查，可以在命令行加 `--experimental-plugins` 启动一次，这个开关不写回配置。

## 1. 插件能做什么

插件是启动器的扩展程序，运行在启动器进程内。它可以给左侧导航加一个入口、画出自己的页面，也可以在宿主执行某些操作时介入，例如换掉下载地址、改一段显示文本、决定某条通知要不要弹。

### 1.1 三类插件

```text
类型   清单 kind    是什么                                  当前状态
A       assembly    C# / XAML 编译型，有 .dll 和 .pri        可用
B       script      免编译 JavaScript，scripts/main.js        可用（仅 JavaScript；Python 计划在 v1.5 之后）
C       -           自带原生依赖（.dll / .so）的插件          默认拒载
```

- **A 编译型**：能力最全，可以注入导航、写 XAML 页面、使用宿主的玻璃组件和主题接口，也可以用代码直接构建界面。
- **B 脚本型**：不用编译，改完重启启动器就生效。它没有 UI，只能在钩子、事件、设置、日志这些位置上工作。
- **C 原生依赖型**：清单里的 `kind` 只接受 `assembly` 与 `script`，写别的会被判定为清单非法。原因很直接：原生库是进程级的，加载进启动器进程后没有任何隔离手段，崩溃和符号冲突都会拖垮整个启动器。插件目录下的 `native\` 会被探测，但那是给极少数受信任场景预留的，不构成支持承诺，也不做安全保证。

### 1.2 能力矩阵

| 能力 | A 编译型 | B 脚本型 | 说明 |
| --- | --- | --- | --- |
| 侧边导航入口 | 支持 | 不支持 | 只有 A 能注册 `plugin:` 开头的导航项 |
| 自定义页面 / UI | 支持 | 不支持 | 编译型 XAML、宽松 XAML、纯代码 UI 三档，见 5.5 |
| 下载地址解析 | 支持（自行接线） | 支持 `hooks.onDownloadRequest` | 宿主在一次下载解析前询问 |
| 文本替换 | 支持（自行接线） | 支持 `hooks.onTextReplace` | 按注册顺序链式叠加 |
| 通知过滤 | 支持（自行接线） | 支持 `hooks.onNotification` | 任一处理者返回 false 就不展示 |
| 生命周期事件 | `IPluginContext.Subscribe` | `lumen.events.on` | 见第 7 节事件表 |
| 持久化设置 | `IPluginSettings` | `lumen.settings` | 落到插件目录的 settings.json |
| 读取当前账号 | 支持（只有展示信息） | 不支持 | 显示名、UUID、头像，不含任何令牌 |
| 列出 / 启动实例 | 支持 | 不支持 | `IPluginHost` |
| 主动发 HTTP | 支持 | 不支持 | 走宿主 HttpClient，20 秒超时 |
| 弹通知 | 支持 | 不支持 | `IPluginHost.Notify` |
| 加载原生库 | 不推荐 | 不支持 | 进程级，隔离不了 |

一条铁律：**任何单个插件的错误只影响它自己**。加载失败、初始化抛异常、钩子抛异常、UI 构造失败，都会被记进日志并把这个插件置为失败或隔离，启动器照常启动。

### 1.3 插件做不到的事

- 拿不到登录令牌、刷新令牌、论坛凭据、AI 密钥，也拿不到皮肤文件的绝对路径。
- 不能热卸载。改了插件文件必须退出启动器再启动，见 4.4。
- 脚本里没有文件、网络、`require`、`eval`、.NET 反射，也没有 `setTimeout` 之类的后台驻留手段。
- 不能把自己藏起来偷偷安装：插件默认不加载，必须由玩家在设置里打开实验性开关。
- 没有进程隔离：A 类插件是任意 .NET 代码，崩溃可能带走启动器。只装你信任的插件。

## 2. 五分钟装一个

### 2.1 插件放在哪

| 内容 | 路径 | 代码 |
| --- | --- | --- |
| 数据根 | `%LOCALAPPDATA%\NovaLink Lumen\LauncherData` | AppPaths.cs:20 |
| 插件根 | `<数据根>\plugins` | PluginPaths.cs:43 |
| 单个插件 | `<数据根>\plugins\<id>` | PluginPaths.cs:52-55 |
| 清单 | `<插件目录>\plugin.json` | PluginPaths.cs:59 |
| 插件设置 | `<插件目录>\settings.json` | PluginPaths.cs:62 |
| 启动状态机 | `<数据根>\plugin-boot.json` | PluginPaths.cs:46 |
| 脚本私有数据 | `<数据根>\plugin-data\<id>` | PluginPaths.cs:64 注释 |
| 日志 | `<数据根>\logs\lumen-yyyyMMdd.log` | AppLog.cs:31 |

用 `--data-root <目录>` 启动参数或环境变量 `NOVALINK_DATA` 可以整体搬走数据根，插件路径随之改变。

### 2.2 目录结构

编译型：

```text
plugins\
  hello.world\
    plugin.json
    Hello.World.dll
    Hello.World.pri
    lib\                 私有依赖（可选）
    native\              原生库（不推荐）
    settings.json        宿主读写，别手改
```

脚本型：

```text
plugins\
  script.hello\
    plugin.json
    scripts\
      main.js
    settings.json
```

### 2.3 三种安装方式

1. **安装插件包**：设置 → 插件 →【安装插件包…】，选一个 `.lumenplugin`。它本质是 zip，根目录必须有 `plugin.json`。包内文件超过 200MB 或 5000 个、路径试图跳出插件目录、缺清单、校验失败，都会给中文原因并取消安装。已存在同名插件时拒绝覆盖，需要先删旧目录。
2. **手动放文件夹**：把整个插件文件夹放进 `plugins\`，文件夹名必须等于清单里的 `id`。放完确认层级是 `plugins\<id>\plugin.json`，不是 `plugins\<id>\<id>\plugin.json`。
3. **从插件商店安装**：发现 →「启动器插件」，挑一个点【安装】，装完同样要重启。

### 2.4 装完要重启

插件在启动器启动阶段统一加载，装完、改完、换完都要退出启动器再打开。设置里的插件列表会跟着目录变化自动刷新，但那只是列表，运行中的插件代码不会换。

### 2.5 先看日志

出问题先看 `%LOCALAPPDATA%\NovaLink Lumen\LauncherData\logs\lumen-yyyyMMdd.log`，插件相关的区域名是 `plugins`、`plugin:<id>`、`plugin-ui`、`plugin-theme`。用法见第 10 节。

## 3. plugin.json 参考

清单字段定义在 `src/NovaLink.Lumen.Core/Plugins/PluginManifest.cs`，校验在 `src/NovaLink.Lumen.Services/Plugins/PluginManifestReader.cs`。JSON 解析用宿主统一的设置（属性名大小写不敏感，**不允许注释和尾随逗号**，JsonDefaults.cs:9-16）。

### 3.1 字段表

| 字段 | 类型 | 必填 | 说明 |
| --- | --- | --- | --- |
| `schema` | int | 是 | 清单格式版本，必须为 1 |
| `id` | string | 是 | 插件唯一标识，正则 `^[a-z0-9][a-z0-9.\-]{2,63}$`，必须等于插件目录名，不能占用 `novalink.` / `system.` 前缀 |
| `name` | string | 是 | 显示名。留空时界面退化成 id |
| `version` | string | 建议 | 插件自己的版本，只用于展示和日志 |
| `author` | string | 否 | 作者 |
| `description` | string | 否 | 一句话说明 |
| `apiVersion` | int | 是 | 插件 API 版本，必须恰好等于 1（PluginApiVersion.cs:10） |
| `minLauncher` | string | 否 | 最低启动器版本；省略表示不限（PluginVersion.cs:40） |
| `maxLauncher` | string | 否 | 最高启动器版本；省略表示不限 |
| `kind` | string | 是 | `assembly` 或 `script`，其它值判非法 |
| `entry` | object | A 类必填 | `assembly`：插件目录内的 .dll；`type`：实现 `ILumenPlugin` 的完整类型名 |
| `scripts` | array | B 类必填 | 每项 `file`（目录内 .js）、`engine`（默认 `jint`）、`when`（默认 `startup`） |
| `capabilities` | string[] | 否 | 能力声明，只记录不授权，v1.5.1 不做强制校验 |
| `ui` | object | 否 | `kind`（信息字段，不校验不读取）、`nav`（导航项数组） |
| `ui.nav[].tag` | string | 有 nav 时必填 | 必须以 `plugin:` 开头（PluginManifestReader.cs:283） |
| `ui.nav[].title` | string | 有 nav 时必填 | 导航文字，不能为空 |
| `ui.nav[].order` | int | 否 | 排序，默认 500（PluginManifest.cs:16） |
| `ui.nav[].glyph` | string | 否 | Segoe Fluent Icons 字形，例如 `\uE8F1`；空时宿主用 `\uE8F1` |
| `ui.nav[].page` | string | 否 | 页面类型全名，宿主反射创建；不写则由插件在 Initialize 里给 PageFactory |
| `resources` | object | 否 | `pri`：PRI 文件名。不写时按 `<程序集同名>.pri` 猜（PluginUiService.cs:221） |
| `loadContext` | object | 否 | `privateAssemblies`、`native`，预声明用；实际按插件目录与 `lib\`、`native\` 探测 |
| `checksum` | string | 否 | 预留字段，v1.5.1 不校验 |

### 3.2 编译型最小样例

```json
{
  "schema": 1,
  "id": "hello.world",
  "name": "Hello World",
  "version": "1.0.0",
  "author": "你的名字",
  "description": "一个最小的编译型插件",
  "apiVersion": 1,
  "minLauncher": "1.5.1",
  "kind": "assembly",
  "entry": {
    "assembly": "Hello.World.dll",
    "type": "Hello.World.HelloPlugin"
  },
  "ui": {
    "nav": [
      {
        "tag": "plugin:hello.world",
        "title": "Hello World",
        "order": 500,
        "glyph": "\uE8F1",
        "page": "Hello.World.MainPage"
      }
    ]
  },
  "resources": {
    "pri": "Hello.World.pri"
  }
}
```

### 3.3 脚本型最小样例

```json
{
  "schema": 1,
  "id": "script.hello",
  "name": "脚本示例",
  "version": "1.0.0",
  "author": "你的名字",
  "description": "演示脚本插件的钩子与设置",
  "apiVersion": 1,
  "kind": "script",
  "scripts": [
    {
      "file": "scripts/main.js",
      "engine": "jint",
      "when": "startup"
    }
  ]
}
```

### 3.4 校验规则

启动器发现插件时会逐条检查，任一条不通过就跳过这个插件，并把原因写进日志：

- `schema` 必须为 1（PluginManifestReader.cs:133）。
- `apiVersion` 必须为 1；不匹配时的玩家提示是「该插件需要更新版本的启动器或插件格式」（:137-138）。
- `kind` 只能是 `assembly` 或 `script`。
- `id` 非空、符合正则、等于目录名、不占用保留前缀。
- A 类：`entry` 必填，`entry.assembly` 是插件目录内真实存在的 .dll，`entry.type` 非空且不含 `..`。
- B 类：`scripts` 非空，每项 `file` 是插件目录内真实存在的文件。
- `ui.nav` 每项非空；`tag` 非空且以 `plugin:` 开头；同一个插件内 tag 不重复；`title` 非空。
- 插件目录体积不超过 200MB、文件数不超过 5000。
- 所有相对路径都经过 `ResolveInside`（:82）检查：含 `..`、绝对路径、跳出插件目录的，一律拒绝。

### 3.5 目前不生效的字段

`capabilities` 只是声明，不会因为写了某项就获得权限；`checksum` 不校验；`ui.kind` 既不校验也不读取（UI 统一自动探测）；`loadContext.privateAssemblies` 与 `native` 暂不读取，实际依赖搜索路径是插件目录与 `lib\`，原生目录是 `native\`（PluginLoadContext.cs:85-96）。

## 4. 插件生命周期

### 4.1 启动流程

`LumenRuntime.InitializePlugins`（LumenRuntime.cs:381）按固定顺序执行：

1. 读开关：`PluginsEnabled = Settings.Settings.ExperimentalPlugins || options.ExperimentalPlugins`（:165）。假就直接返回，不建注册表、不扫描、不注册钩子，并调用 `HookRegistry.Reset()`（:388）把上一次可能残留的钩子还原。
2. 安全模式判定优先于开关：命中安全模式时同样不加载（见 9.3）。
3. 建 `PluginRegistry`，把 UI 门面 `PluginUiService.Instance`（:402）与「从导航 tag 反查插件」的回调 `Configure(() => Plugins.Plugins)`（:409）接上。
4. 扫描 `plugins\`，校验清单，建出插件描述。
5. `Watchdog?.BeginLoad()`（:437）记录本次启动状态，然后加载、初始化所有插件。
6. 全部成功且没有插件失败时，`MarkHealthy()`（:624）把看门狗的失败计数清零。

### 4.2 插件状态

| 状态 | 含义 |
| --- | --- |
| Discovered | 发现了清单并通过初步校验，还没加载 |
| Loaded | 程序集已载入或脚本引擎已创建 |
| Initialized | 初始化完成，这一项才算真正启用（PluginDescriptor.cs） |
| Disabled | 被玩家停用，或与当前启动器版本不兼容 |
| Failed | 加载或初始化失败；连续失败会被隔离 |

### 4.3 关闭与卸载

退出启动器时会依次调用插件的 `Shutdown()`，注销脚本钩子与事件。停用插件在设置 → 插件的开关里，写入 `lumen.json` 的 `disabledPlugins`，重启生效。卸载在设置 → 插件 →【删除】；如果插件 DLL 正被占用，宿主会把整个目录改名挪进 `plugin-trash`，下次启动清理，并留下 `plugin-deletes` 标记。插件私有数据 `plugin-data\<id>` **不会**随卸载删除。

### 4.4 为什么不能热卸载

三个硬原因，短时间改不了：

- XAML 类型与 `IXamlMetadataProvider` 一旦注册进 WinUI，没有反注册 API。
- 插件页面一旦挂到可视化树上，控件树仍引用它的类型；强行换程序集只会出现 `InvalidCastException`。
- `.pri` 资源包没有卸载接口。

所以更新插件的唯一正确姿势是：**退出启动器 → 替换插件目录 → 重新启动**。设置页的开发者工具会尽量帮你把「打包 + 排队替换」做成一步，但最后仍然需要重启。

## 5. 类型 A：C# / XAML 编译型插件

### 5.1 csproj 硬约束

| 设置 | 值 | 不这么做会怎样 |
| --- | --- | --- |
| TargetFramework | `net10.0-windows10.0.19041.0` | 与宿主程序集不匹配 |
| RuntimeIdentifier / PlatformTarget | `win-x64` / `x64` | 加载失败或位数不一致 |
| DisableEmbeddedXbf | `false` | 不产出 .pri，打开页面时 XamlParseException |
| WindowsSdkPackageVersion | ≥ `10.0.26100.86` | 编译报 CS1705 |
| 引用 NovaLink.Lumen.Core | `Private=false` | 把宿主程序集副本打进插件，运行时 InvalidCastException |
| 引用 Microsoft.WindowsAppSDK | `Private=false` | 同上 |

只能引用 `NovaLink.Lumen.Core` 与 Windows App SDK。不能引用 `NovaLink.Lumen` 或 `NovaLink.Lumen.Services`，也不能把共享程序集打进包：`NovaLink.Lumen.*`、`Microsoft.WinUI`、`Microsoft.Windows.*`、`WinRT.Runtime`、`Microsoft.Graphics.Win2D`、`DynamicXaml.WinUI`、`System.*`、`Microsoft.Extensions.*` 都由宿主提供（PluginLoadContext.cs:17-39）。

### 5.2 入口类

public、非抽象、有无参构造函数，实现 `ILumenPlugin`（ILumenPlugin.cs:8-15）：

```csharp
using NovaLink.Lumen.Core.Plugins;

namespace Hello.World;

public sealed class HelloPlugin : ILumenPlugin
{
    private IPluginContext? _context;

    public void Initialize(IPluginContext context)
    {
        _context = context;
        context.Log.Info("Hello World 已启动");

        var page = context.Ui.CreateGlassCard("这个页面由插件提供。", "Hello World");
        if (page is null)
        {
            context.Log.Warn("宿主的玻璃组件不可用，改用纯文本");
        }
    }

    public void Shutdown()
    {
        _context?.Log.Info("Hello World 已退出");
        _context = null;
    }
}
```

`Initialize` 抛异常只会禁用这个插件，不会影响启动器（PluginRegistry.cs:345 用 `Activator.CreateInstance` 构造，异常在该插件范围内捕获）。构造函数不要做重活，UI 相关的东西放到 `Initialize` 里，并且确认在 UI 线程。

### 5.3 注入导航

两条路，选一条即可。

**路线一：清单声明（推荐，最少代码）**

在 `plugin.json` 的 `ui.nav` 里写 `page` 类型全名，宿主用反射创建页面（PluginRegistry.cs:703）。类型无效会记一条 Warn 并跳过这一项。

```json
{
  "ui": {
    "nav": [
      {
        "tag": "plugin:hello.world",
        "title": "Hello World",
        "order": 500,
        "page": "Hello.World.MainPage"
      }
    ]
  }
}
```

**路线二：代码注册**

在 `Initialize` 里调用 `context.Ui.AddNavigation`（PluginUiService.cs:52），自己给 PageFactory：

```csharp
context.Ui.AddNavigation(new PluginNavItem(
    Tag: "plugin:hello.world",
    Title: "Hello World",
    PageFactory: () => new MainPage(),
    Order: 500,
    Glyph: "\uE8F1"));
```

规则：

- `tag` 必须以 `plugin:` 开头。否则宿主记 Warn「忽略未以 plugin: 开头的插件导航项（避免顶掉内置导航）」并丢弃。
- 导航项按 `order` 升序、再按 `title` 排序，插进左侧导航栏。
- 点击后走 `ShellPage.Navigate` 的 `plugin:` 分支，页面放进插件页面宿主；页面构造失败只显示中文提示与【重试】【返回首页】，不会白屏卡死。
- 插件停用或卸载时，宿主会撤掉它的导航项。

### 5.4 PRI 与 XAML 资源

首次点开插件页面时，宿主做三件事（PluginUiService.cs:221）：

1. 找 PRI：`resources.pri` 或按 `<程序集同名>.pri` 猜路径。
2. `DynamicLoader.TryLoadPri`（:235）加载，每个插件只加一次；失败记 Warn「资源加载失败（PRI 可能已损坏或不是当前版本编译的）」。
3. `RegisterMetadataProvider`（:280）：在已加载的程序集里找 `IXamlMetadataProvider` 实现并注册。找不到时记 Info「没有 XAML 元数据提供者（纯代码 UI 插件属正常情况）」。

因此编译型 XAML 插件必须把 `.pri` 一起放进插件目录。只给 .dll 不给 .pri，纯代码 UI 能用，XAML 页面会在解析资源时失败。

### 5.5 T1 / T2 / T3 三档 UI

| 档位 | 写法 | 支持程度 | 前提 |
| --- | --- | --- | --- |
| T1 | 编译型 XAML（.xaml + 代码后置） | 完整支持 | 有 .pri，DisableEmbeddedXbf=false |
| T2 | 宽松 XAML：`XamlReader.Load` 字符串 | 只支持框架内置类型 | 不能引用插件自定义类型 |
| T3 | 纯代码构建 UI | 支持 | 不需要 .pri 或元数据提供者 |

经验法则：能用 T1 就用 T1；动态内容用 T3；T2 只适合极小的一次性片段，因为插件自己的类型在字符串 XAML 里解析不了。

### 5.6 玻璃 UI 组件

宿主提供四个方法，全部要求 **UI 线程** 调用；非 UI 线程会返回 null（`ApplyGlassCard` 直接不生效）并记日志（PluginUiService.cs:335）。它们返回 `object`，因为 Core 不引用 WinUI，直接转成 `UIElement` 使用即可。

| 方法 | 作用 | 实现要点 |
| --- | --- | --- |
| `CreateGlassCard(content, title)` | 一张玻璃卡片 | Border + `GlassCard` 样式，圆角 18，内边距 18/16；title 非空时在内容上方加标题行；内容可以是 `UIElement` 或字符串 |
| `CreateGlassPanel(content)` | 更轻的分组面板 | Border + `GlassRow` 样式，圆角 14，内边距 14/12 |
| `CreateGlassButton(text, onClick)` | 胶囊按钮 | 高 38，圆角 999，`GhostButton` 样式；回调抛异常只记日志并隔离 |
| `ApplyGlassCard(element)` | 把宿主玻璃样式套到你自己的元素上 | Border：套样式并补描边；Panel：只补底色；Control：补背景/描边/圆角；其它类型记 Warn 忽略 |

**为什么必须用 `CreateGlassCard` 而不是自己画 Border**：只有走 `GlassCard` 样式，卡片才会被玻璃收集器识别，自动获得背景折射、悬停高光与深浅主题切换。自己拼一个半透明 Border 看起来像，但不会跟着背景动。

示例：

```csharp
var card = context.Ui.CreateGlassCard(
    "插件可以用宿主提供的玻璃组件，深浅主题自动适配。",
    "玻璃卡片");

var button = context.Ui.CreateGlassButton("点我", () =>
{
    context.Log.Info("按钮被点击");
});

if (card is UIElement cardElement && button is UIElement buttonElement)
{
    var panel = new StackPanel { Spacing = 12 };
    panel.Children.Add(cardElement);
    panel.Children.Add(buttonElement);
    context.Ui.AddNavigation(new PluginNavItem(
        "plugin:hello.world", "Hello World", () => panel));
}
```

### 5.7 主题接口 IPluginTheme

插件自己画的颜色要跟着主题走，用 `context.Theme`（IPluginTheme.cs；宿主实现是 PluginThemeBridge.cs）。`Theme` 永远不为 null，宿主没接上时是 `NullPluginTheme.Instance`（深色色表）。

| 成员 | 说明 |
| --- | --- |
| `IsLight` | 当前是否浅色 |
| `Name` | 固定 `light` / `dark` |
| `Colors` | 语义色表，键固定，值形如 `#AARRGGBB` |
| `Changed` | 主题切换后触发；触发时宿主的画刷已经刷新 |

固定 13 个色键：`Accent`、`Text`、`TextSecondary`、`TextTertiary`、`Surface`、`SurfaceRaised`、`GlassCard`、`GlassBorder`、`Field`、`FieldBorder`、`FillSubtle`、`Danger`、`Warn`。

主题切换发生在 UI 线程，插件收到 `Changed` 后必须整体重建自己的画刷，不要缓存旧 SolidColorBrush：

```csharp
private IPluginTheme? _theme;
private readonly List<Action> _rebuilders = new();

private void WatchTheme(IPluginContext context)
{
    _theme = context.Theme;
    _theme.Changed += (_, _) => RebuildBrushes();
    RebuildBrushes();
}

private void RebuildBrushes()
{
    if (_theme is null) return;
    var text = ParseColor(_theme.Colors["Text"]);
    var accent = ParseColor(_theme.Colors["Accent"]);
    MyTitle.Foreground = new SolidColorBrush(text);
    MyAccentBorder.BorderBrush = new SolidColorBrush(accent);
}

private static Color ParseColor(string hex)
{
    var value = uint.Parse(hex.TrimStart('#'), System.Globalization.NumberStyles.HexNumber);
    return Color.FromArgb(
        (byte)((value >> 24) & 0xFF),
        (byte)((value >> 16) & 0xFF),
        (byte)((value >> 8) & 0xFF),
        (byte)(value & 0xFF));
}
```

注意：`Changed` 的订阅者抛异常会被宿主捕获并记 Warn「插件的主题切换处理器抛出异常，已隔离」，不会影响其它插件；但异常之后的界面更新会停在那一步，所以解析颜色这类代码要自己兜底。

### 5.8 宿主能力

`IPluginContext`（ILumenPlugin.cs:19）是插件能碰到的全部门面：

| 成员 | 类型 | 说明 |
| --- | --- | --- |
| `PluginId` / `PluginVersion` | string | 清单里的 id 与 version |
| `Log` | IPluginLogger | `Info` / `Warn` / `Error(msg, ex?)`，区域名 `plugin:<id>` |
| `Settings` | IPluginSettings | `Get(key)` / `Set(key, value)` / `Flush()`，值一律字符串，落 `plugins\<id>\settings.json` |
| `Host` | IPluginHost | 见下表 |
| `Ui` | IPluginUi | 导航与玻璃组件 |
| `Theme` | IPluginTheme | 见 5.7 |
| `Subscribe(event, handler)` | IDisposable | 订阅生命周期事件，Dispose 即退订 |

`IPluginHost`（IPluginHost.cs:7）：

| 成员 | 说明 |
| --- | --- |
| `LauncherVersion` | 当前启动器版本 |
| `DataRoot` | 数据根 |
| `CurrentAccountName` / `CurrentAccount` | 当前账号的显示名、UUID、头像；**没有任何令牌字段** |
| `ListInstances()` | 已安装实例（id、名称、游戏版本、加载器） |
| `LaunchInstanceAsync(id, ct)` | 请求宿主启动实例，仍走宿主自己的检查流程 |
| `HttpGetAsync(url, headers?, ct)` | 走宿主 HttpClient，20 秒超时，返回状态码与正文 |
| `Notify(title, message, level)` | 弹宿主通知，level 默认 `info` |

插件自己的后台线程、定时器、网络请求由插件负责收尾，`Shutdown` 里要能停下来。宿主不会替插件管理线程。

### 5.9 打包 .lumenplugin

1. `dotnet build -c Release -p:Platform=x64`（或插件自带的 build.ps1）。
2. 确认输出目录里有 `<id>.dll`、`<id>.pri`、`plugin.json`，以及可选的 `lib\`、`native\`。
3. 以插件目录为根打 zip，扩展名改成 `.lumenplugin`。包根必须是 `plugin.json`。
4. 用设置 → 插件 →【安装插件包…】验证能装上并重启后出现导航入口。

### 5.10 常见错误

| 现象 / 错误 | 原因 | 处理 |
| --- | --- | --- |
| CS1705 | WindowsSdkPackageVersion 太低 | 提到 10.0.26100.86 或更高 |
| XamlParseException | 没产出或没打包 .pri | `DisableEmbeddedXbf=false`，把 .pri 放进包 |
| InvalidCastException | 插件自带了宿主程序集副本 | 引用都设 `Private=false` |
| 导航项没出现 | tag 不是 `plugin:` 开头 / page 类型写错 / 清单校验失败 | 看 `[plugins]` 与 `[plugin:<id>]` 日志 |
| 页面加载失败 | 页面构造函数抛异常或资源没加载 | 看 `[plugin-ui]` 日志，先试【重试】 |
| 找不到 ILumenPlugin 入口 | `entry.type` 与命名空间/类名不一致 | 写完整类型名，注意大小写 |

## 6. 类型 B：JavaScript 脚本插件

### 6.1 结构与执行时机

`plugin.json` 里 `kind` 写 `script`，`scripts` 数组列出脚本文件。`when` 目前只认 `startup`（其它值记 Warn 后跳过），`engine` 目前只认 `jint`（大小写不敏感）。

`main.js` 的顶层代码在启动器启动阶段同步执行一遍，执行完就结束，没有常驻事件循环。多个脚本按数组顺序依次同步执行。插件被停用或启动器退出时，它注册的钩子与事件会自动注销。

脚本里没有 `window`、`document`、`process`、`require`、`console`、`fetch`、`setTimeout`。唯一可用的全局对象是 `lumen`。

### 6.2 lumen API

| API | 说明 |
| --- | --- |
| `lumen.log(msg)` / `info` / `warn` / `error(msg, err?)` | 写宿主日志，区域 `plugin:<id>`；单条超过 2000 字符会截断 |
| `lumen.plugin.id` / `.version` | 清单里的 id 与 version |
| `lumen.plugin.dataDir` | 插件私有数据目录 `<数据根>\plugin-data\<id>`，已自动创建 |
| `lumen.settings.get(key)` | 读设置，没有返回 null；键最长 64 字符 |
| `lumen.settings.set(key, value)` | 写设置，值必须能 JSON 序列化，序列化后不超过 8KB；由宿主落盘 |
| `lumen.hooks.onDownloadRequest(fn)` | 注册下载地址解析，返回注册 id |
| `lumen.hooks.onTextReplace(fn)` | 注册文本替换，返回注册 id |
| `lumen.hooks.onNotification(fn)` | 注册通知过滤，返回注册 id |
| `lumen.hooks.off(id)` | 注销一个钩子 |
| `lumen.events.on(name, fn)` | 订阅事件，返回注册 id；未知事件名返回 0 |
| `lumen.events.off(id)` | 退订事件 |

钩子回调的输入与返回值：

- **onDownloadRequest(request)**：`request` 有 `url`、`fileName`、`size`、`sha1`、`purpose`（如 `java`、`game`、`assets`、`modloader`），不包含请求头。返回 `{ url, displayName?, headers?, sha1? }` 表示改下载地址，返回 `null` 表示不处理。
- **onTextReplace(text)**：收到一段待显示文本，返回新字符串表示替换，返回 `null` 表示保留原样。返回超过 1MB 会被丢弃。
- **onNotification(title, body)**：返回 `false` 表示不要展示这条通知，其它返回值按展示处理。

### 6.3 钩子合并规则

多个脚本可以同时注册同一类钩子（ScriptHookBus.cs:134-232）：

- `onDownloadRequest`：按注册顺序依次调用，**第一个返回非 null 的结果生效**，后面的不再问。
- `onTextReplace`：按注册顺序链式叠加，前一个的输出是后一个的输入；返回 null 就保留当前值。
- `onNotification`：依次调用，任意一个返回 `false` 就不展示。
- 所有回调都是同步的，不支持 async/Promise。回调抛异常会被捕获，记进 `plugin:<id>` 日志并回落宿主默认行为。

### 6.4 执行预算与同步限制

| 限制 | 值 | 代码 |
| --- | --- | --- |
| 单次执行超时 | 2000ms | ScriptEngineFactory.cs:18 |
| 内存上限 | 32MB | :15 |
| 语句上限 | 500000 | :12 |
| 递归深度 | 64 | :21 |
| 单个脚本文件 | 2MB | :24 |

脚本没有线程、定时器与回调队列，所有工作必须在启动阶段或钩子回调里同步完成。需要定时刷新数据，只能在钩子里顺手做，或者改写成 A 类插件。

### 6.5 安全沙箱

脚本引擎按最小权限配置：`Interop.Enabled=false`（没有 .NET 反射与互操作）、模块系统关闭（没有 `require`/import）、`StringCompilationAllowed=false`（没有 `eval` 与动态代码）。脚本只能看到宿主注入的 `lumen` 对象，没有文件、网络、进程、注册表、剪贴板。

这不等于绝对安全：`lumen` 自身提供的能力就是脚本的边界，钩子返回的数据也会进入宿主流程。只装你信任的脚本。

### 6.6 错误隔离

编译失败、执行失败、钩子抛异常、事件处理器抛异常，都会被宿主按插件隔离并写日志，关键字包括「脚本编译失败：」「脚本执行失败：」「插件钩子异常已隔离」「事件处理器抛出异常，已隔离」。同一个插件连续失败 3 次（PluginWatchdog.cs:16）会自动隔离，设置页会出现【清除隔离】/【重新启用】。

### 6.7 示例 main.js

完整可运行的模板在仓库 `docs/plugin-template/script-hello/`。

```javascript
// 启动时执行一次。lumen 是宿主注入的唯一全局对象。
lumen.log("脚本插件已启动：" + lumen.plugin.id);

// 1) 设置：读写插件私有配置（宿主落盘到 settings.json）
if (lumen.settings.get("firstRun") !== true) {
    lumen.settings.set("firstRun", true);
    lumen.log("这是第一次启动这个脚本插件");
}

// 2) 文本替换：给页面上的某类文本加前缀
lumen.hooks.onTextReplace(function (text) {
    if (text && text.indexOf("示例") === 0) {
        return "[脚本] " + text;
    }
    return null;
});

// 3) 通知过滤：屏蔽含有指定关键词的通知
lumen.hooks.onNotification(function (title, body) {
    if (title && title.indexOf("测试") >= 0) {
        lumen.log("已屏蔽一条测试通知：" + title);
        return false;
    }
    return true;
});

// 4) 下载地址解析：把示例域名换成镜像地址
lumen.hooks.onDownloadRequest(function (request) {
    if (request.url.indexOf("example.com") >= 0) {
        lumen.log("改写下载地址：" + request.url);
        return { url: request.url.replace("example.com", "mirror.example.com") };
    }
    return null;
});

// 5) 生命周期事件
lumen.events.on("app.shutdown", function () {
    lumen.log("脚本插件即将退出");
});
```

### 6.8 调试脚本插件

- 回调第一行先 `lumen.log` 打点，确认它真的被调用。
- 钩子没生效时，先确认插件状态是 Initialized，而不是 Disabled/Failed。
- 改完脚本必须重启启动器；脚本插件同样不支持热重载。
- 日志过滤：`Select-String -Path "$root\logs\lumen-*.log" -Pattern "plugin:script.hello"`。

## 7. 事件参考

A 类用 `context.Subscribe(PluginEvent.X, handler)`，B 类用 `lumen.events.on("name", fn)`。

| 事件 | A 类枚举 | B 类字符串 | 触发时机 |
| --- | --- | --- | --- |
| 启动完成 | `AppStartup` | `app.startup` | 插件加载完成后 |
| 即将退出 | `AppShutdown` | `app.shutdown` | 启动器关闭前，插件注销前 |
| 启动实例前 | `LaunchPre` | - | 宿主开始一次启动前 |
| 启动实例后 | `LaunchPost` | - | 启动流程结束 |
| 下载解析前 | `DownloadResolve` | `download.before` | 解析下载地址前 |
| 下载完成后 | - | `download.after` | 一次下载结束 |

事件回调里抛异常只记日志，不影响宿主。事件数据用字符串键读取，缺失键返回 null。

## 8. 打包与上架

- 安装包：目录打 zip，扩展名 `.lumenplugin`，包根有 `plugin.json`。建议文件名就是 `<id>.lumenplugin`。
- 开发者工具：打开开发模式后，设置 → 插件 → 开发者工具，可以选一个源码目录一键打包。目录里有 `build.ps1` 就直接跑；否则找同名 `.csproj` 执行 `dotnet build -c Release -p:Platform=x64`；脚本插件直接打包目录（排除 bin/obj/.git/dist）。产物落在源码目录的 `dist\`。
- 文件占用：新包会排队到 `<数据根>\plugin-updates\<id>.lumenplugin`，旧目录改名到 `plugin-trash`，下次启动替换。界面会提示【立即重启】。
- 上架插件商店：仓库加 topic `novalink-lumen-plugin`，仓库根放 `plugin.json`，Release 上传 `<id>.lumenplugin`，可选 `store.json` 补充分类与主页。详见 docs/STORE.md。

## 9. 安全模型

### 9.1 运行在启动器进程内

A 类插件是任意 .NET 代码，和启动器同进程、同 AppDomain。`IPluginHost` 是一个可审计的门面，不是沙箱：插件如果直接反射宿主内部类型，宿主拦不住。强隔离（独立进程/独立 AppDomain）不在 v1.5.1 范围内。B 类脚本受 Jint 沙箱限制，只能看到 `lumen`。

### 9.2 凭据边界

宿主对插件做了四层限制：

1. `IPluginHost.CurrentAccount` 只返回显示名、UUID、头像，`AccessToken`、`RefreshToken`、论坛令牌、`SkinPath` 都不复制（LumenPluginHost.cs:41-71）。
2. `lumen.json` 里的令牌是 `enc:` + Base64 的 DPAPI 密文（SecretProtector.cs:13）。
3. `SecretProtector.Unprotect` 与账号服务取令牌的入口会调用 `PluginTrustBoundary.EnsureTrustedCaller`（:33），沿调用栈找到第一个宿主之外的帧；如果来自可回收的插件加载上下文，直接抛 `UnauthorizedAccessException`，文案是「插件不能读取启动器保存的账户凭据。插件只应通过宿主 API 工作，凭据与密钥不对插件开放。」（PluginTrustBoundary.cs:75-76）。
4. AI 文案由服务端代理，客户端不保存第三方 key。

这是尽力而为的纵深防御，不是密码学意义上的隔离。真正的边界仍然是「只装可信插件」。

### 9.3 实验性开关与安全模式

插件默认关闭。开关在 设置 →「实验性」→「启动器插件」，需要重启生效。关闭时：不扫描插件、不注册钩子、发现页不显示插件商店、设置页的插件分类隐藏，插件目录与数据原样保留。

安全模式优先于开关，任何一条命中都不加载插件：

| 方式 | 行为 |
| --- | --- |
| `--safe-mode` | 本次不加载插件，并标记为诊断态 |
| `--no-plugins` | 本次不加载插件，不标记诊断 |
| `NOVALINK_SAFE_MODE=1` | 同 `--safe-mode`，环境变量 1/true/yes/on 都算真 |
| `NOVALINK_NO_PLUGINS=1` | 同 `--no-plugins` |
| 设置里逐个停用 | 写入 `lumen.json` 的 `disabledPlugins`，重启生效 |

`PluginsDisabled = SafeMode || NoPlugins`（StartupOptions.cs:55）。安全模式下界面会显示横幅，提供【重新启用插件并重启】。

### 9.4 看门狗

`<数据根>\plugin-boot.json` 记录每次启动的状态：`state`、`attempts`、`lastStartUtc`、`lastHealthyUtc`、`safeModeReason` 与每个插件的失败次数。

- 启动开始写 `starting` 并把 `attempts` 加一（PluginWatchdog.cs:103）。
- 全部插件成功时写 `healthy`、`attempts=0`，并清空各插件的失败计数（:123）。
- 下次启动如果读到 `attempts >= 2` 且上次不是 `healthy`，自动进入安全模式并写明原因（:44-61），避免连续崩溃。

单个插件连续失败 3 次会被隔离（`FailCount >= 3`，:177），不再尝试加载，直到玩家清除隔离。

### 9.5 开发者模式 --dev-mode

`--dev-mode` 或环境变量 `NOVALINK_DEV_MODE=1`。按当前实现，它的作用是**把插件加载细节写进日志**，方便定位「为什么没加载」。它不放宽脚本沙箱，也不绕过安全模式。开发者工具入口在开发模式下出现在设置 → 插件里。

### 9.6 玩家侧只会看到友好提示

插件页面失败显示「插件页面加载失败，可以点【重试】再试一次；如果一直失败，请在设置里停用这个插件。」；清单坏、版本不兼容、被隔离、被停用，设置页分别给中文状态；安全模式有横幅。异常堆栈、文件路径、内部错误码只进日志，不弹给玩家。

## 10. 故障排查

### 10.1 日志在哪

`%LOCALAPPDATA%\NovaLink Lumen\LauncherData\logs\lumen-yyyyMMdd.log`，行格式是「时间 [级别] [区域] 消息」（AppLog.cs:23）。插件相关区域：`plugins`（发现与加载）、`plugin:<id>`（单个插件）、`plugin-ui`（导航与页面）、`plugin-theme`（主题）。

```powershell
$root = "$env:LOCALAPPDATA\NovaLink Lumen\LauncherData"
Get-Content "$root\logs\lumen-$(Get-Date -Format yyyyMMdd).log" -Tail 200
Select-String -Path "$root\logs\lumen-*.log" -Pattern "plugin|插件"
```

### 10.2 症状对照

| 现象 | 常见原因 | 先做什么 |
| --- | --- | --- |
| 插件完全不出现 | 放错层级、清单坏、id 与目录名不一致、安全模式 | 看 `[plugins]` 日志；确认 `plugins\<id>\plugin.json` |
| 出现在列表但未加载 | 校验失败、版本不兼容、被停用、被隔离 | 看设置页状态与 `[plugin:<id>]` 日志 |
| 导航没有入口 | 没声明 `ui.nav`、tag 不是 `plugin:` 前缀、page 类型错、title 为空 | 修清单后重启 |
| 页面白屏 | 页面构造函数抛异常、XAML 资源没加载 | 看 `[plugin-ui]`；补 .pri |
| 一开插件页就回首页 | 页面类型创建失败或返回了非 UIElement | 看日志里的失败原因 |
| 反复闪退 | 插件在启动阶段崩溃 | 用 `--safe-mode` 启动，移出最近装的插件 |
| 脚本没执行 | kind 写错、scripts.file 路径错、when/engine 不支持、插件被禁用 | 看「脚本编译失败/执行失败」 |
| 钩子没生效 | 回调返回 null、条件没命中、插件没加载 | 回调首行 `lumen.log` 打点 |
| 想恢复但进不去 | 被安全模式或隔离卡住 | 设置 → 插件 →【重新启用插件并重启】 |

### 10.3 解除隔离

1. **设置 → 插件 →【重新启用插件并重启】**：推荐，会清零失败计数并退出安全模式。
2. 修好插件后正常重启：如果计数还没清零，仍可能被隔离。
3. 删掉 `<数据根>\plugin-boot.json`：清空所有记录，属于非常规手段，只建议在排查时用。

### 10.4 检查清单

```powershell
$dir = "$env:LOCALAPPDATA\NovaLink Lumen\LauncherData\plugins\script.hello"
Get-Content "$dir\plugin.json" -Raw -Encoding UTF8 | ConvertFrom-Json | Format-List
```

## 11. 版本兼容

- `apiVersion` 必须与宿主常量完全相等（v1.5.1 为 1）。不相等时插件不加载，提示「该插件需要更新版本的启动器或插件格式」。不要自己递增这个数字，它由宿主定义。
- `minLauncher` / `maxLauncher` 是可选的软约束，由 `PluginVersion.IsCompatible`（PluginVersion.cs:40）判断：可省略表示不限；支持 1~4 段与 `v` 前缀；`-`/`+`/空格之后的后缀会被忽略；当前版本低于 min 或高于 max 视为不兼容。
- 不兼容的插件会停用，设置页显示「与当前启动器版本不兼容（需要 x ~ y，当前 z）」。
- 建议：保守写 `minLauncher`（例如只写你实际测试过的版本），不要写 `maxLauncher`，除非你确认新版本会破坏你的插件。
- `schema` 必须为 1；`version` 只用于展示，不参与兼容判断。

## 12. 代码地图

| 位置 | 内容 |
| --- | --- |
| src/NovaLink.Lumen.Core/Plugins/PluginManifest.cs | 清单模型与默认值 |
| .../ILumenPlugin.cs | IPluginContext 与插件入口接口 |
| .../IPluginHost.cs | 宿主能力门面 |
| .../IPluginUi.cs | 导航与玻璃组件接口 |
| .../IPluginTheme.cs | 主题接口与 NullPluginTheme |
| .../PluginEvents.cs | 事件枚举与参数 |
| .../PluginTrustBoundary.cs | 凭据访问边界 |
| src/NovaLink.Lumen.Services/Plugins/PluginManifestReader.cs | 清单校验与路径穿越防护 |
| .../PluginRegistry.cs | 发现、加载、初始化、事件分发 |
| .../PluginWatchdog.cs | plugin-boot.json 与失败隔离 |
| .../PluginLoadContext.cs | 插件程序集加载与共享程序集判定 |
| .../PluginPaths.cs | 插件目录约定 |
| .../Scripting/* | Jint 引擎、脚本 API、钩子总线 |
| .../Store/* | 插件商店客户端 |
| src/NovaLink.Lumen/Plugins/PluginUiService.cs | 导航注入、PRI、玻璃组件 |
| .../LumenPluginHost.cs | IPluginHost 实现（账号信息裁剪） |
| .../PluginThemeBridge.cs | IPluginTheme 实现 |
| src/NovaLink.Lumen/Views/Pages/PluginPageHost.xaml(.cs) | 插件页面宿主与失败兜底 |
| src/NovaLink.Lumen.Services/Hooks/HookRegistry.cs | 三个宿主钩子 |
| docs/plugin-template/ | 编译型与脚本型模板 |

## 13. FAQ

**Q：为什么插件默认不加载？**
A：插件运行在启动器进程内，能做的事很多。v1.5.0 起先作为实验性功能，由玩家显式打开，避免误装插件直接生效。

**Q：改了插件代码，为什么没变化？**
A：插件不支持热卸载（见 4.4）。退出启动器再启动。

**Q：脚本插件能画界面吗？**
A：不能。脚本插件只有钩子、事件、设置与日志。要 UI 就写 A 类编译型插件。

**Q：能读取玩家账号的令牌吗？**
A：不能。宿主只给显示名、UUID、头像，取令牌的入口还有调用栈检查（见 9.2）。

**Q：插件崩溃会把启动器带崩吗？**
A：A 类插件同进程，理论上可能；宿主会隔离出错插件并连续两次异常启动后自动进安全模式。B 类脚本的异常被引擎捕获，不会传播到宿主。

**Q：插件数据放在哪？卸载会删吗？**
A：插件设置在 `plugins\<id>\settings.json`，脚本私有数据在 `plugin-data\<id>`。卸载不删 `plugin-data`。

**Q：支持 Python 吗？**
A：v1.5.1 的脚本插件只支持 JavaScript（Jint）。Python 在后续版本计划中。
