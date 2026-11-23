@echo off
setlocal
cd /d "%~dp0"
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0Start-Apollo.ps1" %*
set "APOLLO_LAUNCH_EXIT=%errorlevel%"
if /I "%~1"=="-ConfigurationOnly" exit /b %APOLLO_LAUNCH_EXIT%
if not "%APOLLO_LAUNCH_EXIT%"=="0" (
  echo.
  echo Apollo launcher could not complete. Review the error above.
)
echo.
pause
