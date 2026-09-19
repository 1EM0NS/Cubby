# Cubby 安装脚本（issue #40）。
#
# 为什么是自包含 PowerShell 而不是 Inno Setup / MSIX：技术方案第 12 章把安装包形式列为**待定**
# （MSIX 需要签名证书，本机没有；Inno Setup 不在构建链里）。自包含脚本可读、可审计、
# 能被 verify-install.ps1 自动化验收，且将来换成 Inno Setup 时「无残留」那套断言可以原样复用。
#
# 零提权设计：只写 HKCU 与 %LocalAppData%，不碰 HKLM、不写系统目录，所以不需要管理员。

[CmdletBinding()]
param(
    [string]$Source = '',
    [string]$InstallDirectory = (Join-Path $env:LOCALAPPDATA 'Programs\Cubby'),
    [string]$Version = '',
    [switch]$AutoStart
)

$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot '_common.ps1')

$paths = Get-CubbyPaths -InstallDirectory $InstallDirectory
$source = Resolve-CubbySource -Source $Source

if (-not (Test-Path -LiteralPath (Join-Path $source 'Cubby.App.exe'))) {
    throw "产物目录里没有 Cubby.App.exe：$source"
}

Write-Host "== Cubby 安装 =="
Write-Host "源目录  : $source"
Write-Host "安装到  : $($paths.InstallDirectory)"

# 1. 正在运行的实例必须先退出：文件正被占用时复制会失败
$stopResult = Stop-CubbyProcess
Write-Host "运行实例: $stopResult"

# 2. 复制产物（幂等：覆盖安装）
New-Item -ItemType Directory -Path $paths.InstallDirectory -Force | Out-Null

# 刻意**排除** artifacts 目录（那是诊断产物，不该进安装目录），但**保留 .pdb**——
# 崩溃日志里写的是完整堆栈，没有 PDB 就只能看到一串内存地址，等于白记。
Get-ChildItem -LiteralPath $source -Force |
    Where-Object { $_.Name -ne 'artifacts' } |
    ForEach-Object {
        Copy-Item -LiteralPath $_.FullName -Destination $paths.InstallDirectory -Recurse -Force
    }

# 3. 把卸载脚本一起装进去：卸载信息里的 UninstallString 要指向**安装目录内**的那一份，
#    否则用户把仓库挪走 / 删掉之后，「添加或删除程序」里的卸载按钮就失效了。
New-Item -ItemType Directory -Path $paths.InstalledScripts -Force | Out-Null
foreach ($scriptName in @('uninstall.ps1', '_common.ps1')) {
    Copy-Item -LiteralPath (Join-Path $PSScriptRoot $scriptName) -Destination $paths.InstalledScripts -Force
}

# 4. 开始菜单快捷方式
New-CubbyShortcut -Paths $paths

# 5. 卸载信息（HKCU，免管理员）
$resolvedVersion = $Version
if (-not $resolvedVersion) {
    $fileVersion = (Get-Item -LiteralPath $paths.Executable).VersionInfo.FileVersion
    if ($fileVersion) {
        $resolvedVersion = $fileVersion
    }
    else {
        $resolvedVersion = '0.1.0'
    }
}

$uninstallCommand = "powershell.exe -NoProfile -ExecutionPolicy Bypass -File `"$(Join-Path $paths.InstalledScripts 'uninstall.ps1')`""

New-Item -Path $paths.UninstallKey -Force | Out-Null
Set-ItemProperty -LiteralPath $paths.UninstallKey -Name 'DisplayName'     -Value 'Cubby'
Set-ItemProperty -LiteralPath $paths.UninstallKey -Name 'DisplayVersion'  -Value $resolvedVersion
Set-ItemProperty -LiteralPath $paths.UninstallKey -Name 'Publisher'       -Value 'Cubby'
Set-ItemProperty -LiteralPath $paths.UninstallKey -Name 'InstallLocation' -Value $paths.InstallDirectory
Set-ItemProperty -LiteralPath $paths.UninstallKey -Name 'DisplayIcon'     -Value "$($paths.Executable),0"
Set-ItemProperty -LiteralPath $paths.UninstallKey -Name 'UninstallString' -Value $uninstallCommand
# 静默卸载：默认**保留**用户数据（不带 -Purge），所以可以放心给系统用
Set-ItemProperty -LiteralPath $paths.UninstallKey -Name 'QuietUninstallString' -Value "$uninstallCommand -Quiet -KeepUserData"
Set-ItemProperty -LiteralPath $paths.UninstallKey -Name 'NoModify' -Value 1 -Type DWord
Set-ItemProperty -LiteralPath $paths.UninstallKey -Name 'NoRepair' -Value 1 -Type DWord

# 6. 可选开机自启。写的是与 StartupRegistration 完全相同的键与值名，
#    这样托盘菜单里的「开机自启」勾选状态能如实反映，不会出现"注册表里有两份"。
if ($AutoStart) {
    Set-ItemProperty -LiteralPath $paths.RunKey -Name $paths.RunValueName -Value """$($paths.Executable)"""
    Write-Host "开机自启: 已注册"
}
else {
    Write-Host "开机自启: 未注册（可装完后在托盘菜单里打开，或加 -AutoStart 重装）"
}

$state = Get-CubbyState -Paths $paths
Write-Host ""
Write-Host "安装完成："
Write-Host "  可执行文件 : $($paths.Executable) 存在=$($state.ExecutableExists)"
Write-Host "  开始菜单   : $($paths.Shortcut) 存在=$($state.ShortcutExists)"
Write-Host "  卸载信息   : $($state.UninstallDisplayName) $($state.UninstallVersion)"
Write-Host "  用户数据   : $($paths.UserDataDirectory)（安装不碰它）"
