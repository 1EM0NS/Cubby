using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Cubby.Core.Layout;
using Cubby.Core.Model;
using Cubby.Core.Storage;
using Cubby.Shell.Overlay;

namespace LayoutProbe;

/// <summary>
/// 显示器拓扑探针（技术方案 9.2 节要求的 LayoutProbe）。
///
/// 它回答的问题是：**布局在不同显示器拓扑下会不会把盒子搞丢。**
/// 做法是把「盒子归哪块屏、在那块屏上放不放得下」这个纯函数拿出来，
/// 喂给它构造出来的拓扑矩阵（单屏 / 双屏 / 主副屏交换 / 分辨率变化 / DPI 变化 / 设备名变化），
/// 然后断言每个盒子都落在存在的屏上、而且整盒可见。
///
/// **它不做什么**（这几条是硬边界，不因为"想测得真一点"而放松）：
/// - 不改系统的显示设置（分辨率 / DPI / 主屏）——那会在用户眼前闪黑屏；
/// - 不增删显示器、不触发任何显示变化通知；
/// - 不动任何窗口（不最小化、不激活、不移动别人和自己的窗口）；
/// - 不写布局文件（只读）。
///
/// 因此真实"拔插显示器 / 改分辨率"的端到端验证依然是人工项，本工具只负责其中的逻辑部分。
///
/// 用法：
///   LayoutProbe                         跑全部场景，写 artifacts/layout-report.md
///   LayoutProbe --monitors              只打印当前显示器后退出
///   LayoutProbe --scenario 只剩主屏      只跑名字里含该子串的场景
///   LayoutProbe --layout &lt;路径&gt;         指定布局文件（默认 %AppData%\Cubby\layout.json）
///   LayoutProbe --json                  以 JSON 打到标准输出，便于前后对比
///   LayoutProbe --no-synth              不加合成的边界盒子，只用真实布局里的盒子
///   LayoutProbe --out &lt;路径&gt;            报告输出路径（默认 artifacts/layout-report.md）
///
/// 退出码：0 全部场景通过；1 有场景不通过；2 参数或环境问题。
/// </summary>
internal static class Program
{
    private static int Main(string[] args)
    {
        Console.OutputEncoding = Encoding.UTF8;

        if (Has(args, "--help") || Has(args, "-h"))
        {
            Console.WriteLine("LayoutProbe：显示器拓扑探针。见源文件顶部的用法说明。");
            return 0;
        }

        var layoutPath = ValueOf(args, "--layout") ?? DefaultLayoutPath();
        var asJson = Has(args, "--json");
        var scenarioFilter = ValueOf(args, "--scenario");
        var includeSynthetic = !Has(args, "--no-synth");
        var outPath = ValueOf(args, "--out") ?? Path.Combine("artifacts", "layout-probe-report.md");

        var real = MonitorSurfaces.Enumerate();

        if (Has(args, "--monitors"))
        {
            Console.WriteLine($"当前显示器 {real.Count} 台：");
            foreach (var monitor in real)
            {
                var extent = monitor.DipExtent;
                Console.WriteLine(
                    $"  {monitor.Id,-20} {monitor.Bounds}  DIP 空间 {extent.Width:0}×{extent.Height:0}  " +
                    $"缩放 {monitor.DpiScale:0.##}{(monitor.IsPrimary ? "  [主屏]" : string.Empty)}");
            }

            return 0;
        }

        if (real.Count == 0)
        {
            Console.Error.WriteLine("没有枚举到任何显示器，无法进行拓扑推演。");
            return 2;
        }

        var store = new LayoutStore(layoutPath);
        var document = store.Load();

        var boxes = document.Boxes.ToList();
        if (includeSynthetic)
        {
            var primary = real.FirstOrDefault(m => m.IsPrimary) ?? real[0];
            boxes.AddRange(Scenarios.SyntheticBoxes(primary));
        }

        var scenarios = Scenarios.Build(real);
        if (scenarioFilter is not null)
        {
            scenarios = scenarios
                .Where(s => s.Name.Contains(scenarioFilter, StringComparison.OrdinalIgnoreCase))
                .ToList();

            if (scenarios.Count == 0)
            {
                Console.Error.WriteLine($"没有名字含「{scenarioFilter}」的场景。");
                return 2;
            }
        }

        var results = scenarios.Select(s => Scenarios.Run(s, boxes)).ToList();
        var passed = results.All(r => r.Passed);

        if (asJson)
        {
            Console.WriteLine(JsonSerializer.Serialize(
                new
                {
                    Time = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"),
                    LayoutFile = layoutPath,
                    LayoutLoadDiagnostic = store.LastLoadDiagnostic,
                    Monitors = real.Select(Describe).ToList(),
                    BoxCount = boxes.Count,
                    RealBoxCount = document.Boxes.Count,
                    SyntheticBoxCount = boxes.Count - document.Boxes.Count,
                    Passed = passed,
                    Scenarios = results.Select(r => new
                    {
                        r.Scenario.Name,
                        r.Scenario.Note,
                        r.Scenario.Monitors,
                        r.BoxCount,
                        r.Passed,
                        r.Verdict,
                        Placements = r.Placements,
                    }).ToList(),
                },
                new JsonSerializerOptions
                {
                    WriteIndented = true,
                    Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
                    Converters = { new JsonStringEnumConverter() },
                }));

            return passed ? 0 : 1;
        }

        var report = BuildReport(layoutPath, store.LastLoadDiagnostic, real, document.Boxes.Count, results, passed);
        WriteReport(outPath, report);

        Console.WriteLine($"显示器 {real.Count} 台；场景 {results.Count} 个；落位判定 {results.Sum(r => r.Placements.Count)} 次。");
        Console.WriteLine($"结果：{(passed ? "全部通过" : "有场景不通过")}");
        Console.WriteLine($"报告：{Path.GetFullPath(outPath)}");

        foreach (var failed in results.Where(r => !r.Passed))
        {
            Console.WriteLine($"  ✗ {failed.Scenario.Name}：{failed.Verdict}");
        }

        return passed ? 0 : 1;
    }

    private static string BuildReport(
        string layoutPath,
        string? loadDiagnostic,
        IReadOnlyList<MonitorSurface> real,
        int realBoxCount,
        IReadOnlyList<ScenarioResult> results,
        bool passed)
    {
        var builder = new StringBuilder();

        builder.AppendLine("# 显示器拓扑探针报告（LayoutProbe）");
        builder.AppendLine();
        builder.AppendLine($"时间：{DateTime.Now:yyyy-MM-dd HH:mm:ss}");
        builder.AppendLine();
        builder.AppendLine($"布局文件：`{layoutPath}`");
        if (!string.IsNullOrEmpty(loadDiagnostic))
        {
            builder.AppendLine();
            builder.AppendLine($"> ⚠ 布局读取有问题：{loadDiagnostic}");
        }

        builder.AppendLine();
        builder.AppendLine($"## 当前真实显示器（{real.Count} 台，仅只读枚举）");
        builder.AppendLine();
        foreach (var monitor in real)
        {
            var extent = monitor.DipExtent;
            builder.AppendLine(
                $"- `{monitor.Id}` {monitor.Bounds} 像素 {monitor.Bounds.Width}×{monitor.Bounds.Height}，" +
                $"缩放 {monitor.DpiScale:0.##}，DIP 空间 {extent.Width:0}×{extent.Height:0}" +
                $"{(monitor.IsPrimary ? "（主屏）" : string.Empty)}");
        }

        builder.AppendLine();
        builder.AppendLine($"## 结论");
        builder.AppendLine();
        builder.AppendLine(passed
            ? $"**通过**：{results.Count} 个场景、{results.Sum(r => r.Placements.Count)} 次落位判定，" +
              $"每个盒子都落在存在的显示器上且整盒可见。"
            : $"**未通过**：{results.Count(r => !r.Passed)} / {results.Count} 个场景有问题，详见下方。");
        builder.AppendLine();
        builder.AppendLine($"参与推演的盒子：真实布局 {realBoxCount} 个" +
                           (results.FirstOrDefault() is { BoxCount: var total } && total > realBoxCount
                               ? $"，合成边界盒子 {total - realBoxCount} 个"
                               : "（本次未加合成盒子）") +
                           "。");
        builder.AppendLine();

        builder.AppendLine("## 场景明细");
        builder.AppendLine();

        for (var i = 0; i < results.Count; i++)
        {
            var result = results[i];

            builder.AppendLine($"### {i + 1}. {result.Scenario.Name}　{(result.Passed ? "✅" : "❌")}");
            builder.AppendLine();
            builder.AppendLine($"> {result.Scenario.Note}");
            builder.AppendLine();
            builder.AppendLine($"场景中的显示器：{string.Join("、", result.Scenario.Monitors.Select(m => $"`{m.Id}` {m.Bounds.Width}×{m.Bounds.Height}@{m.DpiScale:0.##}"))}");
            builder.AppendLine();
            builder.AppendLine("| 盒子 | 目标屏 | 匹配方式 | 修正 | 位置（DIP，相对目标屏左上角） | 整盒可见 |");
            builder.AppendLine("|---|---|---|---|---|---|");

            foreach (var placement in result.Placements)
            {
                builder.AppendLine(
                    $"| {placement.BoxName} | `{placement.MonitorId}` | {Describe(placement.Kind)} | " +
                    $"{Describe(placement.Fix)} | ({placement.Bounds.X:0}, {placement.Bounds.Y:0}) " +
                    $"{placement.Bounds.Width:0}×{placement.Bounds.Height:0} | {(placement.FullyVisible ? "是" : "**否**")} |");
            }

            builder.AppendLine();
            builder.AppendLine($"**判定**：{result.Verdict}");
            builder.AppendLine();
        }

        builder.AppendLine("## 本工具不做什么");
        builder.AppendLine();
        builder.AppendLine("上面推演的是**分配逻辑**。以下场景它一概不碰，因为都会动到用户正在用的机器：");
        builder.AppendLine();
        builder.AppendLine("- 真实的改分辨率 / 改 DPI / 换主屏（会在用户眼前闪黑屏）；");
        builder.AppendLine("- 真的拔插显示器、真的触发显示变化通知；");
        builder.AppendLine("- 移动、最小化、激活任何窗口。");
        builder.AppendLine();
        builder.AppendLine("所以「显示器插拔 / DPI 切换」的**端到端**验收仍是人工项：");
        builder.AppendLine("本工具保证的是「分配逻辑对不对」，人工确认的是「接到系统上是不是真的走通了」。");
        builder.AppendLine();

        return builder.ToString();
    }

    private static void WriteReport(string path, string report)
    {
        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        File.WriteAllText(path, report, new UTF8Encoding(false));
    }

    private static object Describe(MonitorSurface monitor) => new
    {
        monitor.Id,
        Bounds = monitor.Bounds.ToString(),
        DipWidth = Math.Round(monitor.DipExtent.Width, 2),
        DipHeight = Math.Round(monitor.DipExtent.Height, 2),
        monitor.DpiScale,
        monitor.IsPrimary,
    };

    private static string Describe(MonitorMatchKind kind) => kind switch
    {
        MonitorMatchKind.ExactId => "原屏仍在",
        MonitorMatchKind.SameResolution => "按分辨率匹配",
        _ => "原屏不在，回退",
    };

    private static string Describe(PlacementFix fix) => fix switch
    {
        PlacementFix.None => "未改动",
        PlacementFix.Moved => "挪回可见范围",
        PlacementFix.Resized => "收进屏幕尺寸",
        _ => "挪回并收进屏幕尺寸",
    };

    private static string DefaultLayoutPath() => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "Cubby",
        "layout.json");

    private static bool Has(string[] args, string name) =>
        args.Any(a => a.Equals(name, StringComparison.OrdinalIgnoreCase));

    private static string? ValueOf(string[] args, string name)
    {
        for (var i = 0; i < args.Length - 1; i++)
        {
            if (args[i].Equals(name, StringComparison.OrdinalIgnoreCase))
            {
                return args[i + 1];
            }
        }

        return null;
    }
}
