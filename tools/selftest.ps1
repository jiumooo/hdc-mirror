# 离线自检入口：编译并运行 HdcMirror.SelfTest，失败时退出码 1。
# 用法：powershell -NoProfile -ExecutionPolicy Bypass -File tools\selftest.ps1
# 说明：不依赖手机、不联网、不需要 NuGet。

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$dotnet = 'C:\dotnet8\dotnet.exe'
if (-not (Test-Path -LiteralPath $dotnet)) {
    $dotnet = 'dotnet'
}

Write-Host '① 编译自检工程…' -ForegroundColor Cyan
& $dotnet build (Join-Path $root 'src\HdcMirror.SelfTest\HdcMirror.SelfTest.csproj') -c Debug --nologo -v q
if ($LASTEXITCODE -ne 0) {
    Write-Host '✗ 编译失败' -ForegroundColor Red
    exit 1
}

$exe = Join-Path $root 'src\HdcMirror.SelfTest\bin\Debug\net8.0-windows\HdcMirror.SelfTest.exe'
if (-not (Test-Path -LiteralPath $exe)) {
    Write-Host "✗ 找不到自检可执行文件：$exe" -ForegroundColor Red
    exit 1
}

Write-Host '② 运行断言…' -ForegroundColor Cyan
& $exe
$code = $LASTEXITCODE
if ($code -eq 0) {
    Write-Host '✔ 自检通过' -ForegroundColor Green
} else {
    Write-Host "✗ 自检失败（退出码 $code）" -ForegroundColor Red
}
exit $code
