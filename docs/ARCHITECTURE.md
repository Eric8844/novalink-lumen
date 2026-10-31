# 架构

NovaLink Lumen 采用三层结构：

1. **Core**：与 UI 无关的模型、路径、版本 JSON、加载器识别、离线 UUID、崩溃解析。
2. **Services**：账户、网络、Java、Minecraft 实例、启动、安装、Modrinth、论坛、库、更新。
3. **WinUI 应用**：Shell、页面、对话框、Win2D 控件、主题与动效。

```text
NovaLink.Lumen.slnx
└─ src
   ├─ NovaLink.Lumen.Core
   ├─ NovaLink.Lumen.Services
   └─ NovaLink.Lumen
      ├─ Controls
      ├─ Styles
      └─ Views
         ├─ Dialogs
         └─ Pages
```

## Core

| 文件 | 职责 |
| --- | --- |
| `AppPaths.cs` | 启动器数据、缓存、日志、游戏目录与实例目录 |
| `Models/VersionJson.cs` | 版本 JSON、库、资源索引、参数 |
| `Models/InstanceModels.cs` | 实例、加载器、世界、图标 |
| `Models/AccountRecord.cs` | 离线 / Microsoft / NovaLink 账户记录 |
| `Models/AppSettings.cs` | 主题、玻璃参数、Java、内存、下载线程、背景与音乐 |
| `LoaderDetector.cs` | 从版本 ID / JSON 识别原版与加载器 |
| `OfflineUuid.cs` | 离线账户 UUID |
| `CrashAnalyzer.cs` | 从日志提取崩溃原因与建议 |

## Services

### 账户与网络

- `Accounts/AccountService.cs`：账户增删改、当前账户、绑定关系、令牌保护。
- `Microsoft/MicrosoftAuthService.cs`：设备码 / 浏览器登录、Xbox Live、Minecraft Services、皮肤与档案。
- `NovaLink/NovaLinkClient.cs`：NovaLink 登录、注册、资料、Microsoft 绑定、皮肤。
- `Http/HttpService.cs`：共享 `HttpClient`、超时、重试与 API 异常。
- `Storage/SecretProtector.cs`：Windows DPAPI 加密令牌。
- `Storage/SettingsService.cs`：设置读写与迁移。

### Minecraft

- `Minecraft/VersionManifestService.cs`：Mojang 版本清单。
- `Minecraft/VersionInstallService.cs`：原版下载、校验、资源索引、natives。
- `Minecraft/LoaderService.cs`：Fabric / Quilt / Forge / NeoForge 安装。
- `Minecraft/InstanceService.cs`：实例扫描、继承合并、重命名、图标、删除。
- `Minecraft/LaunchService.cs`：启动参数、Java 选择、进程生命周期、日志与崩溃。
- `Minecraft/ModpackService.cs`：`.mrpack` / CurseForge ZIP 导入。
- `Minecraft/LibraryService.cs`：模组、资源包、光影、世界的启用 / 禁用 / 删除。

### 内容与社区

- `Modrinth/ModrinthService.cs`：搜索、项目详情、版本、依赖、下载。
- `News/NewsService.cs`：Lumen 快讯。
- `Forum/ForumService.cs`：会话、话题、回复、动态、评论、个人主页。
- `Updates/UpdateService.cs`：GitHub Releases 检查与安装包下载。

### 其他

- `Java/JavaService.cs`：扫描 Java 运行时。
- `Java/JavaInstallerService.cs`：下载并解压 Temurin / Zulu 运行时。
- `Home/HeroArtService.cs`：首页 Hero 图。
- `Home/HeroTaglineService.cs`：按版本缓存首页标语；无 API Key 时使用本地文案。

## WinUI 应用

### Shell 与导航

`Views/ShellPage.xaml` 提供标题栏、搜索、账户菜单、侧边导航与全局进度入口。页面通过 `Frame` 切换：

- `HomePage`
- `VersionsPage`
- `DiscoverPage`
- `ForumPage`
- `LibraryPage`
- `SettingsPage`

### 视觉层

- `Controls/AuroraBackdrop.cs`：Win2D 极光、方块地面、星空。
- `Controls/GlassPanes.cs`：玻璃卡片、菲涅尔边缘、镜面高光、色散。
- `Controls/GlassSettings.cs`：玻璃参数与自适应质量。
- `Controls/Motion.cs`：页面进入、悬停、按压动效。
- `Controls/ProgressCenter.cs`：全局任务与进度快照。
- `Controls/Skeleton.cs`：加载骨架。
- `Styles/Theme.xaml`：颜色、字体、圆角、按钮与输入框。

### 对话框

`Views/Dialogs` 包含账户、版本安装、整合包、项目详情、启动进度、更新、背景编辑、保存位置等对话框。`GlassModal` 统一处理遮罩、卡片、全屏图片预览与关闭。

## 启动流程

```text
点击开始游戏
  → LaunchService 选择账户与 Java
  → 合并版本继承与加载器参数
  → 准备资源、natives、日志文件
  → 启动 java 进程
  → 解析 stdout/stderr 并推进阶段
  → 进入 Running / Exited / Crashed
```

## 数据流

```text
UI 事件
  → Page / Dialog
  → LumenRuntime 单例服务
  → Core 模型 / 文件系统 / HTTP
  → 事件或返回值回到 UI
```

UI 不直接拼接版本 JSON 或启动命令；这些逻辑集中在 Services，便于测试与替换。

## 线程与性能

- 网络、下载、安装、启动均在后台任务执行。
- UI 只通过 `DispatcherQueue` 更新。
- 玻璃与背景使用 Win2D 绘制，窗口失焦或最小化时降低帧率。
- 4K / 集成显卡下自动降低采样与效果强度。
