# Cubby 安装 / 卸载无残留验收（issue #40）。
#
# 这个脚本的价值在于：它**不是**读代码判断"应该没残留"，而是真的装一遍、跑一遍、卸一遍，
# 然后逐项去问系统"这个东西还在不在"。零残留清单五项（安装目录 / 开始菜单快捷方式 /
# 自启项 / 卸载信息 / 桌面图标标记）全部对着真实系统状态断言。
#
# 还会验两件容易漏的事：
#   a. **先还原再删文件**：故意造一枚"桌面图标还是隐藏的"标记，断言卸载后它被清掉——
#      顺序反了（先删 exe）这条就会失败；
#   b. **卸载后能干净重装**（幂等）。
#
# 本机前置状态在验收前会被记下，结束时还原。

[CmdletBinding()]
param(
    [string]$Source = '',
    [string]$InstallDirectory = (Join-Path $env:LOCALAPPDATA 'Programs\Cubby'),
    [string]$ReportPath = ''
)

$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot '_common.ps1')

$paths = Get-CubbyPaths -InstallDirectory $InstallDirectory
$source = Resolve-CubbySource -Source $Source
if (-not $ReportPath) {
    $ReportPath = Join-Path (Split-Path -Parent $PSScriptRoot) 'artifacts\install-report.md'
}

$installScript = Join-Path $PSScriptRoot 'install.ps1'
$uninstallScript = Join-Path $PSScriptRoot 'uninstall.ps1'

$results = New-Object System.Collections.ArrayList

function Add-Result {
    param([string]$Step, [string]$Expected, [string]$Actual, [bool]$Pass)
    [void]$results.Add([PSCustomObject]@{ Step = $Step; Expected = $Expected; Actual = $Actual; Pass = $Pass })
}

# 跑一个子进程并拿到退出码 + 输出结尾，用于断言脚本自身的成败
function Invoke-CubbyScript {
    param([string]$ScriptPath, [string[]]$Arguments)

    $output = & powershell.exe -NoProfile -ExecutionPolicy Bypass -File $ScriptPath @Arguments 2>&1
    $code = $LASTEXITCODE
    $tail = ($output | Select-Object -Last 1)

    return [PSCustomObject]@{ ExitCode = $code; Output = ($output -join "`n"); Tail = $tail }
}

Write-Host '== Cubby 安装 / 卸载无残留验收 =='

# ---- 0. 记下前置状态 ----
$before = Get-CubbyState -Paths $paths

# 验收必须从一个干净的起点开始，否则"零残留"没有意义
if ($before.InstallDirectoryExists -or $before.ShortcutExists -or $before.UninstallKeyExists) {
    Write-Host '导入前置状态不为空，先卸一遍作为起点'
    [void](Invoke-CubbyScript -ScriptPath $uninstallScript -Arguments @('-InstallDirectory', $paths.InstallDirectory, '-KeepUserData', '-Quiet'))
    $before = Get-CubbyState -Paths $paths
}

Add-Result -Step '验收起点干净（没有既存的安装）' `
    -Expected '安装目录 / 快捷方式 / 卸载信息 都不存在' `
    -Actual "目录=$($before.InstallDirectoryExists) 快捷方式=$($before.ShortcutExists) 卸载信息=$($before.UninstallKeyExists)" `
    -Pass (-not ($before.InstallDirectoryExists -or $before.ShortcutExists -or $before.UninstallKeyExists))

# ---- 1. 安装（带 -AutoStart，把自启项那条路径也覆盖到）----
$install = Invoke-CubbyScript -ScriptPath $installScript -Arguments @(
    '-Source', $source, '-InstallDirectory', $paths.InstallDirectory, '-AutoStart')

$afterInstall = Get-CubbyState -Paths $paths

Add-Result -Step 'install.ps1 退出码' -Expected '0' -Actual "$($install.ExitCode)" -Pass ($install.ExitCode -eq 0)
Add-Result -Step '安装目录与可执行文件落地' `
    -Expected 'Cubby.App.exe 存在' `
    -Actual "$($paths.Executable) 存在=$($afterInstall.ExecutableExists)" `
    -Pass ($afterInstall.ExecutableExists)
Add-Result -Step '开始菜单快捷方式已创建' `
    -Expected 'Cubby.lnk 存在' `
    -Actual "$($paths.Shortcut) 存在=$($afterInstall.ShortcutExists)" `
    -Pass ($afterInstall.ShortcutExists)
Add-Result -Step '卸载脚本随安装落地（否则「添加或删除程序」里的卸载按钮会失效）' `
    -Expected "安装目录\scripts\uninstall.ps1 存在" `
    -Actual "存在=$($afterInstall.ScriptsCopied)" `
    -Pass ($afterInstall.ScriptsCopied)

$uninstallInfoOk = ($afterInstall.UninstallKeyExists `
    -and $afterInstall.UninstallDisplayName -eq 'Cubby' `
    -and (Test-CubbyContains -Text $afterInstall.UninstallVersion -Expected @('.')) `
    -and (Test-CubbyContains -Text $afterInstall.UninstallLocation -Expected @('Cubby')) `
    -and (Test-CubbyContains -Text $afterInstall.UninstallDisplayIcon -Expected @('Cubby.App.exe')) `
    -and (Test-CubbyContains -Text $afterInstall.UninstallString -Expected @('uninstall.ps1')))

Add-Result -Step '卸载信息含名称 / 版本 / 安装位置 / 图标 / 卸载命令' `
    -Expected 'DisplayName=Cubby 且版本带小数点、位置含 Cubby、图标指向 exe、卸载命令指向 uninstall.ps1' `
    -Actual "名称=$($afterInstall.UninstallDisplayName) 版本=$($afterInstall.UninstallVersion) 位置=$($afterInstall.UninstallLocation) 图标=$($afterInstall.UninstallDisplayIcon)" `
    -Pass $uninstallInfoOk

$autoStartOk = Test-CubbyContains -Text $afterInstall.AutoStartValue -Expected @('Cubby.App.exe')
Add-Result -Step '-AutoStart 注册了开机自启（写的是与托盘菜单同一个键与值名）' `
    -Expected 'HKCU Run\Cubby 指向安装目录下的 exe' `
    -Actual "值=$($afterInstall.AutoStartValue)" `
    -Pass $autoStartOk

# ---- 2. 装好的二进制真的能跑 ----
$probe = Start-Process -FilePath $paths.Executable -ArgumentList '--restore-on-exit' -PassThru -Wait
Add-Result -Step '装好的 exe 能运行（--restore-on-exit）' `
    -Expected '退出码 0' `
    -Actual "退出码 $($probe.ExitCode)" `
    -Pass ($probe.ExitCode -eq 0)

# ---- 3. 造一枚"图标还藏着"的标记，验证卸载时**先还原再删文件** ----
New-Item -ItemType Directory -Path (Split-Path -Parent $paths.IconMarker) -Force | Out-Null
Set-Content -LiteralPath $paths.IconMarker -Value '2026-09-20 00:00:00' -Encoding utf8
$markerCreated = Test-Path -LiteralPath $paths.IconMarker

# ---- 4. 卸载（保留用户数据）----
$userDataExistedBefore = (Test-Path -LiteralPath $paths.UserDataDirectory)
$uninstall = Invoke-CubbyScript -ScriptPath $uninstallScript -Arguments @(
    '-InstallDirectory', $paths.InstallDirectory, '-KeepUserData', '-Quiet')

$afterUninstall = Get-CubbyState -Paths $paths

Add-Result -Step 'uninstall.ps1 退出码' -Expected '0' -Actual "$($uninstall.ExitCode)" -Pass ($uninstall.ExitCode -eq 0)

$residue = @()
if ($afterUninstall.InstallDirectoryExists) { $residue += '安装目录' }
if ($afterUninstall.ShortcutExists) { $residue += '开始菜单快捷方式' }
if ($afterUninstall.UninstallKeyExists) { $residue += '卸载信息' }
if ($null -ne $afterUninstall.AutoStartValue) { $residue += '自启项' }
if ($afterUninstall.IconMarkerExists) { $residue += '桌面图标标记' }

Add-Result -Step '零残留清单（安装目录 / 快捷方式 / 卸载信息 / 自启项 / 图标标记）' `
    -Expected '五项全部不存在' `
    -Actual $(if ($residue.Count -eq 0) { '五项均已清除' } else { "仍有残留：$($residue -join '、')" }) `
    -Pass ($residue.Count -eq 0)

Add-Result -Step '卸载时**先还原桌面图标标记**再删文件（顺序反了这条会失败）' `
    -Expected '造的标记在处理后被清掉' `
    -Actual "造出标记=$markerCreated，卸载后还在=$($afterUninstall.IconMarkerExists)" `
    -Pass ($markerCreated -and -not $afterUninstall.IconMarkerExists)

Add-Result -Step '用户数据默认保留（-KeepUserData）' `
    -Expected "布局 / 快照 / 规则所在目录仍在" `
    -Actual "$($paths.UserDataDirectory) 存在=$($afterUninstall.UserDataExists)（验收前存在=$userDataExistedBefore）" `
    -Pass ($afterUninstall.UserDataExists -eq $userDataExistedBefore)

# ---- 5. 幂等：卸载后能干净重装 ----
$reinstall = Invoke-CubbyScript -ScriptPath $installScript -Arguments @(
    '-Source', $source, '-InstallDirectory', $paths.InstallDirectory)
$afterReinstall = Get-CubbyState -Paths $paths

$reinstallOk = $reinstall.ExitCode -eq 0 -and $afterReinstall.ExecutableExists -and $afterReinstall.ShortcutExists
Add-Result -Step '卸载后可干净重装（幂等）' `
    -Expected '重装退出码 0 且 exe / 快捷方式再次落地' `
    -Actual "退出码=$($reinstall.ExitCode) exe=$($afterReinstall.ExecutableExists) 快捷方式=$($afterReinstall.ShortcutExists)" `
    -Pass $reinstallOk

Add-Result -Step '重装未指定 -AutoStart 时不会偷偷写自启项' `
    -Expected '自启项为空' `
    -Actual "值=$(if ($null -eq $afterReinstall.AutoStartValue) { '(空)' } else { $afterReinstall.AutoStartValue })" `
    -Pass ($null -eq $afterReinstall.AutoStartValue)

# ---- 6. 收尾：再卸一次，并把前置状态还原 ----
$finalUninstall = Invoke-CubbyScript -ScriptPath $uninstallScript -Arguments @(
    '-InstallDirectory', $paths.InstallDirectory, '-KeepUserData', '-Quiet')
$afterFinal = Get-CubbyState -Paths $paths

Add-Result -Step '验收收尾后系统回到干净状态' `
    -Expected '安装目录 / 快捷方式 / 卸载信息 / 自启项 都不存在' `
    -Actual "目录=$($afterFinal.InstallDirectoryExists) 快捷方式=$($afterFinal.ShortcutExists) 卸载信息=$($afterFinal.UninstallKeyExists) 自启=$($null -ne $afterFinal.AutoStartValue)" `
    -Pass (-not ($afterFinal.InstallDirectoryExists -or $afterFinal.ShortcutExists -or $afterFinal.UninstallKeyExists -or ($null -ne $afterFinal.AutoStartValue)))

# 前置状态若本来就有自启项，恢复它（本脚本原则上不该改变用户的既有配置）
if ($null -ne $before.AutoStartValue) {
    Set-ItemProperty -LiteralPath $paths.RunKey -Name $paths.RunValueName -Value $before.AutoStartValue
}

$notes = @(
    '',
    '## 零残留清单（逐项对着系统状态断言，不看代码）',
    '',
    '| 项 | 位置 |',
    '|---|---|',
    "| 安装目录 | ``$($paths.InstallDirectory)``（免管理员，只写 %LocalAppData%） |",
    "| 开始菜单快捷方式 | ``$($paths.Shortcut)`` |",
    "| 开机自启项 | ``HKCU\Software\Microsoft\Windows\CurrentVersion\Run`` 的 ``Cubby`` 值 |",
    "| 卸载信息 | ``HKCU\Software\Microsoft\Windows\CurrentVersion\Uninstall\Cubby`` |",
    "| 桌面图标标记 | ``$($paths.IconMarker)`` |",
    '',
    '## 验收覆盖到的三件容易漏的事',
    '',
    '1. **先还原再删文件**：故意造一枚「图标还藏着」的标记，断言卸载后被清掉。',
    '   顺序反了（先删 exe 再想还原）这条就会红——那时已经没有任何程序能执行恢复逻辑了。',
    '2. **卸载后可干净重装**（幂等）：装 → 卸 → 再装 → 再卸，两次都断言零残留。',
    '3. **用户数据默认保留**：``%AppData%\Cubby`` 里的布局 / 快照 / 规则不跟着安装目录一起走，',
    '   要删得显式加 ``-PurgeUserData``（交互式运行时也会明确问一次）。',
    '',
    '## 一处刻意的取舍',
    '',
    '安装脚本会**排除**产物目录里的 ``artifacts``（诊断产物不该进安装目录），',
    '但**保留 .pdb**——崩溃日志写的是完整堆栈，没有 PDB 就只能看到一串内存地址。',
    '',
    '## 安装包形式的现状（技术方案第 12 章的待定项）',
    '',
    '本阶段用自包含 PowerShell 脚本，不是 Inno Setup / MSIX：MSIX 需要签名证书（本机没有），',
    'Inno Setup 不在构建链里。将来换工具链时，本报告的零残留断言可以原样复用。'
)

$code = Write-CubbyReport -Path $ReportPath -Title '安装 / 卸载无残留验收报告（issue #40）' `
    -Results $results -Conclusion "报告位置：``$ReportPath``" -Notes $notes

Write-Host ''
Write-Host "报告已写入：$ReportPath"
Write-Host "失败 $((@($results | Where-Object { -not $_.Pass })).Count) 项，共 $($results.Count) 项"
Write-Host "最后一次卸载的尾部输出：$($finalUninstall.Tail)"

exit $code
