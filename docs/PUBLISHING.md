# 发布

## 1. 版本号

发布前统一修改：

- `src/NovaLink.Lumen/NovaLink.Lumen.csproj` 的 `Version` 与 `ApplicationVersion`
- `installer/installer.nsi` 的 `APP_VERSION` 与 `VIProductVersion`
- `README.md` 的当前版本
- `CHANGELOG.md`

## 2. 构建自包含版本

在仓库根目录：

```powershell
dotnet publish src/NovaLink.Lumen/NovaLink.Lumen.csproj `
  -c Release -r win-x64 --self-contained true -o dist/release
```

确认 `dist/release` 至少包含：

- `NovaLink.Lumen.exe`
- `NovaLink.Lumen.pri`
- `Assets/AppIcon.ico`
- `Assets/Images/` 下的图标与 Hero 图
- Windows App SDK 与 .NET 运行时文件

## 3. 生成安装包

安装 NSIS 3.0.4+，然后：

```powershell
makensis installer/installer.nsi
```

产物：

```text
dist/NovaLinkLumen-Setup.exe
```

安装器会：

- 安装到 `%LOCALAPPDATA%\Programs\NovaLink Lumen`
- 写入当前用户卸载注册表项
- 创建开始菜单启动 / 卸载快捷方式
- 覆盖旧 Electron 稳定版与旧 WinUI 预览版，但保留用户数据
- 卸载时询问是否删除启动器数据，默认保留

发布前手动验证：

- 静默安装与普通安装都能完成
- 开始菜单出现“NovaLink Lumen”和“卸载 NovaLink Lumen”
- Windows 设置 → 应用 中能看到 NovaLink Lumen
- 卸载入口能启动卸载程序
- 卸载后用户数据仍存在（除非明确选择删除）
- 旧版本升级后账户与实例仍在

## 4. 计算校验值

```powershell
Get-FileHash dist/NovaLinkLumen-Setup.exe -Algorithm SHA256
```

把 SHA256 写入 Release 说明。

## 5. GitHub Release

1. 创建与版本一致的 tag，例如 `v1.4.0`。
2. 上传 `dist/NovaLinkLumen-Setup.exe`。
3. 资产文件名固定为 `NovaLinkLumen-Setup.exe`，不要带版本号。
4. Release 说明包含：更新内容、系统要求、SHA256、已知问题。
5. 确认 `releases/latest/download/NovaLinkLumen-Setup.exe` 能 302 到最新资产。

启动器的更新检查只接受原生版本化的安装包，不会把旧 Electron 包误判为可用更新。

## 6. 官网与更新接口

如果使用独立更新接口，返回：

```json
{
  "version": "NovaLinkLumen V1.4.0_Desktop",
  "download_url": "https://github.com/<owner>/<repo>/releases/latest/download/NovaLinkLumen-Setup.exe",
  "filename": "NovaLinkLumen-Setup.exe"
}
```

不要把管理员密码、API Key、SMTP 授权码或服务器凭据写入仓库或 Release 说明。

## 7. 回滚

保留上一个版本的安装包与 Release。若新版本出现阻断问题：

- 将 `latest` 指回上一个 Release
- 在 Release 说明中标注问题与回滚方式
- 不要删除用户数据目录
