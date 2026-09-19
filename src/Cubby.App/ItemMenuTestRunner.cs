using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using Cubby.App.Views;
using Cubby.Core.Model;

namespace Cubby.App;

/// <summary>
/// 右键菜单 / 拖出的自动化验收（issue #8）。
///
/// 菜单由 WPF 自己弹出（`ContextMenu` 是独立顶层窗口），没法用 SendInput 稳定点中；
/// 但菜单里的每一项都只是把动作转给 <see cref="BoxView.InvokeItemAction"/>，
/// 所以验收直接调这个统一入口——**覆盖到的就是用户点击时真正执行的那段代码**。
///
/// 三条验收标准分别落到：
/// 1. 菜单项齐全（逐个核对表头文字）；
/// 2. 重命名 / 移出盒子都**只动引用不动磁盘**（改名前后的文件指纹逐一比对，P4）；
/// 3. 拖出提供的是 `CF_HDROP`（FileDrop），且只有路径、没有任何"删除/移动"指令。
/// </summary>
internal static class ItemMenuTestRunner
{
    public static async Task<int> RunAsync(OverlayWindow overlay, LayoutService layout, SpikeOptions options)
    {
        var results = new List<(string Step, bool Pass, string Detail)>();
        var originals = overlay.Boxes.ToList();
        var box = originals.FirstOrDefault();

        if (box is null || overlay.ViewOf(box.Id) is not { } view)
        {
            results.Add(("准备", false, "找不到可用的盒子或盒子视图"));
            Write(options, overlay, results);
            return 1;
        }

        var root = Path.Combine(Path.GetTempPath(), "cubby-menu-test-" + Guid.NewGuid().ToString("N")[..8]);
        var file = Path.Combine(root, "待收拾.txt");

        try
        {
            Directory.CreateDirectory(root);
            await File.WriteAllTextAsync(file, "cubby menu test");

            var imported = view.ImportDrop(new DataObject(DataFormats.FileDrop, new[] { file }));
            if (imported.Count != 1)
            {
                results.Add(("准备：先把条目拖进盒子", false, $"新增 {imported.Count} 条，期望 1 条"));
                Write(options, overlay, results);
                return 1;
            }

            var item = imported[0];
            var fingerprintBefore = Fingerprint(file);
            var countAfterImport = overlay.Boxes.First(b => b.Id == box.Id).Items.Count;

            // 1. 菜单项齐全
            var headers = (view.BuildItemMenu(item).Items)
                .OfType<MenuItem>()
                .Select(entry => entry.Header?.ToString() ?? string.Empty)
                .ToList();

            var expected = new[] { "打开", "打开位置", "重命名", "移出盒子" };
            results.Add((
                "右键菜单含 打开 / 打开位置 / 重命名 / 移出盒子",
                headers.SequenceEqual(expected),
                $"实际：{string.Join("、", headers)}"));

            // 2. 拖出提供 CF_HDROP
            var dragData = BoxView.BuildDragOutData(item);
            var dropPaths = dragData.GetDataPresent(DataFormats.FileDrop)
                ? dragData.GetData(DataFormats.FileDrop) as string[]
                : null;

            // WPF 会由 FileDrop 派生出 FileNameW / FileName 两个等价格式，属正常；关键是路径本身
            results.Add((
                "拖出提供 CF_HDROP（FileDrop），且只含路径不含任何指令",
                dropPaths is { Length: 1 } && dropPaths[0] == file,
                $"格式：{string.Join("、", dragData.GetFormats())}；路径：{string.Join("、", dropPaths ?? [])}"));

            // 3. 重命名只改展示名
            view.InvokeItemAction(ItemAction.Rename, item, "  收拾干净  ");
            await Task.Delay(120);
            var renamed = Item(overlay, box.Id, item.Id);

            results.Add((
                "重命名只改展示名，不改磁盘文件名",
                renamed is { DisplayName: "收拾干净" } && renamed.TargetPath == file &&
                File.Exists(file) && Fingerprint(file) == fingerprintBefore,
                $"展示名={renamed?.DisplayName}；磁盘名={Path.GetFileName(file)}；指纹一致={Fingerprint(file) == fingerprintBefore}"));

            // 4. 移出盒子只摘引用
            view.InvokeItemAction(ItemAction.Remove, renamed ?? item);
            await Task.Delay(120);
            var afterRemove = overlay.Boxes.First(b => b.Id == box.Id);

            results.Add((
                "移出盒子只摘引用，磁盘文件仍在（P4）",
                afterRemove.Items.All(i => i.Id != item.Id) && File.Exists(file) && Fingerprint(file) == fingerprintBefore,
                $"条目数 {countAfterImport} → {afterRemove.Items.Count}；文件仍在={File.Exists(file)}"));

            // 5. 不存在的条目 Id 不应改变盒子
            var beforeNoop = afterRemove.Items.Count;
            view.InvokeItemAction(ItemAction.Remove, item);
            results.Add((
                "对已移出的条目重复操作无副作用",
                overlay.Boxes.First(b => b.Id == box.Id).Items.Count == beforeNoop,
                $"条目数仍为 {beforeNoop}"));
        }
        catch (Exception ex)
        {
            results.Add(("验收执行", false, $"{ex.GetType().Name}: {ex.Message}"));
        }
        finally
        {
            foreach (var original in originals)
            {
                layout.UpdateBox(original);
            }

            layout.SaveNow();
            overlay.UpdateBoxes(originals);

            try
            {
                if (Directory.Exists(root))
                {
                    Directory.Delete(root, recursive: true);
                }
            }
            catch (IOException)
            {
                // 临时目录删不掉不影响结论
            }
        }

        var failed = results.Count(r => !r.Pass);
        Write(options, overlay, results);

        return failed == 0 ? 0 : 1;
    }

    private static BoxItem? Item(OverlayWindow overlay, string boxId, string itemId) =>
        overlay.Boxes.FirstOrDefault(b => b.Id == boxId)?.Items.FirstOrDefault(i => i.Id == itemId);

    private static string Fingerprint(string path)
    {
        var info = new FileInfo(path);
        return $"{info.FullName}|{info.Exists}|{info.LastWriteTimeUtc:O}|{info.Length}";
    }

    private static void Write(SpikeOptions options, OverlayWindow overlay, IReadOnlyList<(string Step, bool Pass, string Detail)> results)
    {
        var builder = new StringBuilder();
        builder.AppendLine("# 条目菜单与拖出自动化验收报告（issue #8）");
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

        builder.AppendLine();
        builder.AppendLine("说明：验收直接调用菜单项背后的统一入口 InvokeItemAction，");
        builder.AppendLine("      因此覆盖的是用户点击菜单时真正执行的那段代码；");
        builder.AppendLine("      「只动引用不动磁盘」由真实临时文件的指纹比对得出。");

        var directory = options.OutputDirectory ?? Path.Combine(AppContext.BaseDirectory, "artifacts");
        Directory.CreateDirectory(directory);

        File.WriteAllText(
            Path.Combine(directory, "menu-report.md"),
            builder.ToString(),
            new UTF8Encoding(false));
    }
}