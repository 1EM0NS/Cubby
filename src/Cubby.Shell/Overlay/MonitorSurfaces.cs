using System.Runtime.InteropServices;
using Cubby.Core.Model;
using Cubby.Shell.Interop;

namespace Cubby.Shell.Overlay;

/// <summary>
/// 显示器信息查询。spike 阶段固定使用主显示器；多显示器适配是 M1 的工作（见 issue #5）。
/// </summary>
public static class MonitorSurfaces
{
    /// <summary>取主显示器的物理像素范围与 DPI 缩放。</summary>
    public static MonitorSurface Primary(nint hwndForDpi)
    {
        var monitor = NativeMethods.MonitorFromPoint(
            new NativeMethods.Point(0, 0),
            NativeMethods.MonitorDefaultToPrimary);

        var info = new NativeMethods.MonitorInfo { Size = Marshal.SizeOf<NativeMethods.MonitorInfo>() };
        if (!NativeMethods.GetMonitorInfo(monitor, ref info))
        {
            // 查询失败时退化为「按 96 DPI 的一块足够大的区域」，避免直接崩
            return new MonitorSurface("primary-fallback", new PixelRect(0, 0, 1920, 1080), 1.0);
        }

        var dpi = hwndForDpi != 0 ? NativeMethods.GetDpiForWindow(hwndForDpi) : 96;
        if (dpi == 0)
        {
            dpi = 96;
        }

        return new MonitorSurface(
            "primary",
            new PixelRect(info.Monitor.Left, info.Monitor.Top, info.Monitor.Right, info.Monitor.Bottom),
            dpi / 96.0);
    }
}