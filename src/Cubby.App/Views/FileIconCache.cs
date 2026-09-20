using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Cubby.Core.Model;
using Cubby.Shell.Desktop;

namespace Cubby.App.Views;

/// <summary>
/// 盒子条目图标的缓存层：把 <see cref="FileIconSource"/> 给出的 HICON 转成 WPF 图像并缓存。
///
/// 缓存键刻意按**扩展名**而不是路径：一个盒子里放十几张图、几十个文档是常态，
/// 每个都去问一次 shell 是没必要的开销，而同一扩展名的图标本来就一样。
/// 例外是"图标由文件自身携带"的几类（exe / lnk / ico …），它们必须按路径缓存，否则会互相串图标。
///
/// 构建只在 UI 线程发生（<c>BoxView</c> 在视觉树里同步建条目），所以这里不加锁。
/// 若将来条目改为异步加载，这里要一起改。
/// </summary>
internal static class FileIconCache
{
    private static readonly Dictionary<string, ImageSource?> Cache = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>图标写在文件自己身上的扩展名：这类必须按路径缓存，不能按扩展名合并。</summary>
    private static readonly HashSet<string> SelfIconed = new(StringComparer.OrdinalIgnoreCase)
    {
        ".exe", ".lnk", ".ico", ".msi", ".scr", ".cpl", ".dll", ".url",
    };

    /// <summary>取条目图标；拿不到返回 null，由调用方降级（绝不让界面因为图标问题渲染不出来）。</summary>
    public static ImageSource? For(BoxItem item)
    {
        var key = KeyOf(item);

        if (Cache.TryGetValue(key, out var cached))
        {
            return cached;
        }

        var (path, isDirectory) = ProbeOf(item);
        var image = FileIconSource.TryRead(path, isDirectory, ToImageSource);

        Cache[key] = image;
        return image;
    }

    /// <summary>已经缓存的图标数量（诊断用）。</summary>
    public static int Count => Cache.Count;

    private static string KeyOf(BoxItem item)
    {
        var extension = Path.GetExtension(item.TargetPath);

        if (item.Kind is ItemKind.Folder or ItemKind.Mapped || string.IsNullOrEmpty(extension))
        {
            return $"kind:{item.Kind}";
        }

        return SelfIconed.Contains(extension)
            ? $"path:{item.TargetPath}"
            : $"ext:{extension.ToLowerInvariant()}";
    }

    /// <summary>
    /// 交给 shell 的路径。网址条目在磁盘上没有对应文件，
    /// 所以给一个"只有扩展名"的占位路径，让 shell 按 <c>.url</c> 关联给出链接图标。
    /// </summary>
    private static (string Path, bool IsDirectory) ProbeOf(BoxItem item) => item.Kind switch
    {
        ItemKind.Folder => (item.TargetPath, true),
        ItemKind.Mapped => (item.TargetPath, true),
        ItemKind.Url => ("link.url", false),
        _ => (item.TargetPath, false),
    };

    private static ImageSource? ToImageSource(nint icon)
    {
        try
        {
            var source = Imaging.CreateBitmapSourceFromHIcon(
                icon,
                Int32Rect.Empty,
                BitmapSizeOptions.FromEmptyOptions());

            // 冻结后可以跨线程复用，也让 WPF 把它当不可变资源对待（渲染更省）
            source.Freeze();
            return source;
        }
        catch (Exception ex) when (ex is ArgumentException or COMException or NotSupportedException)
        {
            return null;
        }
    }
}
