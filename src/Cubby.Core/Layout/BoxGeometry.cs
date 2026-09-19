using Cubby.Core.Model;

namespace Cubby.Core.Layout;

/// <summary>
/// 盒子的几何运算：拖动、缩放，以及边界夹取。纯计算，可完整单测。
/// 所有坐标与尺寸都是 DIP，且**相对所属显示器左上角**。
/// </summary>
public static class BoxGeometry
{
    /// <summary>盒子最小尺寸。再小就没法显示内容了，也避免被拖成一条线之后再也点不到。</summary>
    public const double MinWidth = 160;

    public const double MinHeight = 90;

    /// <summary>按钮栏高度：折叠状态下盒子会收缩到这个高度。</summary>
    public const double TitleBarHeight = 34;

    /// <summary>拖动移动：夹取在显示器范围内。</summary>
    public static DipRect MoveTo(DipRect current, double deltaX, double deltaY, double monitorWidth, double monitorHeight)
    {
        var maxX = monitorWidth - current.Width;
        var maxY = monitorHeight - current.Height;

        return current with
        {
            X = Clamp(current.X + deltaX, 0, maxX),
            Y = Clamp(current.Y + deltaY, 0, maxY),
        };
    }

    /// <summary>缩放（右下角拖动）：保证最小尺寸，且不超出显示器右边界与下边界。</summary>
    public static DipRect ResizeBy(DipRect current, double deltaWidth, double deltaHeight, double monitorWidth, double monitorHeight)
    {
        var maxWidth = monitorWidth - current.X;
        var maxHeight = monitorHeight - current.Y;

        return current with
        {
            Width = Clamp(current.Width + deltaWidth, MinWidth, maxWidth),
            Height = Clamp(current.Height + deltaHeight, MinHeight, maxHeight),
        };
    }

    /// <summary>
    /// 折叠时用的显示高度：只留标题栏，但**不改模型里的 Height**，
    /// 这样展开后尺寸能原样回来。
    /// </summary>
    public static double EffectiveHeight(Box box) =>
        box.IsCollapsed ? TitleBarHeight : box.Bounds.Height;

    private static double Clamp(double value, double min, double max)
    {
        // 显示器比最小尺寸还小时，max 会小于 min；此时以 min 为准，避免出现负数尺寸
        if (max < min)
        {
            max = min;
        }

        return Math.Min(Math.Max(value, min), max);
    }
}