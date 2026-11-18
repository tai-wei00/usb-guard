
# ASCII wrapper: invokes the (Chinese-named) installer without putting Chinese in .bat files.
# 本 wrapper 位于 程序文件\ 内，目标 安装.ps1 与其同级——不要再拼一层"程序文件\"。
& (Join-Path $PSScriptRoot '安装.ps1') @args
exit $LASTEXITCODE
