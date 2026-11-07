#requires -version 5.1
<#
.SYNOPSIS
    按白名单把启动器版本号从旧版本统一改到新版本。

.DESCRIPTION
    项目没有单一版本真源：12 处代码硬编码 + 若干文档字符串。本脚本用
    “文件 + 唯一正则 + 期望旧值”的白名单逐条改写，任何一条对不上就立即中止，
    不会留下改了一半的仓库。

    白名单就是唯一权威清单。新增版本号位置时必须同时做三件事：
      1. 把新位置加进下面的 $sites；
      2. 把位置数量写进 AGENTS.md 的硬约束；
      3. 用 -DryRun 跑一遍，确认“遗留版本号”检查为 0 处。

    改完只打印 git diff --stat，不自动 commit、不自动打 tag。

.EXAMPLE
    powershell -ExecutionPolicy Bypass -File scripts\bump-version.ps1 -From 1.4.1 -To 1.4.2

.EXAMPLE
    powershell -ExecutionPolicy Bypass -File scripts\bump-version.ps1 -To 1.4.2 -DryRun

.NOTES
    本文件含中文，必须保存为 UTF-8 BOM，否则 Windows PowerShell 5.1 会报 ParserError。
#>
[CmdletBinding()]
param(
    # 旧版本号；省略时从 csproj 的 <Version> 读取。
    [string]$From,

    # 新版本号，形如 1.4.2。
    [Parameter(Mandatory = $true)][string]$To,

    # 只检查不写文件。
    [switch]$DryRun,

    # 仓库根目录；省略时取脚本所在目录的上一级。
    [string]$RepoRoot
)

$ErrorActionPreference = "Stop"

if (-not $RepoRoot) { $RepoRoot = Split-Path -Parent $PSScriptRoot }
Set-Location $RepoRoot

function Read-TextFile {
    param([string]$Path)

    $bytes = [System.IO.File]::ReadAllBytes($Path)
    $hasBom = ($bytes.Length -ge 3 -and $bytes[0] -eq 0xEF -and $bytes[1] -eq 0xBB -and $bytes[2] -eq 0xBF)
    $text = [System.Text.Encoding]::UTF8.GetString($bytes)
    if ($hasBom) { $text = $text.Substring(1) }
    return @{ Text = $text; HasBom = $hasBom }
}

function Write-TextFile {
    param([string]$Path, [string]$Text, [bool]$HasBom)

    # 保留原文件的 BOM 状态：.ps1/.nsi 需要 BOM，.cs/.xaml/.md 不需要。
    $encoding = New-Object System.Text.UTF8Encoding($HasBom)
    [System.IO.File]::WriteAllText($Path, $Text, $encoding)
}

if (-not $From) {
    $csproj = Read-TextFile (Join-Path $RepoRoot "src/NovaLink.Lumen/NovaLink.Lumen.csproj")
    $m = [regex]::Match($csproj.Text, "<Version>(\d+\.\d+\.\d+)")
    if (-not $m.Success) { throw "没能从 csproj 读到 <Version>，请用 -From 显式指定旧版本号。" }
    $From = $m.Groups[1].Value
}

if ($From -notmatch "^\d+\.\d+\.\d+$") { throw "旧版本号格式不对：$From" }
if ($To -notmatch "^\d+\.\d+\.\d+$") { throw "新版本号格式不对：$To" }
if ($From -eq $To) { throw "旧版本号与新版本号相同：$From" }

Write-Host "==> 版本号：$From -> $To" -ForegroundColor Cyan

# ---------------------------------------------------------------------------
# 白名单：仓库相对路径 + 只匹配一处版本号的正则，版本号必须放在名为 v 的分组里。
#   Mode  = "to"   新值取 -To（绝大多数位置）
#           "from" 新值取 -From（“上一版”字样，例如升级前的数据备份目录名）
#   Check = "equal"  旧值必须正好等于 -From（防止漏改/改错版本）
#           "not-to" 只要求旧值不是 -To（这类位置记的是上一版，不能按 -From 校验）
#   Count = 该文件里必须出现的处数，默认 1。
# ---------------------------------------------------------------------------
$sites = @(
    # --- csproj：程序集版本 / 文件版本 / 产品版本 / 通道版本 ---
    @{ Path = "src/NovaLink.Lumen/NovaLink.Lumen.csproj"; Pattern = '<AssemblyVersion>(?<v>\d+\.\d+\.\d+)\.0</AssemblyVersion>'; Mode = "to"; Check = "equal"; Note = "AssemblyVersion" },
    @{ Path = "src/NovaLink.Lumen/NovaLink.Lumen.csproj"; Pattern = '<FileVersion>(?<v>\d+\.\d+\.\d+)\.0</FileVersion>'; Mode = "to"; Check = "equal"; Note = "FileVersion" },
    @{ Path = "src/NovaLink.Lumen/NovaLink.Lumen.csproj"; Pattern = '<ApplicationVersion>(?<v>\d+\.\d+\.\d+)\.0</ApplicationVersion>'; Mode = "to"; Check = "equal"; Note = "ApplicationVersion" },
    # Version / InformationalVersion 可以带 -preview.N 后缀：只替换数值，后缀原样保留（1.5.0-preview.1 -> 1.6.0-preview.1）。
    @{ Path = "src/NovaLink.Lumen/NovaLink.Lumen.csproj"; Pattern = '<Version>(?<v>\d+\.\d+\.\d+)(?:-[^<]*)?</Version>'; Mode = "to"; Check = "equal"; Note = "Version" },
    @{ Path = "src/NovaLink.Lumen/NovaLink.Lumen.csproj"; Pattern = '<InformationalVersion>(?<v>\d+\.\d+\.\d+)(?:-[^<]*)?</InformationalVersion>'; Mode = "to"; Check = "equal"; Note = "InformationalVersion" },

    # --- 安装器：产品版本 / 显示版本 / 输出文件名 / 数据备份目录名 ---
    @{ Path = "installer/installer.nsi"; Pattern = '!define APP_VERSION "(?<v>\d+\.\d+\.\d+)"'; Mode = "to"; Check = "equal"; Note = "APP_VERSION" },
    @{ Path = "installer/installer.nsi"; Pattern = '!define APP_VERSION_DISPLAY "(?<v>\d+\.\d+\.\d+)(?: Preview)?"'; Mode = "to"; Check = "equal"; Note = "显示用版本号（测试版带 Preview，正式版没有）" },
    @{ Path = "installer/installer.nsi"; Pattern = 'VIProductVersion "(?<v>\d+\.\d+\.\d+)\.0"'; Mode = "to"; Check = "equal"; Note = "VIProductVersion" },
    @{ Path = "installer/installer.nsi"; Pattern = 'NovaLinkLumen-WinUI-Setup-(?<v>\d+\.\d+\.\d+)\.exe'; Mode = "to"; Check = "equal"; Note = "安装包输出文件名" },
    @{ Path = "installer/installer.nsi"; Pattern = 'Data_Backup_v(?<v>\d+\.\d+\.\d+)'; Mode = "from"; Check = "not-to"; Note = "升级前数据备份目录名（记的是上一版）" },
    @{ Path = "installer/installer.nsi"; Pattern = '/DAPP_VERSION=(?<v>\d+\.\d+\.\d+)'; Mode = "to"; Check = "equal"; Note = "文件头注释里的命令行覆盖示例" },

    # --- Services：通道常量是数值版本号的唯一真源（UserAgent 与 launcher_version 都读它） ---
    @{ Path = "src/NovaLink.Lumen.Services/Updates/LauncherChannel.cs"; Pattern = 'BaseVersion = "(?<v>\d+\.\d+\.\d+)"'; Mode = "to"; Check = "equal"; Note = "通道常量 BaseVersion" },
    @{ Path = "src/NovaLink.Lumen.Services/Updates/UpdateService.cs"; Pattern = 'NovaLinkLumen-WinUI-Setup-(?<v>\d+\.\d+\.\d+)\.exe'; Mode = "to"; Check = "equal"; Note = "资产名过滤的注释示例" },
    @{ Path = "src/NovaLink.Lumen.Services/Storage/VersionSourceDetector.cs"; Pattern = 'TargetVersion = "(?<v>\d+\.\d+\.\d+)"'; Mode = "to"; Check = "equal"; Note = "数据源版本探测目标版本（必须与 BaseVersion 一致）" },

    # --- WinUI：侧栏 / 启动画面 / 设置页 ---
    # ShellPage 的版本 chip 由 UI 负责人改写，形态可能带 " Preview" 后缀，所以这里不校验旧值（只要求不等于新值）。
    @{ Path = "src/NovaLink.Lumen/Views/ShellPage.xaml"; Pattern = 'Text="(?<v>\d+\.\d+\.\d+)(?: Preview)?" FontSize="9\.5"'; Mode = "to"; Note = "侧栏版本号" },
    @{ Path = "src/NovaLink.Lumen/MainWindow.xaml"; Pattern = 'X64 · (?<v>\d+\.\d+\.\d+)'; Mode = "to"; Check = "equal"; Note = "启动画面版本号" },
    @{ Path = "src/NovaLink.Lumen/Views/Pages/SettingsPage.xaml"; Pattern = '备份到 Data_Backup_v(?<v>\d+\.\d+\.\d+)'; Mode = "from"; Check = "not-to"; Note = "版本策略里的备份目录名（上一版）" }

    # --- 关于页版本号不在这里：SettingsPage.xaml.cs 读 LauncherChannelInfo.DisplayVersion ---
    # --- 文档也不在这里逐条列：README / AGENTS / docs\PUBLISHING.md 走下面的整文件替换 ---
)

# 文档侧不做逐处白名单，改为整文件替换（版本号在 prose 里的出现次数会随文档改写变化）。
# CHANGELOG 是叙述性内容，整文件替换会把历史条目改错，必须人工维护。
$docFiles = @("README.md", "AGENTS.md", "docs/PUBLISHING.md")

# ---------------------------------------------------------------------------
# 第一遍：只校验、不改文件。任何一处对不上都在这里中止。
# ---------------------------------------------------------------------------
$files = @{}
$edits = @()

foreach ($site in $sites) {
    $full = Join-Path $RepoRoot $site.Path
    if (-not (Test-Path $full)) { throw "白名单里的文件不存在：$($site.Path)" }

    if (-not $files.ContainsKey($full)) { $files[$full] = Read-TextFile $full }
    $text = $files[$full].Text

    $found = [regex]::Matches($text, $site.Pattern)
    $expected = 1
    if ($site.ContainsKey("Count")) { $expected = $site.Count }
    if ($found.Count -ne $expected) {
        throw ("版本号位置对不上：{0} 期望 {1} 处，实际 {2} 处（{3}）。位置变了就先更新本脚本白名单。" -f $site.Path, $expected, $found.Count, $site.Note)
    }

    $newValue = $To
    if ($site.Mode -eq "from") { $newValue = $From }

    foreach ($m in $found) {
        $group = $m.Groups["v"]
        $old = $group.Value

        if ($site.Check -eq "equal") {
            if ($old -ne $From) {
                throw ("版本号不是预期的旧版本：{0} 里是 {1}，期望 {2}（{3}）。仓库是不是已经改过一半？" -f $site.Path, $old, $From, $site.Note)
            }
        }
        else {
            if ($old -eq $To) { throw ("版本号已经是新版本了：{0}（{1}）。不要重复 bump。" -f $site.Path, $site.Note) }
        }

        if ($old -eq $newValue) { continue }

        $lineNo = 1 + ([regex]::Matches($text.Substring(0, $group.Index), "\n")).Count
        $edits += [pscustomobject]@{
            File = $full; Path = $site.Path; Index = $group.Index; Length = $group.Length;
            Old = $old; New = $newValue; Line = $lineNo
        }
    }
}

# ---------------------------------------------------------------------------
# 文档整文件替换：把 README / AGENTS / PUBLISHING 里所有独立的旧版本号换掉。
# 正则 ((?<![\d.]) + 旧版本 + (?![\d.])) 能命中 v1.4.1、**V1.4.1** 这类写法，
# 但不会误伤 1.4.10、11.4.1、1.4.0。
# ---------------------------------------------------------------------------
$docPattern = "(?<![\d.])" + [regex]::Escape($From) + "(?!\d)(?!\.\d)"
$docSkipped = 0

foreach ($docPath in $docFiles) {
    $full = Join-Path $RepoRoot $docPath
    if (-not (Test-Path $full)) { throw "文档不存在：$docPath" }

    if (-not $files.ContainsKey($full)) { $files[$full] = Read-TextFile $full }
    $text = $files[$full].Text

    foreach ($m in [regex]::Matches($text, $docPattern)) {
        $lineStart = $text.LastIndexOf([char]10, $m.Index) + 1
        $lineEnd = $text.IndexOf([char]10, $m.Index)
        if ($lineEnd -lt 0) { $lineEnd = $text.Length }
        $lineText = $text.Substring($lineStart, $lineEnd - $lineStart)

        # 命令示例（-From 1.4.1 -To 1.4.2）里的旧版本号不替换，否则示例会变成 -From 1.4.2 -To 1.4.2
        if ($lineText -match "-(From|To)\s+\d+\.\d+") { $docSkipped++; continue }

        $lineNo = 1 + ([regex]::Matches($text.Substring(0, $m.Index), "\n")).Count
        $edits += [pscustomobject]@{
            File = $full; Path = $docPath; Index = $m.Index; Length = $m.Length;
            Old = $m.Value; New = $To; Line = $lineNo
        }
    }
}

if ($edits.Count -eq 0) {
    Write-Host "没有需要改写的位置，仓库已经是 $To。" -ForegroundColor Yellow
    return
}

Write-Host "==> 共 $($edits.Count) 处（$($sites.Count) 条代码白名单 + $($docFiles.Count) 份文档整文件替换）" -ForegroundColor Cyan
foreach ($e in $edits) {
    Write-Host ("  {0}:{1}  {2} -> {3}" -f $e.Path, $e.Line, $e.Old, $e.New)
}

if ($DryRun) {
    Write-Host "";
    Write-Host "DryRun：没有写任何文件。" -ForegroundColor Yellow
    return
}

# ---------------------------------------------------------------------------
# 第二遍：按索引从后往前替换，避免改写过程中索引偏移。
# ---------------------------------------------------------------------------
foreach ($file in ($edits | Select-Object -ExpandProperty File -Unique)) {
    $text = $files[$file].Text
    $fileEdits = $edits | Where-Object { $_.File -eq $file } | Sort-Object -Property Index -Descending
    foreach ($e in $fileEdits) {
        $text = $text.Remove($e.Index, $e.Length).Insert($e.Index, $e.New)
    }
    Write-TextFile $file $text $files[$file].HasBom
}

# 遗留检查：白名单文件里若还残留旧版本号，说明有位置没进白名单。
$leftover = 0
foreach ($file in ($edits | Select-Object -ExpandProperty File -Unique)) {
    $text = Read-TextFile $file
    $leftoverMatches = [regex]::Matches($text.Text, "\b" + [regex]::Escape($From) + "\b")
    foreach ($m in $leftoverMatches) {
        $lineStart = $text.Text.LastIndexOf([char]10, $m.Index) + 1
        $lineEnd = $text.Text.IndexOf([char]10, $m.Index)
        if ($lineEnd -lt 0) { $lineEnd = $text.Text.Length }
        $lineText = $text.Text.Substring($lineStart, $lineEnd - $lineStart)
        if ($lineText -match "-(From|To)\s+\d+\.\d+") { continue }

        $lineNo = 1 + ([regex]::Matches($text.Text.Substring(0, $m.Index), "\n")).Count
        Write-Host ("  遗留：{0}:{1} 仍有 {2}" -f ($file.Replace($RepoRoot + [System.IO.Path]::DirectorySeparatorChar, "")), $lineNo, $From) -ForegroundColor Yellow
        $leftover++
    }
}
if ($leftover -gt 0) {
    Write-Host "上面 $leftover 处旧版本号不在白名单里，请确认是否需要手动处理（例如 CHANGELOG）。" -ForegroundColor Yellow
}

Write-Host ""
Write-Host "==> git diff --stat" -ForegroundColor Cyan
git --no-pager diff --stat
Write-Host ""
Write-Host "下一步：git diff 复核 -> powershell -File scripts\build.ps1（归档闸门会要求上一版已归档）" -ForegroundColor Green
