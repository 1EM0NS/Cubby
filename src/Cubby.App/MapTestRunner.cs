using System.IO;
using System.Text;
using Cubby.Core.Model;
using Cubby.Core.Platform;

namespace Cubby.App;

/// <summary>
/// 文件夹映射的自动化验收（issue #15）。
///
/// 三条验收标准分别落到：
/// 1. **实时反映增删**——在临时目录里建/删文件，等监视器把变化推到盒子（带超时的轮询，不睡死等）；
/// 2. **卸载映射后原文件夹内容与位置完全不变**——路径 / 存在性 / 时间戳 / 大小逐项比对；
/// 3. **目标不可用时明确提示**——映射一个不存在的目录必须返回失败并给出原因，而不是静默给个空盒子。
/// </summary>
internal static class MapTestRunner
{
    private static readonly TimeSpan WaitTimeout = TimeSpan.FromSeconds(6);

    public static async Task<int> RunAsync(OverlayWindow overlay, LayoutService layout, OverlayManager manager, SpikeOptions options)
    {
        var results = new List<(string Step, bool Pass, string Detail)>();
        var originals = overlay.Boxes.ToList();
        var box = originals.FirstOrDefault();
        var view = box is null ? null : overlay.ViewOf(box.Id);

        if (box is null || view is null)
        {
            results.Add(("准备", false, "找不到可用的盒子或盒子视图"));
            Write(options, overlay, manager, results);
            return 1;
        }

        var root = Path.Combine(Path.GetTempPath(), "cubby-map-test-" + Guid.NewGuid().ToString("N")[..8]);
        var folder = Path.Combine(root, "被映射的目录");

        try
        {
            Directory.CreateDirectory(folder);
            var alpha = Path.Combine(folder, "甲.txt");
            var beta = Path.Combine(folder, "乙.txt");
            var sub = Path.Combine(folder, "子目录");
            await File.WriteAllTextAsync(alpha, "alpha");
            await File.WriteAllTextAsync(beta, "beta");
            Directory.CreateDirectory(sub);

            // 只对"用户原有的、我们没动过的"条目做指纹比对：
            // 丙.txt 是验收自己加的、乙.txt 是验收自己删的，它们的变化不算数
            var untouchedBefore = new[] { alpha, sub }.ToDictionary(p => p, EntryFingerprint);

            // 1. 建立映射（首次扫描）
            var mapped = view.MapFolder(folder);
            await Task.Delay(200);
            var mappedBox = Current(overlay, box.Id);

            results.Add((
                "映射任意文件夹后盒子内容 = 该文件夹顶层内容",
                mapped && mappedBox.MappedFolder == folder && mappedBox.Items.Count == 3 &&
                mappedBox.Items.All(i => i.Kind == ItemKind.Mapped),
                $"{mappedBox.Items.Count} 条：{string.Join("、", mappedBox.Items.Select(i => i.DisplayName))}"));

            results.Add((
                "目录排在文件前面（子目录 / 甲 / 乙）",
                mappedBox.Items.Count == 3 && mappedBox.Items[0].TargetPath == sub,
                $"首条 = {mappedBox.Items.FirstOrDefault()?.DisplayName}"));

            results.Add((
                "映射诊断说明不会动文件实体",
                view.LastMapDiagnostic?.Contains("不会移动") == true,
                view.LastMapDiagnostic ?? "(无)"));

            // 2. 目录里新增文件 → 盒子跟着变
            var gamma = Path.Combine(folder, "丙.txt");
            await File.WriteAllTextAsync(gamma, "gamma");
            var grew = await WaitForItemsAsync(overlay, box.Id, 4);

            results.Add((
                "目录里新增文件后盒子实时反映（FileSystemWatcher）",
                grew,
                $"等待后条目数 = {Current(overlay, box.Id).Items.Count}（期望 4）"));

            // 3. 目录里删除文件 → 盒子跟着变
            File.Delete(beta);
            var shrank = await WaitForItemsAsync(overlay, box.Id, 3);

            results.Add((
                "目录里删除文件后盒子实时反映",
                shrank,
                $"等待后条目数 = {Current(overlay, box.Id).Items.Count}（期望 3）"));

            var rescanAfterChanges = manager.MappingRescanCount;
            results.Add((
                "变化确实经监视器推给了盒子（重扫 ≥ 2 次、无缓冲区溢出）",
                rescanAfterChanges >= 2 && manager.MappingOverflowCount == 0 && manager.MappingWatcherCount == 1,
                $"监视器 {manager.MappingWatcherCount} 个；重扫 {rescanAfterChanges} 次；溢出重建 {manager.MappingOverflowCount} 次；" +
                string.Join(" | ", manager.MappingDiagnostics)));

            // 空闲 1 秒：不该有任何多余的重扫（事件驱动，没有轮询，这也是 CPU≈0 的前提）
            var rescanBeforeIdle = manager.MappingRescanCount;
            await Task.Delay(1000);
            results.Add((
                "空闲期间不产生多余重扫（事件驱动，无定时扫描）",
                manager.MappingRescanCount == rescanBeforeIdle,
                $"空闲 1 秒内重扫次数 {rescanBeforeIdle} → {manager.MappingRescanCount}"));

            // 4. 磁盘现场：映射 + 监视器工作期间，用户原有的条目一个字节都没动
            var intactDuringMapping = untouchedBefore.All(pair => EntryFingerprint(pair.Key) == pair.Value);
            results.Add((
                "映射期间原文件夹内容与位置不变（P4）",
                intactDuringMapping,
                string.Join(" | ", untouchedBefore.Keys.Select(p => $"{Path.GetFileName(p)} → {EntryFingerprint(p)}"))));

            // 5. 解除映射
            view.UnmapFolder();
            await Task.Delay(200);
            var unmapped = Current(overlay, box.Id);
            var intactAfterUnmap = untouchedBefore.All(pair => EntryFingerprint(pair.Key) == pair.Value);

            results.Add((
                "解除映射后盒子不再引用该目录，且磁盘现场完好",
                unmapped.MappedFolder is null && unmapped.Items.Count == 0 && intactAfterUnmap &&
                manager.MappingWatcherCount == 0 && Directory.Exists(folder),
                $"映射={unmapped.MappedFolder ?? "(无)"}；条目 {unmapped.Items.Count}；监视器 {manager.MappingWatcherCount} 个；" +
                $"原条目指纹一致={intactAfterUnmap}；{view.LastMapDiagnostic ?? "(无诊断)"}"));

            // 6. 目标不可用时必须明确报错
            var missing = Path.Combine(root, "并不存在的目录");
            var ok = view.MapFolder(missing);

            results.Add((
                "映射不存在的目录时明确失败并说明原因",
                !ok && view.LastMapDiagnostic?.Contains("不存在或不可访问") == true && Current(overlay, box.Id).MappedFolder is null,
                view.LastMapDiagnostic ?? "(无诊断)"));

            // 7. 不可用的映射在盒子里要有醒目提示（渲染层用的是同一条扫描函数）
            var warning = FolderMap.Scan(missing);
            results.Add((
                "目标不可用时盒子里会出现醒目提示条目而不是静默空盒子",
                warning.Count == 1 && FolderMap.IsUnavailable(warning[0]),
                warning[0].DisplayName));

            // 8. 监视器被正确释放（不留后台监听）
            results.Add((
                "解除映射后监视器被释放（不留后台监听）",
                manager.MappingWatcherCount == 0,
                $"监视器 {manager.MappingWatcherCount} 个"));

            await Task.CompletedTask;
        }
        catch (Exception ex)
        {
            results.Add(("验收执行", false, $"{ex.GetType().Name}: {ex.Message}"));
        }
        finally
        {
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
        Write(options, overlay, manager, results);

        return failed == 0 ? 0 : 1;
    }

    /// <summary>轮询等待条目数变成期望值。用轮询而不是死等，超时也给出实际数字。</summary>
    private static async Task<bool> WaitForItemsAsync(OverlayWindow overlay, string boxId, int expected)
    {
        var deadline = DateTime.Now + WaitTimeout;

        while (DateTime.Now < deadline)
        {
            if (Current(overlay, boxId).Items.Count == expected)
            {
                return true;
            }

            await Task.Delay(100);
        }

        return Current(overlay, boxId).Items.Count == expected;
    }

    private static Box Current(OverlayWindow overlay, string boxId) =>
        overlay.Boxes.First(b => b.Id == boxId);

    /// <summary>单个条目的现场指纹：路径 + 类型 + 最后写入时间 + 大小。</summary>
    private static string EntryFingerprint(string path)
    {
        if (Directory.Exists(path))
        {
            return $"dir:{path}:{new DirectoryInfo(path).LastWriteTimeUtc:O}";
        }

        if (!File.Exists(path))
        {
            return $"missing:{path}";
        }

        var file = new FileInfo(path);
        return $"file:{path}:{file.LastWriteTimeUtc:O}:{file.Length}";
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

    private static void Write(SpikeOptions options, OverlayWindow overlay, OverlayManager manager, IReadOnlyList<(string Step, bool Pass, string Detail)> results)
    {
        var builder = new StringBuilder();
        builder.AppendLine("# 文件夹映射自动化验收报告（issue #15）");
        builder.AppendLine();
        builder.AppendLine($"- 时间：{DateTime.Now:yyyy-MM-dd HH:mm:ss}");
        builder.AppendLine($"- 显示器：{overlay.Surface?.Id}");
        builder.AppendLine($"- 结论：**{(results.Count > 0 && results.All(r => r.Pass) ? "PASS" : "FAIL")}**");
        builder.AppendLine();
        builder.AppendLine("| 步骤 | 结果 | 细节 |");
        builder.AppendLine("|---|---|---|");

        foreach (var (step, pass, detail) in results)
        {
            builder.AppendLine($"| {step} | {(pass ? "**PASS**" : "**FAIL**")} | {detail} |");
        }

        builder.AppendLine();
        builder.AppendLine("## 映射监视器状态");
        builder.AppendLine();
        if (manager.MappingDiagnostics.Count == 0)
        {
            builder.AppendLine("（验收结束时已解除映射，监视器已全部释放）");
        }
        else
        {
            foreach (var line in manager.MappingDiagnostics)
            {
                builder.AppendLine($"- {line}");
            }
        }

        builder.AppendLine();
        builder.AppendLine("说明：验收在临时目录里真的建过 / 删过文件，用来验证监视器；结束后临时目录整体删除。");
        builder.AppendLine("      映射全程只读目录，磁盘上的用户文件一个字节都没有被改动（P4）。");

        var directory = options.OutputDirectory ?? Path.Combine(AppContext.BaseDirectory, "artifacts");
        Directory.CreateDirectory(directory);

        File.WriteAllText(
            Path.Combine(directory, "map-report.md"),
            builder.ToString(),
            new UTF8Encoding(false));
    }
}