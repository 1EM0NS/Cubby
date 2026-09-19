using Cubby.Core.Model;

namespace Cubby.Core.Layout;

/// <summary>盒子与显示器的匹配方式，用于诊断时说明"为什么落到了这块屏上"。</summary>
public enum MonitorMatchKind
{
    /// <summary>显示器标识完全一致。</summary>
    ExactId,

    /// <summary>标识变了但分辨率一致（例如换了接口、显示器重排）。</summary>
    SameResolution,

    /// <summary>都没匹配上，回退到兜底显示器（盒子仍然保留，不会丢）。</summary>
    Fallback,
}

/// <summary>
/// 把已保存的盒子重新挂到当前显示器上。
/// 同类工具最常见的缺陷就是「拔插显示器后盒子跑到主屏或直接消失」，
/// 所以这里的硬约束是：**任何情况下都必须返回一台显示器**。
/// </summary>
public static class MonitorMatcher
{
    public static (MonitorSurface Monitor, MonitorMatchKind Kind) Resolve(
        string? savedMonitorId,
        int savedWidth,
        int savedHeight,
        IReadOnlyList<MonitorSurface> current,
        MonitorSurface fallback)
    {
        if (current.Count == 0)
        {
            return (fallback, MonitorMatchKind.Fallback);
        }

        if (!string.IsNullOrEmpty(savedMonitorId))
        {
            foreach (var monitor in current)
            {
                if (string.Equals(monitor.Id, savedMonitorId, StringComparison.Ordinal))
                {
                    return (monitor, MonitorMatchKind.ExactId);
                }
            }
        }

        if (savedWidth > 0 && savedHeight > 0)
        {
            foreach (var monitor in current)
            {
                if (monitor.Bounds.Width == savedWidth && monitor.Bounds.Height == savedHeight)
                {
                    return (monitor, MonitorMatchKind.SameResolution);
                }
            }
        }

        return (fallback, MonitorMatchKind.Fallback);
    }

    /// <summary>把盒子按当前显示器重新分配，返回「盒子 → 显示器」的结果。</summary>
    public static IReadOnlyList<(Box Box, MonitorSurface Monitor, MonitorMatchKind Kind)> ResolveAll(
        IEnumerable<Box> boxes,
        IReadOnlyList<MonitorSurface> current,
        MonitorSurface fallback) =>
        boxes.Select(box =>
        {
            var (monitor, kind) = Resolve(box.MonitorId, box.MonitorWidth, box.MonitorHeight, current, fallback);
            return (box, monitor, kind);
        }).ToList();
}