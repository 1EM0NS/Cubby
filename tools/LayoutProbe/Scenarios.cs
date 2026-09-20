using Cubby.Core.Layout;
using Cubby.Core.Model;

namespace LayoutProbe;

/// <summary>一个显示器拓扑场景：假想此刻系统上"存在"的就是这几块屏。</summary>
internal sealed record Scenario(string Name, string Note, IReadOnlyList<MonitorSurface> Monitors);

/// <summary>某个盒子在某个场景里的落位结果。</summary>
internal sealed record Placement(
    string BoxName,
    string MonitorId,
    MonitorMatchKind Kind,
    PlacementFix Fix,
    DipRect Bounds,
    bool FullyVisible)
{
    public string Summary =>
        $"{BoxName} → {MonitorId}｜{Describe(Kind)}｜{Describe(Fix)}｜" +
        $"({Bounds.X:0},{Bounds.Y:0}) {Bounds.Width:0}×{Bounds.Height:0}" +
        (FullyVisible ? string.Empty : "｜⚠ 落在屏幕之外");

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
}

/// <summary>一个场景跑完的结论。</summary>
internal sealed record ScenarioResult(
    Scenario Scenario,
    IReadOnlyList<Placement> Placements,
    int BoxCount,
    bool Passed,
    string Verdict);

/// <summary>
/// 场景构造与断言。这里是本工具的全部"智能"，而且它**只做计算**：
/// 不调用任何会改变系统状态的 API（不改分辨率、不改 DPI、不动任何窗口）。
///
/// 为什么要有它：验收 A5（多屏 / DPI / 分辨率变化后盒子位置正确）在单显示器机器上
/// 根本没法做——但那条验收真正要保证的东西（"每个盒子都落在存在的屏上，而且看得见"）
/// 是一个**纯函数**，可以在这里用构造出来的拓扑穷举。真实拔插显示器的端到端仍然留人工，
/// 两者不是替代关系：一个保证逻辑，一个保证系统集成。
/// </summary>
internal static class Scenarios
{
    private const double Epsilon = 0.01;

    /// <summary>按真实显示器生成拓扑矩阵。</summary>
    public static IReadOnlyList<Scenario> Build(IReadOnlyList<MonitorSurface> real)
    {
        var scenarios = new List<Scenario>();

        if (real.Count == 0)
        {
            return scenarios;
        }

        var primary = real.FirstOrDefault(m => m.IsPrimary) ?? real[0];

        scenarios.Add(new Scenario("现况", "系统此刻实际枚举到的显示器。", real));

        scenarios.Add(new Scenario(
            "只剩主屏",
            "副屏被拔掉 / 睡着 / 正在重排时会短暂枚举不到它。原属副屏的盒子必须回到主屏，而且必须看得见。",
            [primary]));

        scenarios.Add(new Scenario(
            "主屏分辨率调小到 1920×1080",
            "用户手动改分辨率。靠右下的盒子在旧尺寸里合法，在新尺寸里放不下。",
            [Resized(primary, 1920, 1080, 1.0)]));

        scenarios.Add(new Scenario(
            "主屏 DPI 从当前值升到 150%",
            "同一块屏改成 150% 缩放：DIP 可用范围变小，贴边的盒子会越界。",
            [WithScale(primary, 1.5)]));

        scenarios.Add(new Scenario(
            "设备名换了但分辨率不变",
            "换显示接口、重装驱动后设备名会变，靠分辨率回退匹配。",
            [WithId(primary, primary.Id + "-renamed")]));

        if (real.Count == 1)
        {
            // 真实只有一台时，补一台"接着右侧挂上去的"副屏，好让双屏路径也被走到
            var synthetic = new MonitorSurface(
                @"\\.\DISPLAY-synthetic",
                new PixelRect(primary.Bounds.Right, 0, primary.Bounds.Right + 1920, 1080),
                1.0);

            scenarios.Add(new Scenario(
                "扩展出一台 1920×1080 副屏",
                "在主屏右侧再接一台。副屏上还没有任何盒子，所以这一场主要看主屏的盒子有没有被挪动。",
                [primary, synthetic]));
        }

        if (real.Count >= 2)
        {
            scenarios.Add(new Scenario(
                "主副屏交换",
                "在显示设置里把主屏换到另一台。每台屏的尺寸都没变，所以盒子不该被挪动。",
                real.Select(m => m with { IsPrimary = !m.IsPrimary }).ToList()));
        }

        return scenarios;
    }

    /// <summary>
    /// 合成的边界盒子：真实布局里通常只有"放在合理位置"的盒子，
    /// 而越界问题恰恰出在边界上，所以额外造三个来踩线。
    /// </summary>
    public static IReadOnlyList<Box> SyntheticBoxes(MonitorSurface primary)
    {
        var extent = primary.DipExtent;

        return
        [
            new Box("syn-edge", "合成·贴在右下角", new DipRect(extent.Width - 420, extent.Height - 320, 420, 320))
            {
                MonitorId = primary.Id,
                MonitorWidth = primary.Bounds.Width,
                MonitorHeight = primary.Bounds.Height,
            },

            new Box("syn-huge", "合成·比屏幕还大", new DipRect(0, 0, 4000, 3000))
            {
                MonitorId = primary.Id,
                MonitorWidth = primary.Bounds.Width,
                MonitorHeight = primary.Bounds.Height,
            },

            new Box("syn-gone", "合成·原屏已拔（4K）", new DipRect(3000, 1800, 420, 320))
            {
                MonitorId = @"\\.\DISPLAY99",
                MonitorWidth = 3840,
                MonitorHeight = 2160,
            },
        ];
    }

    /// <summary>跑一个场景。判据只有两条：盒子一个都不能丢；每个盒子都必须整盒可见。</summary>
    public static ScenarioResult Run(Scenario scenario, IReadOnlyList<Box> boxes)
    {
        var plans = OverlayPlanner.Plan(scenario.Monitors, boxes);
        var placements = new List<Placement>();

        foreach (var plan in plans)
        {
            var extent = plan.Monitor.DipExtent;

            foreach (var planned in plan.Boxes)
            {
                var bounds = planned.Box.Bounds;

                // "整盒可见"而不是"露一个角"：浮层窗口只覆盖这一块屏，
                // 盒子只要有一部分落在窗口外就点不到、拖不回
                var visible =
                    bounds.X >= -Epsilon &&
                    bounds.Y >= -Epsilon &&
                    bounds.Right <= extent.Width + Epsilon &&
                    bounds.Bottom <= extent.Height + Epsilon;

                placements.Add(new Placement(
                    planned.Box.Name,
                    plan.Monitor.Id,
                    planned.Kind,
                    planned.Fix,
                    bounds,
                    visible));
            }
        }

        var lost = boxes.Count - placements.Count;
        var invisible = placements.Where(p => !p.FullyVisible).ToList();

        var passed = lost == 0 && invisible.Count == 0;

        var verdict = passed
            ? $"通过：{placements.Count} 个盒子全部落在存在的显示器上，且整盒都在该屏的 DIP 范围内。"
            : string.Join(
                "；",
                new[]
                {
                    lost != 0 ? $"有 {lost} 个盒子在分配中丢失（本应 {boxes.Count} 个，实际 {placements.Count} 个）" : null,
                    invisible.Count != 0 ? $"有 {invisible.Count} 个盒子落在屏幕之外：{string.Join("、", invisible.Select(p => p.BoxName))}" : null,
                }.Where(s => s is not null));

        return new ScenarioResult(scenario, placements, boxes.Count, passed, verdict);
    }

    private static MonitorSurface Resized(MonitorSurface source, int width, int height, double scale) =>
        source with
        {
            Bounds = new PixelRect(
                source.Bounds.Left,
                source.Bounds.Top,
                source.Bounds.Left + width,
                source.Bounds.Top + height),
            DpiScale = scale,
        };

    private static MonitorSurface WithScale(MonitorSurface source, double scale) =>
        source with { DpiScale = scale };

    private static MonitorSurface WithId(MonitorSurface source, string id) =>
        source with { Id = id };
}
