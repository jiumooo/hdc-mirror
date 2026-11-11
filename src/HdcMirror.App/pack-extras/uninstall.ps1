$ErrorActionPreference = 'SilentlyContinue'
$OutputEncoding = [System.Text.Encoding]::UTF8
[Console]::OutputEncoding = [System.Text.Encoding]::UTF8

# HdcMirror 卸载脚本
# 由「卸载 HdcMirror.cmd」双击调起；也可右键「使用 PowerShell 运行」。
# 做的事：调 Velopack 卸载器移除程序与快捷方式 → 清数据目录 → 清注册表 → 清残留。

Add-Type -AssemblyName System.Windows.Forms

$scriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path
$installRoot = Split-Path -Parent $scriptDir          # current\ 的上一级
$updateExe = Join-Path $installRoot 'Update.exe'

$answer = [System.Windows.Forms.MessageBox]::Show(
    "确定要卸载 HdcMirror 吗？`n`n将删除：`n· 程序本体与桌面/开始菜单快捷方式`n· 设置、设备清单、日志（%APPDATA%\HdcMirror）`n· 注册表项 HKCU\Software\HdcMirror",
    '卸载 HdcMirror',
    [System.Windows.Forms.MessageBoxButtons]::YesNo,
    [System.Windows.Forms.MessageBoxIcon]::Warning,
    [System.Windows.Forms.MessageBoxDefaultButton]::Button2)

if ($answer -ne [System.Windows.Forms.DialogResult]::Yes) { exit 0 }

Write-Host '① 调起 Velopack 卸载器…'
if (Test-Path $updateExe) {
    Start-Process -FilePath $updateExe -ArgumentList '--uninstall' -Wait
    Write-Host '   已请求卸载'
} else {
    Write-Host '   ! 没找到 Update.exe，跳过（可能已是免安装绿色版）'
}

Write-Host '② 清理数据目录…'
foreach ($dir in @(
        (Join-Path $env:APPDATA 'HdcMirror'),
        (Join-Path $env:LOCALAPPDATA 'HdcMirror'))) {
    if (Test-Path $dir) {
        Remove-Item $dir -Recurse -Force -ErrorAction SilentlyContinue
        Write-Host ('   已删 ' + $dir)
    }
}

Write-Host '③ 清理注册表…'
foreach ($key in @(
        'HKCU:\Software\HdcMirror',
        'HKCU:\Software\Classes\HdcMirror')) {
    if (Test-Path $key) {
        Remove-Item $key -Recurse -Force -ErrorAction SilentlyContinue
        Write-Host ('   已删 ' + $key)
    }
}

Write-Host '④ 清理临时残留…'
foreach ($p in @(
        (Join-Path $env:TEMP 'HdcMirror-update'),
        (Join-Path $env:TEMP 'hdcm-shots'))) {
    if (Test-Path $p) { Remove-Item $p -Recurse -Force -ErrorAction SilentlyContinue }
}

Write-Host '⑤ 清理快捷方式…'
foreach ($lnk in @(
        (Join-Path ([Environment]::GetFolderPath('Desktop')) 'HdcMirror.lnk'),
        (Join-Path ([Environment]::GetFolderPath('Programs')) 'HdcMirror.lnk'))) {
    if (Test-Path $lnk) { Remove-Item $lnk -Force -ErrorAction SilentlyContinue }
}

Write-Host ''
Write-Host '卸载完成。'
Start-Sleep -Seconds 2
