using System.IO;
using System.Text;
using Cubby.Core.Model;
using Cubby.Core.Rules;

namespace Cubby.App;

/// <summary>
/// 归类规则的自动化验收（issue #14）。
///
/// 验收用**独立的临时规则文件**，绝不碰用户自己的 rules.json；
/// 规则的五类条件各造一个真实文件去命中，再逐条核对"谁被哪条规则收走了"。
///
/// 三条验收标准分别落到：
/// 1. 五类条件都能命中（扩展名 / MIME / 时间 / 来源路径 / 正则）；
/// 2. 预览不改动任何东西，应用后可以一键撤销（撤销后条目清单与之前逐项一致）；
/// 3. 全程不移动文件（磁盘指纹前后一致，P4）。
/// </summary>
internal static class RuleTestRunner
{
    public static async Task<int> RunAsync(OverlayWindow overlay, LayoutService layout, OverlayManager manager, SpikeOptions options)
    {
        var results = new List<(string Step, bool Pass, string Detail)>();
        var originals = overlay.Boxes.ToList();
        var box = originals.FirstOrDefault();

        if (box is null)
        {
            results.Add(("准备", false, "该显示器上没有盒子"));
            Write(options, results, "(无)");
            return 1;
        }

        var root = Path.Combine(Path.GetTempPath(), "cubby-rules-test-" + Guid.NewGuid().ToString("N")[..8]);
        var source = Path.Combine(root, "cubby-rules-来源");
        var rulesPath = Path.Combine(root, "rules.json");
        var service = new RuleService(layout, manager.ApplyBoxUpdate, () => box.Id, rulesPath);

        try
        {
            // ---- 造素材：每个条件都有一份"正好命中"的真实文件 ----
            Directory.CreateDirectory(source);
            await File.WriteAllTextAsync(Path.Combine(source, "Screenshot_2026.png"), "png");   // 正则
            await File.WriteAllTextAsync(Path.Combine(source, "photo.jpg"), "jpg");             // MIME + 扩展名
            await File.WriteAllTextAsync(Path.Combine(source, "notes.md"), "md");               // 时间（新）
            await File.WriteAllTextAsync(Path.Combine(source, "app.log"), "log");               // 来源路径
            await File.WriteAllTextAsync(Path.Combine(source, "archive.zip"), "zip");           // 目标盒子不存在
            var old = Path.Combine(source, "old.txt");
            await File.WriteAllTextAsync(old, "old");
            File.SetLastWriteTimeUtc(old, DateTime.UtcNow.AddDays(-30));                        // 时间取反
            Directory.CreateDirectory(Path.Combine(source, "子目录"));                           // 无扩展名，未命中

            var fingerprintBefore = Fingerprint(source);

            // ---- 规则：五类条件各一条，加上一条指向不存在盒子的 ----
            service.Save(new RuleSet
            {
                Rules =
                [
                    NewRule("r-regex", "截图", 1, box.Id, new RuleCondition { Kind = RuleMatchKind.Regex, Value = "^Screenshot" }),
                    NewRule("r-mime", "图片", 2, box.Id,
                        new RuleCondition { Kind = RuleMatchKind.Mime, Value = "image/*" },
                        new RuleCondition { Kind = RuleMatchKind.Extension, Value = "jpg,png,gif" }),
                    NewRule("r-recent", "新文档", 3, box.Id,
                        new RuleCondition { Kind = RuleMatchKind.Extension, Value = "md" },
                        new RuleCondition { Kind = RuleMatchKind.Time, WithinDays = 1 }),
                    NewRule("r-old", "旧文本", 4, box.Id,
                        new RuleCondition { Kind = RuleMatchKind.Extension, Value = "txt" },
                        new RuleCondition { Kind = RuleMatchKind.Time, WithinDays = 1, Negate = true }),
                    NewRule("r-source", "来自该目录的日志", 5, box.Id,
                        new RuleCondition { Kind = RuleMatchKind.SourcePath, Value = "*cubby-rules-*" },
                        new RuleCondition { Kind = RuleMatchKind.Extension, Value = "log" }),
                    NewRule("r-missing", "压缩包", 0, "并不存在的盒子",
                        new RuleCondition { Kind = RuleMatchKind.Extension, Value = "zip" }),
                ],
            });

            service.Reload();
            await File.WriteAllTextAsync(Path.Combine(root, "touch.txt"), "驱动一次落盘");

            results.Add((
                "规则以 JSON 保存并可重新加载",
                File.Exists(rulesPath) && service.Rules.Rules.Count == 6,
                $"{Path.GetFileName(rulesPath)}：{service.Rules.Rules.Count} 条规则；{service.Describe()}"));

            // ---- 预览：不改动任何东西 ----
            var candidates = RuleCandidateSources(source);
            var window = new RulesWindow(service, () => candidates);

            var itemsBefore = Current(overlay, box.Id).Items.Count;
            window.Preview();
            await Task.Delay(150);

            var plan = service.Preview(candidates);
            var byRule = plan.Matches.ToDictionary(m => Path.GetFileName(m.CandidatePath), m => m.RuleId, StringComparer.OrdinalIgnoreCase);

            results.Add((
                "五类条件各自命中（扩展名 / MIME / 时间 / 来源路径 / 正则）",
                byRule.TryGetValue("Screenshot_2026.png", out var byRegex) && byRegex == "r-regex" &&
                byRule.TryGetValue("photo.jpg", out var byMime) && byMime == "r-mime" &&
                byRule.TryGetValue("notes.md", out var byTime) && byTime == "r-recent" &&
                byRule.TryGetValue("old.txt", out var byNegate) && byNegate == "r-old" &&
                byRule.TryGetValue("app.log", out var bySource) && bySource == "r-source",
                string.Join("、", plan.Matches.Select(m => $"{Path.GetFileName(m.CandidatePath)}→{m.RuleId}"))));

            results.Add((
                "优先级生效：截图同时符合图片规则，仍由优先级更高的正则规则收走",
                byRule.TryGetValue("Screenshot_2026.png", out var winner) && winner == "r-regex",
                $"{Path.GetFileName("Screenshot_2026.png")} → {byRule.GetValueOrDefault("Screenshot_2026.png")}"));

            results.Add((
                "整体时间取反：30 天前的 old.txt 由「最近 1 天」取反的分支收走",
                byRule.TryGetValue("old.txt", out var oldRule) && oldRule == "r-old",
                $"old.txt → {byRule.GetValueOrDefault("old.txt")}"));

            results.Add((
                "目标盒子不存在时不硬塞，单独列出来提示",
                plan.MissingTargets.Contains("并不存在的盒子") && plan.Unmatched.Any(p => p.EndsWith("archive.zip", StringComparison.OrdinalIgnoreCase)),
                $"缺失目标：{string.Join("、", plan.MissingTargets)}；未命中 {plan.Unmatched.Count} 条"));

            results.Add((
                "预览不改动盒子，也不改动磁盘",
                Current(overlay, box.Id).Items.Count == itemsBefore && Fingerprint(source) == fingerprintBefore &&
                window.Plan.Length > 0,
                $"条目数仍为 {itemsBefore}；预览区 {window.Plan.Length} 字符"));

            // ---- 应用 + 撤销 ----
            window.ApplyToDesktop();
            await Task.Delay(250);

            var afterApply = Current(overlay, box.Id);
            var addedPaths = afterApply.Items.Select(i => i.TargetPath).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var expectedPaths = plan.Matches.Select(m => m.CandidatePath).ToList();

            var missing = expectedPaths.Where(p => !addedPaths.Contains(p)).ToList();
            results.Add((
                "应用后命中的条目被登记进目标盒子",
                missing.Count == 0 && afterApply.Items.Count == itemsBefore + expectedPaths.Count,
                $"条目 {itemsBefore} → {afterApply.Items.Count}（期望新增 {expectedPaths.Count} 条）" +
                (missing.Count == 0 ? string.Empty : $"；缺失：{string.Join("、", missing.Select(Path.GetFileName))}")));

            results.Add((
                "归类不移动文件（磁盘指纹一致，P4）",
                Fingerprint(source) == fingerprintBefore,
                Fingerprint(source) == fingerprintBefore ? "原目录路径 / 时间戳 / 大小逐项一致" : "指纹有变化！"));

            window.UndoLast();
            await Task.Delay(250);

            var afterUndo = Current(overlay, box.Id);
            results.Add((
                "一键撤销上次归类，条目清单回到应用前",
                afterUndo.Items.Count == itemsBefore &&
                !afterUndo.Items.Any(i => expectedPaths.Contains(i.TargetPath, StringComparer.OrdinalIgnoreCase)) &&
                Fingerprint(source) == fingerprintBefore,
                $"条目 {afterApply.Items.Count} → {afterUndo.Items.Count}；{window.Status}"));

            results.Add((
                "撤销后再次撤销是无害的空操作",
                service.Undo() == 0 && Current(overlay, box.Id).Items.Count == itemsBefore,
                service.LastUndoDescription));

            results.Add((
                "规则明细可读（界面展示的就是这份）",
                window.RuleLines.Count == 6,
                string.Join(" | ", window.RuleLines.Take(2).Select(l => l.Trim()))));
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
            manager.ApplyBoxUpdate(originals.First(b => b.Id == box.Id));

            TryDelete(root);
        }

        var failed = results.Count(r => !r.Pass);
        Write(options, results, service.Describe());

        return failed == 0 && results.Count > 0 ? 0 : 1;
    }

    private static ClassificationRule NewRule(string id, string name, int priority, string boxId, params RuleCondition[] conditions) =>
        new()
        {
            Id = id,
            Name = name,
            TargetBoxId = boxId,
            Priority = priority,
            Conditions = conditions,
        };

    /// <summary>把测试目录当作"候选来源"，走的是与界面相同的构造路径。</summary>
    private static IReadOnlyList<RuleCandidate> RuleCandidateSources(string folder) =>
        RuleCandidates.FromFolder(folder);

    private static Box Current(OverlayWindow overlay, string boxId) =>
        overlay.Boxes.First(b => b.Id == boxId);

    private static string Fingerprint(string folder)
    {
        if (!Directory.Exists(folder))
        {
            return "(目录不存在)";
        }

        return string.Join(
            " | ",
            Directory.GetFileSystemEntries(folder)
                .OrderBy(p => p, StringComparer.OrdinalIgnoreCase)
                .Select(p => Directory.Exists(p)
                    ? $"dir:{p}"
                    : $"file:{p}:{new FileInfo(p).LastWriteTimeUtc:O}:{new FileInfo(p).Length}"));
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

    private static void Write(SpikeOptions options, IReadOnlyList<(string Step, bool Pass, string Detail)> results, string rulesDescription)
    {
        var builder = new StringBuilder();
        builder.AppendLine("# 自动归类规则自动化验收报告（issue #14）");
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
        builder.AppendLine($"规则状态：{rulesDescription}");
        builder.AppendLine();
        builder.AppendLine("说明：验收使用独立的临时规则文件，不触碰用户自己的 rules.json；");
        builder.AppendLine("      规则只决定条目展示在哪个盒子，全程没有移动 / 复制 / 删除任何文件（P4）。");

        var directory = options.OutputDirectory ?? Path.Combine(AppContext.BaseDirectory, "artifacts");
        Directory.CreateDirectory(directory);

        File.WriteAllText(
            Path.Combine(directory, "rules-report.md"),
            builder.ToString(),
            new UTF8Encoding(false));
    }
}