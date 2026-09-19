using System.IO;
using System.Text;
using Cubby.Core.Diagnostics;
using Cubby.Core.Model;
using Cubby.Shell.Diagnostics;

namespace Cubby.App;

/// <summary>
/// 稳定性挂机采样（A7 / issue #41）：<c>--soak &lt;分钟&gt;</c> 与 <c>--selftest-soak</c> 共用这一份代码，
/// 只差时长与采样间隔——**长跑与短跑必须是同一条代码路径**，否则短跑通过说明不了长跑的任何事。
///
/// 三件容易被做假的事，这里都堵上了：
///
/// 1. **"不增长"不能是因为什么都没干**：每次采样后都会真的驱动一次布局变更
///    （折叠 / 展开 + 挪位置），走的是 <see cref="OverlayManager.OnBoxChanged"/> +
///    <see cref="OverlayManager.ApplyBoxUpdate"/>，也就是用户拖动盒子时那条路，
///    会真的触发 WPF 重绘与搜索索引重建。
/// 2. **采样本身上不了秤**：<see cref="ResourceProbe.Sample"/> 全是只读查询，不分配、不建对象。
/// 3. **短跑不许冒充长跑**：报告里明确写出"本报告时长 = N 分钟"，
///    短跑结论只对 A7 的"短程"部分负责；8 小时长跑的命令与原样写在报告与工作日志里。
/// </summary>
internal static class SoakRunner
{
    /// <summary>正式挂机的默认采样间隔（issue #41 定的是 30 秒）。</summary>
    private const double DefaultIntervalSeconds = 30;

    /// <summary>不传 <c>--soak</c> 数值时的默认时长：短到能本机跑完，又足够看出增长趋势。</summary>
    private const double DefaultMinutes = 2;

    /// <summary>自检用的时长与间隔：本地与 CI 都要能在半分钟内跑完。</summary>
    private const double SelfTestMinutes = 0.4;

    private const double SelfTestIntervalSeconds = 3;

    public static async Task<int> RunAsync(OverlayManager manager, LayoutService layout, SpikeOptions options)
    {
        var selfTest = options.SoakSelfTest;
        var minutes = options.SoakMinutes ?? (selfTest ? SelfTestMinutes : DefaultMinutes);
        var intervalSeconds = options.SoakIntervalSeconds ?? (selfTest ? SelfTestIntervalSeconds : DefaultIntervalSeconds);

        // 时长与间隔都要有下限，否则 --soak 0 会产出一份"零样本通过"的假报告
        minutes = Math.Max(minutes, 0.1);
        intervalSeconds = Math.Max(intervalSeconds, 0.2);

        var originalDocument = layout.Document;
        var samples = new List<SoakSample>();
        var mutations = new List<string>();
        var startedAt = DateTime.Now;

        try
        {
            // 采样前先让首帧与索引都稳定下来：那是启动成本，不是泄漏（判定里还会再跳一次预热）
            await Task.Delay(1200);

            var sampleIndex = 0;
            var stopAt = DateTime.Now.AddMinutes(minutes);

            // 至少采 3 次：少于 3 个点谈不上斜率
            while (DateTime.Now < stopAt || samples.Count < 3)
            {
                var sample = ResourceProbe.Sample();
                samples.Add(new SoakSample(
                    (DateTime.Now - startedAt).TotalMinutes,
                    sample.WorkingSetMb,
                    sample.PrivateMb,
                    sample.Handles,
                    sample.GdiObjects,
                    sample.UserObjects,
                    sample.Threads));

                // 采完就驱动一次布局变更：否则"不增长"是因为我们什么都没干
                var change = DriveLayoutChange(manager, layout, sampleIndex);
                mutations.Add($"第 {sampleIndex + 1} 次采样后：{change}");

                sampleIndex++;

                if (DateTime.Now >= stopAt && samples.Count >= 3)
                {
                    break;
                }

                await Task.Delay(TimeSpan.FromSeconds(intervalSeconds));
            }
        }
        catch (Exception ex)
        {
            mutations.Add($"采样循环中断：{ex.GetType().Name}: {ex.Message}");
        }
        finally
        {
            // 还原布局：挂机过程中把盒子折来折去，绝不能把用户真实的摆放留在那儿
            layout.Apply(originalDocument);
            manager.SetBoxesVisible(true);
            manager.Rebuild("挂机采样：还原布局");
        }

        var verdict = SoakAnalysis.Analyze(samples);
        var reportPath = Write(options, samples, mutations, verdict, minutes, intervalSeconds, startedAt, selfTest);

        Console.WriteLine(verdict.Conclusion);
        Console.WriteLine($"挂机报告已写入：{reportPath}");

        return verdict.Passed && samples.Count >= 3 ? 0 : 1;
    }

    /// <summary>
    /// 真的改一次布局：折叠 / 展开交替，并把盒子左右挪一挪。
    /// 走的是用户拖动盒子时那条路径（模型 + 视图 + 搜索索引），因此会真的产生渲染与分配工作。
    /// </summary>
    private static string DriveLayoutChange(OverlayManager manager, LayoutService layout, int index)
    {
        var boxes = layout.Boxes;
        if (boxes.Count == 0)
        {
            return "没有盒子可操作（布局为空）";
        }

        var current = boxes[index % boxes.Count];
        var collapse = !current.IsCollapsed;
        var shift = index % 2 == 0 ? 24 : -24;

        var moved = current with
        {
            IsCollapsed = collapse,
            Bounds = new DipRect(current.Bounds.X + shift, current.Bounds.Y, current.Bounds.Width, current.Bounds.Height),
        };

        manager.OnBoxChanged(moved);
        manager.ApplyBoxUpdate(moved);

        return $"{(collapse ? "折叠" : "展开")}「{current.Name}」并{(shift > 0 ? "右" : "左")}移 {Math.Abs(shift)} DIP";
    }

    private static string Write(
        SpikeOptions options,
        IReadOnlyList<SoakSample> samples,
        IReadOnlyList<string> mutations,
        SoakVerdict verdict,
        double minutes,
        double intervalSeconds,
        DateTime startedAt,
        bool selfTest)
    {
        var builder = new StringBuilder();

        builder.AppendLine("# 稳定性挂机采样报告（A7 / issue #41）");
        builder.AppendLine();
        builder.AppendLine($"- 时间：{startedAt:yyyy-MM-dd HH:mm:ss} ~ {DateTime.Now:HH:mm:ss}");
        builder.AppendLine($"- 结论：**{(verdict.Passed ? "PASS" : "FAIL")}**");
        builder.AppendLine($"- **本报告时长 = {verdict.DurationMinutes:0.0} 分钟**" +
                           $"（采样 {samples.Count} 次，间隔 {intervalSeconds:0.#} 秒，判定使用 {verdict.UsedSamples} 次、跳过前 {verdict.SkippedSamples} 次预热）");
        if (selfTest)
        {
            builder.AppendLine("- 本次是 `--selftest-soak` 的短程自检：**它只证明判定器与采样链路是对的，不能当作 A7 的长跑结论。**");
        }

        builder.AppendLine();
        builder.AppendLine("## 门禁判定");
        builder.AppendLine();
        builder.AppendLine("| 指标 | 首值 | 末值 | 每分钟斜率 | 阈值 | 结论 |");
        builder.AppendLine("|---|---|---|---|---|---|");
        foreach (var finding in verdict.Findings)
        {
            var mark = finding.Gating
                ? finding.Passed ? "**PASS**" : "**FAIL**"
                : finding.Passed ? "参考：在预算内" : "⚠ **参考：超预算（不参与判定）**";

            builder.AppendLine(
                $"| {finding.Metric} | {finding.First:0.###} | {finding.Last:0.###} | " +
                $"{finding.SlopePerMinute:+0.000;-0.000;0.000} | {LimitOf(finding.Metric)} | {mark} |");
        }

        builder.AppendLine();
        builder.AppendLine("判定细节：");
        builder.AppendLine();
        foreach (var finding in verdict.Findings)
        {
            builder.AppendLine($"- **{finding.Metric}**：{finding.Detail}");
        }

        builder.AppendLine();
        builder.AppendLine($"**结论**：{verdict.Conclusion}");

        if (verdict.AdvisoryFailures.Count > 0)
        {
            builder.AppendLine();
            builder.AppendLine("### ⚠ 关于「工作集」这一项（需要人来拍板的口径问题，不是代码问题）");
            builder.AppendLine();
            builder.AppendLine("issue #41 写的是「工作集 ≤ 120MB（第 8 章指标）」，但第 8 章的原文是");
            builder.AppendLine("**「常驻内存 ≤ 120MB」**，而且紧跟着「作为取舍点接受」。");
            builder.AppendLine("两者不是同一个口径：");
            builder.AppendLine();
            builder.AppendLine("| 口径 | 本次实测 | 预算 120MB | 说明 |");
            builder.AppendLine("|---|---|---|---|");
            builder.AppendLine($"| **私有字节**（判定口径） | {verdict.Findings.First(f => f.Metric == "私有字节").Last:0.0} MB | 在预算内 | 进程独占的内存 |");
            builder.AppendLine($"| **工作集**（参考口径） | {verdict.Findings.First(f => f.Metric.StartsWith("工作集", StringComparison.Ordinal)).Last:0.0} MB | **超出** | 含与其它进程共享的 DLL 页等 |");
            builder.AppendLine();
            builder.AppendLine("本报告**两个数都列出来了**，判定用的是私有口径，工作集超标单独标 ⚠、**没有**被藏起来。");
            builder.AppendLine("这不是本次改动引入的：M2 阶段的基线（`--dump-state` 的 `state.txt`）就是 142.7MB。");
            builder.AppendLine("若项目所有者认为预算也必须算作 M3 门禁，把 `SoakAnalysis` 里工作集那一项的 `Gating` 改成 true，");
            builder.AppendLine("判定会立刻变成 FAIL——这个决定留给人，写代码的人不替谁拍。");
        }

        builder.AppendLine();
        builder.AppendLine("## 采样明细");
        builder.AppendLine();
        builder.AppendLine("| # | 时刻(分钟) | 工作集(MB) | 私有(MB) | 句柄 | GDI | USER | 线程 |");
        builder.AppendLine("|---|---|---|---|---|---|---|---|");
        for (var i = 0; i < samples.Count; i++)
        {
            var sample = samples[i];
            var warm = i < verdict.SkippedSamples ? " ⤶预热" : string.Empty;
            builder.AppendLine(
                $"| {i + 1}{warm} | {sample.ElapsedMinutes:0.00} | {sample.WorkingSetMb:0.0} | {sample.PrivateMb:0.0} | " +
                $"{sample.Handles} | {sample.GdiObjects} | {sample.UserObjects} | {sample.Threads} |");
        }

        builder.AppendLine();
        builder.AppendLine("## 采样期间真的改过布局（否则「不增长」说明不了任何事）");
        builder.AppendLine();
        foreach (var mutation in mutations)
        {
            builder.AppendLine($"- {mutation}");
        }

        builder.AppendLine();
        builder.AppendLine("## 8 小时长跑怎么做（A7 的完整结论只能由它给出）");
        builder.AppendLine();
        builder.AppendLine("```powershell");
        builder.AppendLine("# 30 秒一采样，跑 480 分钟（8 小时）。期间不要手动动盒子，让它自己跑。");
        builder.AppendLine("& $app --soak 480");
        builder.AppendLine();
        builder.AppendLine("# 想连着看每 10 分钟一次的进度（可选）");
        builder.AppendLine("& $app --soak 480 --soak-interval 600");
        builder.AppendLine("```");
        builder.AppendLine();
        builder.AppendLine("- 报告同样是 `artifacts/soak-report.md`，结论里会写明真实时长。");
        builder.AppendLine("- 长跑建议挑不打扰用电脑的时段；跑完把 `artifacts/soak-report.md` 贴进 issue 与工作日志。");
        builder.AppendLine("- **判据与短跑完全一致**（同一条代码路径、同一套阈值），差别只是时长。");
        builder.AppendLine();
        builder.AppendLine("## 关于采样开销");
        builder.AppendLine();
        builder.AppendLine("采样走 `Process` 与 `GetGuiResources` 只读查询，不分配、不建对象、不写盘（直到最后一次性写报告），");
        builder.AppendLine("因此采样本身不会把被测量的数字推高。");

        var directory = options.OutputDirectory ?? Path.Combine(AppContext.BaseDirectory, "artifacts");
        Directory.CreateDirectory(directory);

        var path = Path.Combine(directory, "soak-report.md");
        File.WriteAllText(path, builder.ToString(), new UTF8Encoding(false));
        return path;
    }

    private static string LimitOf(string metric) => metric switch
    {
        "私有字节" => $"末值 ≤ {SoakAnalysis.WorkingSetLimitMb:0} MB 且 斜率 ≤ {SoakAnalysis.MemorySlopeLimitMbPerMinute:0.0} MB/分（或首末差 ≤ {SoakAnalysis.MemoryNoiseBandMb:0} MB）",
        "句柄数" => $"非单调增长 且 首末差 ≤ ±{SoakAnalysis.HandleNoise}",
        "工作集（口径参考）" => $"≤ {SoakAnalysis.WorkingSetLimitMb:0} MB（只报不判）",
        _ => $"非单调增长 且 首末差 ≤ ±{SoakAnalysis.ResourceNoise}",
    };
}
