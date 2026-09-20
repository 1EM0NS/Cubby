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

    /// <summary>窗口是否处于最小化状态。全部只读查询。</summary>
    public static bool IsMinimized(nint hwnd) => hwnd != 0 && NativeMethods.IsIconic(hwnd);

    /// <summary>当前前台窗口句柄。</summary>
    public static nint ForegroundWindow() => NativeMethods.GetForegroundWindow();

    /// <summary>
    /// 「最小化且有标题」的顶层窗口数量（≈ 任务栏上会被收起来的那些）。
    ///
    /// 专门为验收准备：断言浮层没消失之前，得先证明**激励真的发生了**。
    /// 否则一条「按了 Win+D 但什么都没变」的路径也会让断言绿——那和没测一样。
    /// </summary>
    public static int CountMinimizedTaskWindows()
    {
        var count = 0;

        NativeMethods.EnumWindows(
            (hwnd, _) =>
            {
                if (NativeMethods.IsIconic(hwnd) && !string.IsNullOrEmpty(NativeMethods.TitleOf(hwnd)))
                {
                    count++;
                }

                return true;
            },
            0);

        return count;
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

    /// <summary>
    /// 桌面图层的层级链：持有 SHELLDLL_DefView 的顶层窗口 → SHELLDLL_DefView → SysListView32。
    /// 只做只读查询，不修改任何东西。M1 做图标吸附时会复用同一套定位逻辑。
    /// 注意：在 Win11 24H2 上持有 DefView 的可能是 Progman 派生的 WorkerW，因此不能写死 Progman。
    /// </summary>
    public static IReadOnlyList<WindowInfo> DesktopLayerChain()
    {
        nint owner = 0;
        nint defView = 0;

        NativeMethods.EnumWindows(
            (hwnd, _) =>
            {
                var found = NativeMethods.FindWindowEx(hwnd, 0, "SHELLDLL_DefView", null);
                if (found == 0)
                {
                    return true;
                }

                owner = hwnd;
                defView = found;
                return false;
            },
            0);

        var listView = defView != 0
            ? NativeMethods.FindWindowEx(defView, 0, "SysListView32", null)
            : 0;

        var chain = new List<WindowInfo>();
        foreach (var hwnd in new[] { owner, defView, listView })
        {
            var info = Describe(hwnd);
            if (info is not null)
            {
                chain.Add(info);
            }
        }

        return chain;
    }
}