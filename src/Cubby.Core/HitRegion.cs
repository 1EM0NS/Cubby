using Cubby.Core.Model;

namespace Cubby.Core;

/// <summary>
/// 命中区域计算。对应设计原则 P2：只有盒子区域参与命中测试，其余区域必须放行给下层窗口。
/// 这里是纯计算，不涉及任何 Win32 调用，便于单元测试。
/// </summary>
public static class HitRegion
{
    /// <summary>把盒子列表换算成指定显示器上的物理像素矩形集合（空矩形会被剔除）。</summary>
    public static IReadOnlyList<PixelRect> ToPhysical(IEnumerable<Box> boxes, MonitorSurface surface) =>
        boxes.Select(b => surface.ToPhysical(b.Bounds))
             .Where(r => !r.IsEmpty)
             .ToList();

    /// <summary>某一点是否落在任一命中区域内。</summary>
    public static bool HitTest(IReadOnlyList<PixelRect> regions, int x, int y)
    {
        foreach (var r in regions)
        {
            if (r.Contains(x, y))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>命中区域的物理像素总面积，用于和显示器面积对比（诊断用）。</summary>
    public static long TotalArea(IReadOnlyList<PixelRect> regions)
    {
        long sum = 0;
        foreach (var r in regions)
        {
            sum += (long)r.Width * r.Height;
        }

        return sum;
    }
}