using Cubby.Core.Model;

namespace Cubby.Core.Layout;

/// <summary>
/// 说明「把这快照还原回去会发生什么」。分辨率或显示器数量变了之后，
/// 直接还原可能让盒子落到别的屏上——用户按下还原之前就该知道这件事，而不是还原完才发现。
/// </summary>
public static class SnapshotDiff
{
    public static string Describe(LayoutDocument snapshot, IReadOnlyList<MonitorSurface> current)
    {
        if (current.Count == 0)
        {
            return "当前没有枚举到显示器，还原后盒子会等下次显示器变化时再落位。";
        }

        var lines = new List<string>
        {
            $"快照记录 {snapshot.Monitors.Count} 台显示器，当前 {current.Count} 台。",
        };

        var missing = snapshot.Monitors.Where(saved => !ExistsIn(current, saved)).ToList();
        if (missing.Count > 0)
        {
            lines.Add($"快照里的 {string.Join("、", missing.Select(m => m.Id))} 当前不存在。");
        }

        var plans = OverlayPlanner.Plan(current, snapshot.Boxes);
        var boxes = plans.SelectMany(p => p.Boxes).ToList();
        var fallback = boxes.Count(b => b.Kind == MonitorMatchKind.Fallback);
        var sameResolution = boxes.Count(b => b.Kind == MonitorMatchKind.SameResolution);
        var adjusted = boxes.Count(b => b.Fix != PlacementFix.None);

        if (boxes.Count == 0)
        {
            lines.Add("快照里没有盒子。");
        }
        else
        {
            lines.Add(
                $"共 {boxes.Count} 个盒子：{boxes.Count - fallback - sameResolution} 个按显示器标识精确落位，" +
                $"{sameResolution} 个按分辨率落位，{fallback} 个会回退到兜底显示器。");
        }

        if (fallback > 0)
        {
            lines.Add("回退的盒子不会丢，但位置可能不在原来的屏上；还原后可以直接拖动调整。");
        }

        // 屏幕比快照时小（或 DPI 更高导致 DIP 空间更小）时，贴边的盒子会被拉回屏内。
        // 这不是错误，但属于"还原后的样子和快照不一样"，必须提前说清楚
        if (adjusted > 0)
        {
            lines.Add($"其中 {adjusted} 个盒子在当前屏幕上放不下，还原时会被挪进可见范围（尺寸或位置有调整）。");
        }

        return string.Join(Environment.NewLine, lines);
    }

    /// <summary>当前是否还有这台显示器：先比设备名，再退一步比分辨率（换接口时设备名会变）。</summary>
    public static bool ExistsIn(IReadOnlyList<MonitorSurface> current, MonitorSurface saved) =>
        current.Any(monitor => string.Equals(monitor.Id, saved.Id, StringComparison.Ordinal)) ||
        current.Any(monitor => monitor.Bounds.Width == saved.Bounds.Width && monitor.Bounds.Height == saved.Bounds.Height);
}