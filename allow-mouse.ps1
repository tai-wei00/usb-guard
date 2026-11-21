#Requires -RunAsAdministrator
# 兼容既有入口：只执行可回滚的输入设备恢复，不添加任何允许设备 ID。
& (Join-Path $PSScriptRoot 'recover-input.ps1') @args
exit $LASTEXITCODE