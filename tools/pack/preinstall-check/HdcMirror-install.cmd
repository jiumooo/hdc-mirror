@echo off
chcp 65001 >nul
title HdcMirror 安装
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0install.ps1"
echo.
pause