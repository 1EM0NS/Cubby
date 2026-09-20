using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Cubby.App.Views;
using Cubby.Core.Layout;
using Cubby.Core.Model;

namespace Cubby.App;

/// <summary>
/// 把盒子**离屏**渲染成一张 PNG，用于设计复核。
///
/// 为什么需要它：盒子长在浮层窗口上，而浮层窗口是盖在用户桌面上的。
/// 每调一次样式就"闪一下"用户的屏幕是不能接受的（尤其是用户在正常用电脑的时候）。
/// 这里直接构造 <see cref="BoxView"/>、测量、排列、渲染成位图，
/// **全程不创建任何窗口**——屏幕上什么都不会出现，随时可以反复渲染对比。
///
/// 底色刻意做成"左浅右深"的渐变，一次就能看出盒子在浅色壁纸和深色壁纸上的表现：
/// 这是桌面工具最容易翻车的地方（在自己的深色底上好看，到了浅色壁纸上就糊了）。
/// </summary>
/// <summary>一次离屏渲染的结果。</summary>
/// <param name="Path">预览图路径。</param>
/// <param name="NoBleed">盒子之外是否完全没有绘制（P2）。</param>
/// <param name="BleedDetail">P2 自检的明细，逐盒子列出。</param>
/// <param name="IconDetail">每个样例图标拿到的位图尺寸与 DPI。图标被拉变形过一次，写出来省得靠肉眼猜。</param>
internal sealed record PreviewResult(string Path, bool NoBleed, string BleedDetail, string IconDetail);

internal static class PreviewRenderer
{
    private const double Gap = 28;

    public static PreviewResult Render(string outputDirectory)
    {
        var surface = new MonitorSurface("preview", new PixelRect(0, 0, 2560, 1440), 1.0) { IsPrimary = true };
        var style = new StyleSettings { CornerRadius = 8, Columns = 4, FontSize = 13, Opacity = 0.85 };

        var boxes = SampleBoxes();
        var heights = boxes.Select(BoxGeometry.EffectiveHeight).ToList();

        // 菜单要先量出来，才能算画布高度。
        // ContextMenu 默认是 Collapsed，不显式打开就量不出尺寸（DesiredSize 会是 0）
        var menu = BuildSampleMenu();
        menu.Visibility = Visibility.Visible;
        menu.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        var menuSize = menu.DesiredSize;

        var width = Math.Max(boxes.Max(b => b.Bounds.Width), menuSize.Width) + (Gap * 2);
        var height = heights.Sum() + menuSize.Height + (Gap * (boxes.Count + 2));

        var canvas = new Canvas { Width = width, Height = height };

        canvas.Background = new LinearGradientBrush
        {
            StartPoint = new Point(0, 0),
            EndPoint = new Point(1, 0),
            GradientStops =
            {
                new GradientStop(Color.FromRgb(0xC3, 0xC3, 0xC3), 0),
                new GradientStop(Color.FromRgb(0x60, 0x60, 0x60), 0.5),
                new GradientStop(Color.FromRgb(0x2A, 0x2A, 0x2A), 1),
            },
        };

        var top = Gap;
        for (var i = 0; i < boxes.Count; i++)
        {
            var view = new BoxView(boxes[i], style, surface);

            Canvas.SetLeft(view, Gap);
            Canvas.SetTop(view, top);
            canvas.Children.Add(view);

            top += heights[i] + Gap;
        }

        // 菜单样例单独画一块，不叠在盒子上（叠上去整张图就看不清了）。
        // 它是**最容易翻车的控件**：不在浮层窗口里，样式一旦没盖住就会弹出系统默认的浅色菜单。
        //
        // ⚠️ 它不能直接挂进视觉树：WPF 明确禁止 ContextMenu 有逻辑/视觉父级
        //    （抛「ContextMenu 不能有逻辑或视觉父级」）。所以先单独渲染成位图，再把位图贴上来。
        menu.Arrange(new Rect(0, 0, menuSize.Width, menuSize.Height));
        menu.UpdateLayout();

        var menuImage = new Image
        {
            Source = RenderToBitmap(menu, (int)Math.Ceiling(menuSize.Width), (int)Math.Ceiling(menuSize.Height)),
            Width = menuSize.Width,
            Height = menuSize.Height,
            Stretch = Stretch.None,
        };

        Canvas.SetLeft(menuImage, Gap);
        Canvas.SetTop(menuImage, top);
        canvas.Children.Add(menuImage);

        canvas.Measure(new Size(width, height));
        canvas.Arrange(new Rect(0, 0, width, height));
        canvas.UpdateLayout();

        var bitmap = new RenderTargetBitmap(
            (int)Math.Ceiling(width),
            (int)Math.Ceiling(height),
            96,
            96,
            PixelFormats.Pbgra32);
        bitmap.Render(canvas);

        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));

        Directory.CreateDirectory(outputDirectory);
        var path = Path.Combine(outputDirectory, "design-preview.png");

        using (var stream = File.Create(path))
        {
            encoder.Save(stream);
        }

        // 顺带做 P2 自检：盒子外的像素一旦非零就会抢走桌面与动态壁纸的点击（这是产品的立身之本）。
        // 放在离屏做，随时可验证，而不用把浮层真的显示到用户屏幕上。
        var details = new List<string>();
        var noBleed = true;

        foreach (var box in boxes)
        {
            var (clear, detail) = CheckNoBleed(box, style, surface);
            noBleed &= clear;
            details.Add($"{box.Name} — {detail}");
        }

        return new PreviewResult(path, noBleed, string.Join("；", details), DescribeIcons(boxes));
    }

    /// <summary>
    /// 逐个报告样例图标拿到的位图尺寸与 DPI。
    /// 存在的理由：图标"看起来被压扁"时，靠肉眼分不清是**取到的位图不是正方形**，
    /// 还是**那个图标本来就长这样**。把尺寸写出来，一眼就能分辨。
    /// </summary>
    private static string DescribeIcons(IReadOnlyList<Box> boxes) => string.Join(
        "；",
        boxes
            .SelectMany(box => box.Items)
            .Select(item => FileIconCache.For(item) is BitmapSource bitmap
                ? $"{item.DisplayName} {bitmap.PixelWidth}×{bitmap.PixelHeight}@{bitmap.DpiX:0}"
                : $"{item.DisplayName} 取不到图标"));

    /// <summary>把一个元素单独渲染成位图——用于那些不能挂进视觉树的控件（例如 ContextMenu）。</summary>
    private static BitmapSource RenderToBitmap(FrameworkElement element, int width, int height)
    {
        var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(element);
        bitmap.Freeze();
        return bitmap;
    }

    /// <summary>
    /// 把盒子渲染到一块**透明**画布上（四周留出外扩带），断言外扩带里没有任何 alpha&gt;0 的像素。
    /// 外扩带是阴影 / 外发光最先污染的地方，也正是 P2 的判据所在。
    /// </summary>
    private static (bool Clear, string Detail) CheckNoBleed(Box box, StyleSettings style, MonitorSurface surface)
    {
        const int bleed = 6;

        var boxWidth = (int)Math.Ceiling(box.Bounds.Width);
        var boxHeight = (int)Math.Ceiling(BoxGeometry.EffectiveHeight(box));
        var width = boxWidth + (bleed * 2);
        var height = boxHeight + (bleed * 2);

        var host = new Canvas { Width = width, Height = height };

        var view = new BoxView(box, style, surface);
        Canvas.SetLeft(view, bleed);
        Canvas.SetTop(view, bleed);
        host.Children.Add(view);

        host.Measure(new Size(width, height));
        host.Arrange(new Rect(0, 0, width, height));
        host.UpdateLayout();

        var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(host);

        var stride = width * 4;
        var pixels = new byte[stride * height];
        bitmap.CopyPixels(pixels, stride, 0);

        var offenders = 0;

        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                var inBox = x >= bleed && x < bleed + boxWidth && y >= bleed && y < bleed + boxHeight;

                if (!inBox && pixels[(y * stride) + (x * 4) + 3] > 0)
                {
                    offenders++;
                }
            }
        }

        return offenders == 0
            ? (true, "外扩带完全透明")
            : (false, $"外扩带有 {offenders} 个非透明像素");
    }

    /// <summary>
    /// 右键菜单的样例。刻意放进预览图：**菜单是最容易翻车的控件**——
    /// 它不在浮层窗口里，样式一旦没盖住，就会在深色盒子上弹出一个系统默认的浅色菜单。
    /// 上一版正是如此（白底黑字的菜单压在深色盒子上，非常刺眼）。
    /// </summary>
    private static ContextMenu BuildSampleMenu()
    {
        var menu = new ContextMenu();

        menu.Items.Add(new MenuItem { Header = "打开" });
        menu.Items.Add(new MenuItem { Header = "打开位置" });
        menu.Items.Add(new Separator());
        menu.Items.Add(new MenuItem { Header = "重命名" });
        menu.Items.Add(new MenuItem { Header = "移出盒子" });

        return menu;
    }

    /// <summary>
    /// 三个样例盒子：正常（各类条目）、映射文件夹（有状态强调条）、空（引导态）。
    /// 条目用**不存在的路径 + 真实扩展名**——shell 会按扩展名给出对应程序的图标，
    /// 既能展示各类图标，又不去碰用户磁盘上的任何东西。
    /// </summary>
    private static IReadOnlyList<Box> SampleBoxes()
    {
        var folder = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            "Documents",
            "Cubby预览");

        string InFolder(string name) => System.IO.Path.Combine(folder, name);

        return
        [
            new Box("preview", "工作资料", new DipRect(0, 0, 372, 296))
            {
                Columns = 4,
                Items =
                [
                    new BoxItem("p1", "项目文档", folder, ItemKind.Folder),
                    new BoxItem("p2", "预算表.xlsx", InFolder("预算表.xlsx"), ItemKind.File),
                    new BoxItem("p3", "会议记录.docx", InFolder("会议记录.docx"), ItemKind.File),
                    new BoxItem("p4", "产品手册.pdf", InFolder("产品手册.pdf"), ItemKind.File),
                    new BoxItem("p5", "封面图.png", InFolder("封面图.png"), ItemKind.File),
                    new BoxItem("p6", "素材包.zip", InFolder("素材包.zip"), ItemKind.File),
                    new BoxItem("p7", "待办.txt", InFolder("待办.txt"), ItemKind.File),
                    new BoxItem("p8", "官网首页", "https://example.com", ItemKind.Url),
                ],
            },

            new Box("preview-mapped", "设计素材（映射）", new DipRect(0, 0, 372, 200))
            {
                Columns = 4,
                MappedFolder = folder,
                Items =
                [
                    new BoxItem("m1", "图标.ico", InFolder("图标.ico"), ItemKind.Mapped),
                    new BoxItem("m2", "取色板.png", InFolder("取色板.png"), ItemKind.Mapped),
                    new BoxItem("m3", "字体说明.txt", InFolder("字体说明.txt"), ItemKind.Mapped),
                ],
            },

            new Box("preview-empty", "新盒子", new DipRect(0, 0, 372, 150))
            {
                Columns = 4,
                Items = [],
            },
        ];
    }
}
