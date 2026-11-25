@echo off
setlocal
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0Run-Apollo-Postman-Tests.ps1" %*
exit /b %ERRORLEVEL%
