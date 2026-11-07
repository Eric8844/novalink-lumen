# NovaLink Lumen 插件模板（类型 A：C# + 原生 UI）

五分钟做出一个能出现在导航栏里的插件页面。

## 0. 你需要什么

- .NET 10 SDK（`dotnet --list-sdks` 能看到 10.x）
- NovaLink Lumen v1.5.1 及以上（液态玻璃组件与主题令牌是 v1.5.1 新增的，更早版本只能自己写画刷）
- 本模板目录（`docs/plugin-template/`），把它整个复制到仓库外面再用，别直接改这里

## 1. 改三处名字

| 位置 | 字段 | 说明 |
|---|---|---|
| `plugin.json` | `id` | 反向域名风格，只能小写字母/数字/点/短横线，必须与安装目录同名 |
| `Hello.World.csproj` | `AssemblyName` | 一般就写 `你的ID`，注意要与 `plugin.json` 的 `entry.assembly` 一致 |
| `HelloPlugin.cs` | 命名空间与类名 | 要与 `plugin.json` 的 `entry.type` 完全一致（含命名空间） |

## 2. 编译

```powershell
.\build.ps1
```

产出 `dist\hello.world.lumenplugin`（本机实测：dll 27 KB + `.pri` 2 KB，包 12 KB）。

编译中间产物在 `bin\x64\<配置>\net10.0-windows10.0.19041.0\win-x64\`（带 `-p:Platform=x64` 会多一层 `x64`）。

两个 PowerShell 5.1 的坑，模板已经处理好，你改文件时别改回去：
- `build.ps1` 必须存成 **UTF-8 with BOM**，否则中文注释/提示会被读成乱码并导致语法错误；
- `Compress-Archive` / `Expand-Archive` 只认 `.zip`，所以打包时先压成 `.zip` 再改名，安装时先复制成 `.zip` 再解压。

如果报这三类错，按提示改：

| 报错 | 原因 | 修法 |
|---|---|---|
| `CS1705` | WinRT 投影版本太旧 | 保持 `WindowsSdkPackageVersion` = `10.0.26100.86` 或更高 |
| 运行时 `XamlParseException` | 插件没有自己的 `.pri` | 确认 `DisableEmbeddedXbf=false`，且 `plugin.json` 的 `resources.pri` 写对 |
| `InvalidCastException`（类型转换） | 插件把宿主程序集又自己带了一份 | `Private=false` + 打包时排除共享程序集（`build.ps1` 已处理） |

## 3. 安装

```powershell
$dir = "$env:LOCALAPPDATA\NovaLink Lumen\LauncherData\plugins\hello.world"
New-Item -ItemType Directory -Force -Path $dir | Out-Null
Copy-Item .\dist\hello.world.lumenplugin "$env:TEMP\hello.world.zip" -Force
Expand-Archive "$env:TEMP\hello.world.zip" -DestinationPath $dir -Force
```

重启启动器，左侧导航栏底部会出现「示例插件」。

## 4. 调试

- 插件抛异常只会**禁用自己**，不影响启动器；原因写在 `LauncherData\logs\plugins.log`
- 启动器连续两次在加载插件时崩溃，会自动进入安全模式并提示用户
- 开发时可以用 `--dev-mode` 启动启动器，能看到每个插件的校验结果与加载耗时
- `--safe-mode` 可以一次性禁用所有插件（排查"是不是插件的问题"）

## 5. 液态玻璃与主题适配（v1.5.1）

启动器的卡片、按钮都是同一套液态玻璃：半透明底 + 1px 描边 + 边缘折射 + 悬停高光。v1.5.1 把这套配方和主题色表开放给插件，**插件不用自己写任何颜色，也不用自己画高光**。

三个入口全在 `context` 上：

| API | 用途 |
|---|---|
| `context.Ui.CreateGlassCard(content, title)` | 让宿主造一张玻璃大卡片（标题行可选），返回 `object`，塞进 `ContentControl.Content` 即可 |
| `context.Ui.CreateGlassPanel(content)` | 造一块更淡的玻璃面板，卡片内分组用 |
| `context.Ui.CreateGlassButton(text, onClick)` | 造一个胶囊玻璃按钮；回调抛异常只记插件日志，不影响启动器 |
| `context.Ui.ApplyGlassCard(element)` | 把玻璃配方套到**你自己 XAML 写的** Border/Grid/控件上（推荐给整页元素用） |
| `context.Theme` | `IsLight` / `Name` / `Colors` / `Changed` 事件 |

推荐写法（模板里的 `MainPage` 就是完整例子）：

```csharp
// XAML 里写了 <Border x:Name="HeroCard"> ... </Border>
_ctx.Ui.ApplyGlassCard(HeroCard);                     // 原生元素套玻璃

GlassCardHost.Content = _ctx.Ui.CreateGlassCard(     // 宿主造卡片
    new TextBlock { Text = "内容由插件提供" }, "标题");

GlassButtonHost.Content = _ctx.Ui.CreateGlassButton("点我", () => { /* ... */ });
```

主题色表的键（`context.Theme.Colors`，值是 `#AARRGGBB` 字符串）：

| 键 | 用途 |
|---|---|
| `Accent` | 强调色（薄荷色，跟随用户主题） |
| `Text` / `TextSecondary` / `TextTertiary` | 正文 / 次要 / 更弱的文字 |
| `Surface` / `SurfaceRaised` | 页面底色 / 抬起的浮层底色 |
| `GlassCard` / `GlassBorder` | 玻璃卡片底色 / 玻璃描边 |
| `Field` / `FieldBorder` | 输入框底色 / 描边 |
| `FillSubtle` | 浅填充（按钮、标签） |
| `Danger` / `Warn` | 错误 / 警告 |

**四条必须遵守的规则**：

1. **别写死颜色**。用户切到浅色主题时，写死的 `#FFF3F7FE` 会直接看不见。要上色就取 `Theme.Colors`。
2. **切主题时重刷 + 退订**。页面每次导航都是新实例：`Loaded` 里挂 `Theme.Changed`、`Unloaded` 里摘掉，事件里重新取色（模板 `MainPage.xaml.cs` 的 `OnLoaded`/`OnUnloaded`）。
3. **玻璃工厂只能在 UI 线程调**（页面构造、`Loaded`、按钮事件）。后台线程调用会返回 `null` 并记一条日志。
4. **不要自己在玻璃上再画一条 1px 顶部高光**。宿主的渲染层按样式识别卡片并自己画折射与高光，重叠会出现双描边。

> 老版本兼容：这些 API 在 `IPluginUi`/`IPluginTheme` 上都有默认实现（返回 `null` / 固定深色色表），所以在 v1.5.0 上编译的插件在 v1.5.1 上照样能跑；反过来的只用新 API 的插件请在 `plugin.json` 里写 `"minLauncher": "1.5.1"`。

## 6. 能力边界（v1.5.1）

| 可以做 | 不可以做（v1.4.1） |
|---|---|
| 注入导航页面（自带 XAML + `.pri`） | 热卸载 / 不重启替换插件 |
| 读账户名、列实例、启动实例 | 直接读写 `lumen.json`（用 `IPluginSettings`） |
| 发 HTTP 请求（走宿主 HttpClient） | 带自己的原生 DLL 且与宿主同名（默认拒载） |
| 订阅启动/关闭/启动游戏事件 | 修改宿主已有页面 |
| 显示通知 | 常驻后台线程做重活（宿主会在关闭时等待） |
| 用宿主玻璃组件与主题色表（v1.5.1） | 自己给玻璃卡片再叠高光/描边（会出现双描边） |

## 7. 目录结构

```
docs/plugin-template/
├── plugin.json          清单（id/版本/入口/UI 声明）
├── Hello.World.csproj   必须遵守的三条 csproj 约束
├── HelloPlugin.cs       ILumenPlugin 入口
├── MainPage.xaml        插件自带页面
├── MainPage.xaml.cs
├── build.ps1            一键打包
├── store.json           商店元数据（可选，发布到商店用）
├── dist/                打包产物 hello.world.lumenplugin
└── README.md
```

## 8. 发布到插件商店

启动器「发现 → 启动器插件」会扫描带 topic 的 GitHub 公开仓库，具体规则见 [../STORE.md](../STORE.md)。四步上架：

1. **根目录放 `plugin.json`**：就是本目录这份，按第 1 节把 `id` / `name` / `version` / `author` / `description` 换成自己的。商店从这里读清单，缺字段或不合法的仓库会被静默跳过。
2. **仓库打 topic**：GitHub 仓库页 →「Topics」→ 添加 `novalink-lumen-plugin`（或 `gh repo edit --add-topic novalink-lumen-plugin`）。这是唯一的可扫描标识，不写 topic 搜不到。
3. **上传 Release 资产**：`.\build.ps1` 产出 `dist\<id>.lumenplugin`，把它传到一个 Release 并勾选 “Set as the latest release”。默认下载地址是 `https://github.com/{owner}/{repo}/releases/latest/download/<id>.lumenplugin`。
4. **（可选）加 `store.json`**：本目录已提供样例，字段含义：

   | 字段 | 说明 |
   |---|---|
   | `asset` | Release 里的安装包文件名；资产名和 `<id>.lumenplugin` 不一致时才需要写 |
   | `download` | 完整下载地址，**留空字符串**表示按 GitHub Release 的 latest 推导（样例里就是空的） |
   | `tags` | 商店里显示的标签，仅展示 |
   | `homepage` | 项目主页。样例里的 `https://github.com/Eric8844/novalink-lumen-plugin-template` 是模板占位地址，**请换成你自己的仓库地址** |

把下面两行贴到自己仓库 README 顶部即可（徽章纯展示，第二行说明打了 topic 就会出现在商店里）：

```markdown
![NovaLink Lumen 插件](https://img.shields.io/badge/NovaLink%20Lumen-plugin-5ee6b5)
本仓库已上架 NovaLink Lumen 插件商店（打了 topic `novalink-lumen-plugin` 即会出现，无需额外提交）
```

> 商店有缓存（搜索结果 30 分钟、仓库清单 6 小时）：改完 topic 或发了新 Release 后，重启启动器或等缓存过期再搜。搜不到先按 [../STORE.md](../STORE.md) 第六节排查清单逐个排除，最常见的原因是 topic 拼错或仓库根没有合法的 `plugin.json`。
