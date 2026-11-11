#Requires -Version 5.1
<#
.SYNOPSIS
    HdcMirror 仓库隐私扫描闸门（发布前必跑）。

.DESCRIPTION
    扫描仓库中的文本文件，找出会指向「具体某个人 / 具体某一台设备」的信息：
    真实内网地址、设备名、机型、蓝牙地址与 MAC、本地用户路径、
    手机号、邮箱、序列号、访问令牌。

    判定标准（只有一条）：
      这条信息能不能定位到某一个具体的人 / 某一台具体的设备？
        能   -> 命中，必须脱敏
        不能 -> 放行（例如协议端口 8012、通用 shell uid 2000、回环地址 127.0.0.1）

    默认排除：.git\、obj\、bin\、.vs\、_probe\、docs\reference\（上游公开源码）、
    以及本脚本与 pre-push-check.ps1 自身（内含检测关键词，属工具组成部分）。

    规范占位值（写进仓库后不会被本脚本再次命中）：
      设备地址 -> <DEVICE_ADDR>，或 RFC 5737 文档地址 192.0.2.10:5555（示例地址）
      设备名   -> My Phone
      机型     -> HarmonyOS NEXT 真机（型号略）

.PARAMETER Root
    扫描根目录，默认是本脚本所在目录的上一级（即仓库根）。

.PARAMETER Fix
    保守自动替换。仅处理结构确定、替换值唯一的情况：
      内网地址 / 设备名 / 机型 / MAC 与蓝牙设备 ID / 本地用户路径。
    人名、手机号、邮箱、序列号、令牌一律只报告、不动手。

.EXAMPLE
    powershell -NoProfile -ExecutionPolicy Bypass -File tools\privacy-scan.ps1
    退出码 0 = 干净；1 = 存在待处理项。

.EXAMPLE
    powershell -NoProfile -ExecutionPolicy Bypass -File tools\privacy-scan.ps1 -Fix
    先自动替换确定项，再重新扫描并报告剩余项。
#>
[CmdletBinding()]
param(
    [string]$Root,
    [switch]$Fix
)

$ErrorActionPreference = 'Stop'
try { [Console]::OutputEncoding = [System.Text.Encoding]::UTF8 } catch { }

if (-not $Root) { $Root = Split-Path -Parent $PSScriptRoot }
$Root = (Resolve-Path -LiteralPath $Root).Path

# ── 个人昵称以字符码拼装：避免扫描器把自身含有的昵称扫成命中项 ──────────────
$Nick = -join [char[]](0x9152, 0x58A8)          # 操作者设备昵称（2 字）

# ── 扫描范围 ────────────────────────────────────────────────────────────────
$TextExt = @(
    '.md', '.cs', '.proto', '.ps1', '.sh', '.cmd', '.bat',
    '.csproj', '.props', '.sln', '.targets',
    '.json', '.yaml', '.yml', '.xml', '.toml', '.ini',
    '.txt', '.csv', '.log', '.config', '.resx'
)
$BareNames = @('.gitignore', '.gitattributes', '.editorconfig', '.npmrc')
$ExcludeGlobs = @(
    '\.git\*', '*\obj\*', '*\bin\*', '*.vs\*', '*\node_modules\*',
    '_probe\*', 'docs\reference\*'
)
$ExcludeSelf = @('tools\privacy-scan.ps1', 'tools\pre-push-check.ps1')

# ── 白名单：功能性、公开、非个人的命中项，逐条写明理由 ──────────────────────
# 每行格式：相对路径glob|规则ID|理由。只允许放「公开型号/公开常量」这类，
# 个人身份信息（真机 IP、设备昵称、SN、UDID）一律改掉，不许进白名单。
$AllowListPath = Join-Path $PSScriptRoot 'privacy-allowlist.txt'
$AllowEntries = @()
if (Test-Path -LiteralPath $AllowListPath) {
    foreach ($entryLine in (Get-Content -LiteralPath $AllowListPath -Encoding UTF8)) {
        $trimmed = $entryLine.Trim()
        if ($trimmed.Length -eq 0 -or $trimmed.StartsWith('#')) { continue }
        $parts = $trimmed -split '\|'
        if ($parts.Count -ge 2) {
            $AllowEntries += [pscustomobject]@{
                Glob   = $parts[0].Trim()
                RuleId = $parts[1].Trim()
                Reason = $(if ($parts.Count -ge 3) { $parts[2].Trim() } else { '' })
            }
        }
    }
}

# ── 检测规则 ────────────────────────────────────────────────────────────────
# Fixable = $true 的规则才会被 -Fix 自动改写，且替换值唯一确定。
$Rules = @(
    @{
        Id = 'PRIVATE_IP'; Desc = '真实内网地址'; Fixable = $true
        Pattern = '(?<![\d.])(?:192\.168\.\d{1,3}\.\d{1,3}|10\.\d{1,3}\.\d{1,3}\.\d{1,3}|172\.(?:1[6-9]|2\d|3[01])\.\d{1,3}\.\d{1,3})(?::\d{1,5})?(?![\d.])'
        Suggest = '192.0.2.10:5555（RFC 5737 文档地址）或 <DEVICE_ADDR>'
    },
    @{
        Id = 'DEVICE_NAME_D'; Desc = '设备名/昵称'; Fixable = $true
        Pattern = $Nick + '的'
        Suggest = 'My '
    },
    @{
        Id = 'DEVICE_NAME'; Desc = '设备名/昵称'; Fixable = $true
        Pattern = $Nick
        Suggest = 'My'
    },
    @{
        Id = 'DEVICE_NAME_S'; Desc = '设备名/昵称（拼音）'; Fixable = $true
        Pattern = '\bJiumo\b'
        Suggest = 'My'
    },
    @{
        Id = 'DEVICE_MODEL'; Desc = '真机机型'; Fixable = $false
        Pattern = '(?:HUAWEI\s+)?(?:ALN-AL\d+|Mate\s*60\s*Pro)'
        Suggest = 'HarmonyOS NEXT 真机（型号略）'
    },
    @{
        Id = 'DEVICE_MODEL'; Desc = '品牌名（泛指化）'; Fixable = $false
        Pattern = '\bHUAWEI\b'
        Suggest = '删除品牌名或写成「真机（型号略）」'
    },
    @{
        Id = 'MAC_ADDR'; Desc = 'MAC 地址'; Fixable = $true
        Pattern = '(?<![0-9A-Fa-f:])(?:[0-9A-Fa-f]{2}[:-]){5}[0-9A-Fa-f]{2}(?![0-9A-Fa-f:])'
        Suggest = 'XX:XX:XX:XX:XX:XX'
    },
    @{
        Id = 'BT_DEVICE_ID'; Desc = '蓝牙设备 ID'; Fixable = $true
        Pattern = 'DEV_[0-9A-Fa-f]{10,}'
        Suggest = 'DEV_XXXXXXXXXXXXXXXX'
    },
    @{
        Id = 'LOCAL_PATH'; Desc = '本地用户目录路径'; Fixable = $true
        Pattern = 'C:\\Users\\[A-Za-z0-9_\-.]+\\'
        Suggest = '%USERPROFILE%\'
    },
    @{
        Id = 'IPV6_LOCAL'; Desc = 'IPv6 本地链路地址'; Fixable = $false
        Pattern = '(?i)\bfe80::[0-9a-f:]{4,}'
        Suggest = '<DEVICE_ADDR>'
    },
    @{
        Id = 'PHONE'; Desc = '手机号'; Fixable = $false
        Pattern = '(?<!\d)1[3-9]\d{9}(?!\d)'
        Suggest = '1XX XXXX XXXX（打码）'
    },
    @{
        Id = 'EMAIL'; Desc = '邮箱地址'; Fixable = $false
        Pattern = '[A-Za-z0-9._%+\-]+@[A-Za-z0-9.\-]+\.[A-Za-z]{2,}'
        Allow   = '(?i)^(?:git|noreply)@github\.com$|(?i)@users\.noreply\.github\.com$|(?i)@example\.(?:com|org|net)$'
        Suggest = '删掉或写成 user@example.com'
    },
    @{
        Id = 'TOKEN'; Desc = '访问令牌/密钥形态'; Fixable = $false
        Pattern = '(?i)(?:bearer\s+[A-Za-z0-9._\-]{12,}|gh[pousr]_[A-Za-z0-9]{20,}|github_pat_[A-Za-z0-9_]{20,}|sk-[A-Za-z0-9]{20,}|AKIA[0-9A-Z]{16}|-----BEGIN [A-Z ]*PRIVATE KEY-----)'
        Suggest = '删除；改由环境变量注入'
    },
    @{
        Id = 'SERIAL'; Desc = '序列号形态'; Fixable = $false
        Pattern = '(?i)\b(?:SN|S/N|serial(?:number)?)\s*[:=]\s*[A-Za-z0-9]{8,}\b'
        Suggest = '<SERIAL>'
    },
    @{
        Id = 'CN_NAME'; Desc = '中文人名+设备词形态'; Fixable = $false
        Pattern = '[\u4e00-\u9fa5]{2,4}的(?:手机|平板|设备|真机|电脑|笔记本|WiFi|Mate|手机壳)'
        Suggest = 'My Phone / 我的手机'
    }
)

# 中文人名启发式的停用前缀：这些是限定词/代词，不是姓名，命中即跳过。
# 目的：避免「真正的设备」「上次选择的设备」这类正常表述被判成个人信息。
$NameStopPrefix = @(
    '真正', '所有', '上次', '这次', '本次', '其他', '其它', '本机', '我们', '你们',
    '他们', '她们', '它们', '自己', '什么', '这个', '那个', '一个', '现有', '已有',
    '当前', '选中', '选择', '目标', '被测', '每台', '任何', '全部', '上述', '以下',
    # 2026-11-11 补充：动词/动补结构也会被误判成人名（「找回来的设备」「已记住的设备」）。
    '回来', '找回', '镜像', '记住', '已记', '要镜', '删除', '移除', '添加', '断开', '连接', '扫描',
    '管理', '支持', '报告', '打开', '关闭', '显示', '隐藏', '设置', '切换', '获取', '读取', '正在',
    '下的', '中的', '上', '倒序', '保存', '匹配', '菜单', '全部', '同一',
    '所有', '我的', '你的', '他的', '她的', '该台', '两台', '多台', '同一',
    '具体', '特定', '真实', '实际', '同一台', '某一台', '哪一',
    '连接', '已连接', '未连接', '可连接', '发现', '可发现', '找到', '扫描', '扫描到',
    '识别', '搜索', '检测', '绑定', '配对', '添加', '删除', '正在', '自动', '手动',
    '默认', '推荐', '最近', '附近', '在线', '离线', '本地', '远程', '待选', '列表',
    '所有可', '当前可', '这次连', '上次连',
    '匹配到', '置菜单下', '上次看', '菜单下的'
    '无线', '调试', '蓝牙', '投屏', '音画', '重启', '重启后', '同一个', '已配对', '未配对'
)

function Get-FixValue {
    param([string]$Id, [string]$Text)
    switch ($Id) {
        'PRIVATE_IP'    { if ($Text -match ':\d{1,5}$') { return '192.0.2.10:5555' } else { return '192.0.2.10' } }
        'DEVICE_NAME_D' { return 'My ' }
        'DEVICE_NAME_S' { return 'My ' }
        'DEVICE_NAME'   { return 'My' }
        'DEVICE_MODEL'  { return 'HarmonyOS NEXT 真机（型号略）' }
        'MAC_ADDR'      { return 'XX:XX:XX:XX:XX:XX' }
        'BT_DEVICE_ID'  { return 'DEV_XXXXXXXXXXXXXXXX' }
        'LOCAL_PATH'    { return '%USERPROFILE%\' }
        default         { return $null }
    }
}

function Test-Allowed {
    param($Rule, [string]$Text)
    if (-not $Rule.ContainsKey('Allow') -or -not $Rule.Allow) { return $false }
    return ($Text -match $Rule.Allow)
}

# ── 收集待扫描文件 ──────────────────────────────────────────────────────────
$AllFiles = Get-ChildItem -LiteralPath $Root -Recurse -File -Force -ErrorAction SilentlyContinue
$Files = @()
foreach ($f in $AllFiles) {
    $rel = $f.FullName.Substring($Root.Length + 1)
    $skip = $false
    foreach ($g in $ExcludeGlobs) { if ($rel -like $g) { $skip = $true; break } }
    if ($skip) { continue }
    if ($ExcludeSelf -contains $rel) { continue }
    if ($BareNames -contains $f.Name) { $Files += $f; continue }
    if ($TextExt -contains ([System.IO.Path]::GetExtension($f.Name).ToLower())) { $Files += $f }
}

# ── 扫描函数 ────────────────────────────────────────────────────────────────
function Invoke-Scan {
    param([System.IO.FileInfo[]]$Targets)
    $found = New-Object System.Collections.ArrayList
    foreach ($f in $Targets) {
        $rel = $f.FullName.Substring($Root.Length + 1)
        $raw = Get-Content -LiteralPath $f.FullName -Raw -Encoding UTF8 -ErrorAction SilentlyContinue
        if ($null -eq $raw -or $raw.Length -eq 0) { continue }
        $lineNo = 0
        foreach ($line in ($raw -split "`r?`n")) {
            $lineNo++
            foreach ($rule in $Rules) {
                foreach ($m in [regex]::Matches($line, $rule.Pattern)) {
                    if (Test-Allowed -Rule $rule -Text $m.Value) { continue }
                    $whitelisted = $false
                    foreach ($ae in $AllowEntries) {
                        if ($ae.RuleId -eq $rule.Id -and $rel -like $ae.Glob) { $whitelisted = $true; break }
                    }
                    if ($whitelisted) { continue }
                    if ($rule.Id -eq 'CN_NAME') {
                        # 正则的 {2,4} 会多吃前面的字（如「一句真正的设备」→ 前缀「句真正」），
                        # 所以按「以停用词结尾」判断，而不是整体相等。
                        $prefix = $m.Value -replace '的.*$', ''
                        $isStop = $false
                        foreach ($s in $NameStopPrefix) {
                            if ($prefix.EndsWith($s)) { $isStop = $true; break }
                        }
                        if ($isStop) { continue }
                    }
                    $found.Add([pscustomobject]@{
                        File    = $rel
                        Line    = $lineNo
                        RuleId  = $rule.Id
                        Desc    = $rule.Desc
                        Match   = $m.Value
                        Suggest = $rule.Suggest
                        Fixable = $rule.Fixable
                    }) | Out-Null
                }
            }
        }
    }
    return $found
}

function Show-Hits {
    param($Hits)
    if ($Hits.Count -eq 0) { return }
    $byFile = $Hits | Group-Object -Property File | Sort-Object Name
    foreach ($grp in $byFile) {
        Write-Host ("  " + $grp.Name) -ForegroundColor Yellow
        foreach ($h in ($grp.Group | Sort-Object Line)) {
            $flag = '只报告'
            if ($h.Fixable) { $flag = '可自动修' }
            Write-Host ("    L{0,-5} [{1}] {2}" -f $h.Line, $h.Desc, $h.Match) -ForegroundColor Red
            Write-Host ("           └─ 建议：{0}　（{1}）" -f $h.Suggest, $flag) -ForegroundColor DarkGray
        }
    }
}

# ── 执行 ────────────────────────────────────────────────────────────────────
Write-Host ""
Write-Host "═══ 隐私扫描 ═══" -ForegroundColor Cyan
Write-Host ("  根目录：{0}" -f $Root) -ForegroundColor DarkGray
Write-Host ("  待扫文件：{0} 个" -f $Files.Count) -ForegroundColor DarkGray
Write-Host ""

$hits = Invoke-Scan -Targets $Files

if ($Fix -and $hits.Count -gt 0) {
    $fixable = @($Rules | Where-Object { $_.Fixable })
    $changedFiles = 0
    Write-Host "── 保守自动替换（仅确定项） ──" -ForegroundColor Cyan
    foreach ($f in $Files) {
        $rel = $f.FullName.Substring($Root.Length + 1)
        $bytes = [System.IO.File]::ReadAllBytes($f.FullName)
        $hadBom = ($bytes.Length -ge 3 -and $bytes[0] -eq 0xEF -and $bytes[1] -eq 0xBB -and $bytes[2] -eq 0xBF)
        $raw = [System.IO.File]::ReadAllText($f.FullName, [System.Text.Encoding]::UTF8)
        $orig = $raw
        foreach ($rule in $fixable) {
            $rid = $rule.Id
            $cb = [System.Text.RegularExpressions.MatchEvaluator]{
                param($m)
                if (Test-Allowed -Rule $rule -Text $m.Value) { return $m.Value }
                $v = Get-FixValue -Id $rid -Text $m.Value
                if ($null -eq $v) { return $m.Value }
                return $v
            }
            $raw = [regex]::Replace($raw, $rule.Pattern, $cb)
        }
        if ($raw -ne $orig) {
            $enc = New-Object System.Text.UTF8Encoding($hadBom)
            [System.IO.File]::WriteAllText($f.FullName, $raw, $enc)
            $changedFiles++
            Write-Host ("  ✔ 已改写 {0}" -f $rel) -ForegroundColor Green
        }
    }
    if ($changedFiles -eq 0) { Write-Host "  （无需改写的文件）" -ForegroundColor DarkGray }
    Write-Host ""
    Write-Host "── 重新扫描 ──" -ForegroundColor Cyan
    $hits = Invoke-Scan -Targets $Files
}

# 中文人名是启发式规则，误报率高（「用户的设备」「已知的设备」都会命中），
# 因此只报告、不阻断：确定性规则（IP/路径/令牌/MAC 等）仍然一票否决。
$heuristic = @($hits | Where-Object { $_.RuleId -eq 'CN_NAME' })
$hard      = @($hits | Where-Object { $_.RuleId -ne 'CN_NAME' })

if ($hard.Count -eq 0) {
    if ($heuristic.Count -gt 0) {
        Write-Host ("⚠ 启发式人名匹配 {0} 处（只报告，不阻断；确认是普通名词即可忽略）：" -f $heuristic.Count) -ForegroundColor Yellow
        Write-Host ""
        Show-Hits -Hits $heuristic
        Write-Host ""
    }

    Write-Host "✔ 未发现确定性个人信息。可以进入下一道闸门。" -ForegroundColor Green
    Write-Host ""
    exit 0
}

Write-Host ("✗ 发现 {0} 处待处理项，涉及 {1} 个文件：" -f $hard.Count, ($hard | Select-Object -ExpandProperty File -Unique).Count) -ForegroundColor Red
Write-Host ""
Show-Hits -Hits $hard
Write-Host ""
Write-Host "处理建议：" -ForegroundColor Cyan
Write-Host "  · 结构确定的项：powershell -NoProfile -ExecutionPolicy Bypass -File tools\privacy-scan.ps1 -Fix"
Write-Host "  · 人名/手机号/邮箱/令牌：必须人工改写，不要提交"
Write-Host "  · 规则见仓库根 RULES.md「隐私红线」"
Write-Host ""
exit 1
