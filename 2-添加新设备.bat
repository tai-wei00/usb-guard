@echo off
setlocal
cd /d "%~dp0"
net session >nul 2>&1
if errorlevel 1 (
  powershell -NoProfile -Command "Start-Process -FilePath '%~f0' -Verb RunAs"
  exit /b
)
echo Review every full instance ID. Enter Y only for the device you intend to trust.
echo USB lock stays enabled. Cancellation or save failure leaves the list unchanged.
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0install.ps1" -AddDevice
if errorlevel 1 (
  echo [X] Add-device operation failed or was cancelled. The list was not changed.
  pause
  exit /b 1
)
echo [OK] Only explicitly confirmed and successfully saved entries were applied.
pause
exit /b 0