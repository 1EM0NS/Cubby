# 设计原则守卫：把 P1-P4 从文档约定变成不可绕过的红线
# 规则说明见 技术方案.md 第 13.5 节
# 本地也能直接跑： pwsh ./tools/scripts/guard.ps1

$ErrorActionPreference = 'Stop'

$rules = @(
    @{ Pattern = 'SetWindowsHookEx';      Principle = 'P1'; Why = '禁止注册全局鼠标钩子' },
    @{ Pattern = 'SetParent';             Principle = 'P3'; Why = '禁止把窗口挂载到 WorkerW' },
    @{ Pattern = 'WS_EX_TRANSPARENT';     Principle = 'P2'; Why = '整窗穿透会让盒子无法交互' },
    @{ Pattern = 'LVM_SETITEMPOSITION32'; Principle = 'P4'; Why = '禁止写入桌面图标位置' },
    @{ Pattern = 'LVM_SETITEMPOSITION';   Principle = 'P4'; Why = '禁止写入桌面图标位置' },
    @{ Pattern = 'LVM_ARRANGE';           Principle = 'P4'; Why = '禁止重排桌面图标' }
)

$roots = @('src', 'tools') | Where-Object { Test-Path $_ }
if (-not $roots) {
    Write-Host '未找到 src/ 或 tools/，跳过设计原则守卫（M0 起生效）'
    exit 0
}

$violations = @()
$files = Get-ChildItem -Path $roots -Recurse -Include *.cs -File |
    Where-Object { $_.FullName -notmatch '[\\/](bin|obj)[\\/]' }

foreach ($file in $files) {
    $lines = @(Get-Content -LiteralPath $file.FullName)
    for ($i = 0; $i -lt $lines.Count; $i++) {
        foreach ($rule in $rules) {
            if ($lines[$i] -notmatch $rule.Pattern) { continue }

            $from = [Math]::Max(0, $i - 2)
            $context = $lines[$from..$i] -join "`n"
            if ($context -match 'guard-exempt') { continue }

            $violations += [PSCustomObject]@{
                File = $file.FullName.Replace($PWD.Path + '\', '')
                Line = $i + 1
                Rule = "$($rule.Principle) $($rule.Why)"
                Code = $lines[$i].Trim()
            }
        }
    }
}

if ($violations.Count -gt 0) {
    foreach ($v in $violations) {
        Write-Host "::error file=$($v.File),line=$($v.Line)::$($v.Rule)"
    }
    Write-Host ''
    Write-Host "设计原则守卫拦截了 $($violations.Count) 处调用：" -ForegroundColor Red
    $violations | Format-Table -AutoSize | Out-String | Write-Host
    Write-Host '如确有必要，请在调用行前两行内加入 // guard-exempt: <原因>，并先补一篇 ADR。'
    exit 1
}

Write-Host "设计原则守卫通过：未发现违反 P1-P4 的调用" -ForegroundColor Green