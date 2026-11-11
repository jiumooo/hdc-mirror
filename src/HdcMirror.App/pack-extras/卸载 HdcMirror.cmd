@echo off
chcp 65001 >nul
setlocal
rem 复制到临时目录再运行：卸载会删掉当前目录，脚本不能在自己所在目录里被删。
set "SRC=%~dp0"
set "TMPP=%TEMP%\HdcMirror-uninstall.ps1"
copy /y "%SRC%uninstall.ps1" "%TMPP%" >nul
powershell.exe -NoProfile -ExecutionPolicy Bypass -WindowStyle Normal -File "%TMPP%"
del /q "%TMPP%" >nul 2>&1
endlocal
