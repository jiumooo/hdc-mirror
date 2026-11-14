<#
.SYNOPSIS
    编译两个安装包：轻量版（需 .NET 8 桌面运行时）与完整版（内置运行时）。

.DESCRIPTION
    产出的两个文件都在 tools\pack\dist\，文件名固定为「HdcMirror-Setup-<版本>.exe」与
    「HdcMirror-Setup-Full-<版本>.exe」—— 名字不变，版本号跟在后面，每次发版只换版本号。

    两个包用的是同一个工程 src\HdcMirror.Installer：
      · 轻量版：直接 publish，包里只有 Velopack 载荷与卸载器；
      · 完整版：publish 时加 -p:IncludeRuntime=true，csproj 会把
        tools\pack\prereq\windowsdesktop-runtime-win-x64.exe 一起嵌进去（约 +55.7 MB）。
    安装时 Program.EnsureRuntime() 先查本机有没有 .NET 8 桌面运行时，缺才静默安装。

    版本号唯一来源是 src\Directory.Build.props 的 <Version>，本脚本不写死、不接受覆盖。

.PARAMETER SkipPrereqCheck
    跳过前置产物检查（卸载器 publish、vpk Setup、运行时安装器）。

.EXAMPLE
    powershell -NoProfile -ExecutionPolicy Bypass -File tools\pack\build-installers.ps1
#>
[CmdletBinding()]
param(
    [switch]$SkipPrereqCheck
)

$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'

# ---------------------------------------------------------------------------
# 0. 固定路径与工具
# ---------------------------------------------------------------------------
$RepoRoot  = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)   # tools\pack -> 仓库根
$PropsPath = Join-Path $RepoRoot 'src\Directory.Build.props'
$InstProj  = Join-Path $RepoRoot 'src\HdcMirror.Installer\HdcMirror.Installer.csproj'
$PackRoot  = Join-Path $RepoRoot 'tools\pack'
$DistDir   = Join-Path $PackRoot 'dist'
$StageLite = Join-Path $PackRoot 'stage-installer-lite'
$StageFull = Join-Path $PackRoot 'stage-installer-full'

# 必须用 8.0 SDK；PATH 里的 dotnet 是 6.0，会直接编译失败
$Dotnet = 'C:\dotnet8\dotnet.exe'
if (-not (Test-Path $Dotnet)) {
    throw "找不到 .NET 8 SDK：$Dotnet（PATH 里的 dotnet 是 6.0，不能替代）"
}

function Write-Step([string]$Text) { Write-Host "`n▶ $Text" -ForegroundColor Cyan }
function Write-Ok([string]$Text)   { Write-Host "  ✔ $Text" -ForegroundColor Green }
function Write-Info([string]$Text) { Write-Host "  · $Text" }
function Write-Warn([string]$Text) { Write-Host "  ! $Text" -ForegroundColor Yellow }

# ---------------------------------------------------------------------------
# 1. 读版本号（唯一来源）
# ---------------------------------------------------------------------------
Write-Step "① 读取版本号：src\Directory.Build.props"
if (-not (Test-Path $PropsPath)) { throw "版本来源缺失：$PropsPath" }

$propsText = Get-Content $PropsPath -Raw -Encoding UTF8
$m = [regex]::Match($propsText, '<Version>\s*([^<]+?)\s*</Version>')
if (-not $m.Success) { throw "Directory.Build.props 里没有 <Version>，版本号无来源，拒绝继续" }

$Version = $m.Groups[1].Value.Trim()
Write-Ok "版本号 = $Version"

$LiteName = "HdcMirror-Setup-$Version.exe"
$FullName = "HdcMirror-Setup-Full-$Version.exe"

# ---------------------------------------------------------------------------
# 2. 前置产物检查
# ---------------------------------------------------------------------------
Write-Step "② 检查前置产物"

$UninstallExe = Join-Path $RepoRoot 'src\HdcMirror.Uninstaller\publish\HdcMirror-Uninstall.exe'
$VpkSetup     = Join-Path $DistDir 'HdcMirror-win-Setup.exe'
$RuntimeExe   = Join-Path $PackRoot 'prereq\windowsdesktop-runtime-win-x64.exe'

$missing = @()
foreach ($item in @(
    @{ Path = $UninstallExe; Desc = '卸载器（src\HdcMirror.Uninstaller\publish\HdcMirror-Uninstall.exe）' },
    @{ Path = $VpkSetup;     Desc = 'Velopack 载荷（tools\pack\dist\HdcMirror-win-Setup.exe）' },
    @{ Path = $RuntimeExe;   Desc = '运行时安装器（tools\pack\prereq\windowsdesktop-runtime-win-x64.exe）' })) {
    if (Test-Path $item.Path) {
        $mb = [math]::Round((Get-Item $item.Path).Length / 1MB, 2)
        Write-Ok ("{0}  {1} MB" -f $item.Desc, $mb)
    } else {
        Write-Warn ("缺少：" + $item.Desc)
        $missing += $item.Desc
    }
}

if ($missing.Count -gt 0 -and -not $SkipPrereqCheck) {
    throw ("前置产物缺失 {0} 项。先跑 tools\pack\build-release.ps1 生成 vpk 载荷，再跑本脚本。" -f $missing.Count)
}

# ---------------------------------------------------------------------------
# 3. 编译轻量版
# ---------------------------------------------------------------------------
Write-Step "③ 编译轻量版（需本机已装 .NET 8 桌面运行时）"
if (Test-Path $StageLite) { Remove-Item $StageLite -Recurse -Force }

# 必须是框架依赖（SelfContained=false）：csproj 默认 SelfContained=true，会把整个
# .NET 运行时打进安装器（实测 82.95 MB），轻量版就不轻了。
# 同时必须关掉单文件压缩：NETSDK1176 —— 只有自包含发布才支持压缩单文件。
$publishBase = @(
    $InstProj
    '-c', 'Release'
    '-r', 'win-x64'
    '-p:SelfContained=false'
    '-p:EnableCompressionInSingleFile=false'
    '--nologo'
)

# 每次 publish 前都必须删 obj：两个包只差 IncludeRuntime 一个属性，MSBuild 的增量
# 缓存会认定「所有项目均是最新的」，嵌入资源根本不重新计算 —— 实测不删 obj 时
# 两个包体积完全一样（都 82.95 MB），运行时要么都没进、要么都进了。
$InstObj = Join-Path $RepoRoot 'src\HdcMirror.Installer\obj'
function Clear-InstallerObj {
    if (Test-Path $InstObj) {
        Remove-Item $InstObj -Recurse -Force
        Write-Info '已清 obj 缓存，强制重新计算嵌入资源'
    }
}

Clear-InstallerObj
& $Dotnet publish @publishBase -o $StageLite
if ($LASTEXITCODE -ne 0) { throw "轻量版 publish 失败，退出码 $LASTEXITCODE" }

$liteBuilt = Join-Path $StageLite 'HdcMirror-Setup.exe'
if (-not (Test-Path $liteBuilt)) { throw "publish 成功但没找到 $liteBuilt —— 检查 csproj 的 AssemblyName" }
Write-Ok ("轻量版编译完成：" + [math]::Round((Get-Item $liteBuilt).Length / 1MB, 2) + " MB")

# ---------------------------------------------------------------------------
# 4. 编译完整版（嵌入官方运行时）
# ---------------------------------------------------------------------------
Write-Step "④ 编译完整版（内置 .NET 8 桌面运行时）"
if (Test-Path $StageFull) { Remove-Item $StageFull -Recurse -Force }

# 必须先删 obj：两次 publish 之间只差 IncludeRuntime 这一个属性，MSBuild 的增量
# 缓存会认定「所有项目均是最新的」，条件嵌入的资源根本不重新计算 —— 实测两次
# 产物体积完全一样（都是 82.95 MB），运行时没进去。
Clear-InstallerObj

& $Dotnet publish @publishBase -o $StageFull -p:IncludeRuntime=true
if ($LASTEXITCODE -ne 0) { throw "完整版 publish 失败，退出码 $LASTEXITCODE" }

$fullBuilt = Join-Path $StageFull 'HdcMirror-Setup.exe'
if (-not (Test-Path $fullBuilt)) { throw "publish 成功但没找到 $fullBuilt —— 检查 csproj 的 AssemblyName" }
Write-Ok ("完整版编译完成：" + [math]::Round((Get-Item $fullBuilt).Length / 1MB, 2) + " MB")

# ---------------------------------------------------------------------------
# 5. 按版本号命名并放到 dist
# ---------------------------------------------------------------------------
Write-Step "⑤ 输出到 tools\pack\dist（文件名带版本号）"
if (-not (Test-Path $DistDir)) { New-Item -ItemType Directory -Path $DistDir | Out-Null }

$liteDest = Join-Path $DistDir $LiteName
$fullDest = Join-Path $DistDir $FullName

Copy-Item $liteBuilt $liteDest -Force
Copy-Item $fullBuilt $fullDest -Force

$liteMb = [math]::Round((Get-Item $liteDest).Length / 1MB, 2)
$fullMb = [math]::Round((Get-Item $fullDest).Length / 1MB, 2)
Write-Ok ("{0}  {1} MB" -f $LiteName, $liteMb)
Write-Ok ("{0}  {1} MB" -f $FullName, $fullMb)

# 完整版必须明显大于轻量版（说明运行时确实嵌进去了），差太少就是没嵌成功
$diffMb = $fullMb - $liteMb
if ($diffMb -lt 40) {
    throw ("完整版只比轻量版大 {0} MB，运行时可能没嵌进去（应约 +55 MB）" -f [math]::Round($diffMb, 2))
}
Write-Ok ("完整版比轻量版大 {0} MB，运行时已嵌入" -f [math]::Round($diffMb, 2))

# ---------------------------------------------------------------------------
# 6. 清理中间目录
# ---------------------------------------------------------------------------
Write-Step "⑥ 清理中间产物"
foreach ($dir in @($StageLite, $StageFull)) {
    if (Test-Path $dir) {
        Remove-Item $dir -Recurse -Force
        Write-Info ("已删除 " + $dir.Replace($RepoRoot + '\', ''))
    }
}

# ---------------------------------------------------------------------------
# 7. 产物清单
# ---------------------------------------------------------------------------
Write-Step "⑦ 产物清单（$DistDir）"
Get-ChildItem $DistDir -File -Filter 'HdcMirror-Setup*.exe' | Sort-Object Name | ForEach-Object {
    Write-Host ("  {0,-40} {1,10} MB" -f $_.Name, [math]::Round($_.Length / 1MB, 2))
}

Write-Host "`n▶ 打包成功：$Version" -ForegroundColor Green
Write-Host "    轻量版：$liteDest" -ForegroundColor Gray
Write-Host "    完整版：$fullDest" -ForegroundColor Gray
