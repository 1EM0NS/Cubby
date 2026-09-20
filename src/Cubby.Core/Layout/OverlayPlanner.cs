using Cubby.Core.Model;

namespace Cubby.Core.Layout;

/// <summary>一个盒子及其落到当前显示器上的匹配方式与被做过的修正。</summary>
/// <param name="Box">**修正后**的盒子：坐标已保证落在目标显示器内（见 <see cref="BoxPlacement"/>）。</param>
/// <param name="Kind">盒子的归属是怎么定下来的（原屏还在 / 按分辨率匹配 / 兜底）。</param>
/// <param name="Fix">为了让它在目标屏上可见而做过的改动；<see cref="PlacementFix.None"/> 表示没动过。</param>
public sealed record PlannedBox(Box Box, MonitorMatchKind Kind, PlacementFix Fix = PlacementFix.None);

/// <summary>某台显示器上应该呈现什么。即使没有盒子也会有一条，因为每台显示器都要有浮层窗口。</summary>
public sealed record MonitorPlan(MonitorSurface Monitor, IReadOnlyList<PlannedBox> Boxes)
{
    /// <summary>是否有盒子是"被迫回退"过来的（原显示器不在了），界面可以据此提示用户。</summary>
    public bool HasFallback => Boxes.Any(b => b.Kind == MonitorMatchKind.Fallback);

    /// <summary>是否有盒子被挪动或缩放过（用户应当被告知，否则会以为布局自己变了）。</summary>
    public bool HasFix => Boxes.Any(b => b.Fix != PlacementFix.None);

    /// <summary>被迫回退 / 被修正的盒子摘要，用于重建日志与诊断。</summary>
    public IReadOnlyList<string> Notes => Boxes
        .Where(b => b.Kind != MonitorMatchKind.ExactId || b.Fix != PlacementFix.None)
        .Select(b => $"{b.Box.Name}（{Describe(b.Kind)}，{Describe(b.Fix)}）")
        .ToList();

    private static string Describe(MonitorMatchKind kind) => kind switch
    {
        MonitorMatchKind.ExactId => "原屏仍在",
        MonitorMatchKind.SameResolution => "按分辨率匹配到别的屏",
        _ => "原屏已不在，回退",
    };

    private static string Describe(PlacementFix fix) => fix switch
    {
        PlacementFix.None => "未改动",
        PlacementFix.Moved => "已挪回可见范围",
        PlacementFix.Resized => "已收进屏幕尺寸",
        _ => "已挪回并收进屏幕尺寸",
    };
}

/// <summary>
/// 把布局分配到显示器上。纯计算，不涉及任何 Win32 调用，因此可以完整单测。
/// 显示器数量或分辨率变化后，App 就是靠它重新决定「哪些盒子画在哪块屏上」。
///
/// 它同时负责**可见性修正**：分配到某块屏之后立刻把盒子夹进这块屏的 DIP 范围。
/// 放在这里而不是渲染层，是因为「盒子属于哪块屏」和「它在这块屏上放不放得下」
/// 是同一个决策的两半——拆开就会有人只做前一半。
/// </summary>
public static class OverlayPlanner
{
    public static IReadOnlyList<MonitorPlan> Plan(
        IReadOnlyList<MonitorSurface> monitors,
        IEnumerable<Box> boxes)
    {
        if (monitors.Count == 0)
        {
            return [];
        }

        var fallback = monitors.FirstOrDefault(m => m.IsPrimary) ?? monitors[0];
        var buckets = monitors.Select(_ => new List<PlannedBox>()).ToList();

        foreach (var box in boxes)
        {
            var (monitor, kind) = MonitorMatcher.Resolve(
                box.MonitorId,
                box.MonitorWidth,
                box.MonitorHeight,
                monitors,
                fallback);

            var index = IndexOf(monitors, monitor);
            if (index < 0)
            {
                index = 0;
            }

            var (fitted, fix) = BoxPlacement.FitInto(box, monitors[index]);
            buckets[index].Add(new PlannedBox(fitted, kind, fix));
        }

        return monitors.Select((monitor, index) => new MonitorPlan(monitor, buckets[index])).ToList();
    }

    /// <summary>按 Id 找显示器在列表里的位置；找不到返回 -1。</summary>
    private static int IndexOf(IReadOnlyList<MonitorSurface> monitors, MonitorSurface target)
    {
        for (var i = 0; i < monitors.Count; i++)
        {
            if (ReferenceEquals(monitors[i], target) || monitors[i].Id == target.Id)
            {
                return i;
            }
        }

        return -1;
    }
}
