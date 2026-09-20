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
internal sealed record PreviewResult(string Path, bool NoBleed, string BleedDetail);

internal static class PreviewRenderer
{
    private const double Gap = 28;

    public static PreviewResult Render(string outputDirectory)
    {
        var surface = new MonitorSurface("preview", new PixelRect(0, 0, 2560, 1440), 1.0) { IsPrimary = true };
        var style = new StyleSettings { CornerRadius = 8, Columns = 4, FontSize = 13, Opacity = 0.85 };

        var boxes = SampleBoxes();
        var heights = boxes.Select(BoxGeometry.EffectiveHeight).ToList();

        var width = boxes.Max(b => b.Bounds.Width) + (Gap * 2);
        var height = heights.Sum() + (Gap * (boxes.Count + 1));

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

        return new PreviewResult(path, noBleed, string.Join("；", details));
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
