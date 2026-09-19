using System.IO;
using Microsoft.Win32;

namespace Cubby.App;

/// <summary>
/// 开机自启：写 <c>HKCU\Software\Microsoft\Windows\CurrentVersion\Run</c>。
///
/// 选 HKCU 而不是 HKLM 的理由：不需要管理员权限，且卸载/关闭开关时删除自己那一项就够了
/// （M3 的安装包会调用 <see cref="Disable"/> 做卸载清理）。
/// </summary>
internal static class StartupRegistration
{
    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "Cubby";

    /// <summary>当前是否已登记自启。</summary>
    public static bool IsEnabled() => ReadValue() is not null;

    /// <summary>已登记的命令行；未登记返回 null。</summary>
    public static string? ReadValue()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath);
            return key?.GetValue(ValueName) as string;
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException)
        {
            return null;
        }
    }

    public static void SetEnabled(bool enabled)
    {
        if (enabled)
        {
            Enable();
        }
        else
        {
            Disable();
        }
    }

    public static void Enable()
    {
        using var key = Registry.CurrentUser.CreateSubKey(RunKeyPath);
        key?.SetValue(ValueName, Quote(ExecutablePath()));
    }

    public static void Disable()
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: true);
        key?.DeleteValue(ValueName, throwOnMissingValue: false);
    }

    /// <summary>当前进程的可执行文件路径。单文件发布下 <c>Environment.ProcessPath</c> 就是 exe 本身。</summary>
    public static string ExecutablePath() =>
        Environment.ProcessPath ?? Path.Combine(AppContext.BaseDirectory, "Cubby.App.exe");

    /// <summary>路径带空格时必须加引号，否则注册表启动项会被截断。</summary>
    private static string Quote(string path) => path.Contains(' ') ? $"\"{path}\"" : path;
}