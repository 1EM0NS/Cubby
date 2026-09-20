using System.IO;
using Microsoft.Win32;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Cubby.App.Views;

/// <summary>
/// 壁纸背景层：给盒子提供「亚克力」质感的底层图像。
///
/// 为什么需要它：DeskBox 的好看有一大半来自 Acrylic——格子不是一块实心底板，而是
/// 「模糊的壁纸 + 深色罩」。WPF 没有系统亚克力，但壁纸就在注册表里，裁一块、模糊、
/// 罩一层深色罩，就是同样的效果（真实感略逊于真亚克力：动态壁纸不会跟着动，见 ADR 取舍）。
///
/// 坐标约定：壁纸按**像素**存放，裁切矩形用虚拟桌面 DIP 坐标 × 该屏 DpiScale 换算。
/// 壁纸铺法（适应 / 填充 / 平铺）会让对齐有偏差——按最常见的"填充"近似，见 Preview 复核。
/// </summary>
internal static class WallpaperBackdrop
{
    /// <summary>是否启用壁纸底层（P2 自检的离屏渲染会临时关掉它，见 PreviewRenderer）。</summary>
    public static bool Enabled { get; set; } = true;

    private static BitmapSource? _wallpaper;
    private static bool _loaded;
    private static readonly object Gate = new();

    /// <summary>壁纸换了（用户在系统里换壁纸）时通知：所有盒子重裁底层。</summary>
    public static event Action? Changed;

    /// <summary>取壁纸在像素矩形 <paramref name="pixelRect"/> 处的裁剪；壁纸不可用返回 null。</summary>
    public static ImageSource? Crop(Int32Rect pixelRect, double expandPixels = 48)
    {
        if (!Enabled)
        {
            return null;
        }

        var wallpaper = EnsureLoaded();

        if (wallpaper is null)
        {
            return null;
        }

        var rect = new Rect(
            pixelRect.X - expandPixels,
            pixelRect.Y - expandPixels,
            pixelRect.Width + (expandPixels * 2),
            pixelRect.Height + (expandPixels * 2));

        // 裁到壁纸边界内；扩出来的边正是给高斯模糊取样用的，越界就按实际有的来
        rect.Intersect(new Rect(0, 0, wallpaper.PixelWidth, wallpaper.PixelHeight));

        if (rect.IsEmpty || rect.Width < 1 || rect.Height < 1)
        {
            return null;
        }

        var crop = new CroppedBitmap(
            wallpaper,
            new Int32Rect(
                (int)Math.Floor(rect.X),
                (int)Math.Floor(rect.Y),
                (int)Math.Ceiling(rect.Width),
                (int)Math.Ceiling(rect.Height)));
        crop.Freeze();
        return crop;
    }

    /// <summary>用户在系统里换壁纸后调用：清缓存并广播，所有盒子重裁。</summary>
    public static void Invalidate()
    {
        lock (Gate)
        {
            _loaded = false;
            _wallpaper = null;
        }

        Changed?.Invoke();
    }

    private static BitmapSource? EnsureLoaded()
    {
        lock (Gate)
        {
            if (_loaded)
            {
                return _wallpaper;
            }

            _loaded = true;
            _wallpaper = LoadCore();
            return _wallpaper;
        }
    }

    private static BitmapSource? LoadCore()
    {
        var path = ReadWallpaperPath();

        if (path is null)
        {
            return null;
        }

        try
        {
            var bitmap = new BitmapImage();
            bitmap.BeginInit();
            bitmap.UriSource = new Uri(path);
            bitmap.CacheOption = BitmapCacheOption.OnLoad;
            bitmap.EndInit();
            bitmap.Freeze();
            return bitmap;
        }
        catch (IOException)
        {
            return null;
        }
        catch (InvalidOperationException)
        {
            // 解码失败（格式不支持 / 文件损坏）：降级到无壁纸的渐变底
            return null;
        }
    }

    /// <summary>
    /// 壁纸路径：优先注册表 <c>HKCU\Control Panel\Desktop\Wallpaper</c>；
    /// 为空时（JPEG 转码壁纸常见）退回 <c>TranscodedWallpaper</c>。纯色桌面返回 null。
    /// </summary>
    private static string? ReadWallpaperPath()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Control Panel\Desktop");
            var fromRegistry = key?.GetValue("Wallpaper") as string;

            if (!string.IsNullOrWhiteSpace(fromRegistry) && File.Exists(fromRegistry))
            {
                return fromRegistry;
            }
        }
        catch (Exception ex) when (ex is IOException or System.Security.SecurityException or ArgumentException)
        {
            // 读不到注册表就继续尝试转码路径
        }

        var transcoded = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            @"Microsoft\Windows\Themes\TranscodedWallpaper");

        return File.Exists(transcoded) ? transcoded : null;
    }
}
