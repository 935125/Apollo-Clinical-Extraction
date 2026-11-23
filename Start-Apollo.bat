@echo off
setlocal
cd /d "%~dp0"
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0Start-Apollo.ps1"
if errorlevel 1 (
  echo.
  echo Apollo launcher could not complete. Review the error above.
)
echo.
pause
