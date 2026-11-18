@echo off
cd /d "%~dp0"
net session >nul 2>&1
if errorlevel 1 (
  powershell -NoProfile -Command "Start-Process -FilePath '%~f0' -Verb RunAs"
  exit /b
)
echo ============================================
echo   USB Guard - Add a new device
echo ============================================
echo.
echo The installer will list USB devices currently connected
echo that are not trusted yet, and ask you to confirm.
echo.
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0setup.ps1" -AddDevice
echo.
pause
