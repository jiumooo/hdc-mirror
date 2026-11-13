<#
.SYNOPSIS
    HdcMirror 发布打包脚本（M5 应用内更新通道）。

.DESCRIPTION
    一条命令完成：读版本号 → dotnet publish(Release/win-x64) → vpk pack → 列出产物。
    版本号唯一来源是 src\Directory.Build.props 的 <Version>，本脚本不写死、不接受覆盖，
    避免出现「安装包 0.1.0 / 程序集 0.2.0」这种 Velopack 会直接拒绝更新的错位。

    ── 为什么默认「框架依赖」而不是「自包含」──
    1) 体积：自包含把 .NET 8 桌面运行时打进包，实测约 +154MB（Setup 155.59MB / full 148MB）；
       框架依赖后 Setup 只剩十几 MB，用户下载与每次全量更新都省一个数量级。
    2) 更新成本：Velopack 的 delta 只能压缩「相对上一版变化的文件」。自包含包里运行时 DLL
       占绝大多数且几乎不变，仍然每次全量重下；框架依赖后包体几乎只剩业务 DLL，
       delta 才有意义。
    3) 运行时由微软统一打补丁：用户机器上的 Microsoft.WindowsDesktop.App 由 Windows Update /
       .NET 安装器维护，不需要每个应用各自发一版来带安全修复。
    代价是目标机必须先有 .NET 8 Desktop Runtime。因此本脚本在打包时把运行时要求写进
    vpk 的 --framework，让 Setup 自己声明依赖；同时 dist 里会附带一份
    「HdcMirror-安装前检查.cmd」，在运行 Setup 之前就能明确告诉用户缺什么、去哪儿下载。
    「检测」绝不能放在应用启动路径里 —— 框架依赖的程序在缺运行时的情况下根本起不来，
    检测必须发生在安装之前。

.PARAMETER SelfContained
    ★ 逃生开关，默认关闭（= 框架依赖）。
    万一运行时检测/引导链路不可用，加本开关一键退回「把 .NET 8 运行时打进包」的老形态：
    --self-contained true，且不再向 vpk 声明 --framework。

.PARAMETER Framework
    框架依赖时告知 Setup 需要哪些运行时（传给 vpk --framework），默认 net8-x64-desktop。
    自包含打包时本参数被忽略（包里自带运行时，声明依赖反而会让 Setup 白装一遍）。
    传空字符串 '' 可显式关闭声明。

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
      老用户升级就等于重新下载整套包。

.NOTES
    Portable.zip 已按所有者要求永久停用：vpk pack 固定带 --noPortable，
    dist 里不会再出现 *-Portable.zip（免安装版本不做）。
    增量包（*-delta.nupkg）保留，应用内更新依赖它。

.EXAMPLE
    powershell -NoProfile -ExecutionPolicy Bypass -File tools\pack\build-release.ps1

.EXAMPLE
    powershell -NoProfile -ExecutionPolicy Bypass -File tools\pack\build-release.ps1 -SelfContained
#>
[CmdletBinding()]
param(
    [switch]$SelfContained,
    [string]$Framework = 'net8-x64-desktop',
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

# Velopack 只认 x.y.z / x.y.z.w 这类语义版本，预发布后缀必须带 -
if ($Version -notmatch '^\d+\.\d+\.\d+') {
    throw "版本号 '$Version' 不是 Velopack 可接受的语义版本（需形如 0.1.0 或 0.1.0-beta1）"
}

# 自包含时忽略 --framework 声明（包内自带运行时，再声明会让 Setup 白装一遍）
$EffectiveFramework = ''
if ($SelfContained) {
    if ($Framework) { Write-Warn "已指定 -SelfContained：忽略 -Framework '$Framework'（包内自带运行时）" }
} else {
    $EffectiveFramework = $Framework
}

$PublishMode = if ($SelfContained) { '自包含 self-contained' } else { '框架依赖 framework-dependent' }

Write-Info "包 ID     = $PackId"
Write-Info "通道      = $Channel"
Write-Info "运行时    = $Runtime"
Write-Info "发布形态  = $PublishMode"
Write-Info "运行时依赖= $(if ($EffectiveFramework) { $EffectiveFramework } else { '(不声明)' })"
Write-Info "便携包    = 停用（固定 --noPortable）"
Write-Info "增量包    = 启用（--delta BestSpeed）"
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
$SelfContainedValue = $SelfContained.IsPresent.ToString().ToLowerInvariant()
if ($SkipPublish) {
    Write-Step "③ 跳过 publish（-SkipPublish）"
    if (-not (Test-Path (Join-Path $StageDir $MainExe))) {
        throw "-SkipPublish 要求 stage 里已有 $MainExe，实际没有"
    }
} else {
    Write-Step "③ dotnet publish（Release / $Runtime / self-contained:$SelfContainedValue）"
    Write-Info "工程：$AppProj"

    $publishArgs = @(
        'publish', $AppProj
        '-c', 'Release'
        '-r', $Runtime
        '--self-contained', $SelfContainedValue
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

    # 语言资源：Directory.Build.props 的 SatelliteResourceLanguages 决定留下哪几个卫星程序集
    $satelliteDirs = @(Get-ChildItem $StageDir -Directory -ErrorAction SilentlyContinue |
        Where-Object { $_.Name -match '^[a-z]{2}(-[A-Za-z0-9]+)*$' } | Sort-Object Name)
    if ($satelliteDirs.Count -gt 0) {
        Write-Info ("语言目录 " + $satelliteDirs.Count + " 个：" + (($satelliteDirs | ForEach-Object { $_.Name }) -join ', '))
    } else {
        Write-Info "语言目录 0 个（资源已内联进主程序集）"
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
    # 所有者要求不做免安装版：固定关掉 Portable 包
    '--noPortable'
)
# 框架依赖时把运行时要求写进安装器元数据，让 Setup 自己知道依赖哪个运行时
if ($EffectiveFramework) {
    $packArgs += @('--framework', $EffectiveFramework)
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
# 5b. 附带安装前运行时检查脚本（检测必须发生在 Setup 之前）
# ---------------------------------------------------------------------------
Write-Step "⑤b 附带安装前检查脚本"
$PreinstallSrc = Join-Path $PackRoot 'preinstall-check\HdcMirror-安装前检查.cmd'
if (Test-Path $PreinstallSrc) {
  $PreinstallDst = Join-Path $DistDir 'HdcMirror-preinstall-check.cmd'   # 必须 ASCII 名：gh CLI 上传会丢掉中文文件名
  Copy-Item $PreinstallSrc $PreinstallDst -Force
  Write-Ok 'dist 内的检查脚本名为 HdcMirror-preinstall-check.cmd（ASCII，供 GitHub 附件上传）'
} else {
    Write-Warn "未找到 $PreinstallSrc，安装包不带前置检查脚本"
}

# 自选安装位置的引导器（ASCII 名，gh CLI 上传不会丢名字）
foreach ($pair in @(
    @{ Src = "preinstall-check\HdcMirror-install.cmd"; Dst = "HdcMirror-install.cmd" },
    @{ Src = "preinstall-check\install.ps1";          Dst = "install.ps1" })) {
    $src = Join-Path $PackRoot $pair.Src
    if (Test-Path -LiteralPath $src) {
        Copy-Item -LiteralPath $src -Destination (Join-Path $DistDir $pair.Dst) -Force
        Write-Ok ("已附带 " + $pair.Dst)
    } else {
        Write-Warn ("未找到 " + $src)
    }
}

# 防御：任何残留的 Portable 产物都清掉（例如 dist 里上一版的旧 zip）
$stalePortable = @(Get-ChildItem $DistDir -File -Filter '*-Portable.zip' -ErrorAction SilentlyContinue)
foreach ($p in $stalePortable) {
    Remove-Item $p.FullName -Force
    Write-Info "已删除残留便携包：$($p.Name)"
}

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

$portableLeft = @($artifacts | Where-Object { $_.Name -like '*-Portable.zip' })
if ($portableLeft.Count -gt 0) {
    Write-Host "  ✗ dist 里仍有 Portable 包：$(($portableLeft | ForEach-Object { $_.Name }) -join ', ')" -ForegroundColor Red
    exit 1
}

Write-Ok "Setup  = $($setup.Name)"
Write-Ok "清单   = $($manifest.Name)"
Write-Ok "便携包 = 无（符合要求）"

$stageMb = if (Test-Path $StageDir) {
    [math]::Round(((Get-ChildItem $StageDir -Recurse -File | Measure-Object Length -Sum).Sum) / 1MB, 2)
} else { 0 }
Write-Ok "stage  = $stageMb MB"

Write-Host "`n▶ 打包成功：$PackId $Version（$PublishMode）" -ForegroundColor Green
Write-Host "  下一步（需人工许可，脚本不代做）：" -ForegroundColor Gray
Write-Host "    1) 建 tag v$Version 并推送" -ForegroundColor Gray
    Write-Host "    2) 建 GitHub Release，附件上传 dist 下全部文件（含 HdcMirror-preinstall-check.cmd）" -ForegroundColor Gray
Write-Host "    3) Release 设为 latest（GithubSource 默认读 /releases/latest）" -ForegroundColor Gray
exit 0
