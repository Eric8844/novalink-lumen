# 发布

发版的完整顺序见最后一节。**最容易漏掉的是第 2 步（归档上一版本）**：`dist/` 被 `.gitignore` 忽略，下一次 `dotnet publish` 会原地覆盖上一个版本的安装包，覆盖即永久丢失。

发版当次的逐项勾选清单见 [RELEASE-v1.4.1.md](RELEASE-v1.4.1.md)——**清单不等于已发布**，执行发布动作前请把本文件读完。

## 1. 版本号（唯一入口：`scripts/bump-version.ps1`）

版本号没有单一真源：**15 条代码位置 + 3 份整文件替换的文档**。不要手改，用脚本：

```powershell
# 先看会改哪些位置，不落盘
powershell -ExecutionPolicy Bypass -File scripts\bump-version.ps1 -From 1.4.1 -To 1.4.2 -DryRun

# 确认无误再落盘
powershell -ExecutionPolicy Bypass -File scripts\bump-version.ps1 -From 1.4.1 -To 1.4.2
```

权威清单 = 脚本里的代码 `$sites` 白名单 + 文档整文件替换清单（`$docFiles`）。代码位置逐条校验：任何一处文件内容与“期望的旧版本”对不上就立即中止，不会留下改了一半的仓库；文档（README / AGENTS / 本文件）按整文件替换，只跳过 powershell 命令示例里的 `-From/-To` 行。改完只打印 `git diff --stat`，**不自动 commit、不自动打 tag**。

白名单覆盖的位置：

| 位置 | 内容 |
|---|---|
| `src/NovaLink.Lumen/NovaLink.Lumen.csproj` | `<Version>`、`<ApplicationVersion>` |
| `installer/installer.nsi` | `APP_VERSION`、`VIProductVersion`、`Data_Backup_v<上一版>` |
| `Services/Updates/UpdateService.cs` | `CurrentVersion`、资产名注释示例 |
| `Services/Http/HttpService.cs` | `UserAgent` |
| `Services/Minecraft/LaunchService.cs` | 两处 `launcher_version` |
| `Views/ShellPage.xaml`、`MainWindow.xaml` | 侧栏与启动画面版本文案 |
| `Views/Pages/SettingsPage.xaml(.cs)` | 设置页版本与版本策略文案 |
| 文档 | `README.md`、`AGENTS.md`、本文件（整文件替换，`-From/-To` 示例行除外） |

`CHANGELOG.md` 是叙述性内容，**人工写**（脚本会提示遗留）。

勿误改：`csproj` 里 Win2D 的 `Version="1.4.0"`、`Package.appxmanifest` 的 `1.0.0.0`。

**已知白名单缺口**：`src/NovaLink.Lumen.Services/Storage/VersionSourceDetector.cs:23` 的 `TargetVersion`（以及同文件 `:26` 的 `FallbackSourceVersion`）不在 `$sites` 里，`bump-version.ps1` 不会改它——每次 bump 后必须手动同步，发版前用 `Select-String -Pattern '1\.4\.'` 扫一遍该文件。

## 2. 归档上一版本（构建之前）

### 2.1 打附注标签

每个已发布版本都有两个标签：

- 发布标签（例如轻量标签 `v1.4.0`）—— **已发布，永不移动、不删除、不重打**；
- 归档标签 `vX.Y.Z-archive`（附注标签）—— 承载产物 sha256 等元数据。

```powershell
git tag -a v1.4.0-archive 031a2f6 -m "v1.4.0 release archive"
git push origin v1.4.0-archive
```

### 2.2 冻结产物

```powershell
powershell -ExecutionPolicy Bypass -File scripts\archive-release.ps1 -Version 1.4.0 -CheckOnly  # 只校验
powershell -ExecutionPolicy Bypass -File scripts\archive-release.ps1 -Version 1.4.0            # 真归档
```

脚本做的事（顺序不能变）：

1. 版本号默认取 csproj 的 `<Version>`，定位 `archives/<版本>/`；
2. 校验版本标签存在，且指向的提交与 `-Commit`（默认 HEAD）一致；
3. 校验 `dist/NovaLinkLumen-Setup.exe` 的产品版本与版本一致（防止把 1.4.1 的包归档成 1.4.0）；
4. 幂等：`MANIFEST.json` 已存在且安装包 sha256 一致 → 跳过；不一致 → **退出码 2，绝不覆盖**；
5. 复制安装包 + `robocopy /MIR dist/release archives/<版本>/release`；
6. `git archive` 生成 `source-<版本>.tar.gz`；
7. 生成 `SHA256SUMS`（安装包、源码快照、`release/` 全量）；
8. **最后**写 `MANIFEST.json` —— 它存在即代表归档完成；
9. 可选 `-Mirror <目录>` 再镜像一份到外部盘/网盘。

> **1.5.0 起要先同步归档脚本**：`scripts/archive-release.ps1:84` 仍固定找 `dist/NovaLinkLumen-Setup.exe`（`:113` 的报错文案也写死这个名字），而新产物是 `dist/NovaLinkLumen-WinUI-Setup-<版本>.exe`。不改这两个地方，归档会直接失败。`scripts/build.ps1:82` 的产物校验同理。

### 2.3 什么进 git

```text
archives/
  README.md                       进 git
  v1.4.0/
    MANIFEST.json                 进 git（完成标记 + 全部校验值）
    SHA256SUMS                    进 git
    NovaLinkLumen-Setup.exe       不进 git（约 68 MB）
    source-v1.4.0.tar.gz          不进 git
    release/                      不进 git（发布目录全量快照）
```

规则与校验方法见 [archives/README.md](../archives/README.md)。

## 3. 构建自包含版本

```powershell
# 测试版（不写通道时默认 Preview）
powershell -ExecutionPolicy Bypass -File scripts\build.ps1

# 正式版：显式传通道（build.ps1 会把 -Channel 透传给 dotnet publish 与安装器）
powershell -ExecutionPolicy Bypass -File scripts\build.ps1 -Channel Stable

# 也可以手写 publish（通道参数必须带上）
dotnet publish src/NovaLink.Lumen/NovaLink.Lumen.csproj -c Release -r win-x64 --self-contained true `
  -p:Platform=x64 -p:NovaLinkChannel=Stable -o dist/release
makensis installer/installer.nsi
```

> **通道是编译期写死的**：`NovaLinkChannel` 不传时默认 `Preview`。这个属性必须在**两个工程**里都成立 —— `src/NovaLink.Lumen/NovaLink.Lumen.csproj`（UI/启动器版本号）与 `src/NovaLink.Lumen.Services/NovaLink.Lumen.Services.csproj`（`LauncherChannelInfo` 就住在 Services 里，用 `#if NOVALINK_PREVIEW` 判断通道）。只改 App 工程会让更新器永远按正式版仓库检查，测试版收不到自己的更新。正式发版**必须**显式传 `-p:NovaLinkChannel=Stable`。测试版流程见 6.1。

> **脚本同步状态（1.5.0 起）**：`scripts/build.ps1` 已支持 `-Channel Preview|Stable`、`-NsisPath`、`-SkipArchiveCheck`，并按 `dist\NovaLinkLumen-WinUI-Setup-<版本>.exe` 交叉校验产品版本、打印 SHA256；`scripts/archive-release.ps1` 已适配新产物名（并保留旧名回退，方便归档 1.4.0/1.4.1）；`scripts/bump-version.ps1` 白名单 17 条、共 32 处。`-NsisPath` 本机示例：`"D:\AI service\NovaLink-Lumen-WinUI\tools\nsis\Bin\makensis.exe"`（NSIS 3.13；3.0.4.1 编 250 MB 数据会报 `Internal compiler error #12345: error creating mmap`）。

脚本依次做：

0. **归档闸门**：`archives/` 下除当前版本外最新的目录若缺少 `MANIFEST.json` → 直接报错并提示先跑 `archive-release.ps1`（逃生阀 `-SkipArchiveCheck` 只给无 `archives/` 的 CI 用，本地发版不许加）；
1. `dotnet publish`（自带 `-p:Platform=x64`，slnx 遗留 x86 平台会和 csproj 打架）；
2. 找 makensis（`NOVALINK_NSIS` → PATH → `%ProgramFiles%\NSIS` → `tools/nsis/Bin`）；
3. 编译安装器，并核对产物产品版本与 csproj `<Version>` 一致；
4. 打印安装包路径、大小、SHA256。

单独执行 publish 时命令是：

```powershell
dotnet publish src/NovaLink.Lumen/NovaLink.Lumen.csproj -c Release -r win-x64 --self-contained true -p:Platform=x64 -o dist/release
```

确认 `dist/release` 至少包含：

- `NovaLink.Lumen.exe`
- `NovaLink.Lumen.pri`
- `Assets/AppIcon.ico`
- `Assets/Images/` 下的图标与 Hero 图
- Windows App SDK 与 .NET 运行时文件

## 4. 生成安装包

安装 NSIS 3.0.4+，然后：

```powershell
makensis installer/installer.nsi
```

产物：`dist/NovaLinkLumen-WinUI-Setup-<版本>.exe`（v1.5.0 起文件名带版本号，由 `installer/installer.nsi` 的 `OutFile` 决定，可用 `/DOUT=` 覆盖）。

### 4.1 本机 NSIS 3.0.4.1 生成不了安装包（2026-11-01 实测）

这台机器上的 NSIS 3.0.4.1（`D:\AI service\desktop\.cache\nsis\nsis-3.0.4.1\Bin\makensis.exe`）编译到写输出阶段会失败：

```text
Internal compiler error #12345: error creating mmap the size of 40378334.
Error - aborting creation process
```

用一个 1 MB 的最小脚本复现，报的是 `Can't open output file`；同目录用 PowerShell / .NET 写文件完全正常，
换成 NSIS 3.13 立刻成功 —— 属于这个 2019 年的 32 位旧版在新系统上的环境问题，不是脚本问题。

绕过办法（本机发版用这条）：

```powershell
# 1) 取 NSIS 3.13：chocolatey 的 nsis.portable 包里就是官方 zip
Invoke-WebRequest 'https://community.chocolatey.org/api/v2/package/nsis.portable' -OutFile nsis.nupkg -UseBasicParsing
Expand-Archive nsis.nupkg -DestinationPath nsis-pkg -Force
Expand-Archive nsis-pkg\tools\nsis-3.13.zip -DestinationPath nsis313 -Force

# 2) 指给 build.ps1（脚本里已有 NOVALINK_NSIS 候选，无需改代码）
$env:NOVALINK_NSIS = '<解压目录>\nsis-3.13\makensis.exe'
powershell -ExecutionPolicy Bypass -File scripts\build.ps1
```

`installer/installer.nsi` 的 `StrFunc.nsh` 用法已兼容两版（新版 `${Using:StrFunc} StrStr`，旧版 `!insertmacro FUNCTION_STRING_StrStr`）。
另外：**改 `installer.nsi` 之后必须确认它仍是 UTF-8 带 BOM**（`Unicode true` + 中文注释需要 BOM），
否则 makensis 会报 `Bad text encoding: installer/installer.nsi:1`。

安装器行为：

- 安装到 `%LOCALAPPDATA%\Programs\NovaLink Lumen`
- 写入当前用户卸载注册表项（含 `DataBackupDir`）
- 创建开始菜单启动 / 卸载快捷方式
- 安装前先结束正在运行的 `NovaLink.Lumen.exe`（否则 `File /r` 会因文件占用失败）
- 覆盖旧 Electron 稳定版与旧 WinUI 预览版，**但保留用户数据**
- 升级安装前把现有 `lumen.json` 与 `plugins/` 快照到 `Data_Backup_v<版本>\installer-snapshot\`（失败只记日志，不阻塞安装）
- 删除旧版本目录前做路径守卫：路径为空、等于数据根、或以 `LauncherData` / `\Data` / `Data_Backup` 结尾时**跳过删除**并写安装日志（`%TEMP%\NovaLinkLumen-install.log`）
- 卸载时问两次：第一问是否删除启动器数据（**默认保留**，文案里列出将被删的目录）；第二问是否同时删除数据备份（**默认保留**，建议保留以便回滚）
- 删除安装目录前断言它不等于数据根目录

三条不变量（写进 `installer/installer.nsi` 顶部注释）：

1. 卸载器只能删显式路径，永不出现 `RMDir /r "$LOCALAPPDATA\NovaLink Lumen"` 这类父目录通配；
2. 备份目录永远在 `LauncherData` 子树之外；
3. 升级安装不删任何用户数据。

发布前手动验证：

- 静默安装与普通安装都能完成
- 开始菜单出现“NovaLink Lumen”和“卸载 NovaLink Lumen”
- Windows 设置 → 应用 中能看到 NovaLink Lumen
- 卸载入口能启动卸载程序
- 卸载后用户数据仍存在（除非明确选择删除）
- 卸载第一问选“是”时 `LauncherData` 消失、但 `Data_Backup_v*` 仍在；第二问选“否”时备份仍在
- 旧版本升级后账户与实例仍在
- v1.4.0 正在运行时直接安装 v1.4.1 能完成，且 `Data_Backup_v1.4.0\installer-snapshot\lumen.json` 存在
- 首次启动 v1.4.1 后 `%LOCALAPPDATA%\NovaLink Lumen\Data_Backup_v1.4.0\BACKUP-INFO.json` 出现
- `NovaLink.Lumen.exe --restore-data-backup` 能生成 `LauncherData.rollback-<时间戳>` 并恢复数据

插件系统手动验证（v1.4.1 新增）：

> **v1.5.0 起插件默认关闭**：下面这些插件验证都要先在 **设置 → 实验性 →「启动器插件」** 打开并点【立即重启】（或手动重启）；不打开时应验的反而是「插件完全不加载」（见本节末尾 1.5.0 清单第 1 条）。临时也可用 `NovaLink.Lumen.exe --experimental-plugins` 启动一次。

- `NovaLink.Lumen.exe --safe-mode` 能启动，侧栏没有插件导航项，设置页「插件」显示安全模式横幅，点「重新启用」后重启恢复
- `NovaLink.Lumen.exe --no-plugins` 与设置 `NOVALINK_SAFE_MODE=1` 同样不加载插件；正常启动时不出现安全模式横幅
- 把 `docs/plugin-template/` 下编译好的 `hello.world` 插件复制到 `%LOCALAPPDATA%\NovaLink Lumen\LauncherData\plugins\hello.world\`，重启后侧栏出现插件导航项，点开能看到插件页面（不是“插件页面加载失败”）
- 把 `docs/plugin-template/script-hello/` 复制到同一插件目录，重启后设置页「插件」列表出现「脚本示例」；日志搜 `plugin:script-hello` 能看到初始化日志，且 `runCount` 每次启动 +1
- 脚本钩子生效：`onTextReplace`/`onNotification`/`onDownloadRequest` 至少各验证一次实际效果（改文本、屏蔽通知、改写下载地址）
- 故意写坏 `scripts/main.js`（例如 `throw new Error("boom")`），启动器仍能正常启动，设置页该插件显示加载失败原因，日志有 `脚本执行失败：`
- 卸载启动器并选择删除数据后，`Data_Backup_v*` 仍在；插件目录随 `LauncherData` 删除，但备份里的 `plugins/` 快照可找回
- 在设置页停用某插件后重启，导航项消失；重新启用后恢复
- 「安装插件包…」选一个 `.lumenplugin`：弹「插件已安装」，插件目录出现 `plugins/<id>`，重启后列表里出现该插件
- 对同一文件再点一次「安装插件包…」：弹「无法安装这个插件」并提示先删除旧目录（不覆盖、不报异常）
- 故意改坏包内 `plugin.json`（删字段/坏 JSON）再装：给出中文原因，插件目录里不留半成品，启动器不崩
- 发现页「启动器插件」商店：断网或 GitHub 不可达时显示空态与中文提示（不崩、不卡死），有缓存时先显示缓存内容
- 在商店里安装一个插件（例如模板仓库）：提示安装成功，`plugins/<id>` 出现，重启后插件生效并在列表里可见
- `--dev-mode` 启动后，设置 → 插件 → 开发者工具点「选择源码目录并打包…」选 `docs/plugin-template`：约 10 秒内显示「已打包并安装」，`docs/plugin-template/dist/hello.world.lumenplugin` 生成
- 插件正在运行时再打包一次同名插件：不弹异常，提示「已打包，重启后替换」；点【立即重启】后新进程日志出现「旧版本插件目录已挪进回收站」与「已应用排队中的插件更新」，插件页面正常打开，`plugin-updates` 清空、`plugin-trash` 为空
- 插件越权读取被拒（信任边界）：先打开 `<数据根>\lumen.json`，确认 `accessToken` / `refreshToken` / `novalinkToken` / `forumToken` 四个字段都是 `enc:` 开头的密文、没有明文令牌；再深度验证——临时给 `docs/plugin-template/Hello.World.csproj` 加一条对 `src\NovaLink.Lumen.Services\NovaLink.Lumen.Services.csproj` 的 `ProjectReference`（同样 `Private=false`），在 `HelloPlugin.Initialize` 末尾加 try/catch 调 `new SecretProtector().Unprotect("x")`，打包安装后重启；日志应出现「信任边界生效：插件不能读取启动器保存的账户凭据…」，**不应**出现「守卫失效」。验证完撤掉这两处临时改动

页面加载与流畅度手动验证（v1.4.1 新增）：

- 进入「库」（116 个模组的实例）：0.3 秒内出现转圈 + 「正在扫描模组...」+ 骨架条，列表分批出现；加载期间拖动窗口、切换左侧导航、看背景方块地面都仍然在动
- 依次进「版本实例 / 首页 / 发现（内容与启动器插件两个分段）/ 论坛 / 库」，每页都有对应的中文加载提示，加载完成后自动收起，不残留遮挡层
- 在「库」里切换 模组 / 世界 / 资源包 / 光影 四个 tab：不再整屏卡住，切换后列表重新分批出现
- 快速连点两个页面（例如点了「库」马上点「发现」）：旧页面的渲染被丢弃，不会把卡片加进错误的页面，也不抛异常

快讯与音乐台手动验证（v1.4.1 新增）：

- 首页快讯：点开一条**没有 `readMoreLink`** 的条目（官方更新日志条目只有 `contentPath`），「在浏览器打开」按钮应**完全隐藏**（不是置灰）；正文上方先出现「正在读取正文…」，读到后整段替换为官方 JSON 的 `body`（纯文本、可滚动、可选中复制），状态行收起；不弹任何错误框
- 首页快讯：断网点开任意一条无链接条目（或正文 404）：正文读取失败后回落到摘要，状态行变成「这条快讯暂时只有摘要，稍后再试或到官网查看。」，仍然不弹错误框；代码层面相对路径 / `ms-appx:` 等非 `http(s)` 链接同样按“没有链接”处理（`NewsDialog.ResolveHttpLink`）
- 首页快讯：点开一条**带合法链接**的条目，点「在浏览器打开」能唤起默认浏览器并跳到原网页，成功后弹窗关闭；若系统没有默认浏览器导致唤起失败，弹窗内出现一行可选中复制的「没能唤起浏览器，可以手动复制链接：…」，不叠第二个对话框
- 音乐台：鼠标移到右下角音乐面板上，面板放大到 1.05 倍（约 180ms）并在控制条上方展开详情（进度条、当前时间 / 总时长、`第 N / M 首 · 播放方式 · 音量 X%`），控制按钮位置不动；等 3 秒确认进度条在走、时间在变（约 450ms 刷新一次）；移开鼠标后恢复原尺寸且播放不中断
- 音乐台：暂停播放后悬停，进度条停在当前位置不继续前进；切到下一首后「第几首共几首」与总时长同步变化；把曲目清空后再次显示，详情区回到收起态

上次启动与游玩记录（v1.4.1 修复）：

- 先记下版本页某实例的「最后启动」值（新装用户是「尚未启动」）；点「开始游戏」等游戏真的进到主菜单，再退出游戏
- 版本页该实例「最后启动」变成刚才的时间、列表右侧变成「刚刚」，详情卡与首页「上次游玩」同步更新；实例列表按最近启动排序时该实例排到最前
- `<数据根>\lumen.json` 的 `instanceMeta.<实例 id>` 出现 `lastPlayed`（毫秒时间戳）与随游玩时长增长的 `playtimeMs`；游戏退出后当前页面自动刷新一次
- 反例：把 Java 路径指到一个不存在的位置再点「开始游戏」，启动失败时 `lastPlayed` **不应**被改写（只有进程成功起来才算一次游玩）
1.5.0 手动验证（实验性插件开关 / 音乐台 / 快讯）：

- 插件实验性开关默认关闭：没有打开过开关时，**设置 → 实验性 →「启动器插件」为关**；侧栏没有插件导航项、「发现」页没有「启动器插件」商店分段、设置左侧分类里没有「插件」；日志只有一行 `[INFO] [plugins] 实验性功能未开启：本次不加载任何插件。`；已存在的 `plugins\<id>` 目录与插件数据（如脚本插件的 `settings.json`）仍在、未被删除
- 打开开关并重启：插件导航项与「发现 → 启动器插件」恢复，设置页「插件」列表能看到已安装插件；再关闭并重启，入口消失但插件目录与数据仍然保留（重新打开即恢复）
- 命令行优先：实验性开关打开时，`NovaLink.Lumen.exe --safe-mode`、`--no-plugins` 或 `NOVALINK_SAFE_MODE=1` 仍然不加载任何插件，设置页显示安全模式横幅
- 音乐台拖动进度：悬停展开详情后拖动进度滑块（`MusicProgress`），播放位置实时跟随（时间文本同步变化）、松手后继续播放；暂停状态下拖动也能定位，恢复播放从新位置开始；拖动期间不会被 450ms 自动刷新拉回去
- 音乐台音量：拖动音量滑块时声音大小实时变化、右侧百分比同步；鼠标移开收起详情时把值写回 `<数据根>\lumen.json` 的 `MusicVolume`，重启启动器后音量保持
- 音乐台浮层按钮可点：详情展开状态下点【上一首】/【播放暂停】/【下一首】都有反应，进度与音量滑块也能拖动（修复前详情区整块不可命中，出现“悬停后点不动”）；点音乐台空白处也能展开/收起详情（悬停不一定触发时用这个）
- 音乐台滑块外观：两条滑块都有可见的灰色轨道 + 薄荷色圆点滑块，已播放部分为薄荷色（不是系统默认的细蓝条）；音量百分比与 `lumen.json` 的 `MusicVolume` 一致、不为 0（曾经保存成 0）
- 音乐台收起：再次点击音乐台时，外框高度与内容透明度一起过渡，不应出现「进度条先消失、外框停一下再收起」的两段式
- 卡片点击与悬停：在首页/版本/发现卡片上来回悬停并点击，不应出现缩放抖一下、位置跳一下的抽搐（进入只做一次上浮 + 放大）
- 快讯图片：点开一条正文含图片的快讯，图片按原始比例完整显示（最大高度 240，不裁切、不拉伸变形）；点击图片打开全屏预览（黑底铺满窗口），点【关闭预览】/点空白处/按 Esc 都能关闭；预览里只有这一张图，没有左右切换；图片地址坏掉时预览显示「该图片无法显示」

浅色与液态玻璃手动验证（v1.5.0 新增）：

- 设置 → 外观与性能 → 主题切「浅色」后逐页浏览：首页 hero 上的文字仍是浅色（压在照片上）、四张主卡有描边与顶部高光、分类 chip 与下拉框可读；**账户下拉菜单（右上角头像）文字为深色、能看清**（修复前是白底白字）
- 浅色下点开任意一个模组详情：正文与「English original」为深色可读；简介翻译超过 4 秒时出现「暂时没有中文简介，请直接看下方英文原文。」而不是一直卡在「正在翻译简介…」
- 浅色下打开插件商店：「启动器插件」的卡片不再是纯白平块，有边界与一点玻璃质感；主按钮为强调色深字
- 任意弹窗（模组详情/快讯/插件文档/删除确认）边缘与正文区都有玻璃层与 1px 顶高光；浅色下正文不透明到能读、深色下仍能看到背景方格透出
- 主题切回深色后重复上面几项：对比度、玻璃、高光都正常（浅色改动不能把深色改坏）
- 浅色下打开「选择版本」弹窗（发现 → 任意模组「简介」→ 版本行）：折叠标题、版本行、类型标签（游戏 / Fabric）、选中行与底部「点击任意版本本应用…」都能读清（修复前是灰底灰字）
- 浅色下点开论坛任意话题详情：「返回话题」是浅底细边按钮而不是灰块，话题卡只有一层边框，回复区淡玻璃；深色下同样能读
- 各页内容区右侧滚动时不压住文字：首页/版本/发现/论坛/库/设置滚到最底，最后一列文字与滚动条之间有留白

插件删除与实时刷新手动验证（v1.5.0 新增）：

- 设置 → 插件 → 某个插件行右侧【删除】：弹二次确认「确定要删除「X」吗？插件文件会被移除，它保存过的数据会保留在插件数据目录里。」；确认后该行消失、左侧导航对应的插件项同步消失、提示「已删除「X」，插件文件和列表项都已移除。」
- 插件正在运行时删除：日志出现「旧版本插件目录已挪进回收站」，目录被整体改名进 `plugin-trash`（**不会**出现只剩 dll、plugin.json 已被删的坏目录）；下次启动 plugin-trash 与 plugin-deletes 都被清理，且 `plugin-data\<id>` 仍在
- 在资源管理器里往 `%LOCALAPPDATA%\NovaLink Lumen\LauncherData\plugins` 新建一个合法插件目录：几秒内设置页插件列表自动多出一行、左侧导航出现新入口，提示「插件目录有变化，列表已自动更新。」（不需要重启）
- 在资源管理器里删掉一个插件目录：几秒内该行消失、导航项消失；插件在后台写自己的缓存/设置文件时**不应**触发刷新（日志里没有「插件目录已变化」）
- 库页：往当前实例的 mods 目录丢入一个 jar，约 1 秒内列表自动多出一行（不需点刷新）；删掉则消失


## 5. 计算校验值

```powershell
Get-FileHash dist/NovaLinkLumen-WinUI-Setup-<版本>.exe -Algorithm SHA256
```

把 SHA256 写入 Release 说明。

## 6. GitHub Release（双资产，硬要求）

> **v1.5.0 起：产物文件名已自带 `WinUI` 与 `-Setup-<版本>`，只需上传一份 `NovaLinkLumen-WinUI-Setup-<版本>.exe`。** 下面的“上传两个资产”是为仍在使用 v1.4.x 的老客户端保留的历史流程；测试版见 6.1。

1. 创建与 `<Version>` 一致的 tag，例如 `v1.4.1`。
2. 上传**两个**资产：
   - `NovaLinkLumen-Setup.exe`（NSIS 原始产物，文件名不带版本号）
   - `NovaLinkLumen-WinUI-Setup-1.4.1.exe`（把同一个文件 `Copy-Item` 改名，内容一致）

   ```powershell
   Copy-Item dist/NovaLinkLumen-Setup.exe dist/NovaLinkLumen-WinUI-Setup-1.4.1.exe
   ```
3. Release 说明包含：更新内容、系统要求、SHA256（两个资产相同）、已知问题。
4. 确认 `releases/latest/download/NovaLinkLumen-Setup.exe` 与 `releases/latest/download/NovaLinkLumen-WinUI-Setup-1.4.1.exe` 都能 302 到最新资产。

**为什么必须双资产**：应用内更新器 `UpdateService.cs:52-55` 只接受文件名含 `WinUI` 或匹配 `-Setup-\d+\.\d+\.\d+` 的 exe，而 NSIS 产物叫 `NovaLinkLumen-Setup.exe`，两者匹配不上 —— 这是 v1.4.0 自动更新永远判定“已是最新”的根因之一。只传一个资产，存量 v1.4.0 客户端就永远收不到 v1.4.1。代价是一次 release 约 136 MB（两个 68 MB 资产）。

单次发布空间吃紧时，先保证 `NovaLinkLumen-WinUI-Setup-<版本>.exe` 上传成功，它是让老客户端能升级的唯一入口。

### 6.1 测试版（Preview）发布流程

测试版和正式版是同一套代码的两个通道，**通道在编译期就写死**，运行期无法切换或伪造：

- 构建测试版：`powershell -ExecutionPolicy Bypass -File scripts\build.ps1` 即可（未指定通道时默认 `Preview`）；要显式写出通道就用 `dotnet publish ... -p:NovaLinkChannel=Preview`。
- 构建正式版：**必须**显式指定通道：`scripts\build.ps1 -Channel Stable`，或手写 `dotnet publish ... -p:NovaLinkChannel=Stable`。默认（不传）编出来的是测试版。
- 产物：`dist\NovaLinkLumen-WinUI-Setup-<版本>.exe`，例如 `NovaLinkLumen-WinUI-Setup-1.5.0.exe`。两个通道文件名规则相同，靠发布到哪个仓库区分。
- 发布仓库：`Eric8844/novalink-lumen-preview`。该仓库**只放安装包、更新日志与说明，不推送源码**。
- tag：`v1.5.0-preview.1`，后续递增 `-preview.2`、`-preview.3`；正式版是纯数字 `v1.5.0`。
- 资产：只上传一份 `NovaLinkLumen-WinUI-Setup-1.5.0.exe` 即可（新命名天然含 `WinUI` 与 `-Setup-1.5.0`，能通过更新器的资产过滤），不再需要 v1.4.x 时代的双份改名。
- Release 说明写清：本版包含什么、已知问题、SHA256。
- **不要勾「Set as a pre-release」**：更新器用的是 `releases/latest` 接口（`src/NovaLink.Lumen.Services/Updates/UpdateService.cs:30-31`），GitHub 的 latest 不返回标记为 pre-release 的 Release。测试版与正式版靠**仓库 + tag** 区分，不需要 pre-release 标记。
- 客户端行为以 `src/NovaLink.Lumen.Services/Updates/LauncherChannel.cs`（通道常量与仓库地址）与 `UpdateService`（标签过滤：测试版只接受 `-preview` / `-beta` / `-rc`，正式版只接受纯数字）为准；跨通道的 Release 会被直接忽略并写一条 `[updates] 忽略跨通道更新：…` 日志。
- 预览版序号比较用**入口程序集**的 `InformationalVersion`（`Assembly.GetEntryAssembly()`）：App 工程的 `<InformationalVersion>1.5.0-preview.N` 才是真源，Services 程序集不带这个后缀。

**注意**：产物的 SHA256 会写进 Release 说明，重建一次安装包就要同步改一次；`releases/latest/download/<资产名>` 是客户端更新器的唯一下载入口，重传资产后要 HEAD 一次确认字节数一致。

## 7. 官网与更新接口

如果使用独立更新接口，返回：

```json
{
  "version": "NovaLinkLumen V1.4.1_Desktop",
  "download_url": "https://github.com/<owner>/<repo>/releases/latest/download/NovaLinkLumen-WinUI-Setup-1.4.1.exe",
  "filename": "NovaLinkLumen-WinUI-Setup-1.4.1.exe"
}
```

不要把管理员密码、API Key、SMTP 授权码或服务器凭据写入仓库或 Release 说明。

## 8. 回滚

保留上一个版本的安装包与 Release。若新版本出现阻断问题：

- 将 `latest` 指回上一个 Release；
- 在 Release 说明中标注问题与回滚方式；
- 不要删除用户数据目录。

用户侧回滚数据：

```powershell
# 把当前数据改名成 LauncherData.rollback-<时间戳>，再从 Data_Backup_v<源版本> 恢复
NovaLink.Lumen.exe --restore-data-backup
```

## 9. 一次完整发版的顺序

```powershell
# 0) 分支干净、CI 绿
git status

# 1) 冻结上一版：附注标签 + 归档（dist/ 里还是上一版产物时做）
git tag -a v1.4.0-archive 031a2f6 -m "v1.4.0 release archive"
git push origin v1.4.0-archive
powershell -ExecutionPolicy Bypass -File scripts\archive-release.ps1 -Version 1.4.0

# 2) 统一版本号
powershell -ExecutionPolicy Bypass -File scripts\bump-version.ps1 -From 1.4.1 -To 1.4.2
#    手工补 CHANGELOG.md，git diff 复核

# 3) 构建（闸门会自动确认第 1 步做过）
#    测试版不带参数（默认 Preview）；正式版必须显式 Stable，build.ps1 目前不透传，见第 3 节
powershell -ExecutionPolicy Bypass -File scripts\build.ps1

# 4) 手测（见第 4 节清单），算 SHA256

# 5) 打发布标签、建 Release，上传单份资产 dist/NovaLinkLumen-WinUI-Setup-<版本>.exe
#    正式版 tag 用纯数字 vX.Y.Z；测试版走 6.1（tag vX.Y.Z-preview.N、独立仓库、不勾 pre-release）
git tag -a v1.4.2 -m "NovaLink Lumen v1.4.2"
git push origin v1.4.2

# 6) 下一个版本开工前，给这一版补归档标签并归档（回到第 1 步）
```
