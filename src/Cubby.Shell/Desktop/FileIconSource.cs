using System.Runtime.InteropServices;
using Cubby.Shell.Interop;

namespace Cubby.Shell.Desktop;

/// <summary>
/// 取文件 / 文件夹 / 链接对应的 **shell 真实图标**。
///
/// 为什么非要用它：图标是"这东西是什么"的第一信息，而 Windows 用户在桌面上看到的一直是
/// 资源管理器给的那套图标。自己画一套（哪怕是做得挺精致的字母方块）都会立刻露馅——
/// 用户一眼就能看出"这不是系统的图标"，观感随即从"桌面的一部分"掉成"某个软件画的方块"。
///
/// 用法上刻意**不向调用方交出 HICON**：句柄一旦外泄就总会有人忘记释放，
/// 而图标句柄泄漏会直接顶高 GDI 对象数（A7 挂机门禁盯的就是这一项）。
/// 所以这里只开一个口子——取出来、交给你转成自己的类型、然后立刻释放。
/// </summary>
public static class FileIconSource
{
    /// <summary>
    /// 取图标并转换。<paramref name="path"/> 可以指向不存在的文件：
    /// 那种情况下会退到"按扩展名给通用图标"，因为盒子里的条目允许指向已被删除的东西，
    /// 而系统仍然知道 <c>.docx</c> 长什么样。拿不到时返回 null，由调用方降级，绝不抛异常。
    /// </summary>
    /// <param name="path">真实路径，或一个只用来表明扩展名的占位路径（如 <c>link.url</c>）。</param>
    /// <param name="isDirectory">占位路径用于区分文件夹与文件（真实路径下由系统自己判断）。</param>
    /// <param name="convert">把 HICON 转成调用方要的类型；返回 null 表示转换失败。</param>
    public static T? TryRead<T>(string? path, bool isDirectory, Func<nint, T?> convert)
        where T : class
    {
        if (string.IsNullOrWhiteSpace(path) || convert is null)
        {
            return null;
        }

        // 先按真实路径问一次：这样 exe、快捷方式能拿到它们各自的图标，而不是"某个 .lnk"
        var icon = Query(path, isDirectory, useFileAttributes: false);

        // 再退到"按扩展名给通用图标"：文件已删除、或调用方给的是占位路径时走这条
        if (icon == 0)
        {
            icon = Query(path, isDirectory, useFileAttributes: true);
        }

        if (icon == 0)
        {
            return null;
        }

        try
        {
            return convert(icon);
        }
        finally
        {
            NativeMethods.DestroyIcon(icon);
        }
    }

    private static nint Query(string path, bool isDirectory, bool useFileAttributes)
    {
        // ByValTStr 字段必须先初始化：传 null 进去封送处理会抛异常
        var info = new NativeMethods.ShFileInfo
        {
            DisplayName = string.Empty,
            TypeName = string.Empty,
        };

        var flags = NativeMethods.ShgfiIcon | NativeMethods.ShgfiLargeIcon;
        var attributes = 0u;

        if (useFileAttributes)
        {
            flags |= NativeMethods.ShgfiUseFileAttributes;
            attributes = isDirectory ? NativeMethods.FileAttributeDirectory : NativeMethods.FileAttributeNormal;
        }

        try
        {
            NativeMethods.SHGetFileInfo(
                path,
                attributes,
                ref info,
                (uint)Marshal.SizeOf<NativeMethods.ShFileInfo>(),
                flags);

            return info.Icon;
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or DllNotFoundException)
        {
            // shell 查询失败只意味着"这次没有图标"，不该影响盒子的渲染
            return 0;
        }
    }
}
