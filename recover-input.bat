@echo off
net session >nul 2>&1
if not %errorLevel% == 0 (
    echo Requesting administrator privileges...
    powershell -NoProfile -Command "Start-Process -FilePath '%~f0' -Verb RunAs"
    exit /b
)
echo Running USB Guard input recovery...
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0recover-input.ps1"
echo.
echo Done. Keyboard and mouse should work now.
pause
