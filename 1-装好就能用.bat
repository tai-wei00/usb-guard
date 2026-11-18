@echo off
chcp 936 >nul
title USB 哨兵 - 加固版安装
net session >nul 2>&1
if errorlevel 1 (
  powershell -NoProfile -Command "Start-Process -FilePath '%~f0' -Verb RunAs"
  exit /b
)
echo ============================================
echo   USB 哨兵 · 加固版
echo ============================================
echo.
echo 正在安装，需要几十秒，请不要关闭窗口。
echo 原来的信任名单会保留，不会丢。
echo.
powershell -NoProfile -ExecutionPolicy Bypass -Command "& '%~dp0程序文件\安装.ps1'; if ($LASTEXITCODE -ne 0) { Write-Host '[X] 安装未成功完成，请看上面的红色报错，把报错发给我。'; Read-Host '按回车退出'; exit 1 } else { Write-Host '[OK] 装好了。右下角出现蓝色 U 图标就是已经保护上了。'; Read-Host '按回车退出' }"