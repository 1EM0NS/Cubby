using Cubby.Core.Model;

namespace Cubby.Core.Layout;

/// <summary>盒子落到目标显示器时做过的可见性修正。</summary>
public enum PlacementFix
{
    /// <summary>坐标本来就合法，什么都没动。</summary>
    None,

    /// <summary>位置跑到屏外了，被拉回屏内。</summary>
    Moved,

    /// <summary>盒子比屏幕还大，被收进屏内。</summary>
    Resized,

    /// <summary>尺寸与位置都修正了。</summary>
    MovedAndResized,
}

/// <summary>
/// 保证盒子在它所属的显示器上**看得见**。
///
/// 为什么需要这一步：盒子的坐标是「相对所属显示器左上角」的 DIP
/// （见 <see cref="Box.Bounds"/> 的说明），这份设计让「换显示器不用重算坐标」，
/// 但它只在一块**同样大**的屏上成立。三种常见情况下它会失效：
///
/// 1. **拔掉副屏**：原属副屏的盒子回退到主屏，坐标却是按副屏尺寸放的——副屏若更宽，
///    X 就超出主屏，盒子整块落在浮层窗口之外；
/// 2. **分辨率调小**：例如 2560×1440 换成 1920×1080，靠右下的盒子被留在屏外；
/// 3. **DPI 调高**：同一块屏 125% → 150%，DIP 可用范围从 2048×1152 缩到 1707×960，
///    原本贴边的盒子越界。
///
/// 而盒子一旦完全落在浮层窗口的客户区之外，**用户连拖都拖不到它**——
/// 它不在任何窗口的命中范围里，鼠标点上去也落不到它身上，等于盒子丢了。
/// 所以这里的判据是「整盒可见」而不是「露一个角」：宁可挪动它，也不能让它不可达。
///
/// 纯计算，不涉及任何 Win32 调用，因此可以完整单测。
/// </summary>
public static class BoxPlacement
{
    /// <summary>浮点比较容差（DIP）。低于这个量的差异不值得动用户的布局。</summary>
    private const double Epsilon = 0.01;

    /// <summary>
    /// 把盒子夹进目标显示器的 DIP 空间。坐标已经合法时**原样返回**，不做任何改动——
    /// 这一条是硬要求：绝大多数重建都发生在显示器没变的情况下，
    /// 若那里也要"顺手规范化"一下，就会在每次启动时悄悄弄脏用户的布局文件。
    /// </summary>
    public static (DipRect Bounds, PlacementFix Fix) FitInto(DipRect bounds, MonitorSurface monitor)
    {
        var extent = monitor.DipExtent;

        // 拿不到有效的屏幕范围时不动任何东西：宁可保持原样，也不要基于一个猜出来的尺寸去挪盒子
        if (extent.Width <= 0 || extent.Height <= 0 || bounds.IsEmpty)
        {
            return (bounds, PlacementFix.None);
        }

        // 尺寸先收：盒子比屏幕还大时放不下，顶满至少让它可达
        var width = Math.Min(bounds.Width, extent.Width);
        var height = Math.Min(bounds.Height, extent.Height);
        var resized = Differs(width, bounds.Width) || Differs(height, bounds.Height);

        // 位置再夹：整盒进屏
        var x = Clamp(bounds.X, 0, extent.Width - width);
        var y = Clamp(bounds.Y, 0, extent.Height - height);
        var moved = Differs(x, bounds.X) || Differs(y, bounds.Y);

        var fix = (moved, resized) switch
        {
            (true, true) => PlacementFix.MovedAndResized,
            (true, false) => PlacementFix.Moved,
            (false, true) => PlacementFix.Resized,
            _ => PlacementFix.None,
        };

        return fix == PlacementFix.None
            ? (bounds, PlacementFix.None)
            : (new DipRect(x, y, width, height), fix);
    }

    /// <summary>给一个盒子做修正，返回修正后的盒子（未改动时返回同一个实例）。</summary>
    public static (Box Box, PlacementFix Fix) FitInto(Box box, MonitorSurface monitor)
    {
        var (bounds, fix) = FitInto(box.Bounds, monitor);

        return fix == PlacementFix.None ? (box, fix) : (box with { Bounds = bounds }, fix);
    }

    private static bool Differs(double a, double b) => Math.Abs(a - b) > Epsilon;

    private static double Clamp(double value, double min, double max) =>
        max <= min ? min : Math.Min(Math.Max(value, min), max);
}
