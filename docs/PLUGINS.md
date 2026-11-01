# 插件系统（v1.4.1 引入，v1.5.0 起为实验性功能）

这是 NovaLink Lumen 的插件开发与使用文档。玩家看第 2、3、7.4、7.8、8 节就够了；开发者从第 4 节开始按类型读第 5 节（C# 编译型）或第 6 节（JS 脚本型）。

v1.4.1 的插件系统只有一条铁律：**任何单个插件的错误都只影响它自己**。插件加载失败、页面崩了、钩子抛异常，启动器都照常可用。

> **注意：从 v1.5.0 起，插件系统属于实验性功能，默认关闭。** 需要到 **设置 →「实验性」（左侧分类，在「外观」与「关于」之间）→「实验性功能」页里的「启动器插件」开关** 打开，再点横幅上的【立即重启】（或手动重启启动器），插件服务、设置里的「插件」页与「发现 → 启动器插件」商店才会出现。关闭状态下所有插件都不会加载、插件商店与插件页不显示，但**插件目录与插件数据不会被删除**；重新打开并重启即恢复。临时体验也可以直接用命令行 `NovaLink.Lumen.exe --experimental-plugins` 启动一次（这个参数只对本次启动有效，不会写回设置）。本文其余章节描述的都是“插件已开启”时的行为。

## 1. 插件能做什么

插件是运行在启动器进程内、由启动器加载的扩展包。一个插件可以：

- 在左侧导航栏注入自己的页面（编译型插件可以完全自定义 XAML 界面）
- 在下载地址解析时接管/改写地址（例如换成镜像加速）
- 改写启动器展示的文本（本地化、术语替换）
- 过滤通知（决定某类通知是否弹出）
- 订阅启动、退出、启动游戏、下载前后等事件
- 读写自己的持久化设置
- （仅编译型）通过宿主门面读账户名、列出/启动实例、发受限 HTTP 请求、弹通知

### 1.1 三类插件与 v1.4.1 状态

| 类型 | 形态 | 要不要编译 | v1.4.1 状态 |
|---|---|---|---|
| **A 编译型** | C# 程序集 + WinUI XAML 页面 | 要（.NET 10 SDK） | **可用**：导航注入、编译型 XAML（PRI）、纯代码 UI 都支持 |
| **B 脚本型** | `scripts/main.js`，宿主内嵌 Jint 执行 | 不要 | **可用（仅 JavaScript）**；Python 计划 v1.5 |
| **C 原生依赖型** | 自带第三方原生 DLL/驱动 | 要 | **默认拒载**：`kind` 只接受 `assembly` / `script`，C 类清单会被判非法（见 4.4） |

> 三类共用同一套目录约定与 `plugin.json` 清单，区别只在 `kind` 字段与包内容。

### 1.2 能力矩阵

| 能力 | A 编译型 | B 脚本型 | C 原生型 |
|---|---|---|---|
| 导航页面 / 自定义 UI | 支持（T1/T3，见 5.6） | 不支持 | 不支持（拒载） |
| 下载地址钩子 | 通过宿主事件/自行接线 | 支持（`lumen.hooks`） | — |
| 文本替换、通知过滤 | 通过宿主事件/自行接线 | 支持（`lumen.hooks`） | — |
| 订阅事件 | 支持（`IPluginContext.Subscribe`） | 支持（`lumen.events`） | — |
| 持久化设置 | 支持（`IPluginSettings`） | 支持（`lumen.settings`） | — |
| 读账户/列实例/启动实例 | 只读显示名 / UUID / 头像（**无令牌**，见 7.2）；列实例、启动实例 | 不暴露 | — |
| 发起 HTTP | 支持（宿主 HttpClient，20 秒超时） | 不暴露 | — |
| 加载原生 DLL | `native\` 目录会被探测，但**不推荐且不做安全隔离** | 不支持 | 拒载 |

## 2. 安装插件

> 前提：v1.5.0 起要先在 **设置 → 实验性 →「启动器插件」** 打开并重启（见文首提示）。开关关着时，插件即使已经在 `plugins\` 目录里也不会被加载。

### 2.1 插件目录在哪

插件放在启动器数据根目录下的 `plugins\` 里。默认数据根目录：

```text
%LOCALAPPDATA%\NovaLink Lumen\LauncherData\plugins\<插件id>\
```

这个路径由代码决定，不是约定：数据根 = `AppPaths.DefaultRoot`（`src/NovaLink.Lumen.Core/AppPaths.cs:20`），插件根 = `<数据根>\plugins`（`src/NovaLink.Lumen.Services/Plugins/PluginPaths.cs:40`）。

相关路径一览：

| 用途 | 路径 | 代码 |
|---|---|---|
| 数据根 | `%LOCALAPPDATA%\NovaLink Lumen\LauncherData` | `AppPaths.cs:20` |
| 插件根 | `<数据根>\plugins` | `PluginPaths.cs:40` |
| 单个插件 | `<数据根>\plugins\<id>`（目录名必须等于清单 `id`） | `PluginPaths.cs:49-52` |
| 插件清单 | `<插件目录>\plugin.json` | `PluginPaths.cs:56` |
| 插件设置 | `<插件目录>\settings.json`（由宿主读写，别手改） | `PluginPaths.cs:59` |
| 启动状态机 | `<数据根>\plugin-boot.json`（隔离/安全模式依据） | `PluginPaths.cs:43` |
| 运行日志 | `<数据根>\logs\lumen-yyyyMMdd.log` | `AppPaths.cs:13`、`AppLog.cs:28` |
| 脚本插件私有数据 | `<数据根>\plugin-data\<id>` | `lumen.plugin.dataDir`（见 6.3） |

如果启动时传了 `--data-root <目录>` 或设置了 `NOVALINK_DATA`，上面的“数据根”整体换掉（`src/NovaLink.Lumen/Startup/StartupOptions.cs:101-119`、`AppPaths.cs:36-38`）。

### 2.2 目录结构示例

编译型（`kind: assembly`）：

```text
plugins\hello.world\
├── plugin.json          清单（必须在插件目录根部）
├── Hello.World.dll      入口程序集（entry.assembly）
├── Hello.World.pri      编译后的 XAML 资源（resources.pri，可选）
├── lib\                 私有依赖程序集（可选，宿主会从插件目录自动探测）
├── native\              原生 DLL（可选，不推荐）
└── settings.json        宿主生成，别手改
```

脚本型（`kind: script`）：

```text
plugins\script.hello\
├── plugin.json
├── scripts\
│   └── main.js          清单 scripts[].file 指向它
└── settings.json        宿主生成
```

### 2.3 安装步骤

**方式一：安装插件包（推荐）**

1. 下载插件的 `.lumenplugin` 文件（本质是 zip，含 `plugin.json`）。
2. 打开「设置 → 插件」，点右上角「安装插件包…」（也可以在空状态里点同名按钮）。
3. 选中文件后启动器自动解压、校验清单、放进 `plugins\<id>`；成功后弹「插件已安装」，重启启动器即生效。
4. 已经装过同名插件时会拒绝覆盖并提示先删除旧目录 —— 升级请先卸载再装（见 2.4）。

实现：`src/NovaLink.Lumen/Plugins/PluginPackageInstaller.cs`。它只做“放到位”，不热加载；包内文件超 200 MB / 5000 个、含目录穿越、缺 `plugin.json`、清单校验失败都会给出中文原因并取消安装。

**方式二：手动复制文件夹**

1. 打开「设置 → 插件」，点「打开插件目录」（等价于自己打开 `%LOCALAPPDATA%\NovaLink Lumen\LauncherData\plugins`）。
2. 把插件文件夹放进去，文件夹名必须与 `plugin.json` 的 `id` 完全一致（例如 `hello.world`）。
3. 确认 `plugins\hello.world\plugin.json` 存在（不是 `plugins\hello.world\hello.world\plugin.json`）。
4. 重启启动器。编译型插件会在左导航底部出现入口；脚本插件在启动时自动执行，没有界面。

**方式三：从插件商店安装**

1. 左侧导航 →「发现」→ 切到「启动器插件」。
2. 选中插件点安装：启动器从该仓库的 Release 下载 `<id>.lumenplugin`，校验后放进 `plugins\<id>`。
3. 重启启动器生效。商店的筛选、缓存与上架规则见第 3 节与 [STORE.md](STORE.md)。

### 2.4 停用、启用与卸载

- **临时停用**：设置 → 插件列表里关掉开关，插件 id 会写进 `lumen.json` 的设置扩展项 `disabledPlugins`（`LumenRuntime.cs:279` 附近读取），重启后不加载。
- **重新启用被隔离的插件**：插件行上的开关重新打开即可（实现为 `PluginRegistry.ClearQuarantine`），下次启动重试；连续失败计数同时清零。
- **退出安全模式**：安全模式横幅上的「重新启用插件并重启」会调用 `PluginRegistry.ClearAllQuarantines()` 解除全部隔离，再清掉安全模式启动标记并重启。看门狗的 `MarkHealthy()` 只清“启动尝试次数/安全模式原因”，**不会**顺手洗白隔离与失败历史 —— 所以两件事都必须做，只调 `MarkHealthy()` 会出现“点了按钮但插件还是没加载”。
- **卸载（推荐）**：设置 → 插件 → 该插件行右侧的【删除】，二次确认后立即从插件列表和左侧导航移除。插件正在运行时 DLL 被进程占用删不掉，启动器会把整个目录改名挪进 `plugin-trash`（下次启动彻底清理），并在 `plugin-deletes` 里留一个待删标记 —— 不会出现“删到一半只剩 dll”的坏目录。插件自己的数据（`plugin-data\<id>`）保留，重装后还能接着用。
- **卸载（手动）**：退出启动器后直接删掉 `plugins\<id>` 文件夹。插件自己的 `settings.json` 随目录删除；`plugin-data\<id>` 与 `plugin-boot.json` 里的历史记录不会自动清，介意的话手动删。
- **列表会跟着文件夹走**：启动器监听 `plugins` 目录（400ms 防抖），在资源管理器里增删、替换插件目录后，插件列表与左侧导航会自动更新，**不需要重启**。启动器自己写缓存不会误触发（只认插件清单/程序集/资源这类文件与目录本身的变化）。
- **安装/更新**：同样是替换 `plugins\<id>` 目录，改完仍建议重启启动器让插件重新加载（原因见 5.8）；新装/新删的插件会即时出现在列表里。

## 3. 从插件商店安装

「发现」页里有「启动器插件」这一栏，列出在 GitHub 上打了 `novalink-lumen-plugin` topic 的开源插件仓库。启动器不托管插件、不审核代码，只做三件事：读仓库根目录的 `plugin.json`、按 Release 资产规则算出安装包地址、把选中的包交给安装器。

上架约定（topic、可选的 `store.json`、Release 资产命名）见 [STORE.md](STORE.md)。

### 3.1 玩家怎么用

1. 左侧导航 →「发现」→ 切到「启动器插件」。
2. 列表默认按「推荐」（GitHub star 数，`PluginStoreSort.Stars`）排序，可切换「最近更新」「名称」；搜索框关键词会和 topic 一起提交给 GitHub。
3. 点安装：启动器下载该插件的 `<id>.lumenplugin`，走与「安装插件包…」相同的校验后放进 `plugins\<id>`，重启启动器生效（同 2.3）。
4. 条目会标出本机是否已安装、已装版本；商店版本更新时显示「可更新」。已装同名插件时的覆盖规则与手动安装一致（见 2.4）。安装包名默认按 `<id>.lumenplugin` 推导，作者也可以用仓库根的 `store.json` 指定别的资产名或下载地址。
5. 当前启动器版本不满足插件的 `minLauncher` / `maxLauncher` 时，安装按钮会禁用并给出中文原因（例如“这个插件需要更新的启动器”），不满足条件的插件不会被悄悄装进去。

### 3.2 缓存、限流与“为什么搜不到”

- 两级缓存：搜索结果 30 分钟（`src/NovaLink.Lumen.Services/Plugins/Store/PluginStoreModels.cs:170` 的 `CacheTtl`），仓库清单（`plugin.json` / `store.json`）6 小时（`src/NovaLink.Lumen.Services/Plugins/Store/PluginStoreClient.cs:28` 的 `ManifestTtl`）。缓存文件是数据根下的 `plugin-store-cache.json`（`PluginStoreModels.cs:173`），有效期内重复打开不再请求 GitHub。
- GitHub 对未登录请求有频率限制（搜索接口大约每分钟 10 次）。超限时列表顶部会给出中文提示（“请求太频繁，稍后再试”），有缓存就先显示缓存。
- 搜不到通常只有三种原因：仓库没打 `novalink-lumen-plugin` topic；仓库根的 `plugin.json` 缺失、JSON 坏了或缺少必填字段（商店要求 `id` 合法、`name` 与 `version` 非空，这类仓库会被静默跳过，不报错）；还没在 Release 里传安装包。`apiVersion` 与启动器不符、或 `minLauncher`/`maxLauncher` 不满足的插件**仍然会出现**，只是安装被禁用。
- 启动器**不审核**插件内容，也没有白名单——商店里的东西等于 GitHub 上公开的、打了 topic 的仓库。装之前自己判断可信度（见 7.1）。

## 4. plugin.json 清单

清单是插件唯一的入口描述，宿主先读它、校验它，再决定加载还是禁用。

### 4.1 最小样例：编译型

```json
{
  "schema": 1,
  "id": "example.hello",
  "name": "示例插件",
  "version": "0.1.0",
  "author": "你的名字",
  "description": "在左导航注入一个页面。",
  "apiVersion": 1,
  "minLauncher": "1.4.1",
  "maxLauncher": "",
  "kind": "assembly",
  "entry": {
    "assembly": "Example.Hello.dll",
    "type": "Example.Hello.HelloPlugin"
  },
  "ui": {
    "kind": "auto",
    "nav": [
      {
        "tag": "plugin:example.hello",
        "title": "示例插件",
        "order": 600,
        "glyph": "\uE8F1",
        "page": "Example.Hello.MainPage"
      }
    ]
  },
  "resources": {
    "pri": "Example.Hello.pri"
  }
}
```

### 4.2 最小样例：脚本型

```json
{
  "schema": 1,
  "id": "script.hello",
  "name": "脚本示例",
  "version": "1.0.0",
  "author": "你的名字",
  "description": "免编译脚本插件：演示日志、设置与三个宿主钩子。",
  "apiVersion": 1,
  "minLauncher": "1.4.1",
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

脚本插件不需要 `entry`，也不需要 `ui`。

### 4.3 字段逐条说明

字段定义在 `src/NovaLink.Lumen.Core/Plugins/PluginManifest.cs`，校验规则在 `src/NovaLink.Lumen.Services/Plugins/PluginManifestReader.cs`。

| 字段 | 类型 | 必填 | 说明 |
|---|---|---|---|
| `schema` | int | 是 | 清单格式版本，v1.4.1 必须是 `1`（`PluginManifestReader.cs:128`） |
| `id` | string | 是 | 插件唯一 id：`^[a-z0-9][a-z0-9.\-]{2,63}$`，必须等于插件目录名，不能占用 `novalink.` / `system.` 前缀 |
| `name` | string | 是 | 显示名；为空时界面退化为 id（`PluginDescriptor.cs:71`） |
| `version` | string | 建议 | 插件自己的版本号，只用于展示与日志 |
| `author` | string | 否 | 作者 |
| `description` | string | 否 | 一句话说明 |
| `apiVersion` | int | 是 | 插件 API 版本，v1.4.1 必须**恰好等于** `1`（`PluginApiVersion.Value`，`PluginManifestReader.cs:132`） |
| `minLauncher` | string | 否 | 最低启动器版本；空/缺省表示不限（`PluginVersion.IsCompatible`） |
| `maxLauncher` | string | 否 | 最高启动器版本；空/缺省表示不限 |
| `kind` | string | 是 | `assembly`（A 类）或 `script`（B 类），其它值一律判非法（`PluginManifestReader.cs:140`） |
| `entry` | object | A 类必填 | `{ assembly, type }`：`assembly` 是插件目录内的 `.dll`，`type` 是实现 `ILumenPlugin` 的完整类型名 |
| `scripts` | array | B 类必填 | 每项 `{ file, engine, when }`：`file` 是插件目录内的 `.js`；`engine` 目前只认 `jint`（缺省 `jint`）；`when` 目前只认 `startup`（缺省 `startup`） |
| `capabilities` | string[] | 否 | 能力声明，仅做去空白归一化与记录，v1.4.1 **不做强制校验**（`PluginManifestReader.cs:154-160`） |
| `ui` | object | 否 | UI 声明：`kind`（信息字段，不校验）、`nav`（导航项数组） |
| `ui.nav[].tag` | string | 是（有 nav 时） | 导航唯一标识，**必须以 `plugin:` 开头**，否则清单判非法（`PluginManifestReader.cs:274`） |
| `ui.nav[].title` | string | 是 | 导航标题，不能为空 |
| `ui.nav[].order` | int | 否 | 排序，默认 `500`（`PluginManifest.DefaultNavOrder`） |
| `ui.nav[].glyph` | string | 否 | 导航图标字符（Segoe Fluent Icons 字形，如 `\uE8F1`）；为空时宿主用 `\uE8F1` |
| `ui.nav[].page` | string | 否 | 页面类型全名，宿主用反射创建（见 5.4） |
| `resources` | object | 否 | `{ pri }`：插件自己的 `.pri` 相对路径；不写时宿主会猜 `<程序集同名>.pri`（`PluginUiService.cs:244-260`） |
| `loadContext` | object | 否 | `{ privateAssemblies, native }`：预声明字段；v1.4.1 实际按插件目录与 `lib\`、`native\` 探测，不读这两个列表 |
| `checksum` | string | 否 | 预留字段，v1.4.1 **不校验** |

### 4.4 校验规则（不满足的后果）

清单能读但校验失败时，插件会被登记为 **Disabled**，设置页里能看到原因，启动器继续正常运行。规则（`PluginManifestReader.cs:99-285`）：

1. `schema` 必须为 1；`apiVersion` 必须等于 1；`kind` 必须是 `assembly` 或 `script`。
2. `id` 不能为空、必须匹配正则、**必须等于插件目录名**、不能用保留前缀。
3. A 类：`entry` 必填，`entry.assembly` 必须是插件目录内真实存在的 `.dll`，`entry.type` 不能为空、不能含 `..`。
4. B 类：`scripts` 非空，每项 `file` 必须是插件目录内真实存在的脚本。
5. `ui.nav`：每项不能为空、`tag` 不能为空且必须以 `plugin:` 开头、`tag` 不能重复、`title` 不能为空。
6. 目录体积不能超过 **200 MB**、文件数不能超过 **5000**（防止误拷整个运行时）。
7. 清单 JSON 走 `JsonDefaults.Options`（`src/NovaLink.Lumen.Core/JsonDefaults.cs:9-16`）：**属性名大小写不敏感**，但**不允许注释和尾随逗号**。
8. 所有相对路径（`entry.assembly`、`scripts[].file`、`resources.pri`）都经 `PluginManifestReader.ResolveInside` 检查，含 `..`、绝对路径、跳出插件目录的一律拒绝（`PluginManifestReader.cs:76-96`）。

### 4.5 关于“声明了但暂未启用”的字段

- `capabilities`：只是声明，不会因此获得或失去能力（宿主门面 `IPluginHost` 的注释也写明“后续按 capabilities 收口”）。别指望写个 `network` 就解锁网络。
- `checksum`：暂不校验。
- `ui.kind`：保留字段，v1.4.1 **既不校验也不读取**（统一自动探测），写 `auto`/`compiled`/`loose`/`code`/`none` 都不影响加载。
- `loadContext.privateAssemblies` / `native`：暂不读取；实际行为是把插件目录、`lib\` 当托管依赖搜索路径，把 `native\` 当原生 DLL 目录（`PluginLoadContext.cs:85-96`）。

## 5. 类型 A：编译型 C# + XAML 插件

现成模板：`docs/plugin-template/`（id `hello.world`，一键 `build.ps1` 打包）。照着改三处名字就能跑。

### 5.1 五分钟流程

1. 复制 `docs/plugin-template/` 到仓库外的工作目录。
2. 改 `plugin.json` 的 `id`，并把文件夹名改成同一个 id。
3. 改 `Hello.World.csproj` 的 `AssemblyName`（与 `entry.assembly` 一致）。
4. 改 `HelloPlugin.cs` 的命名空间/类名（与 `entry.type` 一致）。
5. `powershell -ExecutionPolicy Bypass -File .\build.ps1`，得到 `dist\hello.world.lumenplugin`（本质是 zip）。
6. 解压到 `%LOCALAPPDATA%\NovaLink Lumen\LauncherData\plugins\hello.world\`，重启启动器。

### 5.2 csproj 的关键点

模板 `Hello.World.csproj` 里有四条硬约束，别改：

| 设置 | 值 | 为什么 |
|---|---|---|
| `TargetFramework` | `net10.0-windows10.0.19041.0` | 必须与宿主一致，否则类型/投影对不上 |
| `RuntimeIdentifier` / `PlatformTarget` | `win-x64` / `x64` | 宿主是 x64 |
| `DisableEmbeddedXbf` | `false` | 否则不产出插件自己的 `.pri`，运行期 `XamlParseException` |
| `WindowsSdkPackageVersion` | `10.0.26100.86` 或更高 | 低了会 `CS1705`（WinRT 投影版本比 Windows App SDK 旧） |
| `ProjectReference` 到 `NovaLink.Lumen.Core` | `Private=false` | 不把 Core 复制进插件包，避免与宿主各持一份导致 `InvalidCastException` |
| `Microsoft.WindowsAppSDK` | `Private=false` | 同上，WinUI 由宿主提供 |

插件只能引用 `NovaLink.Lumen.Core`。**不要引用 `NovaLink.Lumen` 或 `NovaLink.Lumen.Services`**，也不要打包这些程序集——它们由宿主的加载上下文共享（`PluginLoadContext.cs:17-39`）。

### 5.3 入口类

入口类必须 `public`、非抽象、带无参构造函数，并实现 `ILumenPlugin`（`ILumenPlugin.cs:8-15`）：

```csharp
using NovaLink.Lumen.Core.Plugins;

namespace Example.Hello;

public sealed class HelloPlugin : ILumenPlugin
{
    public void Initialize(IPluginContext context)
    {
        context.Log.Info("插件已加载。");
        context.Ui.AddNavigation(new PluginNavItem(
            Tag: "plugin:example.hello",
            Title: "示例插件",
            PageFactory: () => new MainPage(context),
            Order: 600,
            Glyph: "\uE8F1"));
    }

    public void Shutdown() { }
}
```

`Initialize` 里别做长时间阻塞；抛异常只会禁用这一个插件（`PluginRegistry.cs:158-177`）。

### 5.4 导航注入的两条路

**路 1：清单声明 `ui.nav[].page`**（推荐）

宿主按 `page` 的类型全名反射创建页面实例（`PluginRegistry.cs:376-393`）。要求：类型是全名（含命名空间）、是 public 非抽象类。类型找不到时只记一条 WARN 并跳过该导航项，不影响插件其余功能。

**路 2：在 `Initialize` 里 `context.Ui.AddNavigation(...)`**

自己给 `PageFactory`，适合需要向页面传依赖（如 `context`）的插件。`tag` 同样必须以 `plugin:` 开头，否则宿主会 Warn 并忽略（`PluginUiService.cs:52-75`）——这是为了防止插件顶掉内置导航。

两条路可以只用一条，也可以都用（`tag` 不要重复）。导航项登记后由宿主排序（先 `order` 再 `title`）并注入左导航底部。

### 5.5 PRI 与 XAML 元数据（宿主自动处理）

如果你用了编译型 XAML，打包时必须带上同名 `.pri`。宿主在用户第一次点开该插件页面时会：

1. 找 PRI：优先用清单 `resources.pri`，否则猜 `<entry.assembly 同名>.pri`（`PluginUiService.cs:244-260`）；
2. 调 `DynamicLoader.TryLoadPri` 挂到宿主（每个插件只做一次；失败只显示“界面资源没能加载，可能需要更新这个插件”）；
3. 在插件程序集里找 `IXamlMetadataProvider` 并注册（`PluginUiService.cs:267-301`）。没有 provider 不报错——纯代码 UI 插件本来就没有，日志里会写一句“纯代码 UI 插件属正常情况”。

**只点开时才加载资源**，所以插件装了但没人点，不会有 PRI 开销。

### 5.6 T1 / T2 / T3 三档 UI 能力

| 档位 | 做法 | v1.4.1 支持度 | 说明 |
|---|---|---|---|
| **T1** | 编译型 XAML（`.xaml` + 生成 `.pri`），模板即此档 | ✅ 完整支持 | 推荐。需要 `DisableEmbeddedXbf=false` + `resources.pri` + 元数据 provider 随程序集生成 |
| **T2** | 宽松 XAML：运行时把 XAML 字符串交给 `XamlReader.Load` | ⚠️ 受限 | 宿主没有为插件提供“宽松 XAML”专用加载通道；你自己调用时只能解析**框架内置类型**，插件自定义类型解析不了 |
| **T3** | 纯代码构建 UI（`Page`/`UserControl` 全代码，无 XAML） | ✅ 支持 | 不需要 `.pri`，也没有元数据 provider；宿主日志会说明属正常情况 |

结论：需要自定义控件就用 T1；想省事、界面简单就用 T3；别指望 T2 解析插件自己的类型。

### 5.7 类型 A 的能力与限制

可以做（全部经由 `IPluginContext`，见 5.11）：

- 读启动器版本、数据根、当前账户名；
- 列出实例、启动实例；
- 发受限 HTTP GET（宿主 HttpClient，20 秒超时，失败返回 `IsSuccess=false`）；
- 弹通知、写日志、读写自己的设置；
- 订阅宿主事件；
- 注入导航页面。

限制：

- **不能热卸载/热替换**（见 5.8）；
- 不要自己读写 `lumen.json`，用 `IPluginSettings`；
- 不要把宿主程序集打包进插件（见 5.2）；
- 插件是**进程内任意代码**，宿主不做沙箱（见 7.1）。

### 5.8 为什么不能热卸载

宿主确实用可回收的 `AssemblyLoadContext` 加载插件，退出时也会调用 `Unload()`，但那只是“尽力而为”：

- WinUI 的 XAML 类型与 `IXamlMetadataProvider` 一旦注册进宿主就**没有反注册 API**，重新加载同类型会冲突；
- 插件页面对象一旦挂上可视化树，就被宿主与 XAML 框架引用，卸载后旧实例仍在；
- 已加载的 `.pri` 也无法从进程里摘掉。

所以 v1.4.1 的更新方式只有一个：**退出启动器 → 替换插件目录 → 重新启动**。

### 5.9 打包

`docs/plugin-template/build.ps1` 做三件事：`dotnet build -c Release -p:Platform=x64` → 核对 `.dll`/`.pri`/`plugin.json` 齐全 → 打成一个 zip 并改名 `.lumenplugin`。

包里**不要**包含这些（加载上下文会从宿主共享，`PluginLoadContext.cs:17-39`）：`Microsoft.WinUI.dll`、`Microsoft.Windows.SDK.NET.dll`、`WinRT.Runtime.dll`、`Microsoft.Graphics.Win2D.dll`、`DynamicXaml.WinUI.dll`、`NovaLink.Lumen*.dll`、`System.*.dll`。

### 5.10 常见编译/运行错误

| 现象 | 原因 | 处理 |
|---|---|---|
| 编译报 `CS1705` | WinRT 投影比 Windows App SDK 旧 | `WindowsSdkPackageVersion` 提到 `10.0.26100.86` 或更高 |
| 运行时报 `XamlParseException` | 没有插件自己的 `.pri` | `DisableEmbeddedXbf=false`，确认 `.pri` 打进包，`resources.pri` 路径正确 |
| 运行时报 `InvalidCastException` | 插件自带了宿主程序集（Core/WinUI）副本 | 相关引用加 `Private=false`，打包时排除共享程序集 |
| 导航项没出现 | `page` 类型名不对 / `tag` 没以 `plugin:` 开头 / 清单校验失败 | 看日志（8.1）里的 WARN |
| 页面显示“插件页面加载失败…” | 页面构造抛异常或 PRI 加载失败 | 看 `[plugin-ui]` 与 `[plugin:<id>]` 日志，修正后重启 |
| 加载时报“找不到实现 ILumenPlugin 的入口类型” | `entry.type` 与类名/命名空间不一致 | 改成完整类型名（含命名空间） |

### 5.11 C# API 一览

全部在 `NovaLink.Lumen.Core.Plugins` 命名空间，插件只需要引用 Core：

| 接口 | 成员 | 说明 |
|---|---|---|
| `ILumenPlugin` | `Initialize(IPluginContext)` / `Shutdown()` | 入口；实例由宿主创建 |
| `IPluginContext` | `PluginId` / `Log` / `Settings` / `Host` / `Ui` / `Subscribe(PluginEvent, Action<PluginEventArgs>)` | 插件能接触到的全部宿主能力 |
| `IPluginLogger` | `Info` / `Warn` / `Error(msg, ex?)` | 写入统一日志，区域名 `plugin:<id>` |
| `IPluginSettings` | `Get(key)` / `Set(key, value)` / `Flush()` | 值按字符串存，写入 `plugins\<id>\settings.json`，宿主节流落盘 |
| `IPluginHost` | `LauncherVersion` / `DataRoot` / `CurrentAccountName` / `CurrentAccount` / `ListInstances()` / `LaunchInstanceAsync(id)` / `HttpGetAsync(url, headers?)` / `Notify(title, msg, level)` | 宿主能力门面；`CurrentAccount` 只有显示名、UUID、头像（`IPluginHost.cs:15-22`），没有任何取令牌的方法；每个方法失败都给明确结果而不是崩进程 |
| `IPluginUi` | `IsAvailable` / `AddNavigation(PluginNavItem)` | 真实实现 `IsAvailable == true` |
| `PluginNavItem` | `(Tag, Title, PageFactory, Order = 500, Glyph = null)` | 导航项；`PageFactory` 返回 `object`，由宿主转成 Page |
| `PluginEvent` | `AppStartup` / `AppShutdown` / `LaunchPre` / `LaunchPost` / `DownloadResolve` / `TextReplace` | 订阅后 `Dispose` 取消 |

## 6. 类型 B：JS 脚本插件

脚本插件不需要编译、不需要 .NET SDK：写一个 `scripts/main.js`，放进插件目录，重启启动器即生效。引擎是 Jint（纯托管 JS 解释器，`src/NovaLink.Lumen.Services/NovaLink.Lumen.Services.csproj:11-12`）。**v1.4.1 只支持 JavaScript；Python 计划 v1.5。**

可直接用的模板：`docs/plugin-template/script-hello/`。

### 6.1 最小结构

```text
plugins\script.hello\
├── plugin.json
└── scripts\main.js
```

`plugin.json` 见 4.2；`main.js` 里直接写顶层代码，启动时同步执行一遍。

### 6.2 执行时机与生命周期

- 每个插件可以声明多个 `scripts` 条目，按数组顺序**同步**执行；
- `when` 目前只认 `startup`（缺省即 startup）；其它值会在发现阶段记一条 WARN 并跳过该条目；
- `engine` 目前只认 `jint`（大小写不敏感）；其它值同样跳过并 Warn；
- 脚本文件在插件初始化时同步执行完，**没有常驻事件循环**；
- 插件被禁用/启动器退出时，它注册的钩子与事件会自动注销，脚本里不用手动清理。

### 6.3 lumen API 全表

脚本里**唯一**可用的全局对象是 `lumen`。没有 `window`、`process`、`require`、`console`、`fetch`、`setTimeout`。

| API | 参数 | 返回 | 说明 |
|---|---|---|---|
| `lumen.log(msg)` | 任意值 | `undefined` | `String(value)` 后写日志（INFO） |
| `lumen.info(msg)` | 任意值 | `undefined` | 同上 |
| `lumen.warn(msg)` | 任意值 | `undefined` | WARN 级别 |
| `lumen.error(msg, err?)` | 任意值；`err` 可为异常/字符串 | `undefined` | ERROR 级别，第二参数会一起记 |
| `lumen.plugin.id` | — | `string` | 插件 id |
| `lumen.plugin.version` | — | `string` | 清单 `version` |
| `lumen.plugin.dataDir` | — | `string` | `<数据根>\plugin-data\<id>`，已自动创建，可用来拼自己数据文件的路径（但脚本没有文件 API） |
| `lumen.settings.get(key)` | `string` | 原类型或 `null` | 返回 `set` 时的类型（字符串/数字/布尔/数组/对象） |
| `lumen.settings.set(key, value)` | `string`, 可 JSON 序列化的值 | `undefined` | 立即写入内存；宿主负责落盘到 `plugins\<id>\settings.json`。键名 ≤ 64 字符、序列化后 ≤ 8 KB（`ScriptHostApi.cs:32-33`） |
| `lumen.hooks.onDownloadRequest(fn)` | `function(req)` | 注册 id（number） | 下载地址解析；返回 `{ url, displayName?, headers?, sha1? }` 或 `null`（不接管） |
| `lumen.hooks.onTextReplace(fn)` | `function(text)` | 注册 id | 文本替换；返回新字符串或 `null`（不修改） |
| `lumen.hooks.onNotification(fn)` | `function(title, body)` | 注册 id | 通知过滤；返回 `false` 表示不展示，`null`/`undefined` 视为展示 |
| `lumen.hooks.off(id)` | 注册 id | `undefined` | 注销单个钩子（插件关闭时全部自动注销） |
| `lumen.events.on(name, fn)` | `name`, `function(args)` | 注册 id；未知事件名返回 `0` | 订阅事件；支持的名字见 6.4 |
| `lumen.events.off(id)` | 注册 id | `undefined` | 注销事件订阅 |

几个硬上限（`ScriptHostApi.cs:31-34`）：单条日志截断到 2000 字符；`onTextReplace` 返回超过 1 MB 的字符串会被丢弃（按未修改处理）；`onNotification` 返回非布尔值按“显示”处理。

`onDownloadRequest` 回调收到的 `req` 字段（宿主 `DownloadRequest`，`HookRegistry.cs:128-163`）：

| 字段 | 类型 | 说明 |
|---|---|---|
| `url` | string | 官方原始下载地址 |
| `fileName` | string \| null | 建议保存文件名 |
| `size` | number \| null | 期望字节数 |
| `sha1` | string \| null | 官方 SHA1；返回镜像时必须保证内容一致 |
| `purpose` | string \| null | 用途：`java` / `game` / `assets` / `modloader` 等 |

> 宿主原本的请求头不暴露给脚本（C# 侧注释也写明“插件可参考，但不得依赖”）。

### 6.4 回调约定与事件名

所有回调**同步返回**，不支持 `async`/`Promise`；返回 Promise 会被当成“不接管/不修改”（v1.5 再考虑异步）。

- 多个脚本注册同一钩子时按注册顺序逐个调用，但合并规则不同（`ScriptHookBus.cs:185-280`）：
  - `onDownloadRequest`：**第一个返回非 `null` 的生效**，后面的脚本不再被询问；
  - `onTextReplace`：**链式叠加**，后一个脚本拿到的是前一个改过的文本；
  - `onNotification`：**任一返回 `false` 就不展示**（全部允许才展示）。
- 钩子抛异常：只记 `[plugin:<id>]` 日志，并回落到宿主默认行为（不接管/不修改/展示），不影响其他插件；
- 事件名：`app.startup`、`app.shutdown`、`download.before`、`download.after`；未知名字记 warn 并返回 `0`；
- 事件处理器抛异常只写该插件日志，不影响宿主流程；
- 钩子是否真的被调用，取决于宿主在下载解析、文本渲染、通知展示三条路径上的调用点是否接入；回调里第一行写 `lumen.log(...)` 是最快的自检方式（见 6.9）。

### 6.5 同步限制与执行预算

- 没有异步：没有 `Promise`、没有 `async/await` 等待、没有 `setTimeout/setInterval`；
- 每次执行超时 **2000 ms**、内存上限 **32 MB**、语句上限 **500000**、递归深度上限 **64**（`ScriptEngineFactory.cs:12-24`），单个脚本文件不超过 **2 MB**；
- 脚本是启动路径的一部分，别写死循环、别做重活。

### 6.6 脚本沙箱

Jint 以受限模式运行（`Interop.Enabled=false`、模块系统关闭、`StringCompilationAllowed=false`）：

| 不能做的事 | 原因 |
|---|---|
| 读写文件 | 没有任何文件 API，也没有 .NET 反射可用 |
| 访问网络 | 没有 `fetch`/`HttpClient`；脚本 API 不暴露 HTTP |
| `require` / `import` | 模块系统未启用 |
| `eval` / `new Function` | 字符串编译被禁用 |
| 用 .NET 类型/反射绕过 | Interop 关闭，拿不到 `System.Type`/`Assembly` |
| 长期驻留后台 | 没有事件循环，脚本执行完就结束 |

### 6.7 错误会被怎么隔离

| 错误 | 日志关键字 | 后果 |
|---|---|---|
| 脚本语法/编译错误 | `脚本编译失败：` | 该插件加载失败；记一次失败计数 |
| 运行期异常 | `脚本执行失败：`（含文件名，截断到 400 字符） | 同上 |
| 钩子回调抛异常 | `插件钩子异常已隔离` | 回落宿主默认行为，插件继续运行 |
| 事件处理器抛异常 | `事件处理器抛出异常，已隔离` | 只记日志 |

同一插件连续失败 **3 次**会被自动隔离（`PluginWatchdog.MaxPluginFailures = 3`，`PluginWatchdog.cs:16`），之后不再自动加载；在设置页点「重新启用」可解除。设置页显示的 `LastError` 形如 `加载失败：<原因>`。

### 6.8 完整的 main.js 示例

```javascript
// API 见 docs/PLUGINS.md 第 6 节。v1.4.1 只支持同步 JS（没有 Promise/await）。

lumen.log("script.hello 已启动，插件版本 " + lumen.plugin.version);
lumen.info("数据目录：" + lumen.plugin.dataDir);

// 1) 设置：读上次的计数，写回 +1
var count = lumen.settings.get("runCount");
count = (typeof count === "number" ? count : 0) + 1;
lumen.settings.set("runCount", count);
lumen.log("这是第 " + count + " 次启动。");

// 2) 下载地址：把 GitHub 官方地址换成镜像（内容必须一致）
lumen.hooks.onDownloadRequest(function (req) {
  if (req.purpose === "modloader" && req.url.indexOf("github.com") >= 0) {
    return { url: req.url.replace("github.com", "mirror.example.com"), displayName: "镜像 A" };
  }
  return null; // 不接管
});

// 3) 文本替换：统一术语
lumen.hooks.onTextReplace(function (text) {
  if (!text) return null;
  return text.replace(/\bMinecraft\b/g, "我的世界");
});

// 4) 通知过滤：屏蔽某类通知
lumen.hooks.onNotification(function (title, body) {
  if (title.indexOf("自动检查更新") >= 0) return false;
  return true;
});

// 5) 事件订阅
lumen.events.on("app.startup", function (args) {
  lumen.log("收到 app.startup 事件。");
});
lumen.events.on("download.before", function (args) {
  lumen.log("即将下载：" + (args && args.url ? args.url : "未知地址"));
});
```

### 6.9 调试脚本插件

1. 先用 `--dev-mode` 启动：日志会多出发现插件、脚本条目、注册钩子数等细节；
2. 打开日志文件（8.1），搜 `plugin:<你的id>`；
3. 钩子是否真的被调用：在回调第一行写 `lumen.log("...")`；
4. 改完 `.js` 必须重启启动器（脚本在启动时执行一次）。

## 7. 安全模型

### 7.1 插件运行在启动器进程内

这是最重要的一句话。**A 类插件是任意 .NET 代码**，与启动器共享同一个进程、同一个 `AppDomain`，能读进程内存里的一切、能访问当前用户能访问的文件与网络。宿主提供的 `IPluginHost` 只是“方便的、可审计的门面”，不是沙箱（`ILumenPlugin.cs:19` 也这么写）。

所以：

- 只安装你信任的插件，尤其是 A 类；
- 插件的 `capabilities` 是声明，不是权限（见 4.5）；
- 宿主能保证的是“插件出错不拖垮启动器”，不是“插件干不了坏事”。
- 凭据与令牌另有一层入口守卫，见 7.2；那同样是尽力而为，不是沙箱。

**v1.5.0 起还多了一层“默认不开”**：`LumenRuntime.PluginsEnabled` 只在设置里 `ExperimentalPlugins` 为真、或本次启动带 `--experimental-plugins` 时才为真（`LumenRuntime.cs:133-135`）。为假时宿主**不建插件注册表、不扫描插件目录、不加载任何插件、也不注册任何钩子**，还会顺手 `HookRegistry.Reset()` 清掉可能残留的委托与静态事件（`LumenRuntime.cs:283-291`）。安全模式判定在这层之上：`--safe-mode` / `--no-plugins` / `NOVALINK_SAFE_MODE` 一旦命中，即使实验性开关打开也不会加载插件。

### 7.2 凭据、令牌与 AI 密钥：插件拿不到什么

这是 v1.4.1 特意收紧的一层。四条事实：

**① 宿主 API 只给账户的“样子”，不给钥匙。** `IPluginHost` 的成员是 `LauncherVersion`、`DataRoot`、`CurrentAccountName`、`CurrentAccount`、`ListInstances()`、`LaunchInstanceAsync()`、`HttpGetAsync()`、`Notify()`（`src/NovaLink.Lumen.Core/Plugins/IPluginHost.cs:7-38`）。`CurrentAccount` 是 `PluginAccount`：**显示名 + UUID + 头像地址**（`:15-22`）；`AccountRecord` 上的 `AccessToken` / `RefreshToken` / `NovalinkToken` / `ForumToken` / `SkinPath` 在 `LumenPluginHost.CurrentAccount` 里根本不会被复制（`src/NovaLink.Lumen/Plugins/LumenPluginHost.cs:41-71`）。**插件没有任何“取令牌”的宿主 API。**

**② 插件读 `lumen.json` 只能看到密文。** 账户令牌在 `<数据根>\lumen.json` 里以 `enc:` + Base64 的形式保存，`enc:` 是 DPAPI（`DataProtectionScope.CurrentUser`）加密后的结果，熵常量是 `NovaLink.Lumen.Accounts.v1`（`src/NovaLink.Lumen.Services/Storage/SecretProtector.cs:13-27`）。文件里没有明文令牌，直接读文件拿到的只是一串密文。

**③ 插件直接调解密 API 会被信任边界拦住。** `SecretProtector.Unprotect` 与 `AccountService` 的四个取令牌方法（`NovalinkTokenFor` / `MicrosoftTokenFor` / `ForumTokenFor` / `MicrosoftRefreshTokenFor`）在入口处调用 `PluginTrustBoundary.EnsureTrustedCaller`（`SecretProtector.cs:29-33`、`AccountService.cs:90-114`）。守卫沿着调用栈找第一个“宿主之外”的帧，如果它来自可回收的插件加载上下文（`PluginTrustBoundary.IsPluginAssembly`，`src/NovaLink.Lumen.Core/Plugins/PluginTrustBoundary.cs:24-34`），就抛 `UnauthorizedAccessException`：

```text
插件不能读取启动器保存的账户凭据。插件只应通过宿主 API 工作，凭据与密钥不对插件开放。
```

文案来自 `PluginTrustBoundary.cs:75-76`；判定逻辑在 `:41-70`。异常只影响**这个插件自己的那次调用**：脚本插件根本调不到这些 .NET API（Jint 没有 CLR 互操作，见 6.6），A 类插件则在它自己的 try/catch 里看到 `UnauthorizedAccessException`，启动器与其它插件不受影响。

**④ AI 文案没有第三方 key 可偷。** 首页标语等 AI 生成由 NovaLink 服务端代理，客户端不内置任何第三方密钥（`src/NovaLink.Lumen.Services/Home/HeroTaglineService.cs:10-13`），插件在本地找不到可复用的模型密钥。

**这套机制的边界，务必读完：**

- 插件与启动器**同进程、同用户**，信任边界是**尽力而为的纵深防御**，不是沙箱；
- A 类插件是任意 .NET 代码，理论上仍可用反射、直接调 DPAPI 等手段绕开公开 API——`PluginTrustBoundary.cs:8-18` 的注释写得很直白：做不到内核级隔离，只能保证“通过公开 API 拿不到令牌”；
- 所以正确的理解是“插件不会顺手拿到令牌”，而不是“插件绝对拿不到任何数据”；
- **只安装可信来源的插件**（插件商店、官方模板、自己审过的源码），装前看一眼作者与仓库；
- 真正的强隔离（独立进程 + 显式 IPC 白名单）留给后续版本，v1.4.1 没有。

### 7.3 脚本插件相对安全

B 类插件受 Jint 沙箱限制（见 6.6），只暴露 `lumen.*` 这组能力：没有文件、网络、反射，也没有账户 API——脚本连 `lumen.plugin.id / version / dataDir` 之外的启动器状态都读不到。

### 7.4 安全模式与禁用开关

| 方式 | 效果 | 说明 |
|---|---|---|
| `--safe-mode` | 本次启动不加载任何插件 | 启动器其余功能正常；属于诊断模式（界面会有提示） |
| `--no-plugins` | 本次启动不加载任何插件 | 与 safe-mode 同样禁用插件，但不带诊断态标记 |
| 环境变量 `NOVALINK_SAFE_MODE=1` | 同 `--safe-mode` | `1/true/yes/on` 都算真（`StartupOptions.cs:115-119`、`:159`） |
| 环境变量 `NOVALINK_NO_PLUGINS=1` | 同 `--no-plugins` | 同上 |
| 设置 → 插件 → 对应开关 | 该插件不加载 | 写入 `disabledPlugins`，重启生效 |

参数解析实现：`src/NovaLink.Lumen/Startup/StartupOptions.cs:86-119`；最终 `PluginsDisabled = SafeMode || NoPlugins`（`:49`），由 `LumenRuntime.cs:242` 传给 `PluginRuntimeOptions.SafeMode`。

**玩家排障口诀**：启动器打不开/一开就闪退 → 用 `--safe-mode` 启动；能打开 → 是插件的问题；逐个移出插件目录定位。

### 7.5 启动看门狗与 plugin-boot.json

宿主在数据根目录维护 `plugin-boot.json`（`PluginPaths.cs:43`，模型见 `PluginBootState.cs:22-51`）：

```json
{
  "schema": 1,
  "state": "healthy",
  "attempts": 0,
  "lastStartUtc": null,
  "lastHealthyUtc": null,
  "safeModeReason": null,
  "plugins": []
}
```

- 开始加载插件时写 `state=starting`、`attempts+1`；
- 启动完全成功后写回 `healthy`、`attempts=0`；
- 如果下次启动读到 `attempts >= 2` 且上次不是 `healthy`，说明**连续两次启动都崩在插件加载阶段**，看门狗自动进入安全模式，并把原因放进 `safeModeReason`（`PluginWatchdog.cs:42-57`、`PluginRegistry.cs:97-100`）；
- 这是为了应对“装了坏插件、启动器反复闪退、用户进不去设置”的死循环。

### 7.6 开发者工具：从源码目录打包并安装

v1.4.1 的开发者工具在 **设置 → 插件 → 开发者工具**（只在 `--dev-mode` 或设置里打开开发模式后出现）：

1. 点「选择源码目录并打包…」选插件源码目录（里面必须有 `plugin.json`）；
2. 宿主按这个顺序打包（`PluginDevBuilder.cs`）：
   - 目录里有 `build.ps1` → 直接跑它（`powershell -NoProfile -ExecutionPolicy Bypass -File`，10 分钟超时），取脚本产出的 `.lumenplugin`；
   - 否则找同名的 `.csproj` → 跑 `dotnet build -c Release -p:Platform=x64`，在 `bin` 下找到含 `entry.assembly` 的输出目录 → 按 `plugin.json` 组装成包（`plugin.json` + 程序集 + `*.pri` + `privateAssemblies` 进 `lib\` + 原生库）；
   - `kind=script` 的插件目录不需要编译，直接把目录打成包（排除 `bin/obj/.git/dist` 等）。
3. 产物落在 **源码目录的 `dist\`**（可用「打开产物目录」查看），路径会显示在设置页；
4. 打包成功后自动安装到插件目录；同名插件会先问一次「删除并重装」。

**“插件正在运行，文件被占用”怎么办**：插件 DLL 在当前进程里映射着，删不掉。这时宿主不会报错，而是**把新包排进队列**：

```text
<DataRoot>\plugin-updates\<id>.lumenplugin      排队中的新版本
<DataRoot>\plugin-trash\<id>-<时间戳>            被替换下来的旧版本（尽力清理）
```

界面上显示「已打包，重启后替换」，点【立即重启】后：新进程在加载插件**之前**（`LumenRuntime.cs` 的 `ApplyPendingUpdates`）把旧目录整体改名挪进 `plugin-trash`（目录改名是原子操作，不会像递归删除那样中途失败、留下半个插件），再解压新包，然后才扫描插件。旧进程如果还没完全退出，改名会重试最多 6 次（每次 400ms）；仍不成功就保留队列，下次启动再试。

所以开发迭代的正确姿势是：改代码 → 设置里点「选择源码目录并打包…」→【立即重启】→ 新版本已生效。**不支持热卸载/热重载**（原因见 5.8，开发者模式的实际放宽范围见 7.7）。

### 7.7 --dev-mode 到底放宽了什么

v1.4.1 的**实际行为**：只增加日志细节（发现插件、脚本条目、注册钩子数、加载耗时等你排查需要的上下文），**不放宽任何安全限制**，也不放宽沙箱。`StartupOptions.cs:18` 的注释写的是设计目标（放宽沙箱/允许热重载），以本节的实现为准。

用法：`NovaLink.Lumen.exe --dev-mode`（或环境变量 `NOVALINK_DEV_MODE=1`）。

### 7.8 玩家会看到什么

玩家的界面里不出现堆栈、文件路径、异常类型：

- 插件页面加载失败 → 页面宿主显示一句人话 + 【重试】/【返回首页】（`PluginPageHost.xaml.cs`）；
- 插件被禁用/隔离 → 设置 → 插件列表里显示原因文本（“已在设置中禁用”“连续加载失败 N 次，已隔离”“与当前启动器版本不兼容”等）；
- 看门狗自动进安全模式 → 设置页插件卡片顶部出现安全模式提示与「重新启用插件」按钮；
- 详细原因只在日志里。

## 8. 故障排查

### 8.1 日志在哪、怎么看

唯一日志文件（每天一个）：

```text
%LOCALAPPDATA%\NovaLink Lumen\LauncherData\logs\lumen-yyyyMMdd.log
```

每行格式：`{时间} [{级别}] [{区域}] {消息}`（`AppLog.cs:20`）。与插件相关的区域：

| 区域 | 内容 |
|---|---|
| `plugins` | 发现、校验、加载、禁用原因、看门狗、安全模式 |
| `plugin:<id>` | 插件自己写的日志、脚本错误、钩子/事件异常 |
| `plugin-ui` | 导航登记、PRI 加载、页面创建失败 |

快速查看（Windows PowerShell 5.1）：

```powershell
$root = "$env:LOCALAPPDATA\NovaLink Lumen\LauncherData"
Get-ChildItem "$root\logs" | Sort-Object LastWriteTime -Descending | Select-Object -First 3
Get-Content "$root\logs\lumen-$(Get-Date -Format yyyyMMdd).log" -Tail 200
Select-String -Path "$root\logs\lumen-*.log" -Pattern 'plugin|插件' | Select-Object -Last 50
```

### 8.2 症状对照表

| 症状 | 常见原因 | 处理 |
|---|---|---|
| 插件完全不出现，设置里也没有 | 放错层级（`plugins\x\x\plugin.json`）、`plugin.json` 缺失/坏 JSON、`id` 与目录名不一致、处于安全模式 | 先看 `[plugins]` 日志里“忽略插件目录”“清单校验失败”；对齐目录名；去掉 `--safe-mode` |
| 设置里有插件但显示“未加载” | 清单校验失败、版本不兼容、被用户禁用、被隔离 | 日志里有具体原因；修清单 / 换版本 / 打开开关 / 点重新启用 |
| 插件加载了但导航栏没有入口 | 没声明 `ui.nav`、`tag` 不是 `plugin:` 开头、`page` 类型名错误、`title` 为空 | 查 `[plugins]` 的“导航项注册失败”和 `[plugin-ui]` 的 WARN |
| 点开导航后页面白屏 | 页面构造函数抛异常、XAML 资源没加载 | 看 `[plugin-ui]` 的“资源加载失败”与 `[plugin:<id>]` 异常；确认 `.pri` 打进包 |
| 首屏就报“插件页面加载失败，可以点【重试】” | 页面类型创建失败（抽象类/无无参构造/类型名错） | 修正 `page` 或 `PageFactory`；或点【重试】再试 |
| 启动器反复闪退 | 某个插件在 `Initialize` 里崩进程 | 用 `--safe-mode` 启动，移出最近安装的插件；看门狗连续两次后会自己进安全模式 |
| 脚本没执行 | `kind` 不是 `script`、`scripts[].file` 不存在、`when`/`engine` 值不对、插件被禁用 | 搜 `plugin:<id>` 看“脚本编译失败/执行失败”；`--dev-mode` 看脚本条目 |
| 钩子没生效 | 回调返回了 `null`、条件没命中、插件未加载 | 在回调第一行 `lumen.log` 打点；确认插件在运行中列表里 |
| 安全模式进不去/想恢复 | 看门狗留下了 `attempts >= 2` | 修好插件后用设置页「重新启用插件」；或退出启动器后删除 `<数据根>\plugin-boot.json` |

### 8.3 解除隔离的三种方式

1. **设置页**：设置 → 插件 → 「重新启用」（推荐，会同时清零失败计数）；
2. **改清单/修文件**：修好导致失败的原因后重启；若已被隔离，仍需用方式 1 或 3 清计数；
3. **手动清状态**：退出启动器，删除 `<数据根>\plugin-boot.json`（会清掉启动尝试次数与所有插件的隔离/失败计数，**不是**常规操作）。

### 8.4 一条命令查清单是否合法

```powershell
$dir = "$env:LOCALAPPDATA\NovaLink Lumen\LauncherData\plugins\hello.world"
Get-Content "$dir\plugin.json" -Raw -Encoding UTF8 | ConvertFrom-Json | Format-List
```

能解析只代表 JSON 合法；`id`/`kind`/路径等规则仍要按 3.4 自查。

## 9. 版本兼容

### 9.1 apiVersion

`apiVersion` 必须与宿主常量**完全相等**（v1.4.1 为 `1`，`src/NovaLink.Lumen.Core/Plugins/PluginApiVersion.cs:10`）。这是刻意的严格匹配：接口一改就递增，旧插件被明确禁用并提示“该插件需要更新版本的启动器或插件格式”，而不是运行期随机抛异常（`PluginManifestReader.cs:132`）。

### 9.2 minLauncher / maxLauncher

由 `PluginVersion.IsCompatible`（`src/NovaLink.Lumen.Services/Plugins/PluginVersion.cs:40-48`）判定：

- 两个字段都可省略/留空，表示不限；
- 版本号支持 1~4 段、可带 `v` 前缀；比较时忽略 `-`/`+`/空格之后的预发布/构建后缀；
- `minLauncher` 写了且当前启动器更低 → 不兼容；`maxLauncher` 写了且当前启动器更高 → 不兼容；
- 当前启动器版本解析失败时按“兼容”处理（宁可让插件试一下）。

不兼容的插件会被禁用，设置页显示“与当前启动器版本不兼容（需要 x ~ y，当前 z）”。

### 9.3 其它版本字段

- `schema`：清单格式版本，必须为 1；
- `version`：插件自己的版本，只用于展示与日志，不参与兼容判定。

### 9.4 给插件作者的建议

- 保守写 `minLauncher`（例如 `1.4.1`），别写 `maxLauncher`，除非你确实依赖了某个特定版本的行为；
- 升级启动器后先看设置页插件列表：被禁用的会写原因；
- 需要新 API 时递增 `apiVersion` 是不行的（宿主只认相等），正确做法是向启动器提需求、由宿主升级 API 版本。

## 10. 代码地图

| 位置 | 内容 |
|---|---|
| `src/NovaLink.Lumen.Core/Plugins/PluginManifest.cs` | `plugin.json` 的 C# 模型与默认值 |
| `src/NovaLink.Lumen.Core/Plugins/ILumenPlugin.cs` | `ILumenPlugin` / `IPluginContext` / 日志 / 设置接口 |
| `src/NovaLink.Lumen.Core/Plugins/IPluginHost.cs` | 宿主能力门面 |
| `src/NovaLink.Lumen.Core/Plugins/IPluginUi.cs` | UI 注入门面与 `PluginNavItem` |
| `src/NovaLink.Lumen.Core/Plugins/PluginEvents.cs` | 事件枚举与载荷 |
| `src/NovaLink.Lumen.Services/Plugins/PluginManifestReader.cs` | 清单读取 + 全部校验规则 |
| `src/NovaLink.Lumen.Services/Plugins/PluginRegistry.cs` | 发现 → 加载 → 初始化 → 事件分发 → 关闭 |
| `src/NovaLink.Lumen.Services/Plugins/PluginWatchdog.cs` | `plugin-boot.json`、安全模式、隔离计数 |
| `src/NovaLink.Lumen.Services/Plugins/PluginLoadContext.cs` | 可回收加载上下文、共享程序集、`lib\`/`native\` |
| `src/NovaLink.Lumen.Services/Plugins/PluginPaths.cs` | 插件相关路径 |
| `src/NovaLink.Lumen.Services/Plugins/Store/PluginStoreClient.cs` | 插件商店客户端：GitHub topic 搜索、仓库清单解析、下载地址推导、两级缓存与限流兜底 |
| `src/NovaLink.Lumen.Services/Plugins/Store/PluginStoreModels.cs` | 插件商店数据契约：`store.json`、排序、topic 与缓存常量 |
| `docs/STORE.md` | 插件商店上架指南（topic / 清单 / Release 资产 / 常见问题） |
| `src/NovaLink.Lumen/Plugins/PluginUiService.cs` | 导航注入、PRI 加载、XAML 元数据、页面创建 |
| `src/NovaLink.Lumen/Plugins/LumenPluginHost.cs` | `IPluginHost` 的真实实现 |
| `src/NovaLink.Lumen/Views/Pages/PluginPageHost.xaml.cs` | 插件页面宿主（失败兜底、重试） |
| `src/NovaLink.Lumen.Services/Hooks/HookRegistry.cs` | 下载解析 / 文本替换 / 通知过滤钩子 |
| `docs/plugin-template/` | 类型 A 模板（可直接编译打包） |
| `docs/plugin-template/script-hello/` | 类型 B 模板（免编译） |

---

相关问题先查日志（8.1），再对照「症状对照表」（8.2）。插件作者需要新能力时，先确认它不是已有 `IPluginHost` 方法能做的事。