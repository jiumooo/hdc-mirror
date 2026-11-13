<#
    推送批准器：只有用户当面说「推」之后才允许运行。
    生成 .git/ALLOW_PUSH，pre-push 钩子看到它就放行一次（30 分钟内有效，用后自动删除）。
#>
[CmdletBinding()]
param([switch]$Revoke)

$gitDir = Join-Path (Split-Path -Parent $PSScriptRoot) '.git'
$flag = Join-Path $gitDir 'ALLOW_PUSH'

if ($Revoke) {
    if (Test-Path $flag) { Remove-Item $flag -Force }
    Write-Host '已撤销推送批准。' -ForegroundColor Yellow
    return
}

[IO.File]::WriteAllText($flag, (Get-Date).ToString('o'), (New-Object System.Text.UTF8Encoding($false)))
Write-Host '已批准一次推送（30 分钟内有效，推送后自动作废）。' -ForegroundColor Green