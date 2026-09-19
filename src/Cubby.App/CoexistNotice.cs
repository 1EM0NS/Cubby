using Cubby.Core.Platform;

namespace Cubby.App;

/// <summary>
/// 「同类软件共存」提示的决策与展示（A8）。
///
/// 抽成一层是为了让自动化验收走的是**用户真正走的那段代码**：
/// 启动时的提示、以及验收里的提示，都从这里进。
/// </summary>
internal static class CoexistNotice
{
    /// <summary>还没被用户确认过的冲突软件（已确认的不再打扰）。</summary>
    internal static IReadOnlyList<CoexistTool> Pending(
        IReadOnlyList<CoexistTool> detected,
        IReadOnlyList<string> acknowledged) =>
        detected
            .Where(tool => !acknowledged.Contains(tool.ProcessName, StringComparer.OrdinalIgnoreCase))
            .ToList();

    /// <summary>检测当前进程并按已确认清单过滤。</summary>
    internal static IReadOnlyList<CoexistTool> PendingFor(LayoutService layout)
    {
        var detected = CoexistTools.DetectRunning();
        LastDetected = detected;
        return Pending(detected, layout.CoexistAcknowledged);
    }

    /// <summary>最近一次检测到的全部冲突软件（诊断用）。</summary>
    internal static IReadOnlyList<CoexistTool> LastDetected { get; private set; } = [];

    /// <summary>启动时调用：有未确认的冲突软件才提示，否则**完全静默**。检测只在启动时做一次，不轮询。</summary>
    internal static CoexistWindow? ShowIfNeeded(LayoutService layout) => Show(PendingFor(layout), layout);

    /// <summary>
    /// 展示提示。勾了「不再提示」就把这批进程名记进布局——
    /// 记进程名而不是"提示过了"这个布尔值，是为了**以后新装了别的同类软件仍会提示一次**。
    /// </summary>
    internal static CoexistWindow? Show(IReadOnlyList<CoexistTool> pending, LayoutService layout)
    {
        if (pending.Count == 0)
        {
            return null;
        }

        var window = new CoexistWindow(pending);

        window.Closed += (_, _) =>
        {
            if (window.RememberChoice)
            {
                layout.AcknowledgeCoexist(window.Detected.Select(tool => tool.ProcessName));
            }
        };

        window.Show();
        return window;
    }
}