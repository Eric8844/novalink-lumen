# AGENTS.md · NovaLink Lumen（工作区项目约定）

本仓库是 **NovaLink Lumen V1.5.1** 的源码（正式版；测试版通道构建方式见 docs/PREVIEW.md）：C# / .NET 10 / WinUI 3 / Win2D 原生 Minecraft 启动器。
当前 HEAD `031a2f6`（2026-10-31），remote `https://github.com/Eric8844/novalink-lumen.git`。

## 先读记忆库，不要重新通读源码

项目记忆库在 **`D:\AI service\NovaLink-Lumen-Memory\`**（不在仓库内，避免污染公开仓）：

- `PROJECT-MEMORY.md` —— 总纲：架构、数据布局、构建发布、已知问题、任务 playbook。**动手前先读这一份。**
- `01-core-auth.md` 路径/模型/登录/HTTP/日志 · `02-minecraft-services.md` 启动/安装/加载器/整合包/Java · `03-winui-ui.md` 页面/对话框/控件/主题 · `04-build-infra.md` csproj/安装器/发版/更新器

按需检索：`grep -n "关键词" D:\AI service\NovaLink-Lumen-Memory\*.md`。

## 硬约束（违反会静默出事）

1. **只改 `D:\AI service\NovaLink-Lumen-Public`**。`NovaLink-Lumen-WinUI` 是无 .git 的开发快照，`NovaLink-Lumen-OpenSource` 是旧 Electron 1.3.0 仓，都不是当前代码。
2. **版本号没有单一真源：12 处代码硬编码 + 8 处文档字符串**。唯一入口是 `scripts/bump-version.ps1`，权威清单 = 它的 `$sites` 白名单（代码）+ `$docFiles`（文档整文件替换）；新增版本号位置必须同步加进去：
   `src/NovaLink.Lumen/NovaLink.Lumen.csproj` 的 `<Version>` / `<ApplicationVersion>`；`installer/installer.nsi` 的 `APP_VERSION` / `VIProductVersion` / `Data_Backup_v<版本>`；`Services/Updates/UpdateService.cs` 的 `CurrentVersion` 与资产名注释；`Services/Http/HttpService.cs` 的 UserAgent；`Services/Minecraft/LaunchService.cs` 的两处 `launcher_version`；`Views/ShellPage.xaml`、`MainWindow.xaml`、`Views/Pages/SettingsPage.xaml` 与 `SettingsPage.xaml.cs` 的界面文案；文档侧 README / AGENTS / docs\PUBLISHING.md 由脚本**整文件替换**（跳过 `-From/-To` 命令示例行）；CHANGELOG 是叙述性内容，人工维护。
   勿误改 csproj 里 Win2D 的 `Version="1.4.0"` 与 `Package.appxmanifest` 的 `1.0.0.0`。
3. **安装包资产名三套互斥**：NSIS 产出无版本号 `NovaLinkLumen-Setup.exe`；应用内更新器只认含 `WinUI` 或 `-Setup-\d+\.\d+\.\d+` 的名字（UpdateService.cs:52-55）；服务端只认 `packages/NovaLinkLumen-Setup-*.exe`。改名/发版前先看 04-build-infra.md §9。
4. 构建必须带 `-p:Platform=x64`（slnx 遗留 x86 平台与 csproj 冲突）。

## 常用命令

```powershell
dotnet build src/NovaLink.Lumen/NovaLink.Lumen.csproj -c Debug -p:Platform=x64
powershell -ExecutionPolicy Bypass -File scripts\build.ps1              # 归档闸门 + publish + makensis + SHA256
powershell -ExecutionPolicy Bypass -File scripts\bump-version.ps1 -To 1.4.2   # 按白名单统一版本号
powershell -ExecutionPolicy Bypass -File scripts\archive-release.ps1 -Version 1.4.0   # 冻结上一版产物到 archives/
```

makensis 本机在 `D:\AI service\desktop\.cache\nsis\nsis-3.0.4.1\Bin`，需手动加 PATH，或设置环境变量 `NOVALINK_NSIS` 指向 `makensis.exe`（`build.ps1` 会优先用它）。
`scripts/*.ps1`、`installer/installer.nsi` 含中文，**必须保存为 UTF-8 BOM + CRLF**，否则 Windows PowerShell 5.1 报 `ParserError: The string is missing the terminator`、makensis 报 `Bad text encoding`。

## 代码约定

- 分层：Core（零 NuGet，纯模型/常量）→ Services（业务 + DPAPI）→ WinUI 应用。Core 不得引 WinUI。
- **没有 MVVM / DI**：页面直接调 `LumenRuntime`（`LumenRuntime.cs` 手写静态服务定位器，`Initialize()` 顺序即依赖拓扑）。
- 导航唯一入口 `ShellPage.Navigate(tag)`，无回退栈，每次切页重建页面 → 静态事件必须在 `Unloaded` 解挂。
- 对话框是纯 C# 静态类 + `GlassModal.Show`（同时只允许一个）。
- 主题颜色键必须 `Styles/Theme.xaml` 与 `ThemeManager.ApplyResources()` 双写。
- WinUI 3 的 FileOpenPicker/FolderPicker/SaveFilePicker 必须 `InitializeWithWindow`。

## 已知问题（详见 PROJECT-MEMORY.md §7）

- v1.4.0 的自动更新永远判定「已是最新」：
  CurrentVersion 写死 1.4.0 + GitHub tag 只有 v1.4.0 + 资产名过滤（`UpdateService.cs:52-55` 只认含 `WinUI` 或 `-Setup-x.y.z` 的名字）不匹配 `NovaLinkLumen-Setup.exe`。
  v1.4.1 的处置是**双资产发布**：同时上传 `NovaLinkLumen-Setup.exe` 与 `NovaLinkLumen-WinUI-Setup-1.4.1.exe`，让存量 v1.4.0 客户端至少能收到一次更新。
  v1.5.0 起改**双通道 + 单资产**：产物名统一 `NovaLinkLumen-WinUI-Setup-<版本>.exe`，测试版发 `Eric8844/novalink-lumen-preview`（只放安装包、更新日志、说明），正式版发 `Eric8844/novalink-lumen-releases`。
- **通道常量必须两个工程都有**：`LauncherChannelInfo` 在 `NovaLink.Lumen.Services` 里用 `#if NOVALINK_PREVIEW` 判断通道，只给 App 工程加 `NovaLinkChannel` 会让更新器永远按正式版仓库检查（1.5.0 发布前踩过）。预览版序号比较取**入口程序集**的 `InformationalVersion`（`1.5.1-preview.N` 只写在 App csproj）。
- `LaunchService.cs:265` 正则丢反斜杠 → `InferJavaMajor` 恒返回 8。
- `Settings.JavaArgs`、`Width/Height/Fullscreen` 未接入启动参数。

## 改完怎么验证

- 能构建：`dotnet build ... -p:Platform=x64` 无错。
- 改了 UI/启动/安装：至少启动一次 `dist/release\NovaLink.Lumen.exe`，并看 `%LOCALAPPDATA%\NovaLink Lumen\LauncherData\logs\lumen-*.log`。
- 改了更新器/安装器：检查产物名、`%LOCALAPPDATA%\Programs\NovaLink Lumen` 安装结果与卸载项。
- 不要为了让测试通过而改断言语义；本项目没有自动化测试，`docs/PUBLISHING.md:72` 的手测清单是唯一质量门。
