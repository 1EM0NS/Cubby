using System.Diagnostics;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Interop;
using Cubby.Core.Storage;
using Cubby.Shell.Diagnostics;
using Cubby.Shell.Overlay;

namespace Cubby.App;

/// <summary>
/// 首次运行引导的自动化验收（issue #38）。
///
/// 引导本身没有技术风险，风险在**"引导该出现时出现了、该闭嘴时真的闭嘴了"**这件事上——
/// 一个每次都弹的欢迎窗是骚扰，一个永远不弹的欢迎窗等于没做。
/// 所以这里把三条路径都钉死，并且全部走**用户真正走的那段代码**（<see cref="OnboardingNotice"/>）：
///
/// 1. **出现**：布局里没有标记 → 启动会自动弹，且界面真的列出那五条；
/// 2. **跳过**：勾了「不再自动显示」→ 落标记 → 再决策为空；不勾 → 下次仍然出现；
/// 3. **重置**：`--reset-onboarding` **另起一个真实进程**跑一遍，再从磁盘重读确认标记被清掉。
///
/// 另外单独钉一条容易做错的地方：欢迎窗必须是**普通可激活窗口**。
/// 浮层是 <c>WS_EX_NOACTIVATE</c> 的（收不到键盘，那正是它不抢动态壁纸焦点的原因），拿它当引导是行不通的。
/// </summary>
internal static class OnboardingTestRunner
{
    public static async Task<int> RunAsync(OverlayManager manager, LayoutService layout, SpikeOptions options)
    {
        var results = new List<(string Step, bool Pass, string Detail)>();
        var original = layout.Document;
        OnboardingWindow? window = null;
        TrayIcon? tray = null;

        await Task.Delay(400);

        try
        {
            // ---- 1. 文案自检（纯逻辑，不需要界面）----
            var points = OnboardingWindow.GuidePoints;
            var allText = string.Join("\n", points.Select(point => $"{point.Title}{point.Detail}"));
            var required = new[] { "拖", "文件实体永不移动", "托盘", "Ctrl+Alt+H", "映射" };
            var missing = required.Where(key => !allText.Contains(key, StringComparison.Ordinal)).ToList();

            results.Add((
                "引导文案覆盖五件事（拖入 / 引用不移动 / 托盘菜单 / Ctrl+Alt+H / 文件夹映射）",
                points.Count == 5 && missing.Count == 0 && points.All(p => p.Title.Length > 0 && p.Detail.Length > 0),
                missing.Count == 0
                    ? $"{points.Count} 条全部命中，合计 {allText.Length} 字"
                    : $"缺关键字：{string.Join("、", missing)}"));

            // ---- 2. 首次运行：自动弹出 ----
            layout.OnboardingShown = false; // 等价于「布局文件与引导标记都不存在」
            var shouldAutoShow = OnboardingNotice.ShouldAutoShow(layout);
            window = OnboardingNotice.ShowIfNeeded(manager, layout);
            await Task.Delay(400);

            results.Add((
                "首次运行（布局里没有引导标记）时欢迎窗自动出现，界面真的列出这五条",
                shouldAutoShow && window is not null && window.PointCount == points.Count,
                window is null
                    ? "没有弹窗（不应发生）"
                    : $"窗口列出 {window.PointCount} 条，与文案清单一致；共 {string.Join("；", window.PointTexts.Take(1))} …"));

            if (window is null)
            {
                results.Add(("后续断言", false, "没有窗口，无法继续"));
                Write(options, results);
                return 1;
            }

            // ---- 3. 欢迎窗必须是普通可激活窗口（浮层不是）----
            var guideHandle = new WindowInteropHelper(window).Handle;
            var overlayHandle = manager.PrimaryWindow?.Host?.Handle ?? 0;
            var guideNoActivate = WindowPlacement.HasExtendedStyle(guideHandle, WindowPlacement.NoActivateFlag);
            var overlayNoActivate = WindowPlacement.HasExtendedStyle(overlayHandle, WindowPlacement.NoActivateFlag);
            var isOurOverlay = manager.IsOurOverlay(guideHandle);

            results.Add((
                "欢迎窗是普通可激活窗口（浮层带 WS_EX_NOACTIVATE，收不到键盘，当不了引导）",
                guideHandle != 0 && !guideNoActivate && !isOurOverlay && overlayNoActivate,
                $"引导窗 0x{guideHandle.ToInt64():X8}：NOACTIVATE={guideNoActivate}、是我们的浮层={isOurOverlay}；" +
                $"对照浮层 0x{overlayHandle.ToInt64():X8}：NOACTIVATE={overlayNoActivate}"));

            // ---- 4. 「创建第一个盒子」真的把盒子建出来并显示出来 ----
            // 先清空布局里的盒子（不重建，避免 EnsureDefaults 立刻又补一个），制造"一个盒子都没有"的现场
            layout.Apply(layout.Document with { Boxes = [] });
            manager.SetBoxesVisible(false); // 顺手把盒子藏起来：验证按钮点完能让它可见

            var emptyBefore = layout.Boxes.Count;
            window.CreateFirstBox();
            await Task.Delay(500);

            var primary = manager.Monitors.FirstOrDefault(m => m.IsPrimary) ?? manager.Monitors.FirstOrDefault();
            var onPrimary = primary is not null && layout.Boxes.Any(b => b.MonitorId == primary.Id);
            var rendered = manager.PrimaryWindow?.BoxVisualCount > 0;
            var visibleNow = manager.BoxesVisible &&
                             (DesktopProbe.Describe(manager.PrimaryWindow?.Host?.Handle ?? 0)?.IsVisible ?? false);

            results.Add((
                "点「创建第一个盒子」后主屏真的多出一个盒子，并且浮层把它画了出来",
                emptyBefore == 0 && layout.Boxes.Count == 1 && onPrimary && rendered && visibleNow,
                $"点击前 {emptyBefore} 个盒子（显示={false}）；点击后 {layout.Boxes.Count} 个、" +
                $"落在主屏={onPrimary}、浮层视图数={manager.PrimaryWindow?.BoxVisualCount}、盒子可见={visibleNow}；" +
                $"说明：{window.Status}"));

            // ---- 5. 幂等：重复点不会叠出一堆盒子 ----
            var afterFirst = layout.Boxes.Count;
            window.CreateFirstBox();
            await Task.Delay(300);

            results.Add((
                "重复点「创建第一个盒子」是幂等的（不叠盒子，而是如实说明已经有了）",
                layout.Boxes.Count == afterFirst && (window.Status ?? string.Empty).Contains("已经有盒子", StringComparison.Ordinal),
                $"盒子数 {afterFirst} → {layout.Boxes.Count}；第二次的说明：{window.Status}"));

            // ---- 6. 勾选「不再自动显示」→ 落标记 → 不再打扰 ----
            window.SetRemember(true);
            window.Start();
            await Task.Delay(400);

            var stored = new LayoutStore(layout.FilePath).Load().OnboardingShown;
            var secondDecision = OnboardingNotice.ShowIfNeeded(manager, layout);

            results.Add((
                "勾「不再自动显示」并关窗后：标记**已落盘**，且下次启动完全静默（不创建窗口）",
                layout.OnboardingShown && stored && !OnboardingNotice.ShouldAutoShow(layout) && secondDecision is null,
                $"内存标记={layout.OnboardingShown}、磁盘标记={stored}、再决策窗口={Describe(secondDecision)}"));

            // ---- 7. 取消勾选 → 标记被写回 false（勾选项是双向的，不是只会单方向生效的装饰）----
            var reopen = OnboardingNotice.Show(manager, layout);
            reopen.SetRemember(false);
            reopen.Start();
            await Task.Delay(300);

            results.Add((
                "取消勾选「不再自动显示」后关窗：标记被写回 false，下次启动仍会出现",
                !layout.OnboardingShown && OnboardingNotice.ShouldAutoShow(layout),
                $"关窗后 内存标记={layout.OnboardingShown}、下一启动应自动弹={OnboardingNotice.ShouldAutoShow(layout)}"));

            // ---- 8. 托盘「使用指引」无视标记，随时能再打开 ----
            tray = new TrayIcon();
            var hasGuideItem = tray.MenuHeaders.Contains("使用指引…", StringComparer.Ordinal);
            OnboardingWindow? fromTray = null;
            tray.GuideRequested += (_, _) => fromTray = OnboardingNotice.Show(manager, layout);

            layout.OnboardingShown = true; // 先让自动弹彻底闭嘴
            var clicked = tray.ClickItem("使用指引…");
            await Task.Delay(300);

            results.Add((
                "托盘菜单「使用指引…」：入口存在、点击走通真实事件、且**无视**「不再自动显示」仍能打开",
                hasGuideItem && clicked && fromTray is not null && fromTray.PointCount == points.Count &&
                !OnboardingNotice.ShouldAutoShow(layout),
                $"菜单含该项={hasGuideItem}、点击成功={clicked}、打开窗口={Describe(fromTray)}；" +
                $"同一时刻自动弹应保持闭嘴={!OnboardingNotice.ShouldAutoShow(layout)}"));

            fromTray?.Close();
            await Task.Delay(200);

            // ---- 9. --reset-onboarding：另起一个真实进程跑，再从磁盘重读确认 ----
            layout.OnboardingShown = true;
            layout.SaveNow();

            var exePath = Environment.ProcessPath ?? Path.Combine(AppContext.BaseDirectory, "Cubby.App.exe");
            var (resetOk, resetDetail) = await RunResetCommandAsync(exePath);
            var afterReset = new LayoutStore(layout.FilePath).Load().OnboardingShown;

            results.Add((
                "`--reset-onboarding` 真跑一次子进程就能重置标记（重读磁盘确认，换机 / 演示前重放引导靠它）",
                resetOk && !afterReset,
                $"子进程 {resetDetail}；重置后磁盘上的引导标记={(afterReset ? "仍为 true（失败）" : "false（已重置）")}"));
        }
        catch (Exception ex)
        {
            results.Add(("验收执行", false, $"{ex.GetType().Name}: {ex.Message}"));
        }
        finally
        {
            // 还原现场：窗口、托盘、盒子、引导标记，一个都不留
            try
            {
                window?.Close();
            }
            catch (InvalidOperationException)
            {
                // 已经关掉了
            }

            tray?.Dispose();

            layout.Apply(original);
            manager.SetBoxesVisible(true);
            manager.Rebuild("首次引导验收还原");
        }

        var failed = results.Count(r => !r.Pass);
        Write(options, results);

        return failed == 0 && results.Count > 0 ? 0 : 1;
    }

    private static string Describe(Window? window) => window is null ? "（无，符合预期）" : $"已创建（{window.Title}）";

    /// <summary>
    /// 用**真实进程**跑一次 <c>--reset-onboarding</c>。
    /// 单元测试只能证明 <c>OnboardingNotice.Reset</c> 这个函数写得对，
    /// 这里额外证明命令行这条路径真的接上了——包括参数解析与整个启动顺序。
    /// </summary>
    private static async Task<(bool Ok, string Detail)> RunResetCommandAsync(string exePath)
    {
        if (!File.Exists(exePath))
        {
            return (false, $"找不到可执行文件：{exePath}");
        }

        try
        {
            using var process = Process.Start(new ProcessStartInfo(exePath, "--reset-onboarding")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
            });

            if (process is null)
            {
                return (false, "子进程启动返回 null");
            }

            var exited = await Task.Run(() => process.WaitForExit(20000));
            if (!exited)
            {
                process.Kill(entireProcessTree: true);
                return (false, "20 秒内没有退出，已强杀");
            }

            return (process.ExitCode == 0, $"{Path.GetFileName(exePath)} --reset-onboarding → exit {process.ExitCode}");
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception or IOException)
        {
            return (false, $"{ex.GetType().Name}: {ex.Message}");
        }
    }

    private static void Write(SpikeOptions options, IReadOnlyList<(string Step, bool Pass, string Detail)> results)
    {
        var builder = new StringBuilder();
        builder.AppendLine("# 首次运行引导自动化验收报告（issue #38）");
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
        builder.AppendLine("## 引导标记与重置方式");
        builder.AppendLine();
        builder.AppendLine("- 标记落在布局文档的 `OnboardingShown` 字段里，跟着 `%AppData%\\Cubby\\layout.json` 一起走。");
        builder.AppendLine("- **重置方式一**：`Cubby.App.exe --reset-onboarding`（本次验收真跑了一次子进程）。");
        builder.AppendLine("- **重置方式二**：直接删掉 `%AppData%\\Cubby\\layout.json`——配置没了，引导自然重新出现。");
        builder.AppendLine("- **手动打开**：托盘菜单「使用指引…」，无视标记，随时能看。");
        builder.AppendLine("- 欢迎窗的「不再自动显示」复选框默认勾选，关闭时以它为准**双向**写回标记：");
        builder.AppendLine("  勾着关 → 以后不自动弹；取消勾选再关 → 下次启动仍然出现（用户是能反悔的）。");
        builder.AppendLine();
        builder.AppendLine("## 一处刻意的取舍");
        builder.AppendLine();
        builder.AppendLine("首次运行时 `OverlayManager.Rebuild` 里的 `EnsureDefaults` **已经**会按每台显示器建好一个默认盒子，");
        builder.AppendLine("所以「创建第一个盒子」在真实首次运行里通常是**幂等命中**（返回「主屏已经有盒子了」），");
        builder.AppendLine("它真正兜住的是「用户把盒子全删了」这种情况。验收因此先把盒子清空再点，");
        builder.AppendLine("并在第二次点击时单独断言幂等——把一个像空操作的按钮变成两条可证据化的断言。");
        builder.AppendLine("未把它改成「首次运行不自动建盒子」是因为那会动到 M1 就定下的行为（程序刚启动不该是一片空白），");
        builder.AppendLine("收益不足以抵掉回归风险。");

        var directory = options.OutputDirectory ?? Path.Combine(AppContext.BaseDirectory, "artifacts");
        Directory.CreateDirectory(directory);

        File.WriteAllText(
            Path.Combine(directory, "onboard-report.md"),
            builder.ToString(),
            new UTF8Encoding(false));
    }
}
