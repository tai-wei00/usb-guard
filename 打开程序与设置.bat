@echo off
setlocal
for /d %%D in ("%~dp0*") do (
  if exist "%%~fD\UsbGuard.exe" (
    start "" "%%~fD\UsbGuard.exe" --show-main
    exit /b 0
  )
)
echo [X] UsbGuard.exe was not found. Run setup first.
exit /b 1