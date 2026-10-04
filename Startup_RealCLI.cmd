@echo off
setlocal EnableExtensions
set "ROOT=%~dp0"
rem Attach to the same certified development deployment as Startup_IDVB.cmd.
powershell -NoProfile -ExecutionPolicy Bypass -File "%ROOT%Tools\DevelopmentBuild.ps1" -Mode Cli -RepositoryRoot "%ROOT%."
exit /b %ERRORLEVEL%
