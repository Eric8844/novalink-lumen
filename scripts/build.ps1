# 构建并打包 NovaLink Lumen
# 用法：powershell -ExecutionPolicy Bypass -File scripts/build.ps1
#   可选：-Channel Stable|Preview   发布通道，默认 Stable（正式版）。
#          正式版产出 1.5.0 的包，走正式版仓库（novalink-lumen-releases）；
#          测试版显式写 -Channel Preview，产出 1.5.0 Preview 的包，走测试版仓库（novalink-lumen-preview）。
#   可选：-SkipArchiveCheck        跳过“上一版本已归档”闸门（CI 无 archives/ 时用；本地发版必须走完整流程）
#   可选：-NsisPath <makensis.exe> 显式指定 NSIS（等价于设置环境变量 NOVALINK_NSIS）
#
# 发版顺序（顺序错了会覆盖掉上一个版本的安装包，dist/ 被 gitignore 忽略，覆盖即永久丢失）：
#   1) 打标签 vX.Y.Z-archive
#   2) scripts/archive-release.ps1 -Version <上一版>      <- 把 dist/ 里的旧产物冻结进 archives/
#   3) scripts/bump-version.ps1 -To <新版本>
#   4) 本脚本（下面的闸门会检查第 2 步真的做过）
#
# 本文件含中文，必须保存为 UTF-8 BOM，否则 Windows PowerShell 5.1 会报 ParserError。
[CmdletBinding()]
param(
    [ValidateSet("Stable", "Preview")]
    [string]$Channel = "Stable",
    [switch]$SkipArchiveCheck,
    [string]$NsisPath
)

$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
Set-Location $root

# ---------------------------------------------------------------------------
# step 0：版本号与归档闸门
# publish 会原地覆盖 dist/，所以必须在 publish 之前确认“上一个版本的产物已经归档”。
# csproj 的 <Version> 允许带 -preview.N 后缀（测试版），这里只取数值部分做文件名与校验。
# ---------------------------------------------------------------------------
$csprojPath = Join-Path $root "src/NovaLink.Lumen/NovaLink.Lumen.csproj"
$csprojText = [System.IO.File]::ReadAllText($csprojPath)
$verMatch = [regex]::Match($csprojText, "<Version>\s*(\d+\.\d+\.\d+)")
if (-not $verMatch.Success) { throw "无法从 csproj 读取 <Version>。" }
$ver = $verMatch.Groups[1].Value

$isPreview = $Channel -eq "Preview"
$displayVer = if ($isPreview) { "$ver Preview" } else { $ver }
$channelLabel = if ($isPreview) { "测试版" } else { "正式版" }
Write-Host "==> 版本 $ver（$channelLabel，显示为 $displayVer）" -ForegroundColor Cyan

if (-not $SkipArchiveCheck) {
    $prev = Get-ChildItem "$root/archives" -Directory -ErrorAction SilentlyContinue |
            Where-Object { $_.Name -ne $ver } | Sort-Object Name -Descending | Select-Object -First 1
    if ($prev -and -not (Test-Path "$($prev.FullName)/MANIFEST.json")) {
        throw "上一版本 $($prev.Name) 尚未归档（缺少 archives/$($prev.Name)/MANIFEST.json）。先运行 scripts\archive-release.ps1 -Version $($prev.Name)，确认无误后再构建；确实要强行继续请加 -SkipArchiveCheck。"
    }
    if ($prev) { Write-Host "==> 上一版本 $($prev.Name) 已归档" -ForegroundColor Cyan }
}

# ---------------------------------------------------------------------------
# step 1：publish
# -p:Platform=x64 不能省：slnx 里遗留的 x86 平台会和 csproj 打架（见 AGENTS.md）。
# -p:NovaLinkChannel 决定编译常量 NOVALINK_PREVIEW：只有 Preview 才加，客户端据此选更新仓库。
# 正式版额外把 Version/InformationalVersion 改成纯数字，避免二进制里还写着 preview。
# ---------------------------------------------------------------------------
Write-Host "==> dotnet publish（通道 $Channel）" -ForegroundColor Cyan
$publishArgs = @(
    "publish",
    "src/NovaLink.Lumen/NovaLink.Lumen.csproj",
    "-c", "Release",
    "-r", "win-x64",
    "--self-contained", "true",
    "-p:Platform=x64",
    "-p:NovaLinkChannel=$Channel",
    "-o", "dist/release"
)
if (-not $isPreview) {
    $publishArgs += "-p:Version=$ver"
    $publishArgs += "-p:InformationalVersion=$ver"
}
dotnet @publishArgs
if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed" }

# ---------------------------------------------------------------------------
# step 2：找 makensis
# 查找顺序：-NsisPath > PATH > 环境变量 NOVALINK_NSIS > Program Files > 仓库内 tools/nsis/Bin。
# 本机 NSIS 装在非常规位置时用 -NsisPath 或 NOVALINK_NSIS 指过去，不要把绝对路径写进公开仓。
# ---------------------------------------------------------------------------
$makensis = $null
if ($NsisPath) {
    if (-not (Test-Path $NsisPath)) { throw "找不到 -NsisPath 指定的 makensis：$NsisPath" }
    $makensis = (Resolve-Path $NsisPath).Path
}
if (-not $makensis) {
    $makensis = Get-Command makensis.exe -ErrorAction SilentlyContinue | Select-Object -First 1 -ExpandProperty Source
}
if (-not $makensis -and $env:NOVALINK_NSIS -and (Test-Path $env:NOVALINK_NSIS)) {
    $makensis = $env:NOVALINK_NSIS
}
if (-not $makensis) {
    $candidates = @(
        "$env:ProgramFiles\NSIS\makensis.exe",
        "$root/tools/nsis/Bin/makensis.exe"
    )
    $programFilesX86 = [Environment]::GetEnvironmentVariable("ProgramFiles(x86)")
    if ($programFilesX86) { $candidates += (Join-Path $programFilesX86 "NSIS\makensis.exe") }
    $makensis = $candidates | Where-Object { $_ -and (Test-Path $_) } | Select-Object -First 1
}
if (-not $makensis) {
    throw "未找到 makensis.exe。请安装 NSIS 3.0.4+，或把 NSIS 放到 tools/nsis/Bin，或用 -NsisPath / 环境变量 NOVALINK_NSIS 指向 makensis.exe。"
}

# ---------------------------------------------------------------------------
# step 3：编译安装器
# 安装包名带版本号：NovaLinkLumen-WinUI-Setup-<ver>.exe。
# 这样 v1.4.x 时代的“双资产改名”兜底不再需要 —— 文件名同时含 WinUI 与 -Setup-x.y.z，
# 能过 UpdateService 的资产过滤。正式版与测试版同名规则，只是进不同仓库。
# ---------------------------------------------------------------------------
$setupName = "NovaLinkLumen-WinUI-Setup-$ver.exe"
$outRel = "..\dist\$setupName"
Write-Host "==> makensis ($makensis)" -ForegroundColor Cyan
& $makensis "/DAPP_VERSION=$ver" "/DAPP_VERSION_DISPLAY=$displayVer" "/DOUT=$outRel" "installer/installer.nsi"
if ($LASTEXITCODE -ne 0) { throw "makensis failed" }

$setup = Join-Path $root "dist/$setupName"
if (-not (Test-Path $setup)) { throw "makensis 报成功但没找到 $setup。" }

# 安装器的产品版本来自 installer.nsi 的 APP_VERSION，和 csproj 是两处硬编码。
# 这里对一次，避免发布出“文件是 1.5.0、属性里还是 1.4.0”的包。
$fileVer = [System.Diagnostics.FileVersionInfo]::GetVersionInfo($setup)
$setupVer = "$($fileVer.FileMajorPart).$($fileVer.FileMinorPart).$($fileVer.FileBuildPart)"
if ($setupVer -ne $ver) {
    throw "安装包产品版本是 $setupVer，但 csproj 的 <Version> 是 $ver。检查 installer/installer.nsi 的 APP_VERSION。"
}

Write-Host "==> done: $setup (版本 $setupVer，通道 $channelLabel)" -ForegroundColor Green
Get-Item $setup | Select-Object FullName, Length, LastWriteTime
Get-FileHash $setup -Algorithm SHA256 | Select-Object Hash
