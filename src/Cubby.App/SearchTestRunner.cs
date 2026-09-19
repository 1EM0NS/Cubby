using System.Diagnostics;
using System.IO;
using System.Text;
using Cubby.Core.Model;

namespace Cubby.App;

/// <summary>
/// 盒子内即时搜索的自动化验收（issue #13）。
///
/// 三条验收标准分别落到：
/// 1. **输入即时过滤 &lt; 100ms（500 条规模）**——真的塞 500 条进盒子，反复查询取最慢的一次；
/// 2. **FileSystemWatcher 缓冲区溢出后自动重建索引且不崩溃**——往真实映射目录里狂写几千个文件
///    （默认 8KB 缓冲必然溢出，我们调到 64KB 但仍可能溢出），断言事后**状态自洽**（索引与目录一致）
///    且进程没有崩；溢出次数如实记录，不硬凑；
/// 3. **空闲 CPU≈0**——没有任何定时器，空闲 1 秒内索引重建次数不增长。
/// </summary>
internal static class SearchTestRunner
{
    private const int SyntheticCount = 500;
    private const int FloodCount = 2000;

    public static async Task<int> RunAsync(OverlayWindow overlay, LayoutService layout, OverlayManager manager, SpikeOptions options)
    {
        var results = new List<(string Step, bool Pass, string Detail)>();
        var originals = overlay.Boxes.ToList();
        var box = originals.FirstOrDefault();
        var view = box is null ? null : overlay.ViewOf(box.Id);

        if (box is null || view is null)
        {
            results.Add(("准备", false, "找不到可用的盒子或盒子视图"));
            Write(options, manager, results);
            return 1;
        }

        var root = Path.Combine(Path.GetTempPath(), "cubby-search-test-" + Guid.NewGuid().ToString("N")[..8]);

        try
        {
            // ---- 1. 500 条规模的即时过滤 ----
            var synthetic = Enumerable.Range(0, SyntheticCount)
                .Select(i => new BoxItem(
                    $"syn-{i}",
                    i % 5 == 0 ? $"report-{i:000}.txt" : $"note-{i:000}.txt",
                    $@"C:\合成\{i:000}\{(i % 5 == 0 ? $"report-{i:000}.txt" : $"note-{i:000}.txt")}",
                    ItemKind.File))
                .ToList();

            manager.ApplyBoxUpdate(box with { Items = synthetic });
            await Task.Delay(300);

            results.Add((
                "把 500 条塞进盒子并建立索引",
                manager.Search.CountOf(box.Id) == SyntheticCount,
                $"索引 {manager.Search.CountOf(box.Id)} 条"));

            manager.OnSearchRequested(box);
            await Task.Delay(200);

            var window = manager.SearchWindowOf(box.Id);
            if (window is null)
            {
                results.Add(("打开搜索窗口", false, "搜索窗口没有创建"));
            }
            else
            {
                // 20 次查询取最慢的一次：单次快不算快，稳定快才算
                var slowest = 0.0;
                for (var i = 0; i < 20; i++)
                {
                    var watch = Stopwatch.StartNew();
                    var hits = manager.Search.Query(box.Id, "report", limit: 500);
                    watch.Stop();

                    slowest = Math.Max(slowest, watch.Elapsed.TotalMilliseconds);

                    if (i == 0 && hits.Count != SyntheticCount / 5)
                    {
                        results.Add(("过滤结果正确", false, $"report 命中 {hits.Count} 条，期望 {SyntheticCount / 5} 条"));
                    }
                }

                results.Add((
                    "500 条规模下查询耗时 < 100ms",
                    slowest < 100,
                    $"20 次查询里最慢一次 {slowest:0.00} ms（服务记录 {manager.Search.LastQueryMilliseconds:0.00} ms）"));

                // 走窗口自己的输入路径（与用户打字一致）
                window.SetQuery("report-1");
                await Task.Delay(120);
                var firstHits = window.Results;

                results.Add((
                    "输入即时过滤（走窗口 TextChanged 路径）",
                    firstHits.Count > 0 && firstHits.All(i => i.DisplayName.Contains("report-1")) &&
                    firstHits[0].DisplayName.StartsWith("report-1", StringComparison.Ordinal),
                    $"「report-1」命中 {firstHits.Count} 条，首条 {firstHits.FirstOrDefault()?.DisplayName}；{window.Status}"));

                window.SetQuery("note-31");
                await Task.Delay(120);
                var secondHits = window.Results;

                results.Add((
                    "改一个词结果立刻跟着变",
                    secondHits.Count > 0 && secondHits.All(i => i.DisplayName.Contains("note-31")),
                    $"「note-31」命中 {secondHits.Count} 条：{string.Join("、", secondHits.Take(3).Select(i => i.DisplayName))}"));

                window.SetQuery("完全不存在的词");
                await Task.Delay(120);
                results.Add((
                    "无匹配时明确显示 0 条而不是空白",
                    window.Results.Count == 0 && window.Status.Contains("没有匹配项"),
                    window.Status));

                window.SetQuery(string.Empty);
                await Task.Delay(120);
                results.Add((
                    "清空输入恢复全部条目",
                    window.Results.Count == SyntheticCount,
                    $"恢复 {window.Results.Count} 条"));

                // ---- 2. 缓冲区溢出：往真实映射目录里狂写文件 ----
                window.Close();
                var folder = Path.Combine(root, "被洪水淹没的目录");
                await FloodAsync(folder, FloodCount);

                var mapOk = view.MapFolder(folder);
                await Task.Delay(300);
                var indexBeforeFlood = manager.Search.CountOf(box.Id);

                // 已经映射之后再倒一批：这批变化必须经由监视器 → 重扫 → 重建索引
                await FloodAsync(folder, FloodCount, offset: FloodCount, delay: TimeSpan.Zero);
                await Task.Delay(1500);

                var expected = Math.Min(FloodCount * 2, 500); // FolderMap 默认上限 500
                var itemCount = overlay.Boxes.First(b => b.Id == box.Id).Items.Count;
                var indexAfter = manager.Search.CountOf(box.Id);
                var buildsAfterFlood = manager.Search.TotalBuildCount;

                results.Add((
                    "洪水式写入后索引与目录内容自洽（500 条上限内一致、进程未崩溃）",
                    mapOk && itemCount == expected && indexAfter == itemCount,
                    $"目录里 {FloodCount * 2} 个文件（映射上限 500）；期望 {expected} 条，实际条目 {itemCount} / 索引 {indexAfter}；" +
                    $"洪水期间重扫 {manager.MappingRescanCount} 次（真实溢出 {manager.MappingOverflowCount} 次）；" +
                    $"洪水前索引 {indexBeforeFlood} 条"));

                // 真实溢出没法稳定复现，因此**直接驱动溢出后走的那条路径**。
                // 模拟"事件已经丢了"：先清空目录，再改一次目录并立刻强制整体重扫——
                // 重扫必须把这次改动补回来（而不是依赖那批可能已经丢掉的事件）。
                foreach (var file in Directory.GetFiles(folder))
                {
                    File.Delete(file);
                }

                await Task.Delay(900);

                var buildsBeforeForced = manager.Search.TotalBuildCount;
                var lateFile = Path.Combine(folder, "晚到的文件.txt");
                await File.WriteAllTextAsync(lateFile, "late");

                var forced = manager.RequestMappingRescan(box.Id, "缓冲区溢出（验收直接驱动该路径）");
                await Task.Delay(1000);

                var stateAfterForced = overlay.Boxes.First(b => b.Id == box.Id);
                var indexAfterForced = manager.Search.CountOf(box.Id);
                var recovered = stateAfterForced.Items.Any(i => i.DisplayName == "晚到的文件.txt");

                results.Add((
                    "缓冲区溢出后的强制重扫能补回丢失的事件，且不崩溃",
                    forced && recovered && indexAfterForced == stateAfterForced.Items.Count &&
                    manager.Search.TotalBuildCount > buildsBeforeForced,
                    $"强制重扫成功={forced}；补回「晚到的文件.txt」={recovered}；" +
                    $"条目 {stateAfterForced.Items.Count} / 索引 {indexAfterForced}；" +
                    $"索引重建 {buildsBeforeForced} → {manager.Search.TotalBuildCount} 次"));

                var buildsAfter = manager.Search.TotalBuildCount;
                results.Add((
                    "索引重建确实发生过（诊断计数增长）",
                    buildsAfter > 0,
                    $"累计重建 {buildsAfter} 次"));

                // ---- 3. 空闲不轮询 ----
                await Task.Delay(1000);
                results.Add((
                    "空闲 1 秒内索引不重建（事件驱动，无定时扫描）",
                    manager.Search.TotalBuildCount == buildsAfter,
                    $"重建次数 {buildsAfter} → {manager.Search.TotalBuildCount}"));

                view.UnmapFolder();
                await Task.Delay(200);
            }
        }
        catch (Exception ex)
        {
            results.Add(("验收执行", false, $"{ex.GetType().Name}: {ex.Message}"));
        }
        finally
        {
            manager.SearchWindowOf(box.Id)?.Close();
            view?.UnmapFolder();

            foreach (var original in originals)
            {
                layout.UpdateBox(original);
            }

            layout.SaveNow();
            overlay.UpdateBoxes(originals);
            manager.ApplyBoxUpdate(originals.First(b => b.Id == box.Id));

            TryDelete(root);
        }

        var failed = results.Count(r => !r.Pass);
        Write(options, manager, results);

        return failed == 0 && results.Count > 0 ? 0 : 1;
    }

    /// <summary>往目录里快速写入一批小文件，制造足够的文件系统事件。</summary>
    private static async Task FloodAsync(string folder, int count, int offset = 0, TimeSpan? delay = null)
    {
        Directory.CreateDirectory(folder);

        for (var i = 0; i < count; i++)
        {
            var path = Path.Combine(folder, $"flood-{offset + i:0000}.txt");
            await File.WriteAllTextAsync(path, "x");

            if (delay is { } wait && i % 50 == 0)
            {
                await Task.Delay(wait);
            }
        }
    }

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
            // 删不掉不影响结论
        }
    }

    private static void Write(SpikeOptions options, OverlayManager manager, IReadOnlyList<(string Step, bool Pass, string Detail)> results)
    {
        var builder = new StringBuilder();
        builder.AppendLine("# 盒子内即时搜索自动化验收报告（issue #13）");
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
        builder.AppendLine($"索引状态：{manager.SearchDescription}");
        builder.AppendLine();
        builder.AppendLine("说明：索引只在条目变化时重建（映射目录的变化、拖入拖出都算），没有任何定时器；");
        builder.AppendLine("      「缓冲区溢出」用往真实映射目录狂写文件的方式尝试触发，溢出次数如实记录——");
        builder.AppendLine("      真正要保证的是**溢出后状态自洽且不崩溃**，而不是非要凑出一次溢出。");

        var directory = options.OutputDirectory ?? Path.Combine(AppContext.BaseDirectory, "artifacts");
        Directory.CreateDirectory(directory);

        File.WriteAllText(
            Path.Combine(directory, "search-report.md"),
            builder.ToString(),
            new UTF8Encoding(false));
    }
}