using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Cubby.Core.Layout;
using Cubby.Core.Model;
using Cubby.Shell.Overlay;

namespace Cubby.App;

/// <summary>
/// 视觉打磨的自动化验收（issue #37）。
///
/// 打磨最怕的不是"不好看"，而是**把盒子外的像素画成了 alpha&gt;0**——那会直接抢走桌面与动态壁纸的点击，
/// 破坏 P2 这条立身之本。因此这里不看截图"好不好看"（那要靠人眼），而是做两件机器能判定的事：
///
/// 1. **像素级 P2 断言**：把浮层画布渲染成位图，断言「盒子内部 alpha&gt;0」且「盒子外（含紧邻的 6px 外扩带）alpha=0」。
///    后者正是"不许用 DropShadowEffect / 外发光"这条约束的客观证据。
/// 2. **产物**：把渲染结果合成到深色底上存成 PNG，供人眼复核设计（盒子 + 一个带示例条目的样例盒子）。
/// </summary>
internal static class AppearanceTestRunner
{
    /// <summary>外扩带宽度（DIP）：阴影 / 发光若存在，最先污染的就是紧贴盒子的这一圈。</summary>
    private const int BleedBand = 6;

    public static async Task<int> RunAsync(OverlayWindow overlay, LayoutService layout, SpikeOptions options)
    {
        var results = new List<(string Step, bool Pass, string Detail)>();
        var surface = overlay.Surface!;

        // 让布局与首帧都稳定下来，再取样
        await Task.Delay(600);

        var width = (int)Math.Ceiling(overlay.BoxLayer.ActualWidth);
        var height = (int)Math.Ceiling(overlay.BoxLayer.ActualHeight);

        if (width <= 0 || height <= 0)
        {
            results.Add(("渲染浮层画布", false, $"画布尺寸异常：{width}×{height}"));
            Write(options, results, null);
            return 1;
        }

        var pixels = Render(overlay.BoxLayer, width, height);
        results.Add((
            "渲染浮层画布",
            pixels is not null,
            $"画布 {width}×{height} DIP，盒子 {layout.Boxes.Count} 个"));

        if (pixels is null)
        {
            Write(options, results, null);
            return 1;
        }

        var boxes = layout.Boxes;
        var transient = (pixels, width, height);

        // 1. 盒子内部必须真的画出来了
        var insideChecked = 0;
        var insideOpaque = 0;
        var insideDetail = new StringBuilder();

        foreach (var box in boxes)
        {
            var rect = new DipRect(box.Bounds.X, box.Bounds.Y, box.Bounds.Width, BoxGeometry.EffectiveHeight(box));
            var probes = InsetPoints(rect, 14);

            var opaque = probes.Count(p => AlphaAt(transient, p.X, p.Y) > 0);
            insideChecked += probes.Count;
            insideOpaque += opaque;

            if (probes.Count > 0 && opaque != probes.Count)
            {
                insideDetail.Append($"「{box.Name}」{opaque}/{probes.Count}；");
            }
        }

        var insideMissed = insideChecked == 0
            ? "没有可采样的盒子（布局为空）"
            : insideDetail.Length == 0
                ? $"{insideOpaque}/{insideChecked} 个采样点不透明（全部命中）"
                : $"{insideOpaque}/{insideChecked} 个采样点不透明；未命中：{insideDetail}";

        results.Add((
            "盒子内部有实际绘制（渐变底 / 强调条 / 标题栏都在盒子矩形内）",
            insideChecked > 0 && insideOpaque == insideChecked,
            insideMissed));

        // 2. 盒子外紧邻的外扩带必须完全透明 —— 这是"没有外扩阴影 / 发光"的硬证据
        var bleedChecked = 0;
        var bleedHits = new List<string>();

        foreach (var box in boxes)
        {
            var rect = new DipRect(box.Bounds.X, box.Bounds.Y, box.Bounds.Width, BoxGeometry.EffectiveHeight(box));
            var centerX = (int)Math.Round(rect.X + (rect.Width / 2));
            var centerY = (int)Math.Round(rect.Y + (rect.Height / 2));

            var ring = new[]
            {
                ((int)Math.Round(rect.X) - BleedBand, centerY, "左"),
                ((int)Math.Round(rect.X + rect.Width) + BleedBand, centerY, "右"),
                (centerX, (int)Math.Round(rect.Y) - BleedBand, "上"),
                (centerX, (int)Math.Round(rect.Y + rect.Height) + BleedBand, "下"),
            };

            foreach (var (x, y, side) in ring)
            {
                if (x < 0 || y < 0 || x >= width || y >= height)
                {
                    continue; // 出了画布就不算（比如盒子贴着屏幕边缘）
                }

                if (boxes.Any(b => Hit(b, x, y)))
                {
                    continue; // 落在别的盒子上，不能作为"外扩"证据
                }

                bleedChecked++;
                var alpha = AlphaAt(transient, x, y);
                if (alpha != 0)
                {
                    bleedHits.Add($"「{box.Name}」{side}侧 ({x},{y}) alpha={alpha}");
                }
            }
        }

        var bleedDetail = bleedChecked == 0
            ? "没有可采样的外扩点"
            : bleedHits.Count == 0
                ? $"{bleedChecked} 个外扩采样点 alpha 全为 0"
                : $"发现非透明外扩：{string.Join("；", bleedHits.Take(4))}";

        results.Add((
            $"盒子外侧 {BleedBand}px 外扩带完全透明（无外扩阴影 / 发光，P2 像素级证据）",
            bleedChecked > 0 && bleedHits.Count == 0,
            bleedDetail));

        // 3. 远离所有盒子的区域也必须透明
        var farProbes = new[]
        {
            (20, 20), (width / 2, 20), (width - 20, 20),
            (20, height / 2), (width - 20, height / 2),
            (20, height - 20), (width / 2, height - 20), (width - 20, height - 20),
        }
            .Where(p => p.Item1 >= 0 && p.Item2 >= 0 && p.Item1 < width && p.Item2 < height)
            .Where(p => !boxes.Any(b => Hit(b, p.Item1, p.Item2)))
            .ToList();

        var farHits = farProbes.Where(p => AlphaAt(transient, p.Item1, p.Item2) != 0).ToList();
        var farDetail = farHits.Count == 0
            ? $"{farProbes.Count} 个远端采样点 alpha 全为 0"
            : $"发现非透明点：{string.Join("；", farHits.Select(p => $"({p.Item1},{p.Item2})"))}";

        results.Add((
            "盒子之外的画布区域完全透明（点击照常穿透到桌面 / 动态壁纸）",
            farProbes.Count > 0 && farHits.Count == 0,
            farDetail));

        // 4. 样例盒子（带条目）单独渲染一份，供人眼复核设计
        var sampleBox = BuildSampleBox(surface);
        var sampleWidth = (int)sampleBox.Bounds.Width;
        var sampleHeight = (int)BoxGeometry.EffectiveHeight(sampleBox);
        var sampleLayer = new System.Windows.Controls.Canvas
        {
            Width = sampleWidth,
            Height = sampleHeight,
            Background = Brushes.Transparent,
        };

        var sampleView = new Views.BoxView(sampleBox, layout.Style, surface);
        System.Windows.Controls.Canvas.SetLeft(sampleView, 0);
        System.Windows.Controls.Canvas.SetTop(sampleView, 0);
        sampleLayer.Children.Add(sampleView);

        sampleLayer.Measure(new Size(sampleWidth, sampleHeight));
        sampleLayer.Arrange(new Rect(0, 0, sampleWidth, sampleHeight));
        sampleLayer.UpdateLayout();

        var samplePixels = Render(sampleLayer, sampleWidth, sampleHeight);

        // 样例盒子同样断言内部有绘制，避免"预览图是空的但没人发现"
        var sampleOpaque = samplePixels is null
            ? 0
            : InsetPoints(new DipRect(0, 0, sampleBox.Bounds.Width, BoxGeometry.EffectiveHeight(sampleBox)), 14)
                .Count(p => AlphaAt((samplePixels, sampleWidth, sampleHeight), p.X, p.Y) > 0);

        var sampleDetail = samplePixels is null ? "渲染失败" : $"不透明采样点 {sampleOpaque} 个";

        results.Add((
            "样例盒子（含文件夹 / 图片 / 文本 / 网页四类条目）渲染成功",
            samplePixels is not null && sampleOpaque > 0,
            sampleDetail));

        var previewPath = WritePreview(options, transient, boxes, sampleBox, samplePixels);
        results.Add((
            "产出人眼可复核的预览图",
            previewPath is not null && File.Exists(previewPath),
            previewPath is null ? "写入失败" : previewPath));

        var failed = results.Count(r => !r.Pass);
        Write(options, results, previewPath);

        return failed == 0 && results.Count > 0 ? 0 : 1;
    }

    /// <summary>悬浮窗用的样例盒子：四类条目各一，尽量覆盖徽标配色与换行表现。</summary>
    private static Box BuildSampleBox(MonitorSurface surface)
    {
        var items = new[]
        {
            new BoxItem("s1", "项目文档", @"C:\示例\项目文档", ItemKind.Folder),
            new BoxItem("s2", "封面图.png", @"C:\示例\封面图.png", ItemKind.File),
            new BoxItem("s3", "会议记录.docx", @"C:\示例\会议记录.docx", ItemKind.File),
            new BoxItem("s4", "官网首页", "https://example.com", ItemKind.Url),
        };

        return new Box("appearance-sample", "示例盒子", new DipRect(0, 0, 360, 300))
        {
            MonitorId = surface.Id,
            MonitorWidth = surface.Bounds.Width,
            MonitorHeight = surface.Bounds.Height,
            Items = items,
        };
    }

    /// <summary>矩形内部缩进后的 3×3 采样点（避开圆角与外描边）。</summary>
    private static List<(int X, int Y)> InsetPoints(DipRect rect, int inset)
    {
        var points = new List<(int, int)>();

        var left = (int)Math.Round(rect.X) + inset;
        var top = (int)Math.Round(rect.Y) + inset;
        var right = (int)Math.Round(rect.X + rect.Width) - inset;
        var bottom = (int)Math.Round(rect.Y + rect.Height) - inset;

        if (right <= left || bottom <= top)
        {
            return points;
        }

        var midX = (left + right) / 2;
        var midY = (top + bottom) / 2;

        foreach (var x in new[] { left, midX, right })
        {
            foreach (var y in new[] { top, midY, bottom })
            {
                points.Add((x, y));
            }
        }

        return points;
    }

    private static bool Hit(Box box, int x, int y) =>
        x >= box.Bounds.X &&
        y >= box.Bounds.Y &&
        x < box.Bounds.X + box.Bounds.Width &&
        y < box.Bounds.Y + BoxGeometry.EffectiveHeight(box);

    private static byte AlphaAt((byte[] Pixels, int Width, int Height) image, int x, int y)
    {
        if (x < 0 || y < 0 || x >= image.Width || y >= image.Height)
        {
            return 0;
        }

        return image.Pixels[(((y * image.Width) + x) * 4) + 3];
    }

    /// <summary>把视觉对象渲染成 BGRA32 像素（96 DPI，与 DIP 一一对应）。</summary>
    private static byte[]? Render(Visual visual, int width, int height)
    {
        try
        {
            var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
            bitmap.Render(visual);

            var stride = width * 4;
            var pixels = new byte[stride * height];
            bitmap.CopyPixels(pixels, stride, 0);
            return pixels;
        }
        catch (Exception ex) when (ex is COMException or InvalidOperationException or ArgumentException)
        {
            Console.Error.WriteLine($"渲染失败：{ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// 合成一张预览图（纯给人看，不参与断言）：深色底 = 桌面，
    /// 上半部分是**真实浮层里盒子所在的那块区域**（裁到盒子范围，否则整屏截图里盒子只有一个小点），
    /// 下半部分是样例盒子（带四类条目），用来复核条目磁贴的观感。
    /// </summary>
    private static string? WritePreview(
        SpikeOptions options,
        (byte[] Pixels, int Width, int Height) overlayImage,
        IReadOnlyList<Box> boxes,
        Box sampleBox,
        byte[]? samplePixels)
    {
        try
        {
            var sampleWidth = (int)sampleBox.Bounds.Width;
            var sampleHeight = (int)BoxGeometry.EffectiveHeight(sampleBox);
            var gap = 24;

            // 裁到所有盒子的并集（外加一圈留白），让真实布局在预览里看得清
            var crop = CropRect(overlayImage, boxes);

            var canvasWidth = Math.Max(crop.Width, samplePixels is null ? 0 : sampleWidth) + (gap * 2);
            var canvasHeight = crop.Height + (samplePixels is null ? 0 : sampleHeight + 34);

            var visual = new DrawingVisual();
            using (var context = visual.RenderOpen())
            {
                var backdrop = new SolidColorBrush(Color.FromRgb(0x0D, 0x12, 0x17));
                context.DrawRectangle(backdrop, null, new Rect(0, 0, canvasWidth, canvasHeight));

                context.DrawText(Caption($"真实浮层（当前布局，{boxes.Count} 个盒子）"), new Point(gap, 8));
                DrawPixels(context, overlayImage.Pixels, overlayImage.Width, crop, new Point(gap, 28));

                if (samplePixels is not null)
                {
                    var sampleTop = 28 + crop.Height + 20;
                    context.DrawText(Caption("示例盒子（文件夹 / 图片 / 文本 / 网页四类条目）"), new Point(gap, sampleTop - 18));
                    DrawPixels(context, samplePixels, sampleWidth, sampleHeight, new Point(gap, sampleTop));
                }
            }

            var bitmap = new RenderTargetBitmap(canvasWidth, canvasHeight, 96, 96, PixelFormats.Pbgra32);
            bitmap.Render(visual);

            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(bitmap));

            var directory = options.OutputDirectory ?? Path.Combine(AppContext.BaseDirectory, "artifacts");
            Directory.CreateDirectory(directory);

            var path = Path.Combine(directory, "appearance-preview.png");
            using var stream = File.Create(path);
            encoder.Save(stream);
            return path;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            Console.Error.WriteLine($"预览图写入失败：{ex.Message}");
            return null;
        }
    }

    private static FormattedText Caption(string text) => new(
        text,
        System.Globalization.CultureInfo.CurrentCulture,
        FlowDirection.LeftToRight,
        new Typeface("Microsoft YaHei UI"),
        12.5,
        new SolidColorBrush(Color.FromRgb(0x97, 0xA4, 0xB1)),
        1.0);

    /// <summary>所有盒子的并集矩形（外加留白），并夹在画布范围内；没有盒子时返回整块画布。</summary>
    private static Int32Rect CropRect((byte[] Pixels, int Width, int Height) image, IReadOnlyList<Box> boxes)
    {
        if (boxes.Count == 0)
        {
            return new Int32Rect(0, 0, image.Width, image.Height);
        }

        const int padding = 20;

        var left = boxes.Min(b => b.Bounds.X);
        var top = boxes.Min(b => b.Bounds.Y);
        var right = boxes.Max(b => b.Bounds.X + b.Bounds.Width);
        var bottom = boxes.Max(b => b.Bounds.Y + BoxGeometry.EffectiveHeight(b));

        var x = Math.Max(0, (int)Math.Floor(left) - padding);
        var y = Math.Max(0, (int)Math.Floor(top) - padding);
        var w = Math.Min(image.Width - x, (int)Math.Ceiling(right - left) + (padding * 2));
        var h = Math.Min(image.Height - y, (int)Math.Ceiling(bottom - top) + (padding * 2));

        return new Int32Rect(x, y, Math.Max(1, w), Math.Max(1, h));
    }

    /// <summary>把大位图里的某块矩形拷成独立小位图再画（BitmapSource.Create 的带偏移重载要 IntPtr，这里不值得为它上 pin）。</summary>
    private static void DrawPixels(DrawingContext context, byte[] pixels, int fullWidth, Int32Rect crop, Point origin)
    {
        var sourceStride = fullWidth * 4;
        var targetStride = crop.Width * 4;
        var buffer = new byte[targetStride * crop.Height];

        for (var row = 0; row < crop.Height; row++)
        {
            Buffer.BlockCopy(
                pixels,
                ((crop.Y + row) * sourceStride) + (crop.X * 4),
                buffer,
                row * targetStride,
                targetStride);
        }

        DrawPixels(context, buffer, crop.Width, crop.Height, origin);
    }

    private static void DrawPixels(DrawingContext context, byte[] pixels, int width, int height, Point origin)
    {
        var source = BitmapSource.Create(width, height, 96, 96, PixelFormats.Pbgra32, null, pixels, width * 4);
        context.DrawImage(source, new Rect(origin, new Size(width, height)));
    }

    private static void Write(SpikeOptions options, IReadOnlyList<(string Step, bool Pass, string Detail)> results, string? previewPath)
    {
        var builder = new StringBuilder();
        builder.AppendLine("# 视觉打磨自动化验收报告（issue #37）");
        builder.AppendLine();
        builder.AppendLine($"- 时间：{DateTime.Now:yyyy-MM-dd HH:mm:ss}");
        builder.AppendLine($"- 结论：**{(results.Count > 0 && results.All(r => r.Pass) ? "PASS" : "FAIL")}**");
        builder.AppendLine();
        builder.AppendLine("| 步骤 | 结果 | 细节 |");
        builder.AppendLine("|---|---|---|");
        foreach (var (step, pass, detail) in results)
        {
            builder.AppendLine($"| {step} | {(pass ? "**PASS**" : "**FAIL**")} | {detail} |");
        }

        builder.AppendLine();
        builder.AppendLine("说明：好看与否必须人眼看——本报告只负责两条机器能判定的底线：");
        builder.AppendLine($"      ① 盒子内部确实画了东西；② 盒子外（含紧邻 {BleedBand}px 外扩带）alpha 必须为 0。");
        builder.AppendLine("      第②条就是「不许用 DropShadowEffect / 外发光」的客观证据：任何外扩像素都会抢走盒子外的点击（P2）。");
        builder.AppendLine(previewPath is null
            ? "      预览图写入失败。"
            : $"      预览图：{previewPath}（深色底 = 桌面，只有盒子是实的）。");

        var directory = options.OutputDirectory ?? Path.Combine(AppContext.BaseDirectory, "artifacts");
        Directory.CreateDirectory(directory);

        File.WriteAllText(
            Path.Combine(directory, "appearance-report.md"),
            builder.ToString(),
            new UTF8Encoding(false));
    }
}