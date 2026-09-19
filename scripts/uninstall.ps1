# Cubby 卸载脚本（issue #40）。
#
# 两条硬规矩：
# 1. **先还原，再删文件**：桌面图标是我们藏在用户机器上的副作用，顺序反了（先删 exe）就再也恢复不了；
# 2. **用户数据默认保留，并明确询问**：布局、快照、规则都在 %AppData%\Cubby 里，
#    卸载程序不等于用户想把它们扔掉。

[CmdletBinding()]
param(
    [string]$InstallDirectory = (Join-Path $env:LOCALAPPDATA 'Programs\Cubby'),
    [switch]$PurgeUserData,
    [switch]$KeepUserData,
    [switch]$Quiet
)

$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot '_common.ps1')

if ($PurgeUserData -and $KeepUserData) {
    throw '-PurgeUserData 与 -KeepUserData 是互斥的，只能选一个。'
}

$paths = Get-CubbyPaths -InstallDirectory $InstallDirectory

Write-Host "== Cubby 卸载 =="
Write-Host "安装目录: $($paths.InstallDirectory)"

# ---- 1. 先还原：桌面图标 + 开机自启 ----
# 这一步必须在删文件**之前**做。哪怕程序被强杀过、图标还藏着，--restore-on-exit 也能救回来，
# 因为它读的是磁盘上的标记文件，不依赖任何运行时状态。
if (Test-Path -LiteralPath $paths.Executable) {
    Write-Host "还原副作用：调用 --restore-on-exit"
    $process = Start-Process -FilePath $paths.Executable -ArgumentList '--restore-on-exit' -PassThru -Wait
    if ($process.ExitCode -ne 0) {
        Write-Warning "--restore-on-exit 返回 $($process.ExitCode)，继续卸载（下面还会逐项断言）。"
    }
}
else {
    Write-Host "还原副作用：安装目录里已没有 exe，跳过（将直接清残留项）"
}

# ---- 2. 请求正在运行的实例退出并等待 ----
$stopResult = Stop-CubbyProcess
Write-Host "运行实例：$stopResult"

# ---- 3. 删快捷方式 ----
if (Test-Path -LiteralPath $paths.Shortcut) {
    Remove-Item -LiteralPath $paths.Shortcut -Force
    Write-Host "已删除开始菜单快捷方式"
}

# ---- 4. 删卸载信息 ----
if (Test-Path -LiteralPath $paths.UninstallKey) {
    Remove-Item -LiteralPath $paths.UninstallKey -Recurse -Force
    Write-Host "已删除卸载信息（$($paths.UninstallKey)）"
}

# ---- 5. 兜底清自启项 ----
# --restore-on-exit 已经删过一次；这里再查一次是因为它可能因为文件缺失而没跑到。
$runProperty = Get-ItemProperty -LiteralPath $paths.RunKey -Name $paths.RunValueName -ErrorAction SilentlyContinue
if ($null -ne $runProperty) {
    Remove-ItemProperty -LiteralPath $paths.RunKey -Name $paths.RunValueName -Force
    Write-Host "已清除开机自启残留"
}

# ---- 6. 删安装目录 ----
if (Test-Path -LiteralPath $paths.InstallDirectory) {
    Remove-Item -LiteralPath $paths.InstallDirectory -Recurse -Force
    Write-Host "已删除安装目录"
}

# ---- 7. 兜底清桌面图标标记 ----
# 正常情况下 --restore-on-exit 已经清掉了；留着会导致下次启动白跑一次恢复逻辑。
if (Test-Path -LiteralPath $paths.IconMarker) {
    Remove-Item -LiteralPath $paths.IconMarker -Force
    Write-Host "已清除桌面图标标记文件"
}

# ---- 8. 用户数据：默认保留，明确询问 ----
$removeUserData = $false
if ($PurgeUserData) {
    $removeUserData = $true
}
elseif ($KeepUserData) {
    $removeUserData = $false
}
elseif (-not $Quiet -and [Environment]::UserInteractive -and $Host.Name -eq 'ConsoleHost') {
    Write-Host ""
    Write-Host "用户数据（布局、快照、归类规则）在：$($paths.UserDataDirectory)"
    $answer = Read-Host "要一起删除吗？输入 y 删除，其它任意键保留（默认保留）"
    if ($answer -eq 'y' -or $answer -eq 'Y') {
        $removeUserData = $true
    }
}

if ($removeUserData) {
    if (Test-Path -LiteralPath $paths.UserDataDirectory) {
        Remove-Item -LiteralPath $paths.UserDataDirectory -Recurse -Force
        Write-Host "已删除用户数据（-PurgeUserData）"
    }
}
else {
    Write-Host "用户数据已保留：$($paths.UserDataDirectory)"
}

# ---- 9. 卸载结果断言 ----
$state = Get-CubbyState -Paths $paths
$residue = @()
if ($state.InstallDirectoryExists) { $residue += '安装目录' }
if ($state.ShortcutExists) { $residue += '开始菜单快捷方式' }
if ($state.UninstallKeyExists) { $residue += '卸载信息' }
if ($null -ne $state.AutoStartValue) { $residue += '开机自启项' }
if ($state.IconMarkerExists) { $residue += '桌面图标标记' }
if ($state.RunningProcessIds.Count -gt 0) { $residue += "进程 pid $($state.RunningProcessIds -join ',')" }

Write-Host ""
if ($residue.Count -eq 0) {
    Write-Host "卸载完成：零残留（安装目录 / 快捷方式 / 卸载信息 / 自启项 / 图标标记 均已清除）"
    exit 0
}

Write-Warning "卸载后仍有残留：$($residue -join '、')"
exit 1
