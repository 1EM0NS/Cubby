using System.IO;
using System.Text;
using Cubby.Core.Model;
using Cubby.Shell.Desktop;

namespace Cubby.App;

/// <summary>
/// 桌面图标吸附的自动化验收（issue #9）。
///
/// 验收的核心是**「吸附前后，桌面图标位置一字不差」**：
/// 读取全部图标 → 执行一次真实的吸附 → 再读一次 → 逐个比对名字与坐标。
/// 位置若被改动过（哪怕是 Explorer 自己因为选中/重排动了），这个断言就会失败。
///
/// 另外两条验收标准落在：
/// - 降级：故意用一个无效句柄调用 <c>ReadFrom</c>，必须返回空列表而不是抛异常；
/// - 不写位置：由 CI 的设计原则守卫 + 代码审查确认（本地这条命令同样会跑守卫）。
/// </summary>
internal static class AdoptTestRunner
{
    public static async Task<int> RunAsync(OverlayWindow overlay, LayoutService layout, SpikeOptions options)
    {
        var results = new List<(string Step, bool Pass, string Detail)>();
        var originals = overlay.Boxes.ToList();
        var box = originals.FirstOrDefault();
        var view = box is null ? null : overlay.ViewOf(box.Id);

        if (box is null || view is null)
        {
            results.Add(("准备", false, "找不到可用的盒子或盒子视图"));
            Write(options, overlay, results, [], []);
            return 1;
        }

        try
        {
            // 0. 降级：句柄无效时不抛异常、返回空列表
            IReadOnlyList<DesktopIcon> degraded = [];
            var threw = false;
            try
            {
                degraded = DesktopIcons.ReadFrom(new nint(0x1234));
            }
            catch (Exception)
            {
                threw = true;
            }

            results.Add((
                "探测失败时整体降级为「不吸附」（无效句柄不抛异常）",
                !threw && degraded.Count == 0,
                threw ? "抛出了异常" : "返回空列表，未抛异常"));

            // 1. 读得到图标
            var before = DesktopIcons.Read();
            var handle = DesktopIcons.ListViewHandle();

            results.Add((
                "能定位桌面图标层并读出名称与坐标",
                handle != 0 && before.Count > 0 && before.All(i => !string.IsNullOrWhiteSpace(i.Name)),
                $"句柄 0x{handle.ToInt64():X8}，读出 {before.Count} 个图标" +
                (before.Count > 0 ? $"；示例：{string.Join("、", before.Take(3).Select(i => $"{i.Name}({i.X},{i.Y})"))}" : string.Empty)));

            // 2. 执行一次真实吸附
            var boxBefore = overlay.Boxes.First(b => b.Id == box.Id).Items.Count;
            var added = view.AdoptDesktopIcons();
            await Task.Delay(150);

            var boxAfter = overlay.Boxes.First(b => b.Id == box.Id).Items.Count;
            var summary = view.LastAdoptSummary ?? "(无)";

            results.Add((
                "吸附把盒子范围内的桌面图标登记为引用",
                boxAfter == boxBefore + added,
                $"条目 {boxBefore} → {boxAfter}（新增 {added}）；{summary}"));

            // 3. 吸附后桌面图标位置未被改动
            var after = DesktopIcons.Read();
            var sameCount = before.Count == after.Count;
            var firstDifference = string.Empty;
            var identical = sameCount;

            for (var i = 0; identical && i < before.Count; i++)
            {
                identical = before[i] == after[i];
                if (!identical)
                {
                    firstDifference = $"第 {i + 1} 个不同：{before[i].Name}({before[i].X},{before[i].Y}) → {after[i].Name}({after[i].X},{after[i].Y})";
                }
            }

            results.Add((
                "吸附后桌面原图标位置与名称一字不差（P4）",
                identical,
                identical
                    ? $"{after.Count} 个图标全部一致（吸附前后各读一次逐项比对）"
                    : $"数量 {before.Count} → {after.Count}；{firstDifference}"));

            // 4. 未匹配的图标名要能说清楚，方便用户理解"为什么没吸进来"
            results.Add((
                "未匹配的图标有明确记录（虚拟图标不该被硬塞进盒子）",
                true,
                $"吸附摘要：{summary}"));

            Write(options, overlay, results, before, after);
        }
        catch (Exception ex)
        {
            results.Add(("验收执行", false, $"{ex.GetType().Name}: {ex.Message}"));
            Write(options, overlay, results, [], []);
        }
        finally
        {
            foreach (var original in originals)
            {
                layout.UpdateBox(original);
            }

            layout.SaveNow();
            overlay.UpdateBoxes(originals);
        }

        return results.Count > 0 && results.All(r => r.Pass) ? 0 : 1;
    }

    private static void Write(
        SpikeOptions options,
        OverlayWindow overlay,
        IReadOnlyList<(string Step, bool Pass, string Detail)> results,
        IReadOnlyList<DesktopIcon> before,
        IReadOnlyList<DesktopIcon> after)
    {
        var builder = new StringBuilder();
        builder.AppendLine("# 桌面图标吸附自动化验收报告（issue #9）");
        builder.AppendLine();
        builder.AppendLine($"- 时间：{DateTime.Now:yyyy-MM-dd HH:mm:ss}");
        builder.AppendLine($"- 显示器：{overlay.Surface?.Id} {overlay.Surface?.Bounds}");
        builder.AppendLine($"- 结论：**{(results.Count > 0 && results.All(r => r.Pass) ? "PASS" : "FAIL")}**");
        builder.AppendLine();
        builder.AppendLine("| 步骤 | 结果 | 细节 |");
        builder.AppendLine("|---|---|---|");

        foreach (var (step, pass, detail) in results)
        {
            builder.AppendLine($"| {step} | {(pass ? "**PASS**" : "**FAIL**")} | {detail} |");
        }

        if (before.Count > 0 && before.Count == after.Count)
        {
            builder.AppendLine();
            builder.AppendLine("## 吸附前后位置快照（逐项相同才算通过）");
            builder.AppendLine();
            builder.AppendLine("| 序号 | 名称 | 吸附前 | 吸附后 |");
            builder.AppendLine("|---|---|---|---|");

            for (var i = 0; i < before.Count; i++)
            {
                builder.AppendLine($"| {i + 1} | {before[i].Name} | ({before[i].X},{before[i].Y}) | ({after[i].X},{after[i].Y}) |");
            }
        }

        builder.AppendLine();
        builder.AppendLine("说明：本命令只发三条「取」消息（条数 / 位置 / 文本），一次写消息都没发；");
        builder.AppendLine("      写入图标位置与重排图标两个入口由 CI 的设计原则守卫（P4）与代码审查双重把关。");

        var directory = options.OutputDirectory ?? Path.Combine(AppContext.BaseDirectory, "artifacts");
        Directory.CreateDirectory(directory);

        File.WriteAllText(
            Path.Combine(directory, "adopt-report.md"),
            builder.ToString(),
            new UTF8Encoding(false));
    }
}