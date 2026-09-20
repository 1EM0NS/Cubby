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

    /// <summary>
    /// <c>WS_EX_TOOLWINDOW</c> 的位值。浮层带这一位，所以它不进任务栏、不进 Alt+Tab——
    /// 也正因如此，shell 的「显示桌面 / Win+D」不会把它当成一个"应用窗口"收走。
    /// 验收用它来给出「系统级动作为什么收不走浮层」的客观依据，而不是靠嘴说。
    /// </summary>
    public const long ToolWindowFlag = 0x00000080L;

    /// <summary>读某个扩展样式位是否置上。只读查询，不修改任何东西。</summary>
    public static bool HasExtendedStyle(nint hwnd, long style) =>
        hwnd != 0 && ((long)NativeMethods.GetWindowLongPtr(hwnd, NativeMethods.GwlExStyle) & style) == style;

    /// <summary>窗口是否处于最小化状态。</summary>
    public static bool IsMinimized(nint hwnd) => hwnd != 0 && NativeMethods.IsIconic(hwnd);

    /// <summary>
    /// 把窗口最小化。**只给自动化验收用**：用来复现「被显示桌面收起来」的状态，
    /// 产品路径不该调用它——用户没有任何入口能把浮层最小化。
    /// </summary>
    public static void Minimize(nint hwnd)
    {
        if (hwnd != 0)
        {
            NativeMethods.ShowWindow(hwnd, NativeMethods.SwMinimize);
        }
    }

    /// <summary>把最小化的窗口恢复出来。恢复会激活窗口，验收里只在最后兜底用。</summary>
    public static void Restore(nint hwnd)
    {
        if (hwnd != 0)
        {
            NativeMethods.ShowWindow(hwnd, NativeMethods.SwRestore);
        }
    }

    /// <summary>让窗口不抢焦点、不出现在任务栏与 Alt+Tab 里。</summary>
    public static void MakeNonActivating(nint hwnd) =>
        AddExtendedStyles(hwnd, NativeMethods.WsExNoActivate | NativeMethods.WsExToolWindow);
}