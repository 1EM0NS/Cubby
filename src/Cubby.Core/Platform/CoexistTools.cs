using System.Diagnostics;

namespace Cubby.Core.Platform;

/// <summary>一个已知的桌面接管类软件。</summary>
/// <param name="ProcessName">不含 .exe 的进程名，匹配时不区分大小写。</param>
/// <param name="DisplayName">给用户看的名字。</param>
/// <param name="Why">它会与 Cubby 冲突在哪。</param>
public sealed record CoexistTool(string ProcessName, string DisplayName, string Why);

/// <summary>
/// 共存检测（A8）。
///
/// 设计取舍（见 技术方案.md 第 10 章风险表）：与同类桌面整理软件同时运行时，
/// Cubby 的做法是**启动时检测一次 + 提示用户二选一**，
/// **绝不抢占**——不结束对方进程、不改对方窗口、不隐藏对方功能。
///
/// 反面案例：装机量最大的那类工具会直接接管桌面窗口，两边互抢的结果是用户桌面出问题。
///
/// 另外要特别小心一件事：**Wallpaper Engine 不是冲突软件**。
/// 本项目的立身之本就是与动态壁纸零冲突，把它误判成冲突会毁掉核心卖点，因此名单里单独有 <see cref="Compatible"/>。
/// </summary>
public static class CoexistTools
{
    /// <summary>会与 Cubby 争桌面的同类软件（按进程名匹配，不区分大小写）。</summary>
    public static IReadOnlyList<CoexistTool> Conflicting { get; } =
    [
        new("Fences", "Stardock Fences", "它同样接管桌面图标的排列与展示"),
        new("DeskGo", "腾讯桌面整理", "它同样接管桌面图标，两者同时开启会互相抢桌面"),
        new("DeskGoLite", "腾讯桌面整理（轻量版）", "它同样接管桌面图标，两者同时开启会互相抢桌面"),
        new("DeskGo64", "腾讯桌面整理（64 位）", "它同样接管桌面图标，两者同时开启会互相抢桌面"),
        new("360DesktopAssist", "360 桌面助手", "它同样接管桌面图标与双击空白处行为"),
        new("XiaoZhiDesktop", "小智桌面", "它同样接管桌面图标"),
    ];

    /// <summary>
    /// 明确**可以共存**的进程。列出来是为了把"它不是冲突"写成代码里的断言，而不是靠人记得。
    /// Wallpaper Engine 的互动壁纸正是本项目要保护的场景。
    /// </summary>
    public static IReadOnlyList<string> Compatible { get; } = ["wallpaper32", "wallpaper64"];

    public static bool IsCompatible(string processName) =>
        Compatible.Any(name => name.Equals(processName, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// 从「正在运行的进程名」里挑出冲突软件。纯函数，便于单测与自动化验收注入。
    /// 可共存进程即使误入名单也会被剔除。
    /// </summary>
    public static IReadOnlyList<CoexistTool> Detect(IEnumerable<string> runningProcessNames)
    {
        var running = runningProcessNames
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        return Conflicting
            .Where(tool => running.Contains(tool.ProcessName) && !IsCompatible(tool.ProcessName))
            .ToList();
    }

    /// <summary>检测当前真实运行的进程。任何失败都降级为"没检测到"，绝不因此挡住启动。</summary>
    public static IReadOnlyList<CoexistTool> DetectRunning()
    {
        try
        {
            return Detect(Process.GetProcesses().Select(p => p.ProcessName));
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception or NotSupportedException)
        {
            return [];
        }
    }

    /// <summary>把检测结果写成一句话给用户看。</summary>
    public static string Describe(IReadOnlyList<CoexistTool> tools) =>
        tools.Count == 0
            ? "没有检测到同类桌面整理软件。"
            : string.Join("、", tools.Select(t => $"{t.DisplayName}（{t.ProcessName}.exe）"));
}