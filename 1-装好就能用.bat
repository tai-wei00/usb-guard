@echo off
setlocal
title USB Guard v2.1.0 Setup
net session >nul 2>&1
if errorlevel 1 (
  powershell -NoProfile -Command "Start-Process -FilePath '%~f0' -Verb RunAs"
  exit /b
)
echo USB Guard v2.1.0 - Build 2026-11-20
echo Existing entries are preserved during upgrade. No automatic trust is added.
echo First-time setup requires explicit Y confirmation for each device instance.
echo Invalid candidate version or hash will block EXE replacement.
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0install.ps1"
if errorlevel 1 (
  echo [X] Setup is incomplete or partially complete. Review the failed items above.
  pause
  exit /b 1
)
echo [OK] Setup and verification completed.
pause
exit /b 0