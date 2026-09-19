using System.Runtime.InteropServices;
using Cubby.Core.Model;
using Cubby.Shell.Interop;

namespace Cubby.Shell.Desktop;

/// <summary>
/// 真实桌面图标层（<c>SysListView32</c>）的定位、读取与显隐。
///
/// **只读约定**：本类只发三条"取"消息（条数 / 位置 / 文本），以及窗口级的显示 / 隐藏。
/// **绝不发送任何写入图标位置或重排图标的消息**——CI 的设计原则守卫（`tools/scripts/guard.ps1`
/// 的 P4 规则）会拦截那两个写入入口，本文件一个都没有，由守卫与代码审查双重把关。
///
/// 跨进程读取的套路（Explorer 与我们是两个进程）：
/// 在目标进程里 <c>VirtualAllocEx</c> 出一小块内存 → 用 <c>LVM_*</c> 让 Explorer 把结果写进去 →
/// <c>ReadProcessMemory</c> 取回来 → <c>VirtualFreeEx</c> 释放。
/// 这块内存**只用来传参和接结果**，不会被任何写入型消息使用。
/// </summary>
public static class DesktopIcons
{
    private const int LvmGetItemCount = 0x1004;
    private const int LvmGetItemPosition = 0x1010;
    private const int LvmGetItemTextW = 0x1073;
    private const int LvifText = 0x0001;
    private const int TextCapacity = 260;
    private const uint SendTimeoutMs = 1000;

    /// <summary>桌面图标所在的列表视图句柄；找不到返回 0（例如 Explorer 重启的瞬间）。</summary>
    public static nint ListViewHandle()
    {
        // 任何 native 调用的异常都不该冒到调用方：托盘菜单点一下不该把进程带走
        try
        {
            foreach (var window in Diagnostics.DesktopProbe.DesktopLayerChain())
            {
                if (window.ClassName.Equals("SysListView32", StringComparison.OrdinalIgnoreCase))
                {
                    return window.Handle;
                }
            }
        }
        catch (Exception)
        {
            // 探测失败一律当成"没找到"，由调用方降级处理
        }

        return 0;
    }

    /// <summary>
    /// 读取全部桌面图标（显示名 + 屏幕坐标）。
    /// 任何一步失败都返回**空列表**而不是抛异常——吸附功能据此整体降级为"不吸附"，不影响其他功能。
    /// </summary>
    public static IReadOnlyList<DesktopIcon> Read()
    {
        try
        {
            return ReadFrom(ListViewHandle());
        }
        catch (Exception)
        {
            // 权限不足 / 结构体不兼容 / Explorer 正在重启……一律降级
            return [];
        }
    }

    /// <summary>从指定列表视图句柄读取。抽出来是为了让"句柄无效时降级"这条路径可被单测覆盖。</summary>
    public static IReadOnlyList<DesktopIcon> ReadFrom(nint listView)
    {
        var icons = new List<DesktopIcon>();

        if (listView == 0)
        {
            return icons;
        }

        NativeMethods.GetWindowThreadProcessId(listView, out var processId);
        if (processId == 0)
        {
            return icons;
        }

        var process = NativeMethods.OpenProcess(NativeMethods.ProcessAccessForRead, false, processId);
        if (process == 0)
        {
            return icons;
        }

        nint pointBuffer = 0;
        nint itemBuffer = 0;
        nint textBuffer = 0;

        try
        {
            pointBuffer = NativeMethods.VirtualAllocEx(process, 0, 8, NativeMethods.MemCommit | NativeMethods.MemReserve, NativeMethods.PageReadWrite);
            itemBuffer = NativeMethods.VirtualAllocEx(process, 0, Marshal.SizeOf<NativeMethods.LvItem>(), NativeMethods.MemCommit | NativeMethods.MemReserve, NativeMethods.PageReadWrite);
            textBuffer = NativeMethods.VirtualAllocEx(process, 0, TextCapacity * 2, NativeMethods.MemCommit | NativeMethods.MemReserve, NativeMethods.PageReadWrite);

            if (pointBuffer == 0 || itemBuffer == 0 || textBuffer == 0)
            {
                return icons;
            }

            var count = Send(listView, LvmGetItemCount, 0, 0);
            for (var index = 0; index < count; index++)
            {
                if (!TryReadPosition(listView, process, pointBuffer, index, out var point))
                {
                    continue;
                }

                var name = ReadText(listView, process, itemBuffer, textBuffer, index);
                var screen = ToScreen(listView, point);

                icons.Add(new DesktopIcon(
                    string.IsNullOrEmpty(name) ? $"(图标 {index})" : name,
                    screen.X,
                    screen.Y));
            }
        }
        finally
        {
            if (pointBuffer != 0)
            {
                NativeMethods.VirtualFreeEx(process, pointBuffer, 0, NativeMethods.MemRelease);
            }

            if (itemBuffer != 0)
            {
                NativeMethods.VirtualFreeEx(process, itemBuffer, 0, NativeMethods.MemRelease);
            }

            if (textBuffer != 0)
            {
                NativeMethods.VirtualFreeEx(process, textBuffer, 0, NativeMethods.MemRelease);
            }

            NativeMethods.CloseHandle(process);
        }

        return icons;
    }

    /// <summary>图标层当前是否可见。找不到句柄时返回 true（宁可认为"看得见"，也不谎报状态）。</summary>
    public static bool IsVisible()
    {
        var handle = ListViewHandle();
        return handle == 0 || NativeMethods.IsWindowVisible(handle);
    }

    /// <summary>
    /// 显示 / 隐藏桌面图标。隐藏只是窗口级的 SW_HIDE，随时可恢复，不改变任何图标数据。
    /// 返回 false 表示没找到图标层（此时不做任何事）。
    /// </summary>
    public static bool SetVisible(bool visible)
    {
        var handle = ListViewHandle();
        if (handle == 0)
        {
            return false;
        }

        NativeMethods.ShowWindow(handle, visible ? NativeMethods.SwShowNoActivate : NativeMethods.SwHide);
        return true;
    }

    private static int Send(nint hwnd, int message, nint wParam, nint lParam)
    {
        var sent = NativeMethods.SendMessageTimeout(
            hwnd, message, wParam, lParam,
            NativeMethods.SmtoAbortIfHung, SendTimeoutMs, out var result);

        return sent == 0 ? 0 : unchecked((int)result.ToInt64());
    }

    private static bool TryReadPosition(nint listView, nint process, nint buffer, int index, out NativeMethods.Point point)
    {
        point = default;

        if (Send(listView, LvmGetItemPosition, index, buffer) == 0)
        {
            return false;
        }

        var raw = new byte[8];
        if (!NativeMethods.ReadProcessMemory(process, buffer, raw, raw.Length, out var read) || read.ToInt64() < 8)
        {
            return false;
        }

        point = new NativeMethods.Point(BitConverter.ToInt32(raw, 0), BitConverter.ToInt32(raw, 4));
        return true;
    }

    private static string ReadText(nint listView, nint process, nint itemBuffer, nint textBuffer, int index)
    {
        var item = new NativeMethods.LvItem
        {
            Mask = LvifText,
            Item = index,
            SubItem = 0,
            Text = textBuffer,
            TextMax = TextCapacity,
        };

        var raw = new byte[Marshal.SizeOf<NativeMethods.LvItem>()];
        var handle = GCHandle.Alloc(raw, GCHandleType.Pinned);
        try
        {
            Marshal.StructureToPtr(item, handle.AddrOfPinnedObject(), fDeleteOld: false);
        }
        finally
        {
            handle.Free();
        }

        if (!NativeMethods.WriteProcessMemory(process, itemBuffer, raw, raw.Length, out _))
        {
            return string.Empty;
        }

        // 返回值就是复制过来的字符数（不含结尾 \0）。必须按它截断：
        // 缓冲是复用的，短名字后面会残留上一个长名字的字符，只按 \0 截断会读出脏数据。
        var copied = Send(listView, LvmGetItemTextW, index, itemBuffer);
        if (copied <= 0)
        {
            return string.Empty;
        }

        var length = Math.Min(copied, TextCapacity - 1);
        var text = new byte[TextCapacity * 2];
        if (!NativeMethods.ReadProcessMemory(process, textBuffer, text, text.Length, out var read) ||
            read.ToInt64() < length * 2L)
        {
            return string.Empty;
        }

        return System.Text.Encoding.Unicode.GetString(text, 0, length * 2).TrimEnd('\0').Trim();
    }

    /// <summary>图标位置是列表视图的客户区坐标，换算成虚拟屏幕坐标才能和盒子范围比较。</summary>
    private static NativeMethods.Point ToScreen(nint listView, NativeMethods.Point point)
    {
        var mapped = point;
        NativeMethods.MapWindowPoints(listView, 0, ref mapped, 1);
        return mapped;
    }
}