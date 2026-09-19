using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using Cubby.Core.Model;
using Cubby.Shell.Diagnostics;
using Cubby.Shell.Overlay;

namespace Cubby.App;

/// <summary>
/// 自动化命中测试。
///
/// 设计要点（第一版踩过坑，这里刻意规避）：
/// 1. 不依赖用户桌面状态——被测浮层和一个受控背景层对打，背景层置顶并铺满屏幕，
///    因此无论用户桌面上开着什么，点击都只会落到我们自己的两个窗口上，不会误点第三方程序。
/// 2. 用 WindowFromPoint 判断「这个坐标的点击会被谁收到」（它与真实鼠标路由共用同一套命中测试），
///    再注入一次真实左键，看应该是谁收到、实际是谁收到，双向印证。
/// 3. 全程 await，不阻塞 UI 线程，否则注入的点击无法被自己的消息循环处理。
/// </summary>
internal static class SelfTestRunner
{
    private static readonly TimeSpan SettleDelay = TimeSpan.FromMilliseconds(250);
    private static readonly TimeSpan CursorDelay = TimeSpan.FromMilliseconds(120);
    private static readonly TimeSpan ClickDelay = TimeSpan.FromMilliseconds(180);
    private static readonly TimeSpan ModeSwitchDelay = TimeSpan.FromMilliseconds(400);

    private const string Caveat =
        "受控背景层与被测浮层同属本进程。窗口管理器的命中测试只看窗口形状与逐像素透明度，" +
        "与进程归属无关，因此该结论对第三方窗口同样成立；但真正与 Wallpaper Engine 互动壁纸的端到端表现，" +
        "仍需按诊断面板中的步骤人工确认（那部分无法自动化）。";

    public static async Task<int> RunAsync(OverlayWindow overlay, SpikeOptions options)
    {
        await Task.Delay(SettleDelay);

        var outputDirectory = options.OutputDirectory ?? Path.Combine(AppContext.BaseDirectory, "artifacts");
        Directory.CreateDirectory(outputDirectory);

        var surface = overlay.Surface!;
        var host = overlay.Host!;

        var reports = new List<HitTestReport>();

        // 受控背景层置顶（保证它在用户所有窗口之上），再把被测浮层也置顶（后置顶者在上），
        // 于是「背景层是被测浮层正下方的那一层」，采样才有意义。
        var backdrop = new ProbeBackdropWindow { Surface = surface };
        backdrop.Show();
        await Task.Delay(SettleDelay);
        backdrop.SetTopmost(true);

        host.SuspendAutoBehind();
        WindowPlacement.SetTopmost(host.Handle, true);
        await Task.Delay(SettleDelay);

        try
        {
            foreach (var mode in new[] { HitMode.PerPixelAlpha, HitMode.WindowRegion })
            {
                overlay.ApplyMode(mode);
                await Task.Delay(ModeSwitchDelay);
                overlay.ResetClickCount();
                backdrop.ResetClickCount();

                var report = await ProbeAsync(overlay, backdrop, mode);
                reports.Add(report);

                report.Save(Path.Combine(
                    outputDirectory,
                    $"hittest-{mode}-{DateTime.Now:yyyyMMdd-HHmmss}.md"));
            }
        }
        finally
        {
            WindowPlacement.SetTopmost(host.Handle, false);
            backdrop.SetTopmost(false);
            backdrop.Hide();
            host.ResumeAutoBehind();
            overlay.EnsureBehind();
        }

        File.WriteAllText(
            Path.Combine(outputDirectory, "hittest-summary.md"),
            BuildSummary(reports),
            new UTF8Encoding(false));

        return reports.Count > 0 && reports.All(r => r.Passed) ? 0 : 1;
    }

    private static async Task<HitTestReport> ProbeAsync(OverlayWindow overlay, ProbeBackdropWindow backdrop, HitMode mode)
    {
        var host = overlay.Host!;
        var overlayHwnd = host.Handle;
        var backdropHwnd = backdrop.Handle;
        var surface = overlay.Surface!;

        var hadCursor = MouseClicker.TryGetCursorPosition(out var originalX, out var originalY);
        var samples = new List<HitTestSample>();

        foreach (var point in overlay.SamplePoints())
        {
            // 每次采样前重申一次「浮层在背景层之上」，避免中途被打乱
            WindowPlacement.SetTopmost(overlayHwnd, true);

            MouseClicker.MoveTo(point.X, point.Y);
            await Task.Delay(CursorDelay);

            var probe = DesktopProbe.WindowAt(point.X, point.Y, overlayHwnd);
            var role = ClassifyRole(probe.Handle, overlayHwnd, backdropHwnd);

            var overlayBefore = overlay.OverlayClickCount;
            var backdropBefore = backdrop.ClickCount;

            var injected = MouseClicker.LeftClick();
            await Task.Delay(ClickDelay);

            var overlayGot = overlay.OverlayClickCount > overlayBefore;
            var backdropGot = backdrop.ClickCount > backdropBefore;

            samples.Add(new HitTestSample(
                point.Label,
                point.X,
                point.Y,
                point.ExpectOurs,
                role,
                probe.ClassName,
                probe.ProcessId,
                overlayGot,
                backdropGot,
                injected,
                point.Note));
        }

        if (hadCursor)
        {
            MouseClicker.MoveTo(originalX, originalY);
        }

        var snapshot = DesktopProbe.ZOrderSnapshot(400);
        var index = -1;
        for (var i = 0; i < snapshot.Count; i++)
        {
            if (snapshot[i].Handle == overlayHwnd)
            {
                index = i;
                break;
            }
        }

        var info = DesktopProbe.Describe(overlayHwnd);

        return new HitTestReport
        {
            Mode = mode,
            EnvironmentSummary = BuildEnvironmentSummary(surface),
            MonitorSummary = $"{surface.Id} {surface.Bounds} DPI 缩放 {surface.DpiScale:0.##}",
            Regions = overlay.Regions,
            Samples = samples,
            OverlayVisible = info?.IsVisible ?? false,
            OverlayIndexInZOrder = index,
            TotalTopLevelWindows = snapshot.Count,
            OverlayEnsureBehindCount = host.EnsureBehindCount,
            OverlayWindowSummary = info?.Describe() ?? "(未取到窗口信息)",
            ProbeLayerSummary = backdrop.Describe(),
            BottomWindowSummary = snapshot.Count > 0 ? snapshot[^1].Describe() : "(空)",
            Caveat = Caveat,
        };
    }

    private static HitWindowRole ClassifyRole(nint hitHwnd, nint overlayHwnd, nint backdropHwnd)
    {
        if (hitHwnd == 0)
        {
            return HitWindowRole.Foreign;
        }

        if (DesktopProbe.BelongsTo(hitHwnd, overlayHwnd))
        {
            return HitWindowRole.Overlay;
        }

        return DesktopProbe.BelongsTo(hitHwnd, backdropHwnd)
            ? HitWindowRole.ProbeLayer
            : HitWindowRole.Foreign;
    }

    private static string BuildEnvironmentSummary(MonitorSurface surface) =>
        $"{RuntimeInformation.OSDescription} | .NET {Environment.Version} | " +
        $"{(Environment.Is64BitProcess ? "x64" : "非 x64")} | DPI 缩放 {surface.DpiScale:0.##}";

    private static string BuildSummary(IReadOnlyList<HitTestReport> reports)
    {
        if (reports.Count == 0)
        {
            return "# M0 命中测试汇总\n\n未产生任何报告，测试未执行成功。\n";
        }

        var passed = reports.All(r => r.Passed);
        var builder = new StringBuilder();
        builder.AppendLine("# M0 命中测试汇总");
        builder.AppendLine();
        builder.AppendLine($"- 时间：{DateTime.Now:yyyy-MM-dd HH:mm:ss}");
        builder.AppendLine($"- 环境：{reports[0].EnvironmentSummary}");
        builder.AppendLine($"- 总体结论：**{(passed ? "PASS" : "FAIL")}**");
        builder.AppendLine();

        foreach (var report in reports)
        {
            builder.AppendLine($"## {report.Mode} → {report.Conclusion}");
            builder.AppendLine();
            builder.AppendLine($"- 浮层可见：{report.OverlayVisible}；Z 序序号：{report.OverlayIndexInZOrder} / 共 {report.TotalTopLevelWindows}");
            builder.AppendLine($"- 命中区域：{(report.Regions.Count == 0 ? "（无）" : string.Join("、", report.Regions.Select(r => r.ToString())))}");
            builder.AppendLine();
            builder.AppendLine("| 采样点 | 期望 | 实际命中层 | 注入点击 | 浮层收到 | 背景层收到 | 判定 |");
            builder.AppendLine("|---|---|---|---|---|---|---|");
            foreach (var sample in report.Samples)
            {
                builder.AppendLine(
                    $"| {sample.Label} | {(sample.ExpectOurs ? "拦截" : "放行")} | {sample.RoleText} | " +
                    $"{(sample.ClickWasInjected ? "是" : "否")} | {(sample.OverlayReceivedClick ? "是" : "否")} | " +
                    $"{(sample.ProbeLayerReceivedClick ? "是" : "否")} | **{sample.Verdict}** |");
            }

            builder.AppendLine();
        }

        builder.AppendLine("### 测试边界");
        builder.AppendLine();
        builder.AppendLine(reports[0].Caveat);
        builder.AppendLine();

        return builder.ToString();
    }
}