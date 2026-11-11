#Requires -Version 5.1
<#
.SYNOPSIS
    推送前总闸：隐私扫描 + 样本媒体检查 + 敏感文件检查。

.DESCRIPTION
    三道闸全过才算通过（退出码 0）：
      闸 1  隐私扫描        tools\privacy-scan.ps1 必须退出码 0
      闸 2  真实屏幕内容     samples\ 下的图片/视频/裸码流
                             （*.jpeg *.jpg *.png *.h264 *.h265 *.mp4 *.gif *.webm *.mkv）
                             不得进入提交。本地保留素材是允许的 —— 已被 .gitignore 忽略的
                             只提示不报错；真正会入库的（未忽略或称被 -f 强加）才拦下。
                             确需入库时人工确认后写入 tools\samples-allowlist.txt
      闸 3  敏感文件        暂存区与工作树中不得出现 .env / *.pfx / *.key / config.local.*

    最后无条件打印一行：本仓库禁止未经用户显式许可执行 git push。

.PARAMETER Root
    仓库根，默认是本脚本所在目录的上一级。

.EXAMPLE
    powershell -NoProfile -ExecutionPolicy Bypass -File tools\pre-push-check.ps1
#>
[CmdletBinding()]
param(
    [string]$Root
)

$ErrorActionPreference = 'Stop'
try { [Console]::OutputEncoding = [System.Text.Encoding]::UTF8 } catch { }

if (-not $Root) { $Root = Split-Path -Parent $PSScriptRoot }
$Root = (Resolve-Path -LiteralPath $Root).Path

$Failures = New-Object System.Collections.ArrayList

Write-Host ""
Write-Host "════════════════════════════════════════════════════════" -ForegroundColor Cyan
Write-Host "  推送前检查（pre-push-check）" -ForegroundColor Cyan
Write-Host "════════════════════════════════════════════════════════" -ForegroundColor Cyan

# ── 闸 1：隐私扫描 ──────────────────────────────────────────────────────────
Write-Host ""
Write-Host "▶ 闸 1/3  隐私扫描" -ForegroundColor Cyan
$scanScript = Join-Path $PSScriptRoot 'privacy-scan.ps1'
if (-not (Test-Path -LiteralPath $scanScript)) {
    Write-Host "  ✗ 找不到 tools\privacy-scan.ps1" -ForegroundColor Red
    $Failures.Add('闸1：脚本缺失') | Out-Null
} else {
    $hostExe = (Get-Process -Id $PID).Path
    if (-not $hostExe) { $hostExe = 'powershell.exe' }
    & $hostExe -NoProfile -ExecutionPolicy Bypass -File $scanScript -Root $Root
    if ($LASTEXITCODE -ne 0) {
        Write-Host "  ✗ 闸 1 未通过：仍有个人信息待处理。" -ForegroundColor Red
        $Failures.Add('闸1：隐私扫描有命中') | Out-Null
    } else {
        Write-Host "  ✔ 闸 1 通过" -ForegroundColor Green
    }
}

# ── 闸 2：samples\ 真实屏幕内容 ─────────────────────────────────────────────
Write-Host ""
Write-Host "▶ 闸 2/3  真实屏幕内容检查（samples\）" -ForegroundColor Cyan
$sampleDir = Join-Path $Root 'samples'
$MediaGlobs = @('*.jpeg', '*.jpg', '*.png', '*.h264', '*.h265', '*.mp4', '*.gif', '*.webm', '*.mkv')
$AllowListFile = Join-Path $PSScriptRoot 'samples-allowlist.txt'
$AllowList = @()
if (Test-Path -LiteralPath $AllowListFile) {
    $AllowList = @(Get-Content -LiteralPath $AllowListFile -Encoding UTF8 |
        ForEach-Object { $_.Trim() } |
        Where-Object { $_ -and -not $_.StartsWith('#') })
}

if (-not (Test-Path -LiteralPath $sampleDir)) {
    Write-Host "  － 无 samples\ 目录，跳过" -ForegroundColor DarkGray
} else {
    # 判定标准是「会不会被纳入提交」，不是「磁盘上有没有」。
    # 本地保留素材是正常开发需要；.gitignore 已挡住的就不该报错，
    # 被 -f 强加进索引的（git 对已跟踪文件不再套用 ignore 规则）才必须拦下。
    $GitOk = $false
    if (Get-Command git -ErrorAction SilentlyContinue) {
        git -C $Root rev-parse --is-inside-work-tree *> $null
        if ($LASTEXITCODE -eq 0) { $GitOk = $true }
    }
    if (-not $GitOk) {
        Write-Host "  ⚠ 不在 git 工作树内，无法判定入库风险，按最严格标准处理" -ForegroundColor Yellow
    }

    $bad = @()
    $localOnly = @()
    foreach ($g in $MediaGlobs) {
        foreach ($f in (Get-ChildItem -LiteralPath $sampleDir -Filter $g -File -ErrorAction SilentlyContinue)) {
            $name = $f.Name
            $allowed = $false
            foreach ($pat in $AllowList) { if ($name -like $pat) { $allowed = $true; break } }
            if ($allowed) { continue }

            $wouldCommit = $true
            if ($GitOk) {
                $rel = $f.FullName.Substring($Root.Length).TrimStart('\', '/').Replace('\', '/')
                git -C $Root check-ignore --quiet -- $rel
                # 退出码 0 = 已被忽略，不会入库；1 = 未忽略，会入库
                if ($LASTEXITCODE -eq 0) { $wouldCommit = $false }
            }
            if ($wouldCommit) { $bad += $name } else { $localOnly += $name }
        }
    }

    if ($bad.Count -gt 0) {
        Write-Host ("  ✗ {0} 个真实屏幕内容文件会进入提交：" -f $bad.Count) -ForegroundColor Red
        foreach ($b in ($bad | Sort-Object)) { Write-Host ("      {0}" -f $b) -ForegroundColor Red }
        Write-Host "     这些文件含真实手机画面（第三方 App 界面与浏览内容），不得入库。" -ForegroundColor DarkGray
        Write-Host "     先确认 .gitignore 覆盖到它；确需入库时人工确认后写入 tools\samples-allowlist.txt" -ForegroundColor DarkGray
        $Failures.Add('闸2：真实屏幕内容会进入提交') | Out-Null
    } else {
        Write-Host "  ✔ 闸 2 通过（无真实屏幕内容会进入提交）" -ForegroundColor Green
    }
    if ($localOnly.Count -gt 0) {
        Write-Host ("  － 本地保留 {0} 个素材仅存于磁盘、已被忽略，不会入库：{1}" -f $localOnly.Count, (($localOnly | Sort-Object) -join ', ')) -ForegroundColor DarkGray
    }
}

# ── 闸 3：敏感文件 ──────────────────────────────────────────────────────────
Write-Host ""
Write-Host "▶ 闸 3/3  敏感文件检查（.env / *.pfx / *.key / config.local.*）" -ForegroundColor Cyan
$SensitiveGlobs = @(
    '.env', '.env.*', '*.pfx', '*.p12', '*.key', '*.pem', '*.jks', '*.keystore',
    'config.local.*', 'appsettings.local.json', 'id_rsa*', 'id_ed25519*', '*.secret'
)
$candidates = @()
Push-Location -LiteralPath $Root
try {
    $statusOut = @(& git status --porcelain 2>$null)
    foreach ($line in $statusOut) {
        if (-not $line -or $line.Length -lt 4) { continue }
        $p = $line.Substring(3).Trim()
        if ($p -match '^".*"$') { $p = $p.Substring(1, $p.Length - 2) }
        if ($p -match '\s->\s') { $p = ($p -split '\s->\s')[-1] }
        $candidates += $p
    }
    $cachedOut = @(& git diff --cached --name-only 2>$null)
    foreach ($line in $cachedOut) {
        if (-not $line) { continue }
        if ($line -match '^".*"$') { $line = $line.Substring(1, $line.Length - 2) }
        $candidates += $line
    }
} catch {
    Write-Host ("  ! git 调用失败（可能不在仓库内）：{0}" -f $_.Exception.Message) -ForegroundColor Yellow
} finally {
    Pop-Location
}

$candidates = @($candidates | Where-Object { $_ } | Select-Object -Unique)
$sensHits = @()
foreach ($c in $candidates) {
    $leaf = ($c -split '/')[-1]
    foreach ($g in $SensitiveGlobs) {
        if ($leaf -like $g) { $sensHits += $c; break }
    }
}
if ($sensHits.Count -gt 0) {
    Write-Host ("  ✗ 发现 {0} 个敏感文件进入待提交范围：" -f $sensHits.Count) -ForegroundColor Red
    foreach ($s in ($sensHits | Sort-Object)) { Write-Host ("      {0}" -f $s) -ForegroundColor Red }
    Write-Host "     这些文件不得入库；请加入 .gitignore 并移出暂存区。" -ForegroundColor DarkGray
    $Failures.Add('闸3：敏感文件') | Out-Null
} else {
    Write-Host "  ✔ 闸 3 通过" -ForegroundColor Green
}

# ── 结论 ────────────────────────────────────────────────────────────────────
Write-Host ""
Write-Host "════════════════════════════════════════════════════════" -ForegroundColor Cyan
if ($Failures.Count -eq 0) {
    Write-Host "  结论：三道闸全部通过。" -ForegroundColor Green
} else {
    Write-Host ("  结论：未通过，共 {0} 项阻塞：" -f $Failures.Count) -ForegroundColor Red
    foreach ($x in $Failures) { Write-Host ("      - {0}" -f $x) -ForegroundColor Red }
}
Write-Host ""
Write-Host "  ⚠ 本仓库禁止未经用户显式许可执行 git push。人工确认后再推。" -ForegroundColor Yellow
Write-Host "════════════════════════════════════════════════════════" -ForegroundColor Cyan
Write-Host ""

if ($Failures.Count -eq 0) { exit 0 }
exit 1
