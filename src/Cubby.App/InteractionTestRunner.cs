using System.IO;
using System.Text;
using Cubby.Core.Layout;
using Cubby.Core.Model;
using Cubby.Shell.Diagnostics;
using Cubby.Shell.Overlay;

namespace Cubby.App;

/// <summary>
/// 盒子交互的自动化验收：注入真实鼠标去拖动、缩放、折叠、锁定，再断言模型是否如预期变化。
///
/// 关键设计：验收期间把浮层临时置顶。原因是浮层按设计永远在应用窗口之下，
/// 而验收时用户桌面上很可能有个最大化窗口盖着盒子区域——那样点击会（正确地）落到那个窗口上，
/// 测试就会假失败。置顶之后，注入的鼠标必然落在被测窗口上，结论才可复现。
/// </summary>
internal static class InteractionTestRunner
{
    private const double Tolerance = 2.0;

    public static async Task<int> RunAsync(OverlayWindow overlay, LayoutService layout, SpikeOptions options)
    {
        var surface = overlay.Surface!;
        var host = overlay.Host!;
        var originals = overlay.Boxes.ToList();
        var results = new List<(string Step, bool Pass, string Detail)>();

        var movedCursor = MouseClicker.TryGetCursorPosition(out var originalCursorX, out var originalCursorY);

        host.SuspendAutoBehind();
        WindowPlacement.SetTopmost(host.Handle, true);
        await Task.Delay(500);

        try
        {
            var box = overlay.Boxes.FirstOrDefault();
            if (box is null)
            {
                results.Add(("准备", false, "该显示器上没有盒子"));
            }
            else
            {
                var scale = surface.DpiScale;
                var originX = surface.Bounds.Left;
                var originY = surface.Bounds.Top;

                int PhysX(double dipX) => originX + (int)Math.Round(dipX * scale);

                int PhysY(double dipY) => originY + (int)Math.Round(dipY * scale);

                // 1. 拖动标题栏移动
                var grabX = PhysX(box.Bounds.X + 120);
                var grabY = PhysY(box.Bounds.Y + (BoxGeometry.TitleBarHeight / 2));
                await MouseClicker.DragAsync(grabX, grabY, grabX - 40, grabY + 300);
                await Task.Delay(250);

                var afterMove = Current(overlay, box.Id);
                var expectX = box.Bounds.X - (40 / scale);
                var expectY = box.Bounds.Y + (300 / scale);
                results.Add((
                    "拖动标题栏移动盒子",
                    Math.Abs(afterMove.Bounds.X - expectX) <= Tolerance && Math.Abs(afterMove.Bounds.Y - expectY) <= Tolerance,
                    $"({afterMove.Bounds.X:0},{afterMove.Bounds.Y:0}) 期望 ({expectX:0},{expectY:0})"));

                // 2. 拖动右下角手柄缩放
                var gripX = PhysX(afterMove.Bounds.X + afterMove.Bounds.Width) - 6;
                var gripY = PhysY(afterMove.Bounds.Y + afterMove.Bounds.Height) - 6;
                await MouseClicker.DragAsync(gripX, gripY, gripX + 80, gripY + 60);
                await Task.Delay(250);

                var afterResize = Current(overlay, box.Id);
                results.Add((
                    "拖动右下角手柄缩放",
                    Math.Abs(afterResize.Bounds.Width - (afterMove.Bounds.Width + (80 / scale))) <= Tolerance &&
                    Math.Abs(afterResize.Bounds.Height - (afterMove.Bounds.Height + (60 / scale))) <= Tolerance,
                    $"{afterResize.Bounds.Width:0}×{afterResize.Bounds.Height:0}"));

                // 3. 点击折叠按钮
                var titleY = PhysY(afterResize.Bounds.Y + (BoxGeometry.TitleBarHeight / 2));
                var collapseX = PhysX(afterResize.Bounds.X + afterResize.Bounds.Width - 22);
                await MouseClicker.DragAsync(collapseX, titleY, collapseX, titleY, steps: 1);
                await Task.Delay(250);

                var afterCollapse = Current(overlay, box.Id);
                results.Add((
                    "点击折叠按钮",
                    afterCollapse.IsCollapsed && Math.Abs(afterCollapse.Bounds.Height - afterResize.Bounds.Height) <= Tolerance,
                    $"折叠={afterCollapse.IsCollapsed}，模型高度仍为 {afterCollapse.Bounds.Height:0}"));

                // 4. 点击锁定按钮
                var lockX = PhysX(afterCollapse.Bounds.X + afterCollapse.Bounds.Width - 54);
                await MouseClicker.DragAsync(lockX, titleY, lockX, titleY, steps: 1);
                await Task.Delay(250);

                var afterLock = Current(overlay, box.Id);
                results.Add((
                    "点击锁定按钮",
                    afterLock.IsLocked,
                    $"锁定={afterLock.IsLocked}"));

                // 5. 锁定后拖动应无效
                var beforeX = afterLock.Bounds.X;
                await MouseClicker.DragAsync(
                    PhysX(afterLock.Bounds.X + 120), titleY,
                    PhysX(afterLock.Bounds.X + 40), titleY);
                await Task.Delay(250);

                var afterLockedDrag = Current(overlay, box.Id);
                results.Add((
                    "锁定后拖动无效",
                    Math.Abs(afterLockedDrag.Bounds.X - beforeX) <= 0.5,
                    $"X={afterLockedDrag.Bounds.X:0}（拖动前 {beforeX:0}）"));
            }
        }
        finally
        {
            // 还原现场：先恢复模型，再恢复层级
            foreach (var original in originals)
            {
                layout.UpdateBox(original);
            }

            layout.SaveNow();

            WindowPlacement.SetTopmost(host.Handle, false);
            host.ResumeAutoBehind();
            overlay.EnsureBehind();

            if (movedCursor)
            {
                MouseClicker.MoveTo(originalCursorX, originalCursorY);
            }
        }

        var failed = results.Count(r => !r.Pass);
        WriteReport(options, surface, results);

        return failed == 0 && results.Count > 0 ? 0 : 1;
    }

    private static Box Current(OverlayWindow overlay, string boxId) =>
        overlay.Boxes.First(b => b.Id == boxId);

    private static void WriteReport(SpikeOptions options, MonitorSurface surface, IReadOnlyList<(string Step, bool Pass, string Detail)> results)
    {
        var builder = new StringBuilder();
        builder.AppendLine("# 盒子交互自动化验收报告");
        builder.AppendLine();
        builder.AppendLine($"- 时间：{DateTime.Now:yyyy-MM-dd HH:mm:ss}");
        builder.AppendLine($"- 显示器：{surface.Id} {surface.Bounds} DPI 缩放 {surface.DpiScale:0.##}");
        builder.AppendLine($"- 结论：**{(results.Count > 0 && results.All(r => r.Pass) ? "PASS" : "FAIL")}**");
        builder.AppendLine();
        builder.AppendLine("| 步骤 | 结果 | 细节 |");
        builder.AppendLine("|---|---|---|");

        foreach (var (step, pass, detail) in results)
        {
            builder.AppendLine($"| {step} | {(pass ? "**PASS**" : "**FAIL**")} | {detail} |");
        }

        builder.AppendLine();
        builder.AppendLine("说明：验收期间浮层被临时置顶，因此注入的鼠标必然落在被测窗口上；");
        builder.AppendLine("      结束后会恢复模型、重新置底，所以不会影响用户布局。");

        var directory = options.OutputDirectory ?? Path.Combine(AppContext.BaseDirectory, "artifacts");
        Directory.CreateDirectory(directory);

        File.WriteAllText(
            Path.Combine(directory, "interaction-report.md"),
            builder.ToString(),
            new UTF8Encoding(false));
    }
}