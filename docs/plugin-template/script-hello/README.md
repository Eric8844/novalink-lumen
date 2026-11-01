# 脚本插件模板（类型 B：免编译 JavaScript）

不用装 .NET SDK，改一个 `main.js` 就能给启动器加钩子。

## 1. 复制到插件目录

文件夹名必须与 `plugin.json` 的 `id` 一致（本模板是 `script-hello`）：

```powershell
$src = "<本模板所在目录>\script-hello"
$dst = "$env:LOCALAPPDATA\NovaLink Lumen\LauncherData\plugins\script-hello"
New-Item -ItemType Directory -Force -Path $dst | Out-Null
Copy-Item "$src\*" $dst -Recurse -Force
```

也可以从启动器「设置 → 插件 → 打开插件目录」进去手动复制。

## 2. 重启启动器验证

1. 完全退出启动器（托盘里也要退），重新启动；
2. 打开「设置 → 插件」，列表里应出现「脚本示例」，状态为运行中；
3. 打开日志 `%LOCALAPPDATA%\NovaLink Lumen\LauncherData\logs\lumen-yyyyMMdd.log`，搜索 `plugin:script-hello`，应看到：
   - `script-hello 已加载，版本 1.0.0`
   - `这是本插件第 1 次初始化。`（每重启一次数字 +1）
4. 想看更多加载细节，用 `NovaLink.Lumen.exe --dev-mode` 启动。

## 3. 改出自己的插件

- `plugin.json`：改 `id`（同时改插件文件夹名）、`name`、`version`、`author`；`scripts[].file` 指向你的入口脚本。
- `scripts/main.js`：保留需要的钩子，删掉不需要的；日志一律用 `lumen.log`。
- 同一插件可以写多个脚本文件，按 `scripts` 数组顺序同步执行。

## 4. 限制速查（v1.4.1）

| 支持 | 不支持 |
|---|---|
| 同步 JS、`lumen.*` 全部 API | `async/await`、Promise、定时器 |
| 读写自己的设置 | 文件、网络、`require/import`、`eval` |
| 钩子与事件订阅 | 自定义界面（脚本插件没有 UI） |
| — | Python（v1.5 之后才计划） |

完整 API 与安全模型见仓库 `docs/PLUGINS.md`。
