$ErrorActionPreference = 'Stop'
# ASCII wrapper: invoke the Chinese-named installer without embedding Chinese in BAT files.
& (Join-Path $PSScriptRoot '程序文件\安装.ps1') @args
exit $LASTEXITCODE