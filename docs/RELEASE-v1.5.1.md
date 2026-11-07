# v1.5.1 发版清单

> **本清单已于 2026-11-07 执行发布**：源码仓 tag `v1.5.1`（提交 `4beca1f`）、正式版仓 Release [v1.5.1](https://github.com/Eric8844/novalink-lumen-releases/releases/tag/v1.5.1)、源码仓镜像 Release [v1.5.1](https://github.com/Eric8844/novalink-lumen/releases/tag/v1.5.1)。勾选框只代表真正执行过的步骤；未执行的回归项保持未勾选。

版本：**1.5.1**（应用内显示 `1.5.1`，程序集 `1.5.1.0`）

---

## 1. 版本号一致性检查

### 1.1 唯一入口

版本号没有单一真源，全部位置由 `scripts/bump-version.ps1` 的 `$sites` 白名单 + `$docFiles` 文档清单统一改写（见 [PUBLISHING.md 第 1 节](PUBLISHING.md)）。**不要手改**。

本轮已执行：

```powershell
cd "D:\AI service\NovaLink-Lumen-Public"
powershell -ExecutionPolicy Bypass -File scripts\bump-version.ps1 -To 1.5.1
```

- [x] 代码侧 17 处（csproj / installer.nsi / UpdateService / HttpService / LaunchService / LauncherChannel / ShellPage.xaml / MainWindow.xaml / SettingsPage.xaml(.cs) / VersionSourceDetector）
- [x] 文档侧 README / AGENTS / docs/PUBLISHING.md 整文件替换
- [x] 脚本白名单已修：`APP_VERSION_DISPLAY` 正则改为 `!define APP_VERSION_DISPLAY "(?<v>\d+\.\d+\.\d+)(?: Preview)?"`（正式版安装包的显示版本没有 Preview 后缀）
- [x] bump 之后人工回改了被脚本误伤的历史叙述（"v1.5.0 起插件默认关闭"这类描述不能跟着版本号漂移）

### 1.2 自查命令

```powershell
cd "D:\AI service\NovaLink-Lumen-Public"

Select-String -Path src/NovaLink.Lumen/NovaLink.Lumen.csproj,installer/installer.nsi,`
  src/NovaLink.Lumen.Services/Updates/UpdateService.cs,`
  src/NovaLink.Lumen.Services/Http/HttpService.cs,`
  src/NovaLink.Lumen.Services/Minecraft/LaunchService.cs,`
  src/NovaLink.Lumen.Services/Storage/VersionSourceDetector.cs -Pattern '1\.5\.[0-9]+'
```

合法旧值：`installer/installer.nsi` 的 `Data_Backup_v1.5.0`（备份目录名记的是上一版）、`VersionSourceDetector.cs` 里的 1.5.0 源版本常量与注释。

---

## 2. 构建与产物

```powershell
# 应用（三工程）
dotnet build src/NovaLink.Lumen/NovaLink.Lumen.csproj -c Debug -p:Platform=x64

# 正式发版产物（归档闸门 + publish + makensis + SHA256）
powershell -ExecutionPolicy Bypass -File scripts\build.ps1 -NsisPath "D:\AI service\desktop\.cache\nsis\nsis-3.10\makensis.exe"
```

> **NSIS 版本**：本机 `desktop\.cache\nsis\nsis-3.0.4.1` 的 makensis 3.04 在这台机器上无法创建任何输出文件（最小空脚本也报 `Can't open output file`，完整脚本报 `Internal compiler error #12345: error creating mmap`），与压缩方式、TEMP 路径、磁盘/内存都无关。换成 NSIS **3.10** 便携版即可正常构建。3.10 已放到 `desktop\.cache\nsis\nsis-3.10\`，并以用户环境变量 `NOVALINK_NSIS` 指向它；`build.ps1` 不传 `-NsisPath` 时会读这个变量。

- [x] 三工程 0 警告 0 错误
- [x] `dist/release/NovaLink.Lumen.exe` 的 `ProductVersion = 1.5.1`（含 `+b3dd3f5…` 源码版本后缀）、`FileVersion = 1.5.1.0`
- [x] `dist/NovaLinkLumen-WinUI-Setup-1.5.1.exe` 生成：69,398,948 B（修完 `ShellPage.xaml:113` 版本 ToolTip 后重打包的最终产物）
  - sha256 `F909B785741483C44AE61B4BF035B94BE061498477B833CCD74ECE07484F6F2A`（GitHub 上传后返回的 digest 与之一致）
  - 安装包版本资源 `ProductVersion = 1.5.1`、`FileVersion = 1.5.1`

---

## 3. 发布前手测

`docs/PUBLISHING.md:72` 的手测清单是唯一质量门，本轮额外重点：

- [ ] 账户皮肤：离线账户 →「皮肤」→ 输入正版用户名取皮肤 / 选本地 PNG / 清除回默认；正版账户 → 上传本地皮肤后游戏内生效
- [ ] 插件玻璃：装上 `docs/plugin-template` 后，插件页出现玻璃卡与胶囊按钮，切深/浅色立即换配方
- [ ] 插件文档：设置 → 插件 → 插件文档，Markdown 渲染、代码块可复制、正文可全选
- [ ] 老插件兼容：v1.5.0 编译的插件不调用新 API 仍能加载
- [ ] 库页上滑无鬼影：滚到列表中部，标题区与「模组 / 全选 / 批量启用」一行不出现行卡形状的玻璃带；切实例或页签后第一帧行卡就带玻璃（`docs/PUBLISHING.md` 同名条目）
- [ ] 回归：首页 3D 人物、音乐台、发现页/库页玻璃、进度中心、LittleSkin 登录

### 3.1 本轮已实测（2026-11-07）

- [x] 账户皮肤弹窗可打开（设置 → 账户 →「上传到正版」），模型下拉、选图、换回账号皮肤三块都在：`_tmp_prog\v151b-skin.png`
- [x] 插件玻璃：把 `docs/plugin-template` 产物放进 `LauncherData\plugins\hello.world`，插件页出现宿主玻璃卡与胶囊按钮；`theme` 深→浅整页换配方（强调色 `#FF7CEBD6` → `#FF0C9480`）：`_tmp_prog\v151q-dark.png` / `v151q-light.png`
- [x] 插件文档：设置 → 插件 → 插件文档 → 开发文档，Markdown 渲染与代码块「复制」正常：`_tmp_prog\v151b-docs.png`
- [x] 安装包界面：`dist\NovaLinkLumen-WinUI-Setup-1.5.1.exe` 启动即显示「NovaLink Lumen 安装」、页脚 `NovaLink Lumen 1.5.1`、默认目录 `%LOCALAPPDATA%\Programs\NovaLink Lumen`：`_tmp_prog\v151setup.png`
- [x] 真实覆盖升级：`/S` 静默安装覆盖本机 1.5.0 → 安装目录 exe 变 `1.5.1.0`，注册表 `DisplayVersion=1.5.1`、`DataBackupDir=%LOCALAPPDATA%\NovaLink Lumen\Data_Backup_v1.5.0`；启动后日志「版本 1.5.1」，左下徽章与「关于」页显示 1.5.1：`_tmp_prog\v151_installed.png`
- [x] 库页统计跟随实例：切到「1.21.1养老生存优化版」后统计由「76 模组 · 4 世界 · 7 资源 · 占用 2.4 GB」变为「121 模组 · 4 世界 · 11 资源 · 占用 1.5 GB」（`_tmp_prog\ov_before.png` / `ov_switched.png`）
- [x] 库页滚动鬼影：深色 `_tmp_prog\fix_scroll1.png` / `fix_scroll2.png`、浅色 `light2_scroll.png` 上滑后标题区干净；切页签第一帧 `burst_0.png` 行卡即带玻璃；发现页 `disc_scroll.png`、设置页 `settings.png` 无回归
- [x] 升级备份：`Data_Backup_v1.5.0\BACKUP-INFO.json` 记录 `sourceVersion=1.5.0 → targetVersion=1.5.1`、658 文件 / 262,900,396 B；本次升级因 schema 已是 2 且备份已存在而跳过重复备份（日志「数据已是最新（schema 2），跳过升级备份」），旧的 v1.3.0 / v1.4.1 / v1.5.0 三份备份都还在

---

## 4. 归档与备份

- [x] `archives/1.5.0/` 已完成（`MANIFEST.json` + `SHA256SUMS` 进 git，安装包/release 快照/源码 tar 不进 git）
  - exe `NovaLinkLumen-WinUI-Setup-1.5.0.exe` 69,297,059 B
  - sha256 `3818225BD4E17076AB9A0014DBF78E0B4025C42712DEB03346BB2CA8ACFB02E5`
- [x] 发版后已归档 1.5.1：`scripts/archive-release.ps1 -Version 1.5.1`
  - exe `NovaLinkLumen-WinUI-Setup-1.5.1.exe` 69,398,948 B，sha256 `F909B785741483C44AE61B4BF035B94BE061498477B833CCD74ECE07484F6F2A`
  - `archives/1.5.1/`：SHA256SUMS 549 行、release 快照 547 文件、源码 tar.gz；MANIFEST.json 与 SHA256SUMS 进 git，其余被 `.gitignore:45` 挡住
- 安装器的 `Data_Backup_v1.5.0` 目录由升级守卫创建，卸载程序不会删除

---

## 5. 发布步骤（2026-11-07 已执行）

1. [x] `git tag -a v1.5.1` → 提交 `4beca1f`（源码仓 Eric8844/novalink-lumen）
2. [x] `scripts/build.ps1 -NsisPath "%LOCALAPPDATA%\NovaLinkBuild\nsis-3.10\makensis.exe"` 产出安装包（本机 `desktop\.cache\nsis` 路径会被火绒 HIPS 拦住 mmap，必须把 NSIS 复制到用户目录再跑）
3. [x] Release `Eric8844/novalink-lumen-releases`：[v1.5.1](https://github.com/Eric8844/novalink-lumen-releases/releases/tag/v1.5.1)（release id 437465141），资产 `NovaLinkLumen-WinUI-Setup-1.5.1.exe` 69,398,948 B
4. [x] 源码仓推送（`5e6ae5e..b30f6a3` main + tag v1.5.1）+ 镜像 Release：[v1.5.1](https://github.com/Eric8844/novalink-lumen/releases/tag/v1.5.1)
5. [x] 归档 1.5.1（`archives/1.5.1`，提交 `b30f6a3`）
6. [x] 更新器自检：`releases/latest` 返回 v1.5.1、资产名匹配 `-Setup-\d+\.\d+\.\d+`、HEAD 下载 200 且 Content-Length 一致

---

## 6. 更新说明（可直接贴到 Release）

v1.5.1 是一个以「账户皮肤」和「插件开发接口」为主的版本：

- **账户皮肤**：离线账户可以直接用正版账户的皮肤，也能打开本地皮肤文件；正版账户可以把本地皮肤上传到 Mojang，进游戏就是新皮肤。
- **插件玻璃组件**：插件作者现在可以直接调用启动器的液态玻璃卡片、面板、胶囊按钮，样式跟宿主完全一致（含背景折射与悬停高光）。
- **主题开发接口**：新增 `IPluginTheme`，13 个颜色令牌覆盖强调色、文字、玻璃底、描边、危险色等，切深/浅色会实时推事件。
- **插件文档**：应用内「插件文档」改为 Markdown 渲染，代码块等宽字体 + 语言标签 + 复制按钮，正文可全选复制；仓库 `docs/PLUGINS.md` 同步重写为完整开发指南。
- **库页滚动残影**：库页列表上滑后不会再在标题栏位置留下行卡形状的玻璃残影；新行卡出现的第一帧就带玻璃，不再「先透明后叠加」。
- **库页统计跟随实例**：统计行改为只算当前实例，切换版本 / 刷新 / 增删内容后数字立刻重算，不再一直显示全库合计。
- 版本号统一到 1.5.1，上一版 1.5.0 已冻结进 `archives/1.5.0`。
