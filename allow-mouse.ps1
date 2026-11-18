# 放行当前插着的鼠标/键盘：调用安装脚本的 -AddDevice。
# 安装脚本会把当前设备写进信任名单，并同步到 AllowInstanceIDs（v2 的实例级白名单）。
# 需管理员权限；由「修复-放行此鼠标.bat」以提权方式调用。
$ErrorActionPreference = 'Stop'

$setup = Join-Path $PSScriptRoot 'setup.ps1'
if (-not (Test-Path -LiteralPath $setup)) {
    Write-Host '[X] setup.ps1 not found. Run 1-install first.' -ForegroundColor Red
    exit 1
}

& $setup -AddDevice
if ($LASTEXITCODE -ne 0) {
    Write-Host '[X] AddDevice failed. See messages above.' -ForegroundColor Red
    exit 1
}

Write-Host '[*] Rescanning devices, please wait...' -ForegroundColor Cyan
try { pnputil /scan-devices | Out-Null } catch { }
Write-Host '[OK] Done. If the mouse still does not work, run recover-keyboard.bat.' -ForegroundColor Green
