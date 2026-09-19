using Cubby.Shell.Interop;

namespace Cubby.Shell.Desktop;

/// <summary>
/// 真实桌面图标层（<c>SysListView32</c>）的定位与显隐。
///
/// 这里**只做窗口级显示/隐藏**，绝不调用任何 LVM_* 写入接口（P4）：
/// 图标位置、排列顺序、选中状态一概不碰。定位链与 <see cref="Diagnostics.DesktopProbe"/> 共用。
/// </summary>
public static class DesktopIcons
{
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
}