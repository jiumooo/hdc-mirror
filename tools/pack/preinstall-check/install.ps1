#Requires -Version 5.1
<#
    自选安装位置的引导器。
    1. 弹目录选择框；
    2. 在所选目录后自动补 HdcMirror 子目录（选 D:\Programs -> 装到 D:\Programs\HdcMirror）；
    3. 用 Setup.exe --installto 装进去，数据会放在 <安装根>\data。
#>
[CmdletBinding()]
param(
    [string]$InstallRoot,
    [switch]$Silent
)

Add-Type -AssemblyName System.Windows.Forms | Out-Null

$here = Split-Path -Parent $MyInvocation.MyCommand.Path
$setup = Join-Path $here 'HdcMirror-win-Setup.exe'
if (-not (Test-Path -LiteralPath $setup)) {
    Write-Host '找不到 HdcMirror-win-Setup.exe，请把它和本脚本放在同一个文件夹里。' -ForegroundColor Red
    exit 1
}

if ([string]::IsNullOrWhiteSpace($InstallRoot)) {
    $dialog = New-Object System.Windows.Forms.FolderBrowserDialog
    $dialog.Description = '选择安装位置：程序会装到你选中的目录下的 HdcMirror 文件夹里'
    $dialog.ShowNewFolderButton = $true
    if ($dialog.ShowDialog() -ne [System.Windows.Forms.DialogResult]::OK) {
        Write-Host '已取消安装。'
        exit 0
    }
    $InstallRoot = $dialog.SelectedPath
}

# 自动补 HdcMirror 子目录；用户已经选到 HdcMirror 目录就不再重复加
if ((Split-Path -Leaf $InstallRoot) -ne 'HdcMirror') {
    $InstallRoot = Join-Path $InstallRoot 'HdcMirror'
}

Write-Host ('安装位置：' + $InstallRoot) -ForegroundColor Cyan
Write-Host '正在安装（首次会检查 .NET 8 桌面运行时）…' -ForegroundColor Cyan

$arguments = @('--installto', $InstallRoot)
if ($Silent) { $arguments += '--silent' }

$process = Start-Process -FilePath $setup -ArgumentList $arguments -Wait -PassThru
if ($process.ExitCode -ne 0) {
    Write-Host ('安装程序返回 ' + $process.ExitCode + '，安装未完成。') -ForegroundColor Red
    exit $process.ExitCode
}

$exe = Join-Path $InstallRoot 'current\HdcMirror.exe'
if (Test-Path -LiteralPath $exe) {
    Write-Host '安装完成。' -ForegroundColor Green
    Write-Host ('程序目录：' + $InstallRoot) 
    Write-Host ('数据目录：' + (Join-Path $InstallRoot 'data'))
    $launch = Read-Host '现在启动 HdcMirror？(Y/n)'
    if ($launch -notmatch '^[nN]') { Start-Process -FilePath $exe }
} else {
    Write-Host '安装程序已结束，但没有在目标位置找到 HdcMirror.exe，请检查上面的输出。' -ForegroundColor Yellow
}