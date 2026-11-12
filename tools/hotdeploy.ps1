<#
  热更新：把当前源码编译结果直接覆盖到本机已安装的 HdcMirror 上，不打安装包、不改版本号。

  用途：修 bug / 调界面时的快速轮转。
  正式发版仍然走 tools\pack\build-release.ps1 + 安装包，由仓库所有者测试通过后才提交。

  参数：
    -Release      用 Release 配置编译（默认 Debug，编译更快）
    -NoStart      只部署，不自动启动
    -NoBuild      跳过编译，只把现有 bin 输出同步过去

  安装目录约定（Velopack）：
    %LOCALAPPDATA%\HdcMirror\current\HdcMirror.exe        ← 被热更新的目标
    %LOCALAPPDATA%\HdcMirror\Update.exe                   ← 安装器，热更新不动它
    %LOCALAPPDATA%\HdcMirror\packages\                    ← 原始安装包，热更新不动它
#>
[CmdletBinding()]
param(
    [switch]$Release,
    [switch]$NoStart,
    [switch]$NoBuild
)

$ErrorActionPreference = 'Stop'
[Console]::OutputEncoding = [Text.Encoding]::UTF8

$repo    = Split-Path $PSScriptRoot -Parent
$dotnet  = 'C:\dotnet8\dotnet.exe'
$config  = if ($Release) { 'Release' } else { 'Debug' }
$proj    = Join-Path $repo 'src\HdcMirror.App\HdcMirror.App.csproj'
$binDir  = Join-Path $repo "src\HdcMirror.App\bin\$config\net8.0-windows"
$install = Join-Path $env:LOCALAPPDATA 'HdcMirror\current'
$exe     = Join-Path $install 'HdcMirror.exe'

Write-Host '▶ 热更新开始' -ForegroundColor Cyan
Write-Host "  仓库：$repo"
Write-Host "  目标：$install"

if (-not (Test-Path $exe)) {
    Write-Host '  ✗ 目标不存在：本机还没装过 HdcMirror。' -ForegroundColor Red
    Write-Host '    先跑一次安装包（tools\pack\dist\HdcMirror-win-Setup.exe），之后才能热更新。' -ForegroundColor Yellow
    exit 2
}

Write-Host '① 结束正在运行的 HdcMirror…'
$running = Get-Process HdcMirror -ErrorAction SilentlyContinue
if ($running) {
    foreach ($p in $running) {
        Write-Host ("   结束 PID " + $p.Id)
        try { $p.Kill() } catch { Write-Host ("   跳过：" + $_.Exception.Message) }
    }
    Start-Sleep -Milliseconds 900
} else {
    Write-Host '   没有运行中的实例'
}

# hdc.exe 是被应用拉起的常驻服务端，会占着 tools\libusb_shared.dll，不结束就覆盖不了
# 注意：进程 Path 可能返回 8.3 短名（例如 ...\Local\HDCMIR~1\current\tools\hdc.exe），
# 所以不能用 StartsWith 精确前缀，只能按 HdcMirror / HDCMIR 关键字匹配。
$hdcBusy = Get-Process hdc -ErrorAction SilentlyContinue | Where-Object {
    $_.Path -and ($_.Path -match 'HDCMIR')
}
if ($hdcBusy) {
    foreach ($p in $hdcBusy) {
        Write-Host ("   结束残留 hdc 服务 PID " + $p.Id)
        try { $p.Kill() } catch { Write-Host ("   跳过：" + $_.Exception.Message) }
    }
    Start-Sleep -Milliseconds 700
}

if (-not $NoBuild) {
    Write-Host "② 编译（$config）…"
    if (-not (Test-Path $dotnet)) {
        Write-Host "  ✗ 找不到 $dotnet（.NET 8 SDK 应装在 C:\dotnet8）" -ForegroundColor Red
        exit 3
    }
    $out = & $dotnet build $proj -c $config -v q --nologo 2>&1
    $code = $LASTEXITCODE
    $errs = $out | Select-String -Pattern ': error' | Select-Object -First 6
    if ($errs) { $errs | ForEach-Object { Write-Host ('   ' + $_.Line.Trim()) -ForegroundColor Red } }
    if ($code -ne 0) {
        Write-Host "  ✗ 编译失败，退出码 $code（安装目录未改动）" -ForegroundColor Red
        exit $code
    }
    Write-Host '   ✔ 编译通过，0 错误'
} else {
    Write-Host '② 跳过编译（-NoBuild）'
}

if (-not (Test-Path $binDir)) {
    Write-Host "  ✗ 找不到编译输出：$binDir" -ForegroundColor Red
    exit 4
}

Write-Host '③ 同步到安装目录（/MIR：源里没有的旧文件一并删除，避免残留旧 native 库；不动 Update.exe / packages）…'
$robolog = & robocopy $binDir $install /MIR /NFL /NDL /NJH /NJS /NP /R:2 /W:1 /XF *.pdb 2>&1
$rc = $LASTEXITCODE
# robocopy: 0=无变化 1=有复制 2=多余文件 3=1+2 … 8 以上才是失败；负数=被中断
if ($rc -lt 0 -or $rc -ge 8) {
    $robolog | Select-Object -Last 8 | ForEach-Object { Write-Host ('   ' + $_) }
    Write-Host "  ✗ robocopy 失败，退出码 $rc" -ForegroundColor Red
    exit $rc
}
Write-Host "   ✔ 同步完成（robocopy 退出码 $rc）"

$newVer = (Get-Item $exe).VersionInfo.FileVersion
$newTime = (Get-Item $exe).LastWriteTime.ToString('HH:mm:ss')
Write-Host "   主程序：$newVer  写入时间 $newTime"

if ($NoStart) {
    Write-Host '④ 已按要求不启动（-NoStart）'
} else {
    Write-Host '④ 启动新版本…'
    Start-Process -FilePath $exe | Out-Null
    for ($i = 1; $i -le 24; $i++) {
        Start-Sleep -Milliseconds 500
        $p = Get-Process HdcMirror -ErrorAction SilentlyContinue | Select-Object -First 1
        if ($p) {
            $p.Refresh()
            if ($p.MainWindowHandle -ne 0) {
                Write-Host ("   ✔ 已启动 PID " + $p.Id)
                break
            }
        }
        if ($i % 4 -eq 0) { Write-Host ("   等待窗口… 已等 " + [int]($i * 0.5) + ' 秒') }
    }
}

Write-Host '✔ 热更新完成（版本号未变，装出来的仍是同一个版本的安装记录）' -ForegroundColor Green
