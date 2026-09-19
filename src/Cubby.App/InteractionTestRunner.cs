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
                // 先归一化被测盒子的初始状态。
                // 折叠会让缩放手柄消失、锁定会让拖动与缩放失效，而这些都是**用户会保存下来的正常状态**；
                // 更麻烦的是：上一次验收若在还原之前被杀（开发时会），磁盘上就会留下一个折叠盒子，
                // 于是后续每一次验收都会莫名其妙地失败。验收不该依赖用户当下的状态。
                var normalized = box with
                {
                    IsCollapsed = false,
                    IsLocked = false,
                    Bounds = box.Bounds with
                    {
                        Width = Math.Max(320, box.Bounds.Width),
                        Height = Math.Max(260, box.Bounds.Height),
                    },
                };

                overlay.UpdateBoxes(originals.Select(b => b.Id == box.Id ? normalized : b).ToList());
                box = normalized;

                results.Add((
                    "准备：把被测盒子归一化为展开 / 未锁定 / 尺寸不小于 320×260",
                    !box.IsCollapsed && !box.IsLocked,
                    $"原始状态 折叠={originals.First(b => b.Id == box.Id).IsCollapsed} / " +
                    $"锁定={originals.First(b => b.Id == box.Id).IsLocked} / " +
                    $"尺寸 {originals.First(b => b.Id == box.Id).Bounds.Width:0}×{originals.First(b => b.Id == box.Id).Bounds.Height:0}"));

                await Task.Delay(200);

                var scale = surface.DpiScale;
                var originX = surface.Bounds.Left;
                var originY = surface.Bounds.Top;

                int PhysX(double dipX) => originX + (int)Math.Round(dipX * scale);

                int PhysY(double dipY) => originY + (int)Math.Round(dipY * scale);

                // 会话 8 的教训：浮层按设计在应用窗口之下，若桌面上有别的置顶窗口压住盒子，
                // 注入的点击会（正确地）落到那个窗口上，测试就假失败。
                // 所以每一步之前都先确认"这个点真的归我们"，并把占用者写进报告——
                // 否则只会看到一个莫名其妙的 FAIL。
                string PointOwner(int x, int y)
                {
                    var probe = DesktopProbe.WindowAt(x, y, 0);
                    return DesktopProbe.BelongsTo(probe.Handle, host.Handle)
                        ? "浮层"
                        : $"{probe.ClassName}「{probe.Title}」(pid {probe.ProcessId})";
                }

                // 1. 拖动标题栏移动
                var grabX = PhysX(box.Bounds.X + 120);
                var grabY = PhysY(box.Bounds.Y + (BoxGeometry.TitleBarHeight / 2));
                var grabOwner = PointOwner(grabX, grabY);
                await MouseClicker.DragAsync(grabX, grabY, grabX - 40, grabY + 300);
                await Task.Delay(250);

                var afterMove = Current(overlay, box.Id);
                var expectX = box.Bounds.X - (40 / scale);
                var expectY = box.Bounds.Y + (300 / scale);
                results.Add((
                    "拖动标题栏移动盒子",
                    Math.Abs(afterMove.Bounds.X - expectX) <= Tolerance && Math.Abs(afterMove.Bounds.Y - expectY) <= Tolerance,
                    $"({afterMove.Bounds.X:0},{afterMove.Bounds.Y:0}) 期望 ({expectX:0},{expectY:0})；抓取点命中 {grabOwner}"));

                // 2. 拖动右下角手柄缩放
                // 抓取点往内缩 10 像素而不是贴着角：盒子是圆角的（CornerRadius 12），
                // 手柄自己还带 3 的小圆角，**最角落那几个像素在逐像素 alpha 下是透明的**，
                // 贴角抓取会时不时穿到桌面上去（表现为"缩放没反应"）
                var gripX = PhysX(afterMove.Bounds.X + afterMove.Bounds.Width) - 10;
                var gripY = PhysY(afterMove.Bounds.Y + afterMove.Bounds.Height) - 10;
                var gripOwner = PointOwner(gripX, gripY);
                await MouseClicker.DragAsync(gripX, gripY, gripX + 80, gripY + 60);
                await Task.Delay(250);

                var afterResize = Current(overlay, box.Id);
                results.Add((
                    "拖动右下角手柄缩放",
                    Math.Abs(afterResize.Bounds.Width - (afterMove.Bounds.Width + (80 / scale))) <= Tolerance &&
                    Math.Abs(afterResize.Bounds.Height - (afterMove.Bounds.Height + (60 / scale))) <= Tolerance,
                    $"{afterResize.Bounds.Width:0}×{afterResize.Bounds.Height:0}；抓取点 ({gripX},{gripY}) 命中 {gripOwner}"));

                // 3. 点击折叠按钮
                var titleY = PhysY(afterResize.Bounds.Y + (BoxGeometry.TitleBarHeight / 2));
                var collapseX = PhysX(afterResize.Bounds.X + afterResize.Bounds.Width - 22);
                var collapseOwner = PointOwner(collapseX, titleY);
                var clickBefore = overlay.OverlayClickCount;
                var wpfBefore = overlay.WpfMouseDownCount;
                await MouseClicker.DragAsync(collapseX, titleY, collapseX, titleY, steps: 1);
                await Task.Delay(250);

                var afterCollapse = Current(overlay, box.Id);
                results.Add((
                    "点击折叠按钮",
                    afterCollapse.IsCollapsed && Math.Abs(afterCollapse.Bounds.Height - afterResize.Bounds.Height) <= Tolerance,
                    $"折叠={afterCollapse.IsCollapsed}，模型高度仍为 {afterCollapse.Bounds.Height:0}；" +
                    $"点击点 ({collapseX},{titleY}) 命中 {collapseOwner}；" +
                    $"Win32 左键 +{overlay.OverlayClickCount - clickBefore}、WPF +{overlay.WpfMouseDownCount - wpfBefore}"));

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

            // 内存里的浮层也要还原：验收把它归一化过（展开 / 未锁定 / 改过尺寸）
            overlay.UpdateBoxes(originals);

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