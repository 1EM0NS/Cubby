using Cubby.Core.Model;
using Cubby.Shell.Interop;

namespace Cubby.Shell.Overlay;

/// <summary>
/// 窗口位置与层级的小工具。集中放扩展样式的位运算，避免多处各写一遍导致漂移。
/// </summary>
public static class WindowPlacement
{
    private static readonly nint HwndTopmost = new(-1);
    private static readonly nint HwndNotTopmost = new(-2);

    /// <summary>把窗口铺满指定显示器（使用物理像素，绕开 DIP 换算）。</summary>
    public static void PlaceOn(nint hwnd, MonitorSurface surface)
    {
        if (hwnd == 0)
        {
            return;
        }

        NativeMethods.SetWindowPos(
            hwnd,
            0,
            surface.Bounds.Left,
            surface.Bounds.Top,
            surface.Bounds.Width,
            surface.Bounds.Height,
            NativeMethods.SwpNoActivate | NativeMethods.SwpNoZOrder | NativeMethods.SwpShowWindow);
    }

    /// <summary>置顶/取消置顶。产品里不用置顶，只有自测的受控背景层会用到。</summary>
    public static void SetTopmost(nint hwnd, bool topmost)
    {
        if (hwnd == 0)
        {
            return;
        }

        NativeMethods.SetWindowPos(
            hwnd,
            topmost ? HwndTopmost : HwndNotTopmost,
            0,
            0,
            0,
            0,
            NativeMethods.SwpNoMove | NativeMethods.SwpNoSize | NativeMethods.SwpNoActivate);
    }

    /// <summary>给窗口加上扩展样式位（只做按位或，不动其他位）。</summary>
    public static void AddExtendedStyles(nint hwnd, long styles)
    {
        if (hwnd == 0)
        {
            return;
        }

        var current = (long)NativeMethods.GetWindowLongPtr(hwnd, NativeMethods.GwlExStyle);
        NativeMethods.SetWindowLongPtr(hwnd, NativeMethods.GwlExStyle, (nint)(current | styles));
    }

    /// <summary>
    /// <c>WS_EX_NOACTIVATE</c> 的位值。公开出来是为了让**跨程序集的验收**能断言
    /// 「浮层是不抢焦点的、引导窗是普通可激活窗口」——两者的区别就在这一位上。
    /// </summary>
    public const long NoActivateFlag = 0x08000000L;

    /// <summary>读某个扩展样式位是否置上。只读查询，不修改任何东西。</summary>
    public static bool HasExtendedStyle(nint hwnd, long style) =>
        hwnd != 0 && ((long)NativeMethods.GetWindowLongPtr(hwnd, NativeMethods.GwlExStyle) & style) == style;

    /// <summary>让窗口不抢焦点、不出现在任务栏与 Alt+Tab 里。</summary>
    public static void MakeNonActivating(nint hwnd) =>
        AddExtendedStyles(hwnd, NativeMethods.WsExNoActivate | NativeMethods.WsExToolWindow);
}