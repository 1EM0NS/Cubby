using System.Runtime.InteropServices;
using Cubby.Core.Model;
using Cubby.Shell.Interop;

namespace Cubby.Shell.Overlay;

/// <summary>
/// 显示器枚举与查询，全部是只读查询，不修改系统显示设置。
/// </summary>
public static class MonitorSurfaces
{
    /// <summary>枚举彻底失败时的兜底值，保证上层永远拿得到一台显示器而不是空列表。</summary>
    private static readonly MonitorSurface Fallback =
        new("fallback", new PixelRect(0, 0, 1920, 1080), 1.0) { IsPrimary = true };

    /// <summary>最近一次枚举失败的原因（正常时为 null）。用来诊断"为什么枚举不到显示器"。</summary>
    public static string? LastEnumerationError { get; private set; }

    /// <summary>枚举当前所有显示器：设备名（如 <c>\\.\DISPLAY1</c>）、物理像素范围、每屏 DPI 缩放。</summary>
    public static IReadOnlyList<MonitorSurface> Enumerate()
    {
        LastEnumerationError = null;
        var results = new List<MonitorSurface>();

        NativeMethods.MonitorEnumProc callback = (nint monitor, nint hdc, ref NativeMethods.Rect rect, nint data) =>
        {
            // 回调里绝不能向外抛异常：它跨越 native 帧，托管异常处理器抓不到，
            // 表现就是 STATUS_FATAL_USER_CALLBACK_EXCEPTION 直接结束进程。
            try
            {
                results.Add(Describe(monitor));
                return true;
            }
            catch (Exception ex)
            {
                LastEnumerationError = $"{ex.GetType().Name}: {ex.Message}";
                return false;
            }
        };

        var ok = NativeMethods.EnumDisplayMonitors(0, 0, callback, 0);

        // 必须保活：回调正在进行时若委托被 GC 回收，native 侧会调用到已释放的存根
        GC.KeepAlive(callback);

        if (!ok || results.Count == 0)
        {
            return [Fallback];
        }

        return results;
    }

    /// <summary>取主显示器；没有标记为主屏的取第一台；都没有则用兜底值。</summary>
    public static MonitorSurface Primary()
    {
        var monitors = Enumerate();

        foreach (var monitor in monitors)
        {
            if (monitor.IsPrimary)
            {
                return monitor;
            }
        }

        return monitors.Count > 0 ? monitors[0] : Fallback;
    }

    /// <summary>某个屏幕坐标落在哪台显示器上。</summary>
    public static MonitorSurface FromPoint(int x, int y, IReadOnlyList<MonitorSurface>? monitors = null)
    {
        monitors ??= Enumerate();

        foreach (var monitor in monitors)
        {
            if (monitor.Bounds.Contains(x, y))
            {
                return monitor;
            }
        }

        return Primary();
    }

    private static MonitorSurface Describe(nint monitor)
    {
        // Device 是固定长度的内联字符数组（ByValTStr）。**必须先初始化**：
        // 传 null 进去时封送处理可能抛异常，而这个异常发生在 native 回调里，会直接把进程带走。
        var info = new NativeMethods.MonitorInfoEx
        {
            Size = Marshal.SizeOf<NativeMethods.MonitorInfoEx>(),
            Device = string.Empty,
        };
        if (!NativeMethods.GetMonitorInfoEx(monitor, ref info))
        {
            return Fallback;
        }

        // 每屏 DPI：PMv2 感知下 MDT_EFFECTIVE_DPI 即为该屏实际缩放
        var dpi = 96u;
        if (NativeMethods.GetDpiForMonitor(monitor, NativeMethods.MdtEffectiveDpi, out var dpiX, out _) == 0 && dpiX > 0)
        {
            dpi = dpiX;
        }

        var id = string.IsNullOrWhiteSpace(info.Device)
            ? $"monitor-{monitor.ToInt64():X}"
            : info.Device;

        return new MonitorSurface(
            id,
            new PixelRect(info.Monitor.Left, info.Monitor.Top, info.Monitor.Right, info.Monitor.Bottom),
            dpi / 96.0)
        {
            IsPrimary = (info.Flags & NativeMethods.MonitorInfoFlagPrimary) != 0,
        };
    }
}