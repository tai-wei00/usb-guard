@echo off
setlocal
title Apply verified USB Guard v2.1.0 fix
net session >nul 2>&1
if errorlevel 1 (
  powershell -NoProfile -Command "Start-Process -FilePath '%~f0' -Verb RunAs"
  exit /b
)
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0deploy-fix.ps1"
if errorlevel 1 (
  echo [X] Deployment failed. See .bugfix\usb-guard-fixes\deployment-result.json
  pause
  exit /b 1
)
echo [OK] Formal EXE version, hash and source manifest were verified after deployment.
echo No task, registry, whitelist or config changes were made by deployment.
echo The formal instance was not auto-started; file deployment is not claimed as active runtime.
pause
exit /b 0