using System.IO;
using System.Text;
using Cubby.Core.Model;
using Cubby.Shell.Overlay;

namespace Cubby.Shell.Diagnostics;

/// <summary>采样点上实际会收到点击的那一层。</summary>
public enum HitWindowRole
{
    /// <summary>被测浮层。</summary>
    Overlay,

    /// <summary>自测用的受控背景层。</summary>
    ProbeLayer,

    /// <summary>其他窗口：说明环境不可控，本次判定失败。</summary>
    Foreign,
}

/// <summary>
/// 单个采样点的结果。
/// ExpectOurs 为 true 表示落在盒子内、应当由浮层接收；false 表示在盒子外、应当放行给下层。
/// </summary>
public sealed record HitTestSample(
    string Label,
    int X,
    int Y,
    bool ExpectOurs,
    HitWindowRole Role,
    string HitWindowClass,
    uint HitWindowProcessId,
    bool OverlayReceivedClick,
    bool ProbeLayerReceivedClick,
    bool ClickWasInjected,
    string Note)
{
    /// <summary>期望与实际是否一致。</summary>
    public bool IsConsistent => ExpectOurs ? Role == HitWindowRole.Overlay : Role != HitWindowRole.Overlay;

    /// <summary>一致，且注入的点击确实被「应该收到它的那一层」收到了。</summary>
    public bool Pass => IsConsistent
        && (!ClickWasInjected || (ExpectOurs ? OverlayReceivedClick : ProbeLayerReceivedClick));

    public string Verdict => Pass ? "PASS" : "FAIL";

    public string RoleText => Role switch
    {
        HitWindowRole.Overlay => "被测浮层",
        HitWindowRole.ProbeLayer => "受控背景层",
        _ => $"其他窗口（{HitWindowClass}）",
    };
}

/// <summary>一次命中测试的完整报告，可渲染成 markdown 归档。</summary>
public sealed class HitTestReport
{
    public required HitMode Mode { get; init; }

    public required string EnvironmentSummary { get; init; }

    public required string MonitorSummary { get; init; }

    public required IReadOnlyList<PixelRect> Regions { get; init; }

    public required IReadOnlyList<HitTestSample> Samples { get; init; }

    public required bool OverlayVisible { get; init; }

    public required int OverlayIndexInZOrder { get; init; }

    public required int TotalTopLevelWindows { get; init; }

    public required int OverlayEnsureBehindCount { get; init; }

    public required string OverlayWindowSummary { get; init; }

    public required string ProbeLayerSummary { get; init; }

    public required string BottomWindowSummary { get; init; }

    public required string Caveat { get; init; }

    public bool Passed => OverlayVisible && Samples.Count > 0 && Samples.All(s => s.Pass);

    public string Conclusion => Passed ? "PASS" : "FAIL";

    public string ToMarkdown()
    {
        var builder = new StringBuilder();
        builder.AppendLine($"## 命中测试报告 · {Mode}");
        builder.AppendLine();
        builder.AppendLine($"- 结论：**{Conclusion}**");
        builder.AppendLine($"- 环境：{EnvironmentSummary}");
        builder.AppendLine($"- 显示器：{MonitorSummary}");
        builder.AppendLine($"- 命中区域：{(Regions.Count == 0 ? "（无）" : string.Join("、", Regions.Select(r => r.ToString())))}");
        builder.AppendLine($"- 被测浮层：{OverlayWindowSummary}");
        builder.AppendLine($"- 受控背景层：{ProbeLayerSummary}");
        builder.AppendLine($"- 浮层可见：{OverlayVisible}；Z 序序号：{OverlayIndexInZOrder} / 共 {TotalTopLevelWindows} 个可见顶层窗口（0 为最顶层）");
        builder.AppendLine($"- 浮层置底触发次数：{OverlayEnsureBehindCount}");
        builder.AppendLine($"- Z 序最底部窗口：{BottomWindowSummary}");
        builder.AppendLine();
        builder.AppendLine("| 采样点 | 坐标 | 期望 | 实际命中层 | 命中窗口类名 | 注入点击 | 浮层收到 | 背景层收到 | 判定 |");
        builder.AppendLine("|---|---|---|---|---|---|---|---|---|");
        foreach (var sample in Samples)
        {
            builder.AppendLine(
                $"| {sample.Label} | ({sample.X},{sample.Y}) | {(sample.ExpectOurs ? "盒子内·拦截" : "盒子外·放行")} | " +
                $"{sample.RoleText} | {sample.HitWindowClass} | {(sample.ClickWasInjected ? "是" : "否")} | " +
                $"{(sample.OverlayReceivedClick ? "是" : "否")} | {(sample.ProbeLayerReceivedClick ? "是" : "否")} | **{sample.Verdict}** |");
        }

        builder.AppendLine();

        var notes = Samples.Where(s => !string.IsNullOrEmpty(s.Note)).Select(s => $"- {s.Label}：{s.Note}").ToList();
        if (notes.Count > 0)
        {
            builder.AppendLine("备注：");
            notes.ForEach(note => builder.AppendLine(note));
            builder.AppendLine();
        }

        builder.AppendLine($"### 本次测试的边界");
        builder.AppendLine();
        builder.AppendLine(Caveat);
        builder.AppendLine();

        return builder.ToString();
    }

    public void Save(string path)
    {
        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        File.WriteAllText(path, ToMarkdown(), new UTF8Encoding(false));
    }
}