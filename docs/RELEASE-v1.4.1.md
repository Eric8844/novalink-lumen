# v1.4.1 发版清单

> **本文档是清单，不代表已经发布。** 里面的勾选框只有真正执行过才能打勾；发布动作（打 tag、上传 GitHub Release、改官网接口）不在本文档范围内，执行前请完整阅读 [PUBLISHING.md](PUBLISHING.md)。

版本：**1.4.1**（应用内显示 `1.4.1`，程序集 `1.4.1.0`）

---

## 1. 版本号一致性检查

### 1.1 唯一入口

版本号没有单一真源，全部位置由 `scripts/bump-version.ps1` 的 `$sites` 白名单 + `$docFiles` 文档清单统一改写（见 [PUBLISHING.md 第 1 节](PUBLISHING.md)）。**不要手改**。

`$sites` 里的 15 条代码位置（脚本会自动逐条校验“期望几处、实际几处”）：

| 文件 | 改什么 |
|---|---|
| `src/NovaLink.Lumen/NovaLink.Lumen.csproj` | `<ApplicationVersion>`、`<Version>` |
| `installer/installer.nsi` | `APP_VERSION`、`VIProductVersion`、`Data_Backup_v<上一版>`、文件头 `/DAPP_VERSION=` 示例 |
| `src/NovaLink.Lumen.Services/Updates/UpdateService.cs` | `CurrentVersion`、双资产名注释示例 |
| `src/NovaLink.Lumen.Services/Http/HttpService.cs` | `UserAgent` |
| `src/NovaLink.Lumen.Services/Minecraft/LaunchService.cs` | 两处 `launcher_version` |
| `src/NovaLink.Lumen/Views/ShellPage.xaml` | 侧栏版本号 |
| `src/NovaLink.Lumen/MainWindow.xaml` | 启动画面版本号 |
| `src/NovaLink.Lumen/Views/Pages/SettingsPage.xaml.cs` | 设置页版本文本 |
| `src/NovaLink.Lumen/Views/Pages/SettingsPage.xaml` | 版本策略说明里的 `Data_Backup_v<上一版>` |

`$docFiles`：`README.md`、`AGENTS.md`、`docs/PUBLISHING.md`（整文件替换，跳过 `-From/-To` 示例行）。

> 白名单缺口：`src/NovaLink.Lumen.Services/Storage/VersionSourceDetector.cs:23` 的 `TargetVersion`（及 `:26` 的 `FallbackSourceVersion`）**不在 `$sites` 里**，脚本不会改它。每次 bump 后手动同步，下面的自查命令能扫到。

### 1.2 自查命令

```powershell
cd "D:\AI service\NovaLink-Lumen-Public"

# 1) 版本号主线：上面这些文件里应只剩 1.4.1
Select-String -Path src/NovaLink.Lumen/NovaLink.Lumen.csproj,installer/installer.nsi,`
  src/NovaLink.Lumen.Services/Updates/UpdateService.cs,`
  src/NovaLink.Lumen.Services/Http/HttpService.cs,`
  src/NovaLink.Lumen.Services/Minecraft/LaunchService.cs,`
  src/NovaLink.Lumen/Views/ShellPage.xaml,src/NovaLink.Lumen/MainWindow.xaml,`
  src/NovaLink.Lumen/Views/Pages/SettingsPage.xaml,`
  src/NovaLink.Lumen/Views/Pages/SettingsPage.xaml.cs,`
  src/NovaLink.Lumen.Services/Storage/VersionSourceDetector.cs -Pattern '1\.4\.[0-9]+'

# 2) 排查残留的 1.4.0：只允许这几类合法旧值
#    - NovaLink.Lumen.csproj 里 Win2D 的 Version="1.4.0"
#    - installer.nsi 的 Data_Backup_v1.4.0（备份目录名，记的是上一版）
#    - VersionSourceDetector.cs 的 1.4.0（源版本推断与注释）
```

构建完成后核对产物版本（**旧 `dist/` 长时间不重建会残留上一版，必须重新跑第 2 节的构建**）：

```powershell
$expect = ([xml](Get-Content src\NovaLink.Lumen\NovaLink.Lumen.csproj)).Project.PropertyGroup.Version | Select-Object -First 1
(Get-Item dist\NovaLinkLumen-Setup.exe).VersionInfo.ProductVersion   # 应等于 $expect（如 1.4.1）
(Get-Item dist\release\NovaLink.Lumen.exe).VersionInfo.FileVersion   # 应是 $expect.0（如 1.4.1.0）
```

### 1.3 CHANGELOG

`CHANGELOG.md` 顶部有完整的 `## 1.4.1` 段（人工写，`bump-version.ps1` 不碰）：备份归档机制、`Data_Backup_v<源版本>`、`--restore-data-backup`、全类型插件系统（A 可用 / B 可用 JS only / C 默认拒载）、安全模式与看门狗、`--dev-mode`、设置页插件卡片、插件商店、版本号统一、双资产发布、快讯详情按钮修复、音乐台悬停详情。发版说明直接从这一节改写。

## 2. 构建（含归档闸门）

```powershell
# 先把上一版（1.4.0）冻结：附注标签 + archives/1.4.0/（否则 build.ps1 会拒绝继续）
powershell -ExecutionPolicy Bypass -File scripts\archive-release.ps1 -Version 1.4.0 -CheckOnly
powershell -ExecutionPolicy Bypass -File scripts\archive-release.ps1 -Version 1.4.0

# 再构建自包含版本 + 安装包（内部已带 -p:Platform=x64）
powershell -ExecutionPolicy Bypass -File scripts\build.ps1
```

- [ ] `archives/1.4.0/MANIFEST.json` 存在（归档完成标记）
- [ ] `scripts/build.ps1` 无报错，末尾打印安装包路径、大小、SHA256
- [ ] `dist/release/` 至少包含 `NovaLink.Lumen.exe`、`NovaLink.Lumen.pri`、`Assets/AppIcon.ico`、`Assets/Images/`
- [ ] 第 1.2 节的产物版本检查通过（安装包 `ProductVersion` = 1.4.1，`NovaLink.Lumen.exe` `FileVersion` = 1.4.1.0）

## 3. 双资产（硬要求，不能只传一个）

```powershell
Copy-Item dist/NovaLinkLumen-Setup.exe dist/NovaLinkLumen-WinUI-Setup-1.4.1.exe
```

发布时必须同时上传：

| 资产名 | 用途 |
|---|---|
| `NovaLinkLumen-Setup.exe` | NSIS 原始产物，官网 / 手动下载 |
| `NovaLinkLumen-WinUI-Setup-1.4.1.exe` | **存量 v1.4.0 客户端自动更新的唯一入口** |

原因：应用内更新器 `UpdateService.cs:52-55` 只接受文件名含 `WinUI` 或匹配 `-Setup-\d+\.\d+\.\d+` 的 exe，而 NSIS 产物名 `NovaLinkLumen-Setup.exe` 两者都不匹配——只传一个资产，老客户端会永远判定“已是最新”。两个文件内容相同，SHA256 也应相同。

## 4. GitHub Release 步骤

- [ ] 创建与 `<Version>` 一致的 tag：`git tag v1.4.1`（**发布标签，永不移动、不删除、不重打**）
- [ ] 上传两个资产（见第 3 节），确认都上传成功（单次 release 约 136 MB）
- [ ] Release 说明包含：更新内容（取自 `CHANGELOG.md` 的 1.4.1 段）、系统要求（Windows 10/11 x64）、两个资产的 SHA256、已知问题（见第 8 节）
- [ ] 确认这两个地址都能 302 到最新资产：
  - `https://github.com/<owner>/<repo>/releases/latest/download/NovaLinkLumen-Setup.exe`
  - `https://github.com/<owner>/<repo>/releases/latest/download/NovaLinkLumen-WinUI-Setup-1.4.1.exe`
- [ ] 若走独立更新接口（`releases/latest` 不可用时），返回 `version` / `download_url` / `filename`，见 [PUBLISHING.md 第 7 节](PUBLISHING.md)

## 5. 数据备份（升级前自动生成）

首次启动 v1.4.1 时，`UpgradeGuard` 会在读取 `lumen.json` **之前**把现有数据原子快照到：

```text
%LOCALAPPDATA%\NovaLink Lumen\Data_Backup_v<源版本>\
```

- 源版本按 `lumen.json` 的 `lastSeenVersion` 推断；纯 v1.4.0 用户（字段为空）按 **1.4.0** 处理，所以本次是 `Data_Backup_v1.4.0`；
- 写入 `BACKUP-INFO.json` 作为完成标记；
- 备份目录固定在 `LauncherData` **之外**（同级的 `NovaLink Lumen\Data_Backup_v*`），卸载删数据不会连带删备份；
- 安装器在覆盖程序文件前还会做一份 `installer-snapshot` 到同一备份目录（失败只记日志，不阻塞安装）。

- [ ] 首次启动 v1.4.1 后 `Data_Backup_v1.4.0\BACKUP-INFO.json` 出现
- [ ] `Data_Backup_v1.4.0\lumen.json` 可打开，账户与实例数量正确
- [ ] `NovaLink.Lumen.exe --restore-data-backup` 先把当前数据改名成 `LauncherData.rollback-<时间戳>`，再恢复备份；两个目录都在

## 6. 卸载不删备份

卸载器问两次，**默认都是保留**：

1. 第一问：是否删除启动器数据（`LauncherData`）→ 默认否；
2. 第二问：是否同时删除数据备份（`Data_Backup_v*`）→ 默认否，建议保留以便回滚。

删除安装目录前还有路径守卫：路径为空、等于数据根、或以 `LauncherData` / `\Data` / `Data_Backup` 结尾时跳过删除并写安装日志（`%TEMP%\NovaLinkLumen-install.log`）。

- [ ] 第一问选“是”：`LauncherData` 消失，`Data_Backup_v*` 仍在
- [ ] 第二问选“否”：备份仍在（可再次用 `--restore-data-backup` 恢复）
- [ ] 安装目录被删除，且 `%LOCALAPPDATA%\NovaLink Lumen` 本身没有被 `RMDir /r` 清空

## 7. 插件商店兼容性：`eric8844.serverstatus`

已上架的示例插件（Minecraft 服务器状态查询，见 [STORE.md](STORE.md) 末尾）是本次发版的兼容性基准：

- `plugin.json`：`id` = `eric8844.serverstatus`，`apiVersion` = `1`，`minLauncher` = `1.4.1`；
- `store.json`：`asset` = `eric8844.serverstatus.lumenplugin`，`download` 留空（走 Release 默认地址）；
- 仓库打了 topic `novalink-lumen-plugin`，Release `v1.0.0` 资产名与 `store.json` 一致。

商店里的兼容性判定在 `PluginStoreClient.CheckCompatibility`（`src/NovaLink.Lumen.Services/Plugins/Store/PluginStoreClient.cs:351-366`）：`apiVersion` 必须等于 `1`；`minLauncher` 高于当前版本、或 `maxLauncher` 低于当前版本，都会**照常显示但标记不可安装并给出中文原因**。

- [ ] 「发现 → 启动器插件」搜「服务器状态」，条目显示为可安装（不是“需要启动器 X 或更高版本”）
- [ ] 安装后 `%LOCALAPPDATA%\NovaLink Lumen\LauncherData\plugins\eric8844.serverstatus\plugin.json` 存在
- [ ] 重启后左侧导航出现「服务器状态」，点开是插件页面而不是加载失败
- [ ] 卸载该插件（设置 → 插件）后重启，导航项消失、启动器正常

## 8. 发版后冒烟（全新机器 + 覆盖升级各一遍）

- [ ] 全新安装 v1.4.1：能启动、能登录、版本显示 1.4.1
- [ ] 存量 v1.4.0 覆盖安装：账户、实例、设置都在；`Data_Backup_v1.4.0` 生成
- [ ] 应用内「检查更新」能识别到 v1.4.1（验证双资产真的生效）
- [ ] `--safe-mode` 能启动，侧栏无插件导航项，设置页出现安全模式提示
- [ ] 首页快讯：无 `readMoreLink` 的条目**不显示**「在浏览器打开」，站内正文能从官方 `contentPath` 读出全文；带合法 `http(s)` 链接的条目按钮能唤起浏览器
- [ ] 音乐台：悬停放大到 1.05 倍并展开进度条与「第 N/M 首 · 播放方式 · 音量」，移开恢复，进度在走；暂停后不前进
- [ ] 插件模板（`docs/plugin-template` 编译型 / `script-hello` 脚本型）安装后导航与钩子生效
- [ ] 插件越权读取被拒（见 [PUBLISHING.md](PUBLISHING.md) 的插件手测条目：`lumen.json` 只有 `enc:` 密文，解密 API 抛 `UnauthorizedAccessException`）
- [ ] 卸载后 `Data_Backup_v*` 仍在，`--restore-data-backup` 可回滚

## 9. 回滚

- 线上阻断：把 `latest` 指回上一个 Release，说明问题与回滚方式，**不要删除用户数据目录**；
- 用户侧：`NovaLink.Lumen.exe --restore-data-backup`（当前数据改名为 `LauncherData.rollback-<时间戳>` 后恢复备份）；
- 安装包与 Release 至少保留两个版本。

## 10. 已知限制（写进 Release 说明）

- 插件与启动器**同进程**：凭据边界（宿主只给账户显示名/UUID/头像、令牌对插件是 `enc:` 密文、解密 API 拒绝插件）是**尽力而为的纵深防御**，不是沙箱；只安装可信来源插件，强隔离在后续版本（见 [PLUGINS.md 第 7 节](PLUGINS.md)）。
- 脚本插件 **v1.4.1 只支持 JavaScript（Jint）**，Python 计划 v1.5；脚本同步执行，不支持 `async/Promise`。
- 类型 C（原生依赖插件）**默认拒载**；类型 A 插件改完必须重启启动器，**不支持热卸载**。
- 插件改动后需要重启；打包器在插件运行时会排队，重启后替换。

## 11. 本次构建记录（2026-11-01，**只构建，未发布**）

```text
dotnet publish → dist\release\（355 个文件，合计约 238 MB）
makensis installer\installer.nsi → dist\NovaLinkLumen-Setup.exe

dist\NovaLinkLumen-Setup.exe               69,312,728 B   ProductVersion 1.4.1
dist\NovaLinkLumen-WinUI-Setup-1.4.1.exe   69,312,728 B   （同字节副本，双资产用）
SHA256（两个文件相同）：56FA5345E285774EB677ED7196F12335A13246FBB599BCAF5D09B304DEC0ECD8
```

- [x] `archives/1.4.0/MANIFEST.json` 存在，`build.ps1` 的归档闸门放行（“上一版本 1.4.0 已归档”）
- [x] 三工程 `dotnet build -c Debug -p:Platform=x64` 全部 0 警告 0 错误（Core / Services / App）
- [x] 安装包 `ProductVersion` / `FileVersion` = 1.4.1，`ProductName` = NovaLink Lumen
- [ ] 尚未上传 GitHub、尚未打 `v1.4.1` 标签——发布动作留给人工执行（本文件是清单，不是发布记录）

**环境注意（本机）：** 内置的 NSIS 3.0.4.1 在这台机器上**无法生成安装包**——报
`Internal compiler error #12345: error creating mmap the size of 40378334`，用一个 1 MB 的最小脚本复现则是
`Can't open output file`（PowerShell 写同一个目录完全正常，换 NSIS 3.13 立刻成功），所以本次改用
NSIS 3.13（来源：chocolatey 包 `nsis.portable` 里的 `tools/nsis-3.13.zip`）：

```powershell
$env:NOVALINK_NSIS = 'D:\tmp\nsis311\nsis\nsis-3.13\makensis.exe'
powershell -ExecutionPolicy Bypass -File scripts\build.ps1
```

`installer/installer.nsi` 的 `StrFunc.nsh` 调用已改成两版兼容（新版用 `${Using:StrFunc} StrStr`，
旧版用 `!insertmacro FUNCTION_STRING_StrStr`），两条路径都在本机验证过。

---

相关文档：[PUBLISHING.md](PUBLISHING.md)（完整发版流程）、[PLUGINS.md](PLUGINS.md)（插件安装/开发/安全）、[STORE.md](STORE.md)（插件商店上架）、[CHANGELOG.md](../CHANGELOG.md)。
