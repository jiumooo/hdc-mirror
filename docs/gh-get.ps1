# gh-get.ps1 — GitHub 文件/仓库下载助手（多路由自动回退 + 测速择优）
# 用法: .\gh-get.ps1 -Repo owner/repo [-Branch main] [-Path src/a.java] [-OutDir .\dl] [-Zip]
param(
  [Parameter(Mandatory=$true)][string]$Repo,
  [string]$Branch = 'main',
  [string]$Path,
  [string]$OutDir = '.',
  [switch]$Zip
)
$ErrorActionPreference = 'Continue'
$direct = "https://raw.githubusercontent.com/$Repo/$Branch"
$routes = @(
  @{ n='ghfast.top';  p="https://ghfast.top/https://raw.githubusercontent.com/$Repo/$Branch" },
  @{ n='jsDelivr';    p="https://cdn.jsdelivr.net/gh/$Repo@$Branch" },
  @{ n='fastly.jsdelivr'; p="https://fastly.jsdelivr.net/gh/$Repo@$Branch" },
  @{ n='ghps.cc';     p="https://ghps.cc/https://raw.githubusercontent.com/$Repo/$Branch" }
)
function Fetch($url, $dest) {
  try {
    $sw = [Diagnostics.Stopwatch]::StartNew()
    Invoke-WebRequest $url -OutFile $dest -UseBasicParsing -TimeoutSec 30
    $sw.Stop()
    if ((Test-Path $dest) -and (Get-Item $dest).Length -gt 0) {
      Write-Host ("  ✔ {0} ({1} KB / {2} ms)" -f $url.Split('/')[2], [math]::Round((Get-Item $dest).Length/1KB,1), $sw.ElapsedMilliseconds)
      return $true
    }
  } catch { }
  return $false
}
if ($Zip) {
  $zipRoutes = @(
    @{ n='ghfast'; u="https://ghfast.top/https://github.com/$Repo/archive/refs/heads/$Branch.zip" },
    @{ n='直连';   u="https://github.com/$Repo/archive/refs/heads/$Branch.zip" }
  )
  New-Item -ItemType Directory -Force -Path $OutDir | Out-Null
  $dest = Join-Path $OutDir "$($Repo.Split('/')[-1])-$Branch.zip"
  foreach ($r in $zipRoutes) { if (Fetch $r.u $dest) { Write-Host "  完成（$($r.n)）"; return } }
  Write-Host '  ✗ 全部路由失败'
  exit 1
}
New-Item -ItemType Directory -Force -Path (Join-Path $OutDir (Split-Path $Path -Parent)) | Out-Null
$dest = Join-Path $OutDir ($Path -replace '/','\')
if (Fetch "$direct/$Path" $dest) { return }
foreach ($r in $routes) { if (Fetch "$($r.p)/$Path" $dest) { Write-Host "  （回退到 $($r.n)）"; return } }
Write-Host "  ✗ $Path 全部路由失败"; exit 1