@echo off
net session >nul 2>&1
if not %errorLevel% == 0 (
    echo Requesting administrator privileges...
    powershell -NoProfile -Command "Start-Process -FilePath '%~f0' -Verb RunAs"
    exit /b
)
echo Running emergency keyboard recovery...
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0recover-keyboard.ps1"
echo.
echo Done. If the keyboard still does not work, unplug and replug the USB receiver.
pause
