<#
.SYNOPSIS
    HdcMirror 发布打包脚本（M5 应用内更新通道）。

.DESCRIPTION
    一条命令完成：读版本号 → dotnet publish(Release/win-x64) → vpk pack → 列出产物。
    版本号唯一来源是 src\Directory.Build.props 的 <Version>，本脚本不写死、不接受覆盖，
    避免出现「安装包 0.1.0 / 程序集 0.2.0」这种 Velopack 会直接拒绝更新的错位。

.PARAMETER SelfContained
    打成自包含包（不需要用户预装 .NET 8 Desktop Runtime，体积约 +130MB）。
    默认 false：框架依赖包，体积小，但目标机必须已有 .NET 8 Desktop Runtime。

.PARAMETER Framework
    框架依赖时告知 Setup 需要哪些运行时（传给 vpk --framework），例如 net8-x64-desktop。
    留空则不做运行时前置检查。

.PARAMETER Channel
    Velopack 更新通道，默认 win。生成 releases.win.json。客户端 GithubSource 隐式读同名清单，
    两者必须一致，否则永远「已是最新」。

.PARAMETER SkipPublish
    跳过 dotnet publish，直接对现有 stage 目录打包（调试脚本用）。

.PARAMETER KeepStage
    保留 stage 目录的旧内容（默认每次都清空，防止上一版的残留 dll 混进新包）。

.PARAMETER SkipVeloAppCheck
    跳过 vpk 对「主程序 Main 里是否调用了 VelopackApp.Build().Run()」的静态校验。
    只在主程序尚未接入、需要先把打包链路打通时使用（M5 POC 阶段即如此）。
    ★ 正式发版必须去掉本开关：没有 VelopackApp 接入的包，安装后无法自我更新。

.PARAMETER CleanDist
    打包前清空 dist 目录。
    ★ 只在「同一版本号重打」时用：vpk 发现 channel 里已存在 >= 当前版本的产物会直接拒绝。
    ★ 跨版本正常发版时不要加：dist 同时是增量更新的基线库，清空后只能出全量包，
      老用户升级就等于重新下载 80MB+。

.EXAMPLE
    powershell -NoProfile -ExecutionPolicy Bypass -File tools\pack\build-release.ps1

.EXAMPLE
    powershell -NoProfile -ExecutionPolicy Bypass -File tools\pack\build-release.ps1 -SelfContained
#>
[CmdletBinding()]
param(
    [switch]$SelfContained,
    [string]$Framework = '',
    [string]$Channel = 'win',
    [switch]$SkipPublish,
    [switch]$KeepStage,
    [switch]$SkipVeloAppCheck,
    [switch]$CleanDist
)

$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'

# ---------------------------------------------------------------------------
# 0. 固定路径与工具
# ---------------------------------------------------------------------------
$RepoRoot   = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)   # tools\pack -> 仓库根
$PropsPath  = Join-Path $RepoRoot 'src\Directory.Build.props'
$AppProj    = Join-Path $RepoRoot 'src\HdcMirror.App\HdcMirror.App.csproj'
$PackRoot   = Join-Path $RepoRoot 'tools\pack'
$StageDir   = Join-Path $PackRoot 'stage'
$DistDir    = Join-Path $PackRoot 'dist'
$MainExe    = 'HdcMirror.exe'
$PackId     = 'HdcMirror'
$Runtime    = 'win-x64'

# 必须用 8.0 SDK；PATH 里的 dotnet 是 6.0，会直接编译失败
$Dotnet = 'C:\dotnet8\dotnet.exe'
if (-not (Test-Path $Dotnet)) {
    throw "找不到 .NET 8 SDK：$Dotnet（PATH 里的 dotnet 是 6.0，不能替代）"
}

# vpk 与 Velopack 库必须同版：库在 src\UpdatePoc\UpdatePoc.csproj 里锁 1.2.161
$VpkVersion = '1.2.161'
$Vpk = Join-Path $env:USERPROFILE ".dotnet\tools\vpk.exe"

function Write-Step([string]$Text) { Write-Host "`n▶ $Text" -ForegroundColor Cyan }
function Write-Ok([string]$Text)   { Write-Host "  ✔ $Text" -ForegroundColor Green }
function Write-Info([string]$Text) { Write-Host "  · $Text" }

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

# Velopack 只认 x.y.z / x.y.z.w 这类语义版本，预发布后缀必须带 -
if ($Version -notmatch '^\d+\.\d+\.\d+') {
    throw "版本号 '$Version' 不是 Velopack 可接受的语义版本（需形如 0.1.0 或 0.1.0-beta1）"
}

Write-Info "包 ID     = $PackId"
Write-Info "通道      = $Channel"
Write-Info "运行时    = $Runtime"
Write-Info "自包含    = $([bool]$SelfContained)"
Write-Info "跳过 AppCheck = $([bool]$SkipVeloAppCheck)$(if ($SkipVeloAppCheck) { '  ← 仅 POC 打通链路用，正式发版必须关闭' } else { '' })"

# ---------------------------------------------------------------------------
# 2. 准备输出目录
# ---------------------------------------------------------------------------
Write-Step "② 准备目录"
if (-not (Test-Path $PackRoot)) { New-Item -ItemType Directory -Path $PackRoot -Force | Out-Null }

if ($CleanDist) {
    if (Test-Path $DistDir) {
        Remove-Item $DistDir -Recurse -Force
        Write-Info "已清空 dist（-CleanDist：仅同版本重打时用，会丢掉增量基线）"
    }
}

if (-not (Test-Path $DistDir)) { New-Item -ItemType Directory -Path $DistDir -Force | Out-Null }

if (-not $SkipPublish) {
    if ($KeepStage) {
        if (-not (Test-Path $StageDir)) { New-Item -ItemType Directory -Path $StageDir -Force | Out-Null }
        Write-Info "保留 stage 旧内容（-KeepStage）"
    } else {
        if (Test-Path $StageDir) {
            Remove-Item $StageDir -Recurse -Force
            Write-Info "已清空 stage（防止旧版本残留 dll 混进新包）"
        }
        New-Item -ItemType Directory -Path $StageDir -Force | Out-Null
    }
}
Write-Ok "stage = $StageDir"
Write-Ok "dist  = $DistDir"

# ---------------------------------------------------------------------------
# 3. dotnet publish
# ---------------------------------------------------------------------------
if ($SkipPublish) {
    Write-Step "③ 跳过 publish（-SkipPublish）"
    if (-not (Test-Path (Join-Path $StageDir $MainExe))) {
        throw "-SkipPublish 要求 stage 里已有 $MainExe，实际没有"
    }
} else {
    Write-Step "③ dotnet publish（Release / $Runtime / self-contained:$([bool]$SelfContained)）"
    Write-Info "工程：$AppProj"

    $publishArgs = @(
        'publish', $AppProj
        '-c', 'Release'
        '-r', $Runtime
        "--self-contained", $SelfContained.IsPresent.ToString().ToLowerInvariant()
        '-o', $StageDir
        '-p:PublishReadyToRun=false'
        '--nologo'
    )

    & $Dotnet @publishArgs
    if ($LASTEXITCODE -ne 0) { throw "dotnet publish 失败，退出码 $LASTEXITCODE" }

    $exePath = Join-Path $StageDir $MainExe
    if (-not (Test-Path $exePath)) {
        throw "publish 成功但 stage 里没有 $MainExe —— 检查 AssemblyName 是否被改动"
    }
    Write-Ok "publish 完成，$MainExe 已就位"

    # hdc.exe 由 csproj 的 None + Link 带入，必须一起进包，否则投屏功能全废
    if (Test-Path (Join-Path $StageDir 'tools\hdc.exe')) {
        Write-Ok "hdc.exe 已随包（tools\hdc.exe）"
    } else {
        Write-Host "  ✗ 警告：stage 里没有 tools\hdc.exe，设备控制会失效" -ForegroundColor Yellow
    }
}

# ---------------------------------------------------------------------------
# 4. 安装 / 更新 vpk CLI
# ---------------------------------------------------------------------------
Write-Step "④ 检查 vpk CLI（要求 $VpkVersion，与 Velopack 库同版）"
if (Test-Path $Vpk) {
    Write-Info "已存在，执行 update 并锁到 $VpkVersion"
    & $Dotnet tool update -g vpk --version $VpkVersion 2>&1 | ForEach-Object { Write-Info $_ }
} else {
    Write-Info "未安装，执行 install"
    & $Dotnet tool install -g vpk --version $VpkVersion 2>&1 | ForEach-Object { Write-Info $_ }
}
if ($LASTEXITCODE -ne 0) { throw "vpk 安装/更新失败，退出码 $LASTEXITCODE" }
if (-not (Test-Path $Vpk)) { throw "vpk 安装后仍找不到：$Vpk" }
Write-Ok "vpk 就绪：$Vpk"

# ---------------------------------------------------------------------------
# 5. vpk pack
# ---------------------------------------------------------------------------
Write-Step "⑤ vpk pack"
$packArgs = @(
    'pack'
    '--packId', $PackId
    '--packVersion', $Version
    '--packDir', $StageDir
    '--mainExe', $MainExe
    '--outputDir', $DistDir
    '--channel', $Channel
    '--runtime', $Runtime
    '--packTitle', 'HdcMirror'
    '--packAuthors', 'HdcMirror'
    '--delta', 'BestSpeed'
)
# 只在框架依赖 + 指定了 Framework 时才传，自包含包不需要
if (-not $SelfContained -and $Framework) {
    $packArgs += @('--framework', $Framework)
}
# vpk 会静态检查主程序 Main 是否调用了 VelopackApp.Build().Run()；
# 主程序未接入时（如 M5 POC）用本开关放行，产物不具备自更新能力。
if ($SkipVeloAppCheck) {
    $packArgs += @('--skipVeloAppCheck')
}
$icon = Join-Path $RepoRoot 'src\HdcMirror.App\Assets\app.ico'
if (Test-Path $icon) { $packArgs += @('--icon', $icon) }

Write-Info ("vpk " + ($packArgs -join ' '))
& $Vpk @packArgs
if ($LASTEXITCODE -ne 0) { throw "vpk pack 失败，退出码 $LASTEXITCODE" }
Write-Ok "pack 完成"

# ---------------------------------------------------------------------------
# 6. 产物清单
# ---------------------------------------------------------------------------
Write-Step "⑥ 产物清单（$DistDir）"
$artifacts = Get-ChildItem $DistDir -File | Sort-Object Name
if (-not $artifacts) { throw "dist 目录为空，打包未产出任何文件" }

$total = 0
foreach ($f in $artifacts) {
    $mb = [math]::Round($f.Length / 1MB, 2)
    $total += $f.Length
    Write-Host ("  {0,-46} {1,10} MB" -f $f.Name, $mb)
}
Write-Host ("  {0,-46} {1,10} MB" -f '(合计)', [math]::Round($total / 1MB, 2)) -ForegroundColor Yellow

$setup = $artifacts | Where-Object { $_.Name -like '*-Setup.exe' }
$manifest = $artifacts | Where-Object { $_.Name -like 'releases.*.json' }
if (-not $setup)    { Write-Host "  ✗ 没有 *-Setup.exe" -ForegroundColor Red; exit 1 }
if (-not $manifest) { Write-Host "  ✗ 没有 releases.*.json" -ForegroundColor Red; exit 1 }

Write-Ok "Setup  = $($setup.Name)"
Write-Ok "清单   = $($manifest.Name)"

Write-Host "`n▶ 打包成功：$PackId $Version" -ForegroundColor Green
Write-Host "  下一步（需人工许可，脚本不代做）：" -ForegroundColor Gray
Write-Host "    1) 建 tag v$Version 并推送" -ForegroundColor Gray
Write-Host "    2) 建 GitHub Release，附件上传 dist 下全部文件" -ForegroundColor Gray
Write-Host "    3) Release 设为 latest（GithubSource 默认读 /releases/latest）" -ForegroundColor Gray
exit 0
