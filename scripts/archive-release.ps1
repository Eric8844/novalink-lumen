#requires -version 5.1
<#
.SYNOPSIS
    把一个已发布的启动器版本冻结进 archives/<版本>/ 离线归档。

.DESCRIPTION
    发版顺序里这一步必须排在“打标签”之后、“改版本号”之前：
        git tag -a v1.4.0-archive 031a2f6  ->  archive-release.ps1 -Version 1.4.0
    因为 dist/ 被 .gitignore 忽略，下一次 publish 会原地覆盖安装包，覆盖即永久丢失。

    写进 archives/<版本>/ 的东西：
      NovaLinkLumen-Setup.exe   安装包（不进 git，靠 MANIFEST 里的 sha256 互校）
      source-<版本>.tar.gz      git archive 源码快照（不进 git）
      release/                  dist/release 全量快照（不进 git）
      SHA256SUMS                exe + tar.gz + release 全量校验值（进 git）
      MANIFEST.json             归档完成标记（进 git，最后一步才写）

    幂等：MANIFEST.json 已存在且 exe 的 sha256 一致 -> 跳过；不一致 -> 退出码 2，绝不覆盖。

.EXAMPLE
    powershell -ExecutionPolicy Bypass -File scripts\archive-release.ps1 -Version 1.4.0

.EXAMPLE
    powershell -ExecutionPolicy Bypass -File scripts\archive-release.ps1 -Version 1.4.0 -CheckOnly

.NOTES
    本文件含中文，必须保存为 UTF-8 BOM，否则 Windows PowerShell 5.1 会报 ParserError。
#>
[CmdletBinding()]
param(
    # 要归档的版本号，形如 1.4.0；省略时取 csproj 的 <Version>。
    [string]$Version,

    # 该版本对应的提交；默认 HEAD。必须与版本标签指向的提交一致。
    [string]$Commit = "HEAD",

    # 仓库根目录；省略时取脚本所在目录的上一级。
    [string]$RepoRoot,

    # 可选：归档完成后再镜像一份到外部盘/网盘目录。
    [string]$Mirror,

    # 只做步骤 1-4 的校验，不复制任何文件（发布前自检用）。
    [switch]$CheckOnly
)

$ErrorActionPreference = "Stop"

if (-not $RepoRoot) { $RepoRoot = Split-Path -Parent $PSScriptRoot }
Set-Location $RepoRoot

function Get-Sha256 {
    param([string]$Path)

    $sha = [System.Security.Cryptography.SHA256]::Create()
    $stream = [System.IO.File]::OpenRead($Path)
    try { $bytes = $sha.ComputeHash($stream) } finally { $stream.Dispose(); $sha.Dispose() }
    return ([System.BitConverter]::ToString($bytes) -replace "-", "")
}

function Invoke-Git {
    param([string[]]$Arguments)

    $out = & git -C $RepoRoot @Arguments 2>&1
    if ($LASTEXITCODE -ne 0) { throw ("git {0} 失败：{1}" -f ($Arguments -join " "), ($out -join " ")) }
    return ($out -join "`n").Trim()
}

function Read-CsprojVersion {
    # <Version> 允许带 -preview.N 后缀（测试版），归档只认数值部分：1.5.0-preview.1 -> 1.5.0。
    $path = Join-Path $RepoRoot "src/NovaLink.Lumen/NovaLink.Lumen.csproj"
    $m = [regex]::Match([System.IO.File]::ReadAllText($path), "<Version>\s*(\d+\.\d+\.\d+)")
    if (-not $m.Success) { throw "没能从 csproj 读到 <Version>。" }
    return $m.Groups[1].Value
}

# ---------------------------------------------------------------------------
# 步骤 1：版本号与归档目录
# ---------------------------------------------------------------------------
if (-not $Version) { $Version = Read-CsprojVersion }
if ($Version -notmatch "^\d+\.\d+\.\d+$") { throw "版本号格式不对：$Version（应为 1.4.0 这种形式）" }

$archDir = Join-Path $RepoRoot "archives/$Version"
$manifestPath = Join-Path $archDir "MANIFEST.json"
# 安装包命名：v1.5.0 起是 dist/NovaLinkLumen-WinUI-Setup-<ver>.exe；
# 归档 v1.4.0 这类旧版本时回退到旧的 dist/NovaLinkLumen-Setup.exe。
$exeSrc = Join-Path $RepoRoot "dist/NovaLinkLumen-WinUI-Setup-$Version.exe"
if (-not (Test-Path $exeSrc)) {
    $legacyExe = Join-Path $RepoRoot "dist/NovaLinkLumen-Setup.exe"
    if (Test-Path $legacyExe) { $exeSrc = $legacyExe }
}
$exeName = Split-Path -Leaf $exeSrc
$releaseSrc = Join-Path $RepoRoot "dist/release"

Write-Host "==> 归档版本：$Version" -ForegroundColor Cyan

# ---------------------------------------------------------------------------
# 步骤 2：版本标签必须存在，且与 -Commit 指向同一次提交
# ---------------------------------------------------------------------------
$tag = $null
foreach ($candidate in @("v$Version-archive", "v$Version")) {
    git -C $RepoRoot rev-parse --verify --quiet "refs/tags/$candidate" > $null
    if ($LASTEXITCODE -eq 0) { $tag = $candidate; break }
}
if (-not $tag) { throw "找不到版本标签（v$Version-archive 或 v$Version）。归档前必须先打标签，见 docs/PUBLISHING.md 的归档章节。" }

$tagCommit = Invoke-Git @("rev-parse", "$tag^{commit}")
$commitSha = Invoke-Git @("rev-parse", "$Commit^{commit}")
if ($tagCommit -ne $commitSha) {
    throw "标签 $tag 指向 $tagCommit，但 -Commit ($Commit) 是 $commitSha。只能归档标签指向的那次提交。"
}
Write-Host "    标签 $tag -> $commitSha"

# ---------------------------------------------------------------------------
# 步骤 3：安装包必须真实存在，且产品版本与要归档的版本一致
# ---------------------------------------------------------------------------
if (-not (Test-Path $exeSrc)) { throw "找不到 dist\NovaLinkLumen-WinUI-Setup-$Version.exe（也没有旧的 dist\NovaLinkLumen-Setup.exe）。先跑 scripts\build.ps1 生成安装包。" }
$info = [System.Diagnostics.FileVersionInfo]::GetVersionInfo($exeSrc)
$fileVer = "$($info.FileMajorPart).$($info.FileMinorPart).$($info.FileBuildPart)"
if ($fileVer -ne $Version) {
    throw "$exeName 的产品版本是 $fileVer，与要归档的 $Version 不一致。拒绝把新版安装包归档成旧版本。"
}
Write-Host "    安装包版本 $fileVer 一致"

# ---------------------------------------------------------------------------
# 步骤 4：幂等检查（已归档且一致就跳过，不一致绝不覆盖）
# ---------------------------------------------------------------------------
$exeSha = Get-Sha256 $exeSrc
if (Test-Path $manifestPath) {
    $existing = [System.IO.File]::ReadAllText($manifestPath) | ConvertFrom-Json
    if ($existing.installer.sha256 -eq $exeSha) {
        Write-Host "archives/$Version 已归档且校验一致，跳过。" -ForegroundColor Green
        exit 0
    }
    Write-Host "archives/$Version/MANIFEST.json 已存在，但安装包 sha256 与记录不一致：" -ForegroundColor Red
    Write-Host ("    记录 {0}" -f $existing.installer.sha256) -ForegroundColor Red
    Write-Host ("    当前 {0}" -f $exeSha) -ForegroundColor Red
    Write-Host "归档不可覆盖。要重新归档请先人工确认并删除 archives/$Version 目录。" -ForegroundColor Red
    exit 2
}

if (-not (Test-Path $releaseSrc)) { throw "找不到 $releaseSrc。先跑 scripts\build.ps1。" }

if ($CheckOnly) {
    Write-Host "";
    Write-Host "CheckOnly：校验通过，可以归档（未复制任何文件）。" -ForegroundColor Green
    Write-Host ("    exe   {0}  B  {1}" -f (Get-Item $exeSrc).Length, $exeSha)
    exit 0
}

# ---------------------------------------------------------------------------
# 步骤 5：复制安装包 + 全量快照 dist/release
# ---------------------------------------------------------------------------
if (-not (Test-Path $archDir)) { New-Item -ItemType Directory -Path $archDir -Force | Out-Null }
$exeDst = Join-Path $archDir $exeName
Copy-Item -LiteralPath $exeSrc -Destination $exeDst -Force
Write-Host "    安装包已复制"

$releaseDst = Join-Path $archDir "release"
& robocopy $releaseSrc $releaseDst /MIR /NFL /NDL /NJH /NJS /R:2 /W:2 | Out-Null
if ($LASTEXITCODE -ge 8) { throw "robocopy 复制 dist/release 失败，退出码 $LASTEXITCODE。" }
Write-Host "    release 快照已同步"

# ---------------------------------------------------------------------------
# 步骤 6：源码快照（git archive）
# ---------------------------------------------------------------------------
$sourceName = "source-$Version.tar.gz"
$sourceDst = Join-Path $archDir $sourceName
$sourceSha = ""
& git -C $RepoRoot archive --format=tar.gz -o $sourceDst $tag 2>&1 | Out-Null
if ($LASTEXITCODE -ne 0) {
    Write-Host "    警告：git archive 失败，源码快照缺失（其余归档照常）。" -ForegroundColor Yellow
    $sourceName = ""
}
else {
    $sourceSha = Get-Sha256 $sourceDst
    Write-Host "    源码快照已生成"
}

# ---------------------------------------------------------------------------
# 步骤 7：SHA256SUMS（安装包 + 源码快照 + release 全量）
# ---------------------------------------------------------------------------
$releaseFiles = @(Get-ChildItem -LiteralPath $releaseDst -Recurse -File | Sort-Object FullName)
$lines = New-Object System.Collections.Generic.List[string]
$lines.Add(("{0}  {1}" -f $exeSha, $exeName))
if ($sourceName) { $lines.Add(("{0}  {1}" -f $sourceSha, $sourceName)) }

$fingerprintLines = New-Object System.Collections.Generic.List[string]
$totalBytes = 0
foreach ($f in $releaseFiles) {
    $rel = $f.FullName.Substring($releaseDst.Length + 1) -replace "\\", "/"
    $sha = Get-Sha256 $f.FullName
    $lines.Add(("{0}  release/{1}" -f $sha, $rel))
    $fingerprintLines.Add(("{0}`t{1}`t{2}" -f $rel, $f.Length, $sha))
    $totalBytes += $f.Length
}

$sumsPath = Join-Path $archDir "SHA256SUMS"
$utf8NoBom = New-Object System.Text.UTF8Encoding($false)
[System.IO.File]::WriteAllText($sumsPath, (($lines -join "`n") + "`n"), $utf8NoBom)

# 整体指纹：把 (相对路径, 长度, sha256) 排序后拼起来再取 hash，单值即可验 541 个文件。
$fingerprintLines.Sort([System.StringComparer]::Ordinal)
$fingerprintText = ($fingerprintLines -join "`n") + "`n"
$hashBytes = [System.Security.Cryptography.SHA256]::Create().ComputeHash([System.Text.Encoding]::UTF8.GetBytes($fingerprintText))
$fingerprint = ([System.BitConverter]::ToString($hashBytes) -replace "-", "")
Write-Host ("    SHA256SUMS 已写入（{0} 行）" -f $lines.Count)

# ---------------------------------------------------------------------------
# 步骤 8：最后写 MANIFEST.json —— 本文件存在即代表归档完成
# ---------------------------------------------------------------------------
$dotnetSdk = "unknown"
try { $dotnetSdk = (& dotnet --version 2>$null | Select-Object -First 1) } catch { }
if (-not $dotnetSdk) { $dotnetSdk = "unknown" }

$nsisVersion = "unknown"
try {
    $makensis = Get-Command makensis.exe -ErrorAction SilentlyContinue
    if ($makensis) { $nsisVersion = (& $makensis.Source -VERSION 2>$null | Select-Object -First 1) }
    elseif ($env:NOVALINK_NSIS -and (Test-Path $env:NOVALINK_NSIS)) {
        $nsisVersion = (& $env:NOVALINK_NSIS -VERSION 2>$null | Select-Object -First 1)
    }
} catch { }
if (-not $nsisVersion) { $nsisVersion = "unknown" }

$manifest = [ordered]@{
    schema = 1;
    version = $Version;
    archivedAtUtc = (Get-Date).ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ssZ");
    gitTag = $tag;
    gitCommit = $commitSha;
    installer = [ordered]@{
        name = $exeName;
        bytes = (Get-Item $exeDst).Length;
        sha256 = $exeSha
    };
    sourceArchive = [ordered]@{
        name = $sourceName;
        sha256 = $sourceSha
    };
    releaseTree = [ordered]@{
        files = $releaseFiles.Count;
        totalBytes = $totalBytes;
        fingerprintSha256 = $fingerprint
    };
    buildHost = [ordered]@{
        dotnetSdk = $dotnetSdk;
        rid = "win-x64";
        nsis = $nsisVersion;
        platform = "x64"
    };
    githubRelease = "https://github.com/Eric8844/novalink-lumen-releases/releases/tag/v$Version";
    notes = "由 scripts/archive-release.ps1 生成；本文件存在即表示归档完成"
}

$json = $manifest | ConvertTo-Json -Depth 6
# MANIFEST.json 的最后一行必须是换行，diff 才干净。
[System.IO.File]::WriteAllText($manifestPath, ($json -replace "`r`n", "`n") + "`n", $utf8NoBom)
Write-Host "MANIFEST.json 已写入" -ForegroundColor Green

# ---------------------------------------------------------------------------
# 步骤 9：可选镜像到外部盘
# ---------------------------------------------------------------------------
if ($Mirror) {
    if (-not (Test-Path $Mirror)) {
        Write-Host "    警告：镜像目标 $Mirror 不存在，跳过镜像。" -ForegroundColor Yellow
    }
    else {
        $mirrorDir = Join-Path $Mirror "v$Version"
        & robocopy $archDir $mirrorDir /MIR /NFL /NDL /NJH /NJS /R:1 /W:1 | Out-Null
        if ($LASTEXITCODE -ge 8) { Write-Host "    警告：镜像失败，退出码 $LASTEXITCODE（本地归档已完整）。" -ForegroundColor Yellow }
        else { Write-Host "    已镜像到 $mirrorDir" }
    }
}

Write-Host ""
Write-Host "归档完成：archives/$Version" -ForegroundColor Green
Write-Host ("    exe     {0} B  {1}" -f (Get-Item $exeDst).Length, $exeSha)
Write-Host ("    release {0} 个文件，指纹 {1}" -f $releaseFiles.Count, $fingerprint)
Write-Host "下一步：git add archives/$Version/MANIFEST.json archives/$Version/SHA256SUMS，然后 bump-version.ps1。" -ForegroundColor Green
