# 构建并打包 NovaLink Lumen
# 用法：powershell -ExecutionPolicy Bypass -File scripts/build.ps1

$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
Set-Location $root

Write-Host "==> dotnet publish" -ForegroundColor Cyan
dotnet publish "src/NovaLink.Lumen/NovaLink.Lumen.csproj" `
  -c Release -r win-x64 --self-contained true -o "dist/release"
if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed" }

$makensis = Get-Command makensis -ErrorAction SilentlyContinue | Select-Object -First 1 -ExpandProperty Source
if (-not $makensis) {
  $candidates = @(
    "$env:ProgramFiles\NSIS\makensis.exe",
    "${env:ProgramFiles(x86)}\NSIS\makensis.exe",
    "$root\tools\nsis\Bin\makensis.exe"
  )
  $makensis = $candidates | Where-Object { Test-Path $_ } | Select-Object -First 1
}
if (-not $makensis) {
  throw "未找到 makensis.exe。请安装 NSIS 3.0.4+，或把 NSIS 放到 tools/nsis/Bin。"
}

Write-Host "==> makensis" -ForegroundColor Cyan
& $makensis "installer/installer.nsi"
if ($LASTEXITCODE -ne 0) { throw "makensis failed" }

$setup = Join-Path $root "dist/NovaLinkLumen-Setup.exe"
Write-Host "==> done: $setup" -ForegroundColor Green
Get-Item $setup | Select-Object FullName, Length, LastWriteTime
Get-FileHash $setup -Algorithm SHA256 | Select-Object Hash
