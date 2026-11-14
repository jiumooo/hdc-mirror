@echo off
setlocal EnableExtensions
title HdcMirror 安装前检查

echo.
echo ============================================================
echo    HdcMirror 安装前检查 —— .NET 8 桌面运行时
echo ============================================================
echo.

if "%HDCMIRROR_CHECK_SIMULATE_MISSING%"=="1" goto :simulate

:check
set "FOUND="
set "FOUNDVER="

rem ---- 1) 直接看运行时目录（最可靠，不依赖 PATH 与注册表布局）----
for /f "delims=" %%D in ('dir /b /ad "%ProgramFiles%\dotnet\shared\Microsoft.WindowsDesktop.App\8.*" 2^>nul') do (
    set "FOUND=1"
    set "FOUNDVER=%%D"
)

rem ---- 2) dotnet --list-runtimes 兜底（PATH 里的 dotnet 可能是旧版，但它仍能列出全部运行时）----
if not defined FOUND (
    where dotnet >nul 2>nul
    if not errorlevel 1 (
        for /f "tokens=2" %%V in ('dotnet --list-runtimes 2^>nul ^| findstr /C:"Microsoft.WindowsDesktop.App 8."') do (
            set "FOUND=1"
            set "FOUNDVER=%%V"
        )
    )
)

rem ---- 3) 注册表兜底（安装器写入位置不统一，32/64 两处都查）----
if not defined FOUND (
    reg query "HKLM\SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall" /s /f "Windows Desktop Runtime - 8." /d >nul 2>nul
    if not errorlevel 1 set "FOUND=1"
)
if not defined FOUND (
    reg query "HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall" /s /f "Windows Desktop Runtime - 8." /d >nul 2>nul
    if not errorlevel 1 set "FOUND=1"
)

if defined FOUND goto :ok

:missing
echo   [缺少] 未检测到 .NET 8 桌面运行时（Microsoft.WindowsDesktop.App 8.x）
echo.
echo   HdcMirror 采用框架依赖发布，安装与运行前必须先装这个运行时：
echo.
echo        .NET 8 Desktop Runtime  ^(x64^)
echo.
echo   官方下载页：
echo        https://dotnet.microsoft.com/download/dotnet/8.0
echo.
echo   在页面里选「.NET Desktop Runtime 8.0.x」→「Windows x64」下载安装即可。
echo   只装 ASP.NET Core 或 .NET Runtime 是不够的，必须是 Desktop Runtime。
echo.
if "%HDCMIRROR_CHECK_SIMULATE_MISSING%"=="1" goto :simulate
echo   按回车打开下载页 ^(也可以直接手动访问上面的地址^)……
pause >nul
start "" "https://dotnet.microsoft.com/download/dotnet/8.0"
echo.
echo   安装完成后回到本窗口，按任意键重新检查。
pause >nul
goto :check

:ok
echo   [通过] 已检测到 .NET 8 桌面运行时：%FOUNDVER%
echo.
if exist "%~dp0HdcMirror-win-Setup.exe" goto :askrun
echo   现在可以双击 HdcMirror-win-Setup.exe 安装了。
echo   ^(本目录里没找到 Setup.exe，请先把它和本文件放在同一目录^)
goto :done

:askrun
set "GO="
set /p "GO=现在启动安装程序吗？直接按回车 = 启动，输入 N 回车 = 退出： "
if /i "%GO%"=="N" goto :done
echo   正在启动 HdcMirror-win-Setup.exe ……
start "" "%~dp0HdcMirror-win-Setup.exe"
goto :done

:simulate
echo   [模拟模式] 已强制走「缺少运行时」分支，仅用于验证提示文案。
echo             不会打开浏览器、不等待按键。
echo.
echo   ---- 以下是用户实际会看到的内容 ----
echo   [缺少] 未检测到 .NET 8 桌面运行时（Microsoft.WindowsDesktop.App 8.x）
echo   HdcMirror 采用框架依赖发布，安装与运行前必须先装这个运行时：
echo        .NET 8 Desktop Runtime  ^(x64^)
echo   官方下载页： https://dotnet.microsoft.com/download/dotnet/8.0
echo   装完后重新双击本文件即可继续安装。
goto :done

:done
echo.
echo   检查结束。
endlocal