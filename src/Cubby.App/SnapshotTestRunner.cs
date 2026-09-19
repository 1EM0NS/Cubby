using System.IO;
using System.Text;
using Cubby.Core.Model;

namespace Cubby.App;

/// <summary>
/// 布局快照的自动化验收（issue #16）。走的是快照窗口里那条真实路径：
/// 建快照 → 打乱布局 → 还原 → 逐项比对盒子位置与条目归属。
///
/// 验收会在**真实快照目录**里产生几份快照（因为要验证"列得出来"），结束时只删掉本次自己建的，
/// 用户原有的快照一份不动。
/// </summary>
internal static class SnapshotTestRunner
{
    public static async Task<int> RunAsync(OverlayManager manager, LayoutService layout, SpikeOptions options)
    {
        var results = new List<(string Step, bool Pass, string Detail)>();
        var created = new List<string>();

        // 快照时要带上真实布局，否则"盒子一个不少"这条断言没有意义
        var keepBefore = layout.SnapshotKeep;
        layout.SnapshotKeep = 0; // 验收期间不许按保留策略删任何东西（目录里可能有用户的快照）

        var window = new SnapshotsWindow(layout, manager);
        var before = Fingerprint(layout);
        var beforeBoxes = layout.Boxes.Count;
        var beforeItems = layout.Boxes.Sum(b => b.Items.Count);

        try
        {
            // 1. 手动建快照
            var snapshot = layout.CreateSnapshot("验收");
            created.Add(snapshot.FilePath);

            results.Add((
                "手动创建快照并写入快照目录",
                File.Exists(snapshot.FilePath) && snapshot.BoxCount == beforeBoxes && snapshot.ItemCount == beforeItems,
                $"{Path.GetFileName(snapshot.FilePath)}：盒子 {snapshot.BoxCount} 个 / 条目 {snapshot.ItemCount} 条"));

            // 2. 列表里按时间列得出来（走窗口自己的 Refresh，与用户点「新建快照」后看到的一致）
            window.Refresh();
            var listed = window.Snapshots;
            results.Add((
                "快照在窗口列表里按时间列出（且就是绑定到界面的那份数据）",
                listed.Any(s => s.FilePath == snapshot.FilePath) && window.BoundCount == listed.Count && listed.Count >= 1,
                $"列表 {listed.Count} 份（界面绑定 {window.BoundCount} 份）；最新：{listed.FirstOrDefault()?.Describe()}"));

            // 3. 打乱布局：移动盒子 + 加条目 + 改名
            var target = layout.Boxes.FirstOrDefault();
            if (target is not null)
            {
                layout.UpdateBox(target with
                {
                    Name = "被改乱的盒子",
                    Bounds = target.Bounds with { X = target.Bounds.X + 137, Y = target.Bounds.Y + 61 },
                    Items = [.. target.Items, new BoxItem("snap-test-item", "临时条目.txt", @"C:\temp\临时条目.txt", ItemKind.File)],
                });

                layout.SaveNow();
                await Task.Delay(100);
            }

            var scrambled = Fingerprint(layout);
            results.Add((
                "打乱后的布局确实与原布局不同（否则还原断言不成立）",
                scrambled != before,
                $"盒子名/位置/条目数：{beforeBoxes} → {layout.Boxes.Count}；指纹不同={scrambled != before}"));

            // 4. 还原
            var document = layout.Snapshots.TryLoad(snapshot.FilePath, out var diagnostic);
            if (document is null)
            {
                results.Add(("读取快照", false, diagnostic ?? "未知原因"));
            }
            else
            {
                window.Restore(document);
                await Task.Delay(200);
                created.AddRange(layout.Snapshots.List()
                    .Where(s => !created.Contains(s.FilePath) && s.Label == "还原前")
                    .Select(s => s.FilePath));

                results.Add((
                    "还原后盒子位置与条目归属与原布局完全一致",
                    Fingerprint(layout) == before,
                    Fingerprint(layout) == before
                        ? $"指纹一致；盒子 {layout.Boxes.Count} 个 / 条目 {layout.Boxes.Sum(b => b.Items.Count)} 条"
                        : $"不一致：\n原 {before}\n现 {Fingerprint(layout)}"));

                results.Add((
                    "还原前自动留了一份「还原前」快照（还原本身可回退）",
                    layout.Snapshots.List().Any(s => s.Label == "还原前"),
                    $"快照目录现有 {layout.Snapshots.List().Count} 份"));
            }

            // 5. 损坏的快照要能明确报错，而不是静默失败
            var broken = Path.Combine(layout.Snapshots.Directory, "layout-19990101-000000-000-broken.json");
            Directory.CreateDirectory(layout.Snapshots.Directory);
            await File.WriteAllTextAsync(broken, "{ 这不是合法 JSON");
            created.Add(broken);

            var loaded = layout.Snapshots.TryLoad(broken, out var brokenDiagnostic);
            results.Add((
                "损坏的快照给出明确诊断而不是静默失败",
                loaded is null && !string.IsNullOrWhiteSpace(brokenDiagnostic),
                brokenDiagnostic ?? "(没有诊断信息)"));

            var missing = layout.Snapshots.TryLoad(Path.Combine(layout.Snapshots.Directory, "不存在.json"), out var missingDiagnostic);
            results.Add((
                "快照文件不存在时也有说明",
                missing is null && missingDiagnostic == "快照文件不存在",
                missingDiagnostic ?? "(没有诊断信息)"));
        }
        catch (Exception ex)
        {
            results.Add(("验收执行", false, $"{ex.GetType().Name}: {ex.Message}"));
        }
        finally
        {
            layout.SnapshotKeep = keepBefore;
            layout.SaveNow();
            window.Close();

            // 只清理本次验收自己建的快照，绝不碰用户原有的
            foreach (var path in created.Distinct())
            {
                try
                {
                    if (File.Exists(path))
                    {
                        File.Delete(path);
                    }
                }
                catch (IOException)
                {
                    // 删不掉就留着，不影响结论
                }
            }
        }

        var failed = results.Count(r => !r.Pass);
        Write(options, layout, results);

        return failed == 0 && results.Count > 0 ? 0 : 1;
    }

    /// <summary>布局指纹：盒子标识 / 名称 / 位置 / 折叠锁定状态 / 每个条目的目标路径。</summary>
    private static string Fingerprint(LayoutService layout) =>
        string.Join(
            "|",
            layout.Boxes
                .OrderBy(b => b.Id, StringComparer.Ordinal)
                .Select(b =>
                    $"{b.Id}~{b.Name}~{b.Bounds}~{b.IsLocked}~{b.IsCollapsed}~" +
                    string.Join(",", b.Items.Select(i => i.TargetPath))));

    private static void Write(SpikeOptions options, LayoutService layout, IReadOnlyList<(string Step, bool Pass, string Detail)> results)
    {
        var builder = new StringBuilder();
        builder.AppendLine("# 布局快照自动化验收报告（issue #16）");
        builder.AppendLine();
        builder.AppendLine($"- 时间：{DateTime.Now:yyyy-MM-dd HH:mm:ss}");
        builder.AppendLine($"- 快照目录：{layout.Snapshots.Directory}");
        builder.AppendLine($"- 布局文件：{layout.FilePath}");
        builder.AppendLine($"- 结论：**{(results.Count > 0 && results.All(r => r.Pass) ? "PASS" : "FAIL")}**");
        builder.AppendLine();
        builder.AppendLine("| 步骤 | 结果 | 细节 |");
        builder.AppendLine("|---|---|---|");

        foreach (var (step, pass, detail) in results)
        {
            builder.AppendLine($"| {step} | {(pass ? "**PASS**" : "**FAIL**")} | {detail} |");
        }

        builder.AppendLine();
        builder.AppendLine("说明：验收在真实快照目录里建过几份快照（为了验证列表），结束后只删本次自己建的；");
        builder.AppendLine("      还原走的是快照窗口的 Restore 路径（含「还原前」自动快照），不是另写一份逻辑；");
        builder.AppendLine("      保留策略与版本校验在单元测试里用独立临时目录验证，不在这里删用户的快照。");

        var directory = options.OutputDirectory ?? Path.Combine(AppContext.BaseDirectory, "artifacts");
        Directory.CreateDirectory(directory);

        File.WriteAllText(
            Path.Combine(directory, "snapshot-report.md"),
            builder.ToString(),
            new UTF8Encoding(false));
    }
}