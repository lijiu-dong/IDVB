@echo off
chcp 65001 >nul
setlocal EnableExtensions

rem Start the real overlay_game test window with the pipe consumed by RealCLI.
set "ROOT=%~dp0"
set "PIPE=IDVB.OverlayGame"
set "OVERLAY_EXE=%ROOT%Tools\overlay_game\build\Release\dwrg.exe"

if not exist "%OVERLAY_EXE%" set "OVERLAY_EXE=%ROOT%Tools\overlay_game\build\Debug\dwrg.exe"
if not exist "%OVERLAY_EXE%" set "OVERLAY_EXE=%ROOT%Tools\overlay_game\dwrg.exe"

if not exist "%OVERLAY_EXE%" (
    where cmake >nul 2>nul
    if errorlevel 1 (
        echo 未找到地图模拟器 dwrg.exe，且本机无法使用 CMake。
        echo 请安装 CMake 和 C++ 编译工具，或手动构建 Tools\overlay_game。
        exit /b 2
    )
    echo 未找到地图模拟器，正在构建 Tools\overlay_game 的 Release 版本……
    cmake -S "%ROOT%Tools\overlay_game" -B "%ROOT%Tools\overlay_game\build" -A x64
    if errorlevel 1 exit /b 2
    cmake --build "%ROOT%Tools\overlay_game\build" --config Release --parallel 2
    if errorlevel 1 exit /b 2
    set "OVERLAY_EXE=%ROOT%Tools\overlay_game\build\Release\dwrg.exe"
)

if not exist "%OVERLAY_EXE%" (
    echo 地图模拟器构建结束，但未找到 dwrg.exe。
    exit /b 2
)

for %%I in ("%OVERLAY_EXE%") do set "OVERLAY_DIR=%%~dpI"
echo 正在启动地图模拟器，控制管道：%PIPE%
start "overlay_game" /D "%OVERLAY_DIR%" "%OVERLAY_EXE%" --pipe-name "%PIPE%"
exit /b 0
