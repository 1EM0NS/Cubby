using Cubby.Core.Model;
using Cubby.Shell.Interop;

namespace Cubby.Shell.Diagnostics;

/// <summary>一个顶层窗口的简要描述。</summary>
public sealed record WindowInfo(nint Handle, string ClassName, string Title, uint ProcessId, PixelRect Bounds, bool IsVisible)
{
    public string Describe() =>
        $"0x{Handle.ToInt64():X8} class={ClassName,-28} pid={ProcessId,-7} visible={IsVisible,-5} rect={Bounds}";
}

/// <summary>某屏幕坐标上的命中结果：系统会把鼠标消息发给谁。</summary>
public sealed record ProbeResult(nint Handle, string ClassName, string Title, uint ProcessId, bool IsOurs)
{
    public bool IsDesktopLayer =>
        ClassName.Equals("SysListView32", StringComparison.OrdinalIgnoreCase) ||
        ClassName.Equals("Progman", StringComparison.OrdinalIgnoreCase) ||
        ClassName.Equals("WorkerW", StringComparison.OrdinalIgnoreCase);
}

/// <summary>
/// 桌面探针。全部是只读查询，不做任何修改。
/// WindowFromPoint 与真实鼠标路由使用同一套命中测试，因此可以用它来判断
/// 「这个坐标上的点击会不会落到我们身上」。
/// </summary>
public static class DesktopProbe
{
    public static WindowInfo? Describe(nint hwnd)
    {
        if (hwnd == 0)
        {
            return null;
        }

        NativeMethods.GetWindowThreadProcessId(hwnd, out var pid);
        NativeMethods.GetWindowRect(hwnd, out var rect);

        return new WindowInfo(
            hwnd,
            NativeMethods.ClassNameOf(hwnd),
            NativeMethods.TitleOf(hwnd),
            pid,
            new PixelRect(rect.Left, rect.Top, rect.Right, rect.Bottom),
            NativeMethods.IsWindowVisible(hwnd));
    }

    /// <summary>查询该屏幕坐标上会被命中的窗口，并判断是否属于本进程的浮层。</summary>
    public static ProbeResult WindowAt(int x, int y, nint ourHwnd)
    {
        var hwnd = NativeMethods.WindowFromPoint(new NativeMethods.Point(x, y));
        NativeMethods.GetWindowThreadProcessId(hwnd, out var pid);

        return new ProbeResult(
            hwnd,
            NativeMethods.ClassNameOf(hwnd),
            NativeMethods.TitleOf(hwnd),
            pid,
            BelongsTo(hwnd, ourHwnd));
    }

    /// <summary>目标窗口是否是 ourHwnd 本身或它的子窗口。</summary>
    public static bool BelongsTo(nint hwnd, nint ourHwnd)
    {
        var current = hwnd;
        for (var depth = 0; depth < 16 && current != 0; depth++)
        {
            if (current == ourHwnd)
            {
                return true;
            }

            current = NativeMethods.GetParent(current);
        }

        return false;
    }

    /// <summary>顶层窗口的 Z 序快照，**最顶层在前**。</summary>
    public static IReadOnlyList<WindowInfo> ZOrderSnapshot(int max = 60)
    {
        var list = new List<WindowInfo>();
        NativeMethods.EnumWindows(
            (hwnd, _) =>
            {
                if (list.Count >= max)
                {
                    return false;
                }

                var info = Describe(hwnd);
                if (info is { IsVisible: true, Bounds.Width: > 0 })
                {
                    list.Add(info);
                }

                return true;
            },
            0);

        return list;
    }
}