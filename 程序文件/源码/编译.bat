@echo off
setlocal
for %%I in ("%~dp0..\..") do set "PROJECT_ROOT=%%~fI"
for %%P in ("%~dp0*.ps1") do set "PUBLISH_SCRIPT=%%~fP"
if not defined PUBLISH_SCRIPT (
  echo [X] Release check script is missing.
  exit /b 1
)
echo Building USB Guard v2.1.0 from sources.list.txt ...
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%PUBLISH_SCRIPT%" -Compile -ProjectRoot "%PROJECT_ROOT%"
if errorlevel 1 (
  echo [X] Build or release verification failed.
  exit /b 1
)
if not exist "%PROJECT_ROOT%\UsbGuard.next.exe" (
  echo [X] Candidate EXE was not generated.
  exit /b 1
)
echo [OK] Candidate EXE and build-manifest.json were generated and verified.
exit /b 0