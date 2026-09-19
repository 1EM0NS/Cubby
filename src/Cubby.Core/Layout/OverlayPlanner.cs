using Cubby.Core.Model;

namespace Cubby.Core.Layout;

/// <summary>一个盒子及其落到当前显示器上的匹配方式。</summary>
public sealed record PlannedBox(Box Box, MonitorMatchKind Kind);

/// <summary>某台显示器上应该呈现什么。即使没有盒子也会有一条，因为每台显示器都要有浮层窗口。</summary>
public sealed record MonitorPlan(MonitorSurface Monitor, IReadOnlyList<PlannedBox> Boxes)
{
    /// <summary>是否有盒子是"被迫回退"过来的（原显示器不在了），界面可以据此提示用户。</summary>
    public bool HasFallback => Boxes.Any(b => b.Kind == MonitorMatchKind.Fallback);
}

/// <summary>
/// 把布局分配到显示器上。纯计算，不涉及任何 Win32 调用，因此可以完整单测。
/// 显示器数量或分辨率变化后，App 就是靠它重新决定「哪些盒子画在哪块屏上」。
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

            var index = monitors.ToList().IndexOf(monitor);
            if (index < 0)
            {
                index = 0;
            }

            buckets[index].Add(new PlannedBox(box, kind));
        }

        return monitors.Select((monitor, index) => new MonitorPlan(monitor, buckets[index])).ToList();
    }
}