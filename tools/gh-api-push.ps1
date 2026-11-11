# 通过 GitHub Git Data API 推送初始提交（HTTPS 443 被墙时使用，API 走 api.github.com 可达）
# 用法：powershell -NoProfile -ExecutionPolicy Bypass -File tools\gh-api-push.ps1 -Repo jiumooo/hdc-mirror -Message "..." [-Branch main]
param(
    [Parameter(Mandatory = $true)][string]$Repo,
    [Parameter(Mandatory = $true)][string]$Message,
    [string]$Branch = 'main',
    [string]$ParentSha = ''
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$tmp = Join-Path $env:TEMP ('ghapi-' + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $tmp -Force | Out-Null

function Invoke-GhApi([string]$method, [string]$path, [string]$bodyFile) {
    $args = @('api', '--method', $method, $path)
    if ($bodyFile) { $args += @('--input', $bodyFile) }
    $out = & gh @args 2>&1
    if ($LASTEXITCODE -ne 0) { throw "gh api $method $path 失败：$out" }
    return $out
}

# 只推送 git 已跟踪（暂存区）的文件，行为与 git push 一致
# 用已提交的树（HEAD）而不是暂存区：先 git commit，再跑本脚本
$files = & git -C $root -c core.quotepath=false ls-tree -r HEAD --name-only
if (-not $files) { throw 'HEAD 里没有文件；请先 git add + git commit。' }
$files = @($files)
Write-Host ("▶ 待上传 {0} 个文件" -f $files.Count) -ForegroundColor Cyan

$treeEntries = New-Object System.Collections.Generic.List[string]
$i = 0
foreach ($rel in $files) {
    $i++
    $full = Join-Path $root ($rel -replace '/', '\')
    $bytes = [System.IO.File]::ReadAllBytes($full)
    $b64 = [Convert]::ToBase64String($bytes)
    $payload = @{ content = $b64; encoding = 'base64' } | ConvertTo-Json -Compress
    $pf = Join-Path $tmp 'blob.json'
    [System.IO.File]::WriteAllText($pf, $payload, (New-Object System.Text.UTF8Encoding($false)))
    $sha = (Invoke-GhApi 'POST' "repos/$Repo/git/blobs" $pf | ConvertFrom-Json).sha
    $treeEntries.Add((@{ path = $rel; mode = '100644'; type = 'blob'; sha = $sha } | ConvertTo-Json -Compress))
    if ($i % 20 -eq 0 -or $i -eq $files.Count) {
        Write-Host ("   {0}/{1} 个 blob 已上传" -f $i, $files.Count)
    }
}

Write-Host '▶ 建 tree' -ForegroundColor Cyan
$treeBody = @{ tree = ($treeEntries | ForEach-Object { $_ | ConvertFrom-Json }) } | ConvertTo-Json -Depth 5 -Compress
$tf = Join-Path $tmp 'tree.json'
[System.IO.File]::WriteAllText($tf, $treeBody, (New-Object System.Text.UTF8Encoding($false)))
$treeSha = (Invoke-GhApi 'POST' "repos/$Repo/git/trees" $tf | ConvertFrom-Json).sha
Write-Host ("   tree=$treeSha")

Write-Host '▶ 建 commit' -ForegroundColor Cyan
$parents = @()
if ($ParentSha) { $parents = @($ParentSha) }
$commitBody = @{ message = $Message; tree = $treeSha; parents = $parents } | ConvertTo-Json -Compress
$cf = Join-Path $tmp 'commit.json'
[System.IO.File]::WriteAllText($cf, $commitBody, (New-Object System.Text.UTF8Encoding($false)))
$commitSha = (Invoke-GhApi 'POST' "repos/$Repo/git/commits" $cf | ConvertFrom-Json).sha
Write-Host ("   commit=$commitSha")

Write-Host '▶ 指向分支' -ForegroundColor Cyan
$refBody = @{ ref = "refs/heads/$Branch"; sha = $commitSha } | ConvertTo-Json -Compress
$rf = Join-Path $tmp 'ref.json'
[System.IO.File]::WriteAllText($rf, $refBody, (New-Object System.Text.UTF8Encoding($false)))
$existing = & gh api "repos/$Repo/git/ref/heads/$Branch" 2>$null
if ($LASTEXITCODE -eq 0) {
    Invoke-GhApi 'PATCH' "repos/$Repo/git/refs/heads/$Branch" $rf | Out-Null
} else {
    Invoke-GhApi 'POST' "repos/$Repo/git/refs" $rf | Out-Null
}
Write-Host ("✔ 完成：https://github.com/{0}/tree/{1}" -f $Repo, $Branch) -ForegroundColor Green
Remove-Item -Recurse -Force $tmp
