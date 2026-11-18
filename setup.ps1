# ASCII-named wrapper so .bat files never need to embed a Chinese path.
# PowerShell reads this file as UTF-8, so the Chinese directory name is safe here.
$installer = Join-Path $PSScriptRoot '程序文件\安装.ps1'
if (-not (Test-Path -LiteralPath $installer)) {
    Write-Host '[X] Installer not found:' $installer -ForegroundColor Red
    exit 1
}
& $installer @args
exit $LASTEXITCODE
