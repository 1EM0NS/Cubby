using System.IO;
using System.Text;
using Cubby.Core.Model;
using Cubby.Shell.Diagnostics;
using Cubby.Shell.Overlay;

namespace Cubby.App;

/// <summary>
/// 浮层在「桌面被让出来 / 被 shell 收走」这类场景下能不能活下来 —— **只动 Cubby 自己的窗口**。
///
/// ## 这条验收为什么是现在这个样子（一条被用户纠正过的边界）
///
/// 第一版去**真的把用户桌面上的窗口全收起来**，想复刻「显示桌面」。这在本机实测：
/// 注入 Win+D 不生效、shell 的 `ToggleDesktop` 时灵时不灵，而"自己最小化所有窗口"虽然可行，
/// 却会把用户整台机器的窗口搅一遍（实测确实把用户的桌面搞乱了，用户明确要求不要再这么做）。
///
/// **结论：验收不得操作用户的窗口。** 这类全局动作留在人工项里，而不是让自动化去动别人的机器。
/// 于是这条验收只做两件动不了别人的事：
///
/// 1. **给出"系统级动作收不走浮层"的客观依据**：浮层带 `WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE`，
///    不进任务栏、不进 Alt+Tab ——「显示桌面」处理的是应用窗口，收不走它。这是位断言，不是嘴说。
/// 2. **把"被收走后必须自己回来"这条不变量钉死**：直接把**我们自己的**浮层最小化，
///    断言自动置底会把它恢复（这条曾经是坏的：`SWP_SHOWWINDOW` 不改变最小化状态，见下）。
///
/// 真·Win+D / Win+M 的端到端依然记在人工项里，**这是刻意的**：
/// 自动化能做到的前提是不碰用户的机器，做不到就不做。
///
/// ## 这条验收抓到的真缺陷（已修）
///
/// `OverlayHost.EnsureBehind` 原先只发 `SetWindowPos(..., SWP_SHOWWINDOW)`，
/// 而 **`SWP_SHOWWINDOW` 不改变最小化状态**——窗口一旦被收起来，会一直躺在最小化里，
/// 盒子再也不出现，而方法名和"期望可见"的语义都还在说它会管这件事。
/// 现在它在 `_wantsVisible` 且窗口最小化时先 `ShowWindow(SW_SHOWNOACTIVATE)`（恢复但不抢焦点），
/// 并单独计数 `RestoreFromMinimizedCount`；同时 `OverlayHost` 增加了一组
/// **只看自己进程**的最小化事件钩子（原先那组带 `WineventSkipOwnProcess`，让我们"看不见自己"）。
/// </summary>
internal static class ShowDesktopTestRunner
{
    private static readonly TimeSpan SettleDelay = TimeSpan.FromMilliseconds(900);

    public static async Task<int> RunAsync(OverlayManager manager, LayoutService layout, SpikeOptions options)
    {
        var results = new List<(string Step, bool Pass, string Detail)>();
        var overlay = manager.PrimaryWindow;
        var hwnd = overlay?.Host?.Handle ?? 0;

        await Task.Delay(600);

        try
        {
            if (overlay?.Surface is null || hwnd == 0)
            {
                results.Add(("准备浮层", false, "没有可用的浮层窗口"));
                Write(options, results);
                return 1;
            }

            var box = layout.Boxes.FirstOrDefault();
            if (box is null)
            {
                results.Add(("准备盒子", false, "布局里没有盒子，命中测试无从下手"));
                Write(options, results);
                return 1;
            }

            // ---- 1. 客观依据：浮层的窗口样式决定了系统级"显示桌面"收不走它 ----
            var hasToolWindow = WindowPlacement.HasExtendedStyle(hwnd, WindowPlacement.ToolWindowFlag);
            var hasNoActivate = WindowPlacement.HasExtendedStyle(hwnd, WindowPlacement.NoActivateFlag);

            results.Add((
                "客观依据：浮层带 WS_EX_TOOLWINDOW + WS_EX_NOACTIVATE（不进任务栏/Alt+Tab，所以「显示桌面」不把它当应用窗口）",
                hasToolWindow && hasNoActivate,
                $"toolwindow={hasToolWindow} noactivate={hasNoActivate}；浮层句柄 0x{hwnd.ToInt64():X8}"));

            // ---- 2. 基准：此刻浮层可见、未最小化 ----
            // 这里**不**做命中断言：桌面上可能正开着应用窗口盖住盒子，而浮层按设计永远在
            // 应用窗口之下（A4/P3），那样这条会假红（`--selftest-desktop-icons` 踩过同一个坑）。
            // 命中能力在第 4 步单独测，那时会把浮层临时提到最上层，绕开桌面遮挡。
            var baselineIconic = DesktopProbe.IsMinimized(hwnd);
            var baselineVisible = DesktopProbe.Describe(hwnd)?.IsVisible ?? false;

            results.Add((
                "基准：浮层可见且未被最小化",
                !baselineIconic && baselineVisible,
                $"iconic={baselineIconic} visible={baselineVisible}；浮层句柄 0x{hwnd.ToInt64():X8}"));

            // ---- 3. 把我们自己的浮层收起来，断言它自己回来 ----
            // 立刻查一次「是不是真的被收起来了」——这是激励自检：
            // 上一版先等再查，结果窗口已经被自动置底救回来了，断言反而因"没测到最小化状态"而失败。
            WindowPlacement.Minimize(hwnd);
            var iconicRightAfter = DesktopProbe.IsMinimized(hwnd);

            // 不手动调 EnsureBehind：让它走真实的 shell 事件路径
            //（OverlayHost 现在有一组只看自己进程的最小化事件钩子）
            await Task.Delay(SettleDelay);

            var recoveredIconic = DesktopProbe.IsMinimized(hwnd);
            var recoveredVisible = DesktopProbe.Describe(hwnd)?.IsVisible ?? false;

            results.Add((
                "被收走后自己回来：自动置底把最小化的浮层恢复出来（走真实事件路径，不手动兜底）",
                iconicRightAfter && !recoveredIconic && recoveredVisible,
                $"最小化后立刻查 iconic={iconicRightAfter} → 等待后 iconic={recoveredIconic} visible={recoveredVisible}；" +
                $"自动置底累计 {overlay.Host?.EnsureBehindCount} 次（其中从最小化拉回 {overlay.Host?.RestoreFromMinimizedCount} 次）"));

            // ---- 4. 命中能力：把浮层临时提到最上层再测，绕开桌面遮挡 ----
            // 这是 `--selftest` 用过的同一招：不依赖用户桌面状态，断言才能稳定复现。
            // 只动我们自己的窗口（置顶 / 置底），不碰任何别人的窗口。
            ProbeResult hit;
            ProbeResult outside;

            overlay.Host?.SuspendAutoBehind();
            WindowPlacement.SetTopmost(hwnd, true);
            await Task.Delay(400);

            try
            {
                hit = HitBox(overlay, box);

                var surface = overlay.Surface;
                var outsideX = surface.Bounds.Left + (int)(surface.Bounds.Width * 0.72);
                var outsideY = surface.Bounds.Top + (int)(surface.Bounds.Height * 0.30);
                outside = DesktopProbe.WindowAt(outsideX, outsideY, hwnd);
            }
            finally
            {
                WindowPlacement.SetTopmost(hwnd, false);
                overlay.Host?.ResumeAutoBehind();
                overlay.Host?.EnsureBehind();
                await Task.Delay(400);
            }

            results.Add((
                "命中能力：恢复之后盒子位置仍然命中浮层（把浮层临时置顶后测，绕开桌面遮挡）",
                hit.IsOurs,
                $"盒子标题栏点 → class={hit.ClassName} IsOurs={hit.IsOurs}；" +
                "测完已还原置底（把浮层临时置顶是 --selftest 用过的同一招）"));

            // ---- 5. P2 没有被这次折腾改变：盒子之外的点不归我们 ----
            results.Add((
                "P2 不变：盒子之外的点仍然不归浮层（点击照常穿透给别的窗口或桌面）",
                !outside.IsOurs,
                $"盒子外的点 → class={outside.ClassName} IsOurs={outside.IsOurs} IsDesktopLayer={outside.IsDesktopLayer}"));
        }
        catch (Exception ex)
        {
            results.Add(("验收执行", false, $"{ex.GetType().Name}: {ex.Message}"));
        }
        finally
        {
            // 只兜底我们自己的窗口；不碰用户机器上的任何东西
            if (hwnd != 0 && DesktopProbe.IsMinimized(hwnd))
            {
                WindowPlacement.Restore(hwnd);
                overlay?.Host?.EnsureBehind();
            }
        }

        var failed = results.Count(r => !r.Pass);
        Write(options, results);

        return failed == 0 && results.Count > 0 ? 0 : 1;
    }

    /// <summary>
    /// 在盒子**标题栏**上取一点做命中测试。
    /// 取标题栏而不是盒子中心：折叠状态下 Bounds 仍保留原高度，但那一大片是透明的。
    /// </summary>
    private static ProbeResult HitBox(OverlayWindow overlay, Box box)
    {
        var surface = overlay.Surface!;
        var physical = surface.ToPhysical(box.Bounds);
        var titleHeight = (int)Math.Round(Cubby.Core.Layout.BoxGeometry.TitleBarHeight * surface.DpiScale);

        return DesktopProbe.WindowAt(
            physical.Left + (int)(physical.Width * 0.3),
            physical.Top + Math.Max(1, titleHeight / 2),
            overlay.Host?.Handle ?? 0);
    }

    private static string Describe(ProbeResult probe) => $"class={probe.ClassName} IsOurs={probe.IsOurs}";

    private static void Write(SpikeOptions options, IReadOnlyList<(string Step, bool Pass, string Detail)> results)
    {
        var builder = new StringBuilder();
        builder.AppendLine("# 浮层被 shell 收走后的自恢复自动化验收报告");
        builder.AppendLine();
        builder.AppendLine($"- 时间：{DateTime.Now:yyyy-MM-dd HH:mm:ss}");
        builder.AppendLine($"- 结论：**{(results.Count > 0 && results.All(r => r.Pass) ? "PASS" : "FAIL")}**");
        builder.AppendLine("- 范围：**只操作 Cubby 自己的浮层窗口**，不碰用户机器上的任何其它窗口。");
        builder.AppendLine();
        builder.AppendLine("| 步骤 | 结果 | 细节 |");
        builder.AppendLine("|---|---|---|");
        foreach (var (step, pass, detail) in results)
        {
            builder.AppendLine($"| {step} | {(pass ? "**PASS**" : "**FAIL**")} | {detail} |");
        }

        builder.AppendLine();
        builder.AppendLine("## 覆盖了什么");
        builder.AppendLine();
        builder.AppendLine("1. **「系统级动作收不走浮层」的客观依据**：浮层带 `WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE`，");
        builder.AppendLine("   不进任务栏、不进 Alt+Tab。「显示桌面 / Win+D」处理的是应用窗口，收不走它——这是位断言，不是嘴说。");
        builder.AppendLine("2. **「被收走后必须自己回来」这条不变量**：直接把**我们自己**的浮层最小化，");
        builder.AppendLine("   断言自动置底会恢复它并重新参与命中。");
        builder.AppendLine();
        builder.AppendLine("## 刻意没有覆盖什么（以及为什么）");
        builder.AppendLine();
        builder.AppendLine("**真·Win+D / Win+M 的端到端依然记在人工项里。**");
        builder.AppendLine();
        builder.AppendLine("第一版验收去**真的把用户桌面上的窗口全收起来**复刻「显示桌面」，实测结果：");
        builder.AppendLine();
        builder.AppendLine("| 尝试方式 | 实测结果 |");
        builder.AppendLine("|---|---|");
        builder.AppendLine("| 注入 Win+D（`SendInput` 发「左 Win↓ D↓ D↑ 左 Win↑」） | **不生效**：4 个事件全部投递成功，但最小化窗口数不变、前台也没变 |");
        builder.AppendLine("| shell 的 `Shell.Application.ToggleDesktop` | **时灵时不灵**：有时一棵窗口都不收，有时把用户的窗口全收一遍 |");
        builder.AppendLine("| 自己最小化所有带标题的可见窗口 | 生效，但**会把用户的桌面搅乱**（实测把用户的窗口全部收起来又放开） |");
        builder.AppendLine();
        builder.AppendLine("最后一条踩到了真正的边界：**验收不该去操作用户的窗口。**");
        builder.AppendLine("所以那套代码（`ShellDesktopToggle`）已经删除，这条验收的范围收缩到只动自己的窗口。");
        builder.AppendLine("宁可留一条人工项，也不让自动化去动别人的机器——**人工项可以补，用户的桌面不能白被搅一次。**");
        builder.AppendLine();
        builder.AppendLine("顺带记一笔：真·Win+D 的端到端之所以风险低，正是因为上面第 1 条的样式断言——");
        builder.AppendLine("它是工具窗口，本来就不该被「显示桌面」收走。");
        builder.AppendLine();
        builder.AppendLine("## 这条验收抓到的真缺陷（已修）");
        builder.AppendLine();
        builder.AppendLine("`OverlayHost.EnsureBehind` 原先只发 `SetWindowPos(..., SWP_SHOWWINDOW)`，");
        builder.AppendLine("而 **`SWP_SHOWWINDOW` 不改变最小化状态**——窗口一旦被收起来，会一直躺在最小化里，");
        builder.AppendLine("盒子再也不出现，而方法名和「期望可见」的语义都还在说它会管这件事。");
        builder.AppendLine();
        builder.AppendLine("修了两处：");
        builder.AppendLine();
        builder.AppendLine("1. `EnsureBehind` 在 `_wantsVisible` 且窗口最小化时先 `ShowWindow(SW_SHOWNOACTIVATE)`");
        builder.AppendLine("   （恢复但不抢焦点，P2/A1），并单独计数 `RestoreFromMinimizedCount`；");
        builder.AppendLine("2. `OverlayHost` 增加一组**只看自己进程**的最小化事件钩子——");
        builder.AppendLine("   原先那组带 `WineventSkipOwnProcess`，让我们「看不见自己」，");
        builder.AppendLine("   于是这条不变量只在**别人**触发事件时才成立。");

        var directory = options.OutputDirectory ?? Path.Combine(AppContext.BaseDirectory, "artifacts");
        Directory.CreateDirectory(directory);

        File.WriteAllText(
            Path.Combine(directory, "show-desktop-report.md"),
            builder.ToString(),
            new UTF8Encoding(false));
    }
}
