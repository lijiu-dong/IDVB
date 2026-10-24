@echo off
setlocal EnableExtensions DisableDelayedExpansion
set "IDVB_STARTUP_ROOT=%~dp0"
set "IDVB_STARTUP_CMD=%~f0"
set "IDVB_PWSH="
rem Explorer does not inherit Codex's runtime PATH. Find a PowerShell 7 bootstrap.
if exist "%USERPROFILE%\.dotnet\tools\pwsh.exe" set "IDVB_PWSH=%USERPROFILE%\.dotnet\tools\pwsh.exe"
if not defined IDVB_PWSH if exist "%ProgramFiles%\PowerShell\7\pwsh.exe" set "IDVB_PWSH=%ProgramFiles%\PowerShell\7\pwsh.exe"
if not defined IDVB_PWSH if exist "%USERPROFILE%\.cache\codex-runtimes\codex-primary-runtime\dependencies\native\powershell\pwsh.exe" set "IDVB_PWSH=%USERPROFILE%\.cache\codex-runtimes\codex-primary-runtime\dependencies\native\powershell\pwsh.exe"
for /f "delims=" %%P in ('where.exe pwsh.exe 2^>nul') do if not defined IDVB_PWSH set "IDVB_PWSH=%%P"
if not defined IDVB_PWSH (
    echo [IDVB] PowerShell 7 was not found. Install pwsh before launching IDVB.
    pause
    exit /b 2
)
rem Select the newest available pwsh, elevate, then resolve the certified bin\dev.
rem No fixed build number, old-output fallback, or installed-instance redirect.
"%IDVB_PWSH%" -NoProfile -ExecutionPolicy Bypass -Command "$ErrorActionPreference = 'Stop'; try { $candidates = @($env:IDVB_PWSH; (Get-Command pwsh.exe -CommandType Application -All -ErrorAction SilentlyContinue).Source; (Get-ChildItem -Path (Join-Path $env:ProgramFiles 'PowerShell\*\pwsh.exe') -File -ErrorAction SilentlyContinue).FullName; (Join-Path $env:USERPROFILE '.cache\codex-runtimes\codex-primary-runtime\dependencies\native\powershell\pwsh.exe')); $latest = $candidates | Where-Object { $_ -and (Test-Path -LiteralPath $_ -PathType Leaf) } | Get-Item | Where-Object { $_.VersionInfo.ProductVersion -match '^\d+\.\d+\.\d+' } | Sort-Object { [version]([regex]::Match($_.VersionInfo.ProductVersion, '^\d+\.\d+\.\d+').Value) } -Descending | Select-Object -First 1 -ExpandProperty FullName; if (-not $latest) { throw 'No valid PowerShell 7 executable was found.' }; $identity = [Security.Principal.WindowsIdentity]::GetCurrent(); $principal = [Security.Principal.WindowsPrincipal]::new($identity); if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) { $p = Start-Process -FilePath $env:IDVB_STARTUP_CMD -WorkingDirectory $env:IDVB_STARTUP_ROOT -Verb RunAs -PassThru -ErrorAction Stop; $p.WaitForExit(); exit $p.ExitCode }; & $latest -NoProfile -ExecutionPolicy Bypass -File (Join-Path $env:IDVB_STARTUP_ROOT 'Tools\DevelopmentBuild.ps1') -Mode Gui -RepositoryRoot $env:IDVB_STARTUP_ROOT; exit $LASTEXITCODE } catch { Write-Error $_; exit 2 }"
set "IDVB_STARTUP_EXIT=%ERRORLEVEL%"
if not "%IDVB_STARTUP_EXIT%"=="0" pause
exit /b %IDVB_STARTUP_EXIT%
