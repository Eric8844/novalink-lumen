# Contributing

感谢你愿意改进 NovaLink Lumen。

## 开发环境

- Windows 10 19041+ / Windows 11 x64
- .NET 10 SDK
- Visual Studio 2022/2026（可选）
- NSIS 3.0.4+（仅打包安装器时需要）

## 构建与运行

```powershell
dotnet build src/NovaLink.Lumen/NovaLink.Lumen.csproj -c Debug -p:Platform=x64
src/NovaLink.Lumen/bin/x64/Debug/net10.0-windows10.0.19041.0/win-x64/NovaLink.Lumen.exe
```

提交前至少确认：

- 应用能正常启动，首页、版本、发现、论坛、库、设置可以切换。
- 改动涉及启动 / 安装时，说明测试过的版本、加载器与整合包。
- 改动涉及 Win2D / 玻璃时，最大化、还原、失焦与浅色主题都正常。
- 改动涉及账户时，不把真实令牌、密码或用户数据写入日志。

## 代码约定

- Core 不依赖 WinUI；Services 不直接操作 XAML。
- UI 通过 `LumenRuntime` 调用服务，不在页面里拼接版本 JSON 或启动命令。
- 新增网络请求必须支持取消，并处理超时与 API 错误。
- 新增文件访问必须使用 `AppPaths`，不要硬编码用户目录。
- 图标使用矢量资源；界面文案保持简洁，不使用 emoji。
- 一个 PR 只做一件事，说明动机、行为和验证方式。

## 不要提交

- `bin/`、`obj/`、`dist/`、日志、截图临时文件
- 账户令牌、API Key、管理员密码、SMTP 授权码
- 服务器 SSH / SMB / 远程桌面凭据
- 用户上传的图片、视频、聊天记录
- 完整的 Mojang 资源文件或游戏本体

## 报告问题

请提供：

- 操作系统与版本
- NovaLink Lumen 版本
- 复现步骤
- 相关日志或截图（先删除个人信息）
