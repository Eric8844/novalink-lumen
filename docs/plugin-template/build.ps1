<#
.SYNOPSIS
    把插件编译并打包成 .lumenplugin。

.DESCRIPTION
    产出 dist\<id>.lumenplugin（zip 格式）：
      plugin.json + <id>.dll + <id>.pri + 私有依赖程序集。

    宿主共享的程序集（Microsoft.WinUI、Microsoft.Windows.*、System.*、NovaLink.Lumen.*）
    一律不打包，由 AssemblyLoadContext 从宿主解析，避免版本冲突。

.NOTES
    本文件必须以 UTF-8 with BOM 保存，否则 Windows PowerShell 5.1 会把中文读成乱码。

.EXAMPLE
    .\build.ps1
    .\build.ps1 -Configuration Debug -NoPack
#>
[CmdletBinding()]
param(
    [string]$Configuration = 'Release',
    [switch]$NoPack
)

$ErrorActionPreference = 'Stop'
$here = Split-Path -Parent $MyInvocation.MyCommand.Path
$project = Join-Path $here 'Hello.World.csproj'
$pluginJson = Get-Content (Join-Path $here 'plugin.json') -Raw -Encoding UTF8 | ConvertFrom-Json
$id = $pluginJson.id
$assembly = $pluginJson.entry.assembly

Write-Host "[1/3] 编译 $assembly ($Configuration, win-x64)…"
& dotnet build $project -c $Configuration -p:Platform=x64 --nologo
if ($LASTEXITCODE -ne 0) { throw "编译失败。" }

# 注意：带 -p:Platform=x64 时输出目录会多一层 x64。
$outDir = Join-Path $here "bin\x64\$Configuration\net10.0-windows10.0.19041.0\win-x64"
foreach ($required in @($assembly, ($assembly -replace '\.dll$', '.pri'), 'plugin.json')) {
    if (-not (Test-Path (Join-Path $outDir $required))) {
        throw "缺少产物 $required —— 检查 csproj 里的 DisableEmbeddedXbf=false 与 Content 项。"
    }
}
Write-Host "[2/3] 产物齐全：$outDir"

if ($NoPack) {
    Write-Host "已跳过打包（-NoPack）。"
    return
}

Write-Host "[3/3] 打包 $id.lumenplugin…"
$dist = Join-Path $here 'dist'
New-Item -ItemType Directory -Force -Path $dist | Out-Null
$stage = Join-Path $dist "stage-$id"
if (Test-Path $stage) { Remove-Item $stage -Recurse -Force }
New-Item -ItemType Directory -Force -Path $stage | Out-Null

$shared = @(
    'Microsoft.WinUI.dll', 'Microsoft.Windows.SDK.NET.dll', 'Microsoft.UI.Xaml.dll',
    'WinRT.Runtime.dll', 'Microsoft.Graphics.Win2D.dll', 'DynamicXaml.WinUI.dll',
    'NovaLink.Lumen.Core.dll', 'NovaLink.Lumen.Services.dll', 'NovaLink.Lumen.dll'
)

Copy-Item (Join-Path $outDir $assembly) $stage
Copy-Item (Join-Path $outDir ($assembly -replace '\.dll$', '.pri')) $stage -ErrorAction SilentlyContinue
Copy-Item (Join-Path $here 'plugin.json') $stage

Get-ChildItem $outDir -Filter *.dll | Where-Object {
    $_.Name -ne $assembly -and $shared -notcontains $_.Name
} | ForEach-Object {
    if (-not $_.Name.StartsWith('System.') -and -not $_.Name.StartsWith('Microsoft.')) {
        Copy-Item $_.FullName $stage
    }
}

$zip = Join-Path $dist "$id.lumenplugin"
# Compress-Archive 只认 .zip，先压到临时 zip 再改名。
$tmpZip = Join-Path $dist "$id.zip"
foreach ($stale in @($zip, $tmpZip)) { if (Test-Path $stale) { Remove-Item $stale -Force } }
Compress-Archive -Path (Join-Path $stage '*') -DestinationPath $tmpZip -CompressionLevel Optimal
Move-Item $tmpZip $zip -Force
Remove-Item $stage -Recurse -Force

$size = '{0:N0} KB' -f ((Get-Item $zip).Length / 1KB)
Write-Host "完成：$zip ($size)"
Write-Host "安装：解压到 %LOCALAPPDATA%\NovaLink Lumen\LauncherData\plugins\$id\ 后重启启动器。"
