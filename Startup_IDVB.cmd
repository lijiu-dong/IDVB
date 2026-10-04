@echo off
setlocal EnableExtensions
set "ROOT=%~dp0"
set "IDVB_STARTUP_CMD=%~f0"
rem Elevate this launcher so process inspection also sees elevated installed apps.
net session >nul 2>&1
if errorlevel 1 (
    powershell -NoProfile -ExecutionPolicy Bypass -Command "try { $p = Start-Process -FilePath $env:IDVB_STARTUP_CMD -Verb RunAs -PassThru -ErrorAction Stop; $p.WaitForExit(); exit $p.ExitCode } catch { Write-Error $_; exit 2 }"
    exit /b
)
rem --isolated-dev-instance is always passed by the validated launcher.
rem Never select an old apphost by path priority or EXE timestamp.
powershell -NoProfile -ExecutionPolicy Bypass -File "%ROOT%Tools\DevelopmentBuild.ps1" -Mode Gui -RepositoryRoot "%ROOT%."
if errorlevel 1 (
    pause
    exit /b 2
)
exit /b 0
