using System.IO;
using System.Text;
using System.Windows;
using Cubby.Core.Model;
using Cubby.Shell.Diagnostics;

namespace Cubby.App;

/// <summary>
/// 拖入的自动化验收（issue #7）。
///
/// 系统级拖放（OLE DoDragDrop）没法用 SendInput 复现，所以这里走的是"同一条代码路径"：
/// 用一个真实的 <see cref="DataObject"/>（CF_HDROP 等价物）喂给盒子视图的拖放入口，
/// 再断言模型变化与磁盘现场。
///
/// 三条断言对应三条验收标准：
/// 1. 文件与文件夹都能入盒（含 .url 归类）；
/// 2. **原文件位置不变**——路径、存在性、时间戳、大小逐一比对；
/// 3. 盒子之外的点不会被我们截获，拖拽仍归其他窗口。
/// </summary>
internal static class DropTestRunner
{
    public static async Task<int> RunAsync(OverlayWindow overlay, LayoutService layout, SpikeOptions options)
    {
        var results = new List<(string Step, bool Pass, string Detail)>();
        var originals = overlay.Boxes.ToList();
        var box = originals.FirstOrDefault();

        if (box is null)
        {
            results.Add(("准备", false, "该显示器上没有盒子"));
            Write(options, overlay, results);
            return 1;
        }

        var view = overlay.ViewOf(box.Id);
        if (view is null)
        {
            results.Add(("准备", false, "找不到盒子视图"));
            Write(options, overlay, results);
            return 1;
        }

        var root = Path.Combine(Path.GetTempPath(), "cubby-drop-test-" + Guid.NewGuid().ToString("N")[..8]);
        var file = Path.Combine(root, "报告.txt");
        var folder = Path.Combine(root, "资料夹");
        var url = Path.Combine(root, "主页.url");

        try
        {
            Directory.CreateDirectory(folder);
            await File.WriteAllTextAsync(file, "cubby drop test");
            await File.WriteAllTextAsync(Path.Combine(folder, "内部文件.txt"), "nested");
            await File.WriteAllTextAsync(url, "[InternetShortcut]\r\nURL=https://example.com\r\n");

            var paths = new[] { file, folder, url };
            var before = Snapshot(paths);

            // 1. 拖入：文件 + 文件夹 + .url
            var added = view.ImportDrop(new DataObject(DataFormats.FileDrop, paths));
            await Task.Delay(120);
            var afterImport = overlay.Boxes.First(b => b.Id == box.Id);

            results.Add((
                "拖入文件 / 文件夹 / .url 生成引用",
                added.Count == 3 && afterImport.Items.Count == box.Items.Count + 3,
                $"新增 {added.Count} 条：{string.Join("、", added.Select(a => $"{a.DisplayName}[{a.Kind}]"))}"));

            results.Add((
                "条目类型与显示名正确",
                added.Any(a => a.Kind == ItemKind.Folder && a.DisplayName == "资料夹") &&
                added.Any(a => a.Kind == ItemKind.File && a.DisplayName == "报告.txt") &&
                added.Any(a => a.Kind == ItemKind.Url && a.DisplayName == "主页.url"),
                string.Join("、", added.Select(a => $"{a.DisplayName}={a.Kind}"))));

            // 2. 同一路径再拖一次：不重复入盒
            var again = view.ImportDrop(new DataObject(DataFormats.FileDrop, paths));
            results.Add((
                "重复拖入同一路径不产生重复条目",
                again.Count == 0,
                $"第二次新增 {again.Count} 条"));

            // 3. 原文件位置不变（路径 / 存在性 / 时间戳 / 大小）
            var after = Snapshot(paths);
            var unchanged = before.Count == after.Count;
            var diff = new StringBuilder();
            for (var i = 0; unchanged && i < before.Count; i++)
            {
                if (before[i] != after[i])
                {
                    unchanged = false;
                    diff.Append($"第 {i + 1} 项不同：{before[i]} → {after[i]}");
                }
            }

            results.Add((
                "拖入后原文件位置与时间戳不变（P4）",
                unchanged,
                unchanged
                    ? $"3 项路径 / 存在性 / 时间戳 / 大小全部一致：{string.Join(" | ", after)}"
                    : diff.ToString()));
        }
        catch (Exception ex)
        {
            results.Add(("拖入验收执行", false, $"{ex.GetType().Name}: {ex.Message}"));
        }
        finally
        {
            // 还原现场：模型 → 持久化 → 删除本次自建的临时文件
            foreach (var original in originals)
            {
                layout.UpdateBox(original);
            }

            layout.SaveNow();
            overlay.UpdateBoxes(originals);

            TryDelete(root);
        }

        // 4. 盒子之外的透明区域不会被我们截获（拖拽仍归其他窗口）
        var outside = OutsideProbes(overlay);
        results.Add((
            "盒子外的点不归浮层（拖拽不会被我们抢走）",
            outside.All(p => !p.Ours),
            string.Join("；", outside.Select(p => $"({p.X},{p.Y}) → {(p.Ours ? "浮层" : p.ClassName)}"))));

        var failed = results.Count(r => !r.Pass);
        Write(options, overlay, results);

        return failed == 0 ? 0 : 1;
    }

    private static IReadOnlyList<PointProbe> OutsideProbes(OverlayWindow overlay)
    {
        var surface = overlay.Surface!;
        var hwnd = overlay.Host?.Handle ?? 0;
        var probes = new List<PointProbe>();

        void Add(double fx, double fy)
        {
            var x = surface.Bounds.Left + (int)(surface.Bounds.Width * fx);
            var y = surface.Bounds.Top + (int)(surface.Bounds.Height * fy);
            var result = DesktopProbe.WindowAt(x, y, 0);
            probes.Add(new PointProbe(x, y, result.ClassName, DesktopProbe.BelongsTo(result.Handle, hwnd)));
        }

        Add(0.72, 0.30);
        Add(0.80, 0.60);
        Add(0.45, 0.85);

        return probes;
    }

    /// <summary>每个路径的现场指纹：路径、是否存在、最后写入时间、大小。</summary>
    private static List<string> Snapshot(IEnumerable<string> paths) =>
        paths.Select(path =>
        {
            if (Directory.Exists(path))
            {
                var info = new DirectoryInfo(path);
                return $"{info.FullName}|dir|{info.Exists}|{info.LastWriteTimeUtc:O}";
            }

            var file = new FileInfo(path);
            return $"{file.FullName}|file|{file.Exists}|{file.LastWriteTimeUtc:O}|{file.Length}";
        }).ToList();

    private static void TryDelete(string directory)
    {
        try
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
        catch (IOException)
        {
            // 临时目录删不掉不影响验收结论
        }
    }

    private static void Write(SpikeOptions options, OverlayWindow overlay, IReadOnlyList<(string Step, bool Pass, string Detail)> results)
    {
        var builder = new StringBuilder();
        builder.AppendLine("# 拖入自动化验收报告（issue #7）");
        builder.AppendLine();
        builder.AppendLine($"- 时间：{DateTime.Now:yyyy-MM-dd HH:mm:ss}");
        builder.AppendLine($"- 显示器：{overlay.Surface?.Id} {overlay.Surface?.Bounds} DPI 缩放 {overlay.Surface?.DpiScale:0.##}");
        builder.AppendLine($"- 结论：**{(results.Count > 0 && results.All(r => r.Pass) ? "PASS" : "FAIL")}**");
        builder.AppendLine();
        builder.AppendLine("| 步骤 | 结果 | 细节 |");
        builder.AppendLine("|---|---|---|");

        foreach (var (step, pass, detail) in results)
        {
            builder.AppendLine($"| {step} | {(pass ? "**PASS**" : "**FAIL**")} | {detail} |");
        }

        builder.AppendLine();
        builder.AppendLine("说明：验收用真实临时文件（验收结束后删除），只走「登记引用」这条代码路径；");
        builder.AppendLine("      \"原文件位置不变\"由路径 / 存在性 / 时间戳 / 大小四项逐一比对得出。");

        var directory = options.OutputDirectory ?? Path.Combine(AppContext.BaseDirectory, "artifacts");
        Directory.CreateDirectory(directory);

        File.WriteAllText(
            Path.Combine(directory, "drop-report.md"),
            builder.ToString(),
            new UTF8Encoding(false));
    }

    private readonly record struct PointProbe(int X, int Y, string ClassName, bool Ours);
}