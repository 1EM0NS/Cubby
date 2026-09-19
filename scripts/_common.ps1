# Cubby 安装 / 卸载 / 验收脚本的共用部分（issue #40）。
#
# 单独抽一份，是为了让「安装」「卸载」「验收」三处对同一件事**只有一种算法**——
# 尤其是快捷方式路径与注册表路径：写错一处就会出现「装了却卸不干净」这种最难发现的残留。
#
# PowerShell 5.1 约束（AGENTS.md 记过坑）：不用 && / 三元 / ??；脚本必须以 UTF-8 **带 BOM** 存盘，
# 否则中文会被按 ANSI 解析直接报语法错误。

$ErrorActionPreference = 'Stop'

# 卸载信息在注册表里的位置（HKCU，免管理员）
$script:CubbyUninstallKey = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall\Cubby'
$script:CubbyRunKey = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run'
$script:CubbyRunValueName = 'Cubby'

function Get-CubbyPaths {
    param(
        [string]$InstallDirectory = (Join-Path $env:LOCALAPPDATA 'Programs\Cubby')
    )

    $userData = Join-Path $env:APPDATA 'Cubby'

    [PSCustomObject]@{
        InstallDirectory  = $InstallDirectory
        Executable        = Join-Path $InstallDirectory 'Cubby.App.exe'
        InstalledScripts  = Join-Path $InstallDirectory 'scripts'
        Shortcut          = Join-Path $env:APPDATA 'Microsoft\Windows\Start Menu\Programs\Cubby.lnk'
        UninstallKey      = $script:CubbyUninstallKey
        RunKey            = $script:CubbyRunKey
        RunValueName      = $script:CubbyRunValueName
        UserDataDirectory = $userData
        # 桌面图标「我们藏过」的标记。零残留清单里专门列了它：
        # 卸载后若还留着，下次启动会白跑一次恢复逻辑。
        IconMarker        = Join-Path $userData 'desktop-icons-hidden.flag'
    }
}

# 解析 Release 产物目录。默认按仓库结构找，允许显式指定。
function Resolve-CubbySource {
    param([string]$Source = '')

    if ($Source) {
        return (Resolve-Path -LiteralPath $Source).Path
    }

    $candidate = Join-Path (Split-Path -Parent $PSScriptRoot) 'src\Cubby.App\bin\Release\net8.0-windows'
    if (Test-Path -LiteralPath $candidate) {
        return (Resolve-Path -LiteralPath $candidate).Path
    }

    throw "找不到 Release 产物：$candidate。先跑 dotnet build Cubby.sln -c Release，或用 -Source 指定。"
}

# 把「安装相关的一切」拍成一张快照，安装前后 / 卸载前后都靠它断言。
function Get-CubbyState {
    param([Parameter(Mandatory = $true)]$Paths)

    $uninstallKey = Get-Item -LiteralPath $Paths.UninstallKey -ErrorAction SilentlyContinue
    $runValue = $null
    $runProperty = Get-ItemProperty -LiteralPath $Paths.RunKey -Name $Paths.RunValueName -ErrorAction SilentlyContinue
    if ($null -ne $runProperty) {
        $runValue = $runProperty.($Paths.RunValueName)
    }

    [PSCustomObject]@{
        InstallDirectoryExists = Test-Path -LiteralPath $Paths.InstallDirectory
        ExecutableExists       = Test-Path -LiteralPath $Paths.Executable
        ScriptsCopied          = Test-Path -LiteralPath (Join-Path $Paths.InstalledScripts 'uninstall.ps1')
        ShortcutExists         = Test-Path -LiteralPath $Paths.Shortcut
        UninstallKeyExists     = ($null -ne $uninstallKey)
        UninstallDisplayName   = $(if ($null -ne $uninstallKey) { $uninstallKey.GetValue('DisplayName') } else { $null })
        UninstallVersion       = $(if ($null -ne $uninstallKey) { $uninstallKey.GetValue('DisplayVersion') } else { $null })
        UninstallLocation      = $(if ($null -ne $uninstallKey) { $uninstallKey.GetValue('InstallLocation') } else { $null })
        UninstallDisplayIcon   = $(if ($null -ne $uninstallKey) { $uninstallKey.GetValue('DisplayIcon') } else { $null })
        UninstallString        = $(if ($null -ne $uninstallKey) { $uninstallKey.GetValue('UninstallString') } else { $null })
        AutoStartValue         = $runValue
        IconMarkerExists       = Test-Path -LiteralPath $Paths.IconMarker
        UserDataExists         = Test-Path -LiteralPath $Paths.UserDataDirectory
        RunningProcessIds      = @((Get-Process -Name 'Cubby.App' -ErrorAction SilentlyContinue | Select-Object -ExpandProperty Id))
    }
}

# 请求正在运行的 Cubby 退出并等待。
# 说明：常驻形态只有托盘图标、没有主窗口，CloseMainWindow 是无效的，只能 Stop-Process。
# 但这是**安全的强杀**：桌面图标能不能回来靠的是磁盘上的标记文件，而调用方会**先**跑
# --restore-on-exit（它会把标记清掉并把图标放出来），顺序不能反。
function Stop-CubbyProcess {
    param([int]$TimeoutSeconds = 10)

    $running = Get-Process -Name 'Cubby.App' -ErrorAction SilentlyContinue
    if (-not $running) {
        return '（没有正在运行的实例）'
    }

    $ids = ($running | Select-Object -ExpandProperty Id) -join ','
    $running | Stop-Process -Force -ErrorAction SilentlyContinue

    $deadline = (Get-Date).AddSeconds($TimeoutSeconds)
    while ((Get-Date) -lt $deadline) {
        if (-not (Get-Process -Name 'Cubby.App' -ErrorAction SilentlyContinue)) {
            return "已请求退出并等待完成（pid $ids）"
        }
        Start-Sleep -Milliseconds 250
    }

    throw "等待 $TimeoutSeconds 秒后仍有 Cubby.App 在运行（pid $ids），无法继续。"
}

function New-CubbyShortcut {
    param([Parameter(Mandatory = $true)]$Paths)

    $directory = Split-Path -Parent $Paths.Shortcut
    if (-not (Test-Path -LiteralPath $directory)) {
        New-Item -ItemType Directory -Path $directory -Force | Out-Null
    }

    # WScript.Shell 是创建 .lnk 的标准做法；不用 Add-Type（本机安全策略会拦编译式加载）
    $shell = New-Object -ComObject WScript.Shell
    try {
        $link = $shell.CreateShortcut($Paths.Shortcut)
        $link.TargetPath = $Paths.Executable
        $link.WorkingDirectory = $Paths.InstallDirectory
        $link.IconLocation = "$($Paths.Executable),0"
        $link.Description = 'Cubby · 桌面整理助手'
        $link.Save()
    }
    finally {
        [void][System.Runtime.InteropServices.Marshal]::ReleaseComObject($shell)
    }
}

# 校验一段字符串里是否包含全部关键字（用于断言注册表项内容不是空的占位）
function Test-CubbyContains {
    param(
        [string]$Text,
        [string[]]$Expected
    )

    if (-not $Text) {
        return $false
    }

    foreach ($item in $Expected) {
        if ($Text -notlike "*$item*") {
            return $false
        }
    }

    return $true
}

# 写一份 markdown 报告。$Results 是 @{ Step; Expected; Actual; Pass } 的数组。
function Write-CubbyReport {
    param(
        [Parameter(Mandatory = $true)][string]$Path,
        [Parameter(Mandatory = $true)][string]$Title,
        [Parameter(Mandatory = $true)]$Results,
        [string]$Conclusion = '',
        [string[]]$Notes = @()
    )

    $directory = Split-Path -Parent $Path
    if ($directory -and -not (Test-Path -LiteralPath $directory)) {
        New-Item -ItemType Directory -Path $directory -Force | Out-Null
    }

    $failed = @($Results | Where-Object { -not $_.Pass })
    $overall = 'FAIL'
    if ($Results.Count -gt 0 -and $failed.Count -eq 0) {
        $overall = 'PASS'
    }

    $builder = New-Object System.Text.StringBuilder
    [void]$builder.AppendLine("# $Title")
    [void]$builder.AppendLine()
    [void]$builder.AppendLine("- 时间：$(Get-Date -Format 'yyyy-MM-dd HH:mm:ss')")
    [void]$builder.AppendLine("- 结论：**$overall**")
    if ($Conclusion) {
        [void]$builder.AppendLine("- $Conclusion")
    }
    [void]$builder.AppendLine()
    [void]$builder.AppendLine("| 步骤 | 期望 | 实际 | 结论 |")
    [void]$builder.AppendLine("|---|---|---|---|")

    foreach ($row in $Results) {
        $mark = '**FAIL**'
        if ($row.Pass) {
            $mark = '**PASS**'
        }
        # 表格里的竖线会把列冲散，转义掉
        $actual = ($row.Actual -replace '\|', '\|')
        $expected = ($row.Expected -replace '\|', '\|')
        [void]$builder.AppendLine("| $($row.Step) | $expected | $actual | $mark |")
    }

    if ($Notes.Count -gt 0) {
        [void]$builder.AppendLine()
        foreach ($note in $Notes) {
            [void]$builder.AppendLine($note)
        }
    }

    [System.IO.File]::WriteAllText($Path, $builder.ToString(), (New-Object System.Text.UTF8Encoding($false)))

    if ($overall -eq 'PASS') {
        return 0
    }

    return 1
}
