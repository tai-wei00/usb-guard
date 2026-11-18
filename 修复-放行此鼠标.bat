@echo off
cd /d "%~dp0"
net session >nul 2>&1
if errorlevel 1 (
    echo Requesting administrator privileges...
    powershell -NoProfile -Command "Start-Process -FilePath '%~f0' -Verb RunAs"
    exit /b
)
echo Running USB mouse fix as administrator...
echo.
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0allow-mouse.ps1" > "%~dp0allow-mouse.log" 2>&1
echo.
echo ---- RESULT (saved to allow-mouse.log) ----
type "%~dp0allow-mouse.log"
echo.
echo Press any key to close.
pause >nul
