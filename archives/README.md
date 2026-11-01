# archives/ · 发版产物离线归档

这个目录存在的唯一理由：**`dist/` 被 `.gitignore` 忽略，下一次 `dotnet publish` 会原地覆盖上一个版本的安装包，覆盖即永久丢失。**

GitHub Releases 上的安装包是远端不可变副本；这里的归档是**离线可校验副本**。两边靠 `MANIFEST.json` 里的 SHA256 互校。

## 目录结构

```
archives/
  README.md                     ← 唯一入库的说明文件（本文件）
  v1.4.0/
    MANIFEST.json               ← 入库：归档完成标记 + 全部校验值
    SHA256SUMS                  ← 入库：exe / 源码快照 / release 全量
    NovaLinkLumen-Setup.exe     ← 不入库（约 68 MB）
    source-v1.4.0.tar.gz        ← 不入库（git archive 产物）
    release/                    ← 不入库（发布目录全量快照，数百文件）
```

## 为什么不把二进制提交进 git

68 MB 一旦进入 Git 历史就永久无法真正删除（GitHub 也不能回收），公开仓每次 clone 都要付这个代价。所以：

- **入库**：`MANIFEST.json`、`SHA256SUMS`、本文件。三个都是纯文本，几 KB。
- **不入库**：安装包、源码快照、`release/` 快照。靠 `MANIFEST.json` 的 sha256 证明它们没被篡改。

## 打一个归档

```powershell
# 1) 先给这次发布打附注标签（不改动已有的轻量 tag v1.4.0）
git tag -a v1.4.0-archive 031a2f6 -m "v1.4.0 release archive"
git push origin v1.4.0-archive

# 2) 把 dist/ 里的产物冻结进 archives/
powershell -ExecutionPolicy Bypass -File scripts\archive-release.ps1 -Version 1.4.0

# 3) 只入库台账
git add archives/v1.4.0/MANIFEST.json archives/v1.4.0/SHA256SUMS
```

`scripts/archive-release.ps1` 做的事（顺序不能变）：

1. 版本号默认取 csproj 的 `<Version>`，定位 `archives/<版本>/`；
2. 校验版本标签存在，且指向的提交与 `-Commit`（默认 HEAD）一致；
3. 校验 `dist/NovaLinkLumen-Setup.exe` 的产品版本（`ApplicationVersion`）与版本一致 —— 防止把 1.4.1 的包归档成 1.4.0；
4. 幂等：`MANIFEST.json` 已存在且安装包 sha256 一致 → 跳过；不一致 → **退出码 2，绝不覆盖**；
5. 复制安装包 + `robocopy /MIR dist/release`；
6. `git archive` 源码快照；
7. 生成 `SHA256SUMS`；
8. **最后**写 `MANIFEST.json`（它存在 = 归档完成，前面任何一步失败都不会留下"看起来完成"的归档）；
9. 可选 `-Mirror <目录>` 再镜像一份到外部盘/网盘。

## 校验一个归档

```powershell
# 单个安装包
(Get-FileHash archives/v1.4.0/NovaLinkLumen-Setup.exe -Algorithm SHA256).Hash
# 与 MANIFEST.json 的 installer.sha256 对比

# 全量比对（exe + 源码快照 + release 全量）
cd archives/v1.4.0
# SHA256SUMS 的格式是 "<hash>  <相对路径>"，逐条比对即可
```

`releaseTree.fingerprintSha256` 是把 `(相对路径, 字节数, sha256)` 排序后拼接再取 hash，一个值就能判断整个 `release/` 快照有没有被动过（含缺失/多余文件）。

## 和构建的关系

`scripts/build.ps1` 在 `dotnet publish` **之前**有闸门：`archives/` 下除当前版本外最新的目录若缺少 `MANIFEST.json`，直接 `throw` 并提示先跑 `archive-release.ps1`。逃生阀是 `-SkipArchiveCheck`，仅供没有 `archives/` 的 CI 场景，**本地发版不许用**。

## 硬约束

- `MANIFEST.json` 一旦入库就是历史记录，**永不改写**；要重新归档必须先人工确认并删除整个 `archives/<版本>` 目录。
- 不要移动、删除或重打已经发布过的 git tag（例如轻量 tag `v1.4.0`）；归档信息一律写进新的 `vX.Y.Z-archive` 附注标签。
- 不要把安装包、`release/` 快照强行 `git add -f` 进仓库。
