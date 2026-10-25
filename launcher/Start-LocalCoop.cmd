@echo off
setlocal
set "PSModulePath=%SystemRoot%\System32\WindowsPowerShell\v1.0\Modules;%ProgramFiles%\WindowsPowerShell\Modules"
"%SystemRoot%\System32\WindowsPowerShell\v1.0\powershell.exe" -NoLogo -NoProfile -STA -ExecutionPolicy Bypass -File "%~dp0Start-LocalCoop.ps1" %*
set "LaunchResult=%ERRORLEVEL%"
if not "%LaunchResult%"=="0" (
    echo.
    echo Launcher stopped. Please read the message above.
    pause
)
exit /b %LaunchResult%
