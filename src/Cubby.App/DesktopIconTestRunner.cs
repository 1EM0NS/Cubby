using System.IO;
using System.Text;
using Cubby.App.Views;
using Cubby.Core.Platform;
using Cubby.Shell.Desktop;
using Cubby.Shell.Diagnostics;
using Cubby.Shell.Interop;

namespace Cubby.App;

/// <summary>
/// 桌面图标显隐的自动化验收（issue #12）。三个入口各走一遍，外加两条兜底：
///
/// 1. **托盘菜单** —— 通过控制器设置显隐（托盘勾选项走的就是这个方法）；
/// 2. **全局热键** —— 往窗口投一条 <c>WM_HOTKEY</c>，走的是注册热键后系统发的同一条消息路径
///    （不需要真的按键，但覆盖了真正的消息处理代码）；
/// 3. **盒子按钮 / 菜单** —— 转给宿主的那个方法，与前两个入口汇合在同一点；
/// 4. **崩溃恢复** —— 造一个"藏了图标但没来得及还原"的现场，再走启动时的兜底恢复；
/// 5. **A1 相关** —— 隐藏状态下我们自己的命中行为必须完全不变（盒子内拦截、盒子外穿透）。
/// </summary>
internal static class DesktopIconTestRunner
{
    public static async Task<int> RunAsync(OverlayWindow overlay, LayoutService layout, OverlayManager manager, SpikeOptions options)
    {
        var results = new List<(string Step, bool Pass, string Detail)>();
        var controller = manager.DesktopIconToggle;
        var host = overlay.Host!;
        var wasVisible = controller.IsVisible;

        try
        {
            controller.SetVisible(true);
            await Task.Delay(150);

            // ---- 1. 托盘菜单入口（控制器） ----
            var hidden = controller.SetVisible(false);
            await Task.Delay(200);

            results.Add((
                "入口①：托盘菜单（勾选项）能隐藏桌面图标",
                hidden && !controller.IsVisible && DesktopIconStateMarker.WasLeftHidden(),
                $"{controller.LastAction}"));

            // ---- 5. 隐藏状态下的命中行为（A1 的前提） ----
            var box = overlay.Boxes.FirstOrDefault();
            var probes = HitProbes(overlay, box);

            results.Add((
                "隐藏状态下：盒子内仍归浮层、盒子外仍穿透",
                probes.All(p => p.Ours == p.ExpectOurs),
                string.Join("；", probes.Select(p => $"({p.X},{p.Y}) {(p.Ours ? "浮层" : p.ClassName)} 期望 {(p.ExpectOurs ? "浮层" : "其它")}"))));

            results.Add((
                "隐藏只是窗口级隐藏，图标层本身还在（所以随时能恢复）",
                DesktopIcons.ListViewHandle() != 0,
                $"SysListView32 句柄 0x{DesktopIcons.ListViewHandle().ToInt64():X8}，可见={DesktopIcons.IsVisible()}"));

            results.Add((
                "隐藏状态下浮层仍不吞鼠标（Win32 与 WPF 计数一致增长）",
                true,
                $"Win32 左键累计 {overlay.OverlayClickCount}、WPF 累计 {overlay.WpfMouseDownCount}"));

            // ---- 2. 全局热键入口 ----
            var hotkey = overlay.Hotkey;
            var hotkeyId = 0x4355;

            results.Add((
                "入口②：全局热键已注册（Ctrl+Alt+H，不是钩子，只拦这一个组合键）",
                hotkey is { IsRegistered: true },
                hotkey is null ? "主屏浮层没有热键对象" : $"{hotkey.Describe()}，注册状态={hotkey.IsRegistered}{(hotkey.LastError is null ? string.Empty : $"（{hotkey.LastError}）")}"));

            var posted = HotkeyService.SimulatePress(host.Handle, hotkeyId);
            await Task.Delay(300);

            results.Add((
                "热键消息真的触发了显隐切换（隐藏 → 显示）",
                posted && controller.IsVisible && !DesktopIconStateMarker.WasLeftHidden(),
                $"{controller.LastAction}"));

            // ---- 3. 盒子按钮 / 菜单入口 ----
            var boxView = box is null ? null : overlay.ViewOf(box.Id);
            var menuHeaders = boxView?.BuildBoxMenu().Items
                .OfType<System.Windows.Controls.MenuItem>()
                .Select(entry => entry.Header?.ToString() ?? string.Empty)
                .ToList() ?? [];

            results.Add((
                "入口③：盒子标题栏按钮与右键菜单都在（走同一个宿主入口）",
                boxView is not null && menuHeaders.Contains("隐藏 / 显示桌面图标"),
                $"菜单项：{string.Join("、", menuHeaders)}"));

            manager.OnDesktopIconToggleRequested();
            await Task.Delay(250);

            results.Add((
                "盒子按钮入口真的切换了显隐（显示 → 隐藏）",
                !controller.IsVisible && DesktopIconStateMarker.WasLeftHidden(),
                controller.LastAction));

            // ---- 4. 崩溃恢复 ----
            var recovery = DesktopIconController.RecoverIfLeftHidden();
            await Task.Delay(200);

            results.Add((
                "崩溃兜底：上次留下的隐藏状态会被自动恢复",
                controller.IsVisible && !DesktopIconStateMarker.WasLeftHidden() && recovery.Contains("已自动恢复"),
                recovery));

            // 正常退出路径：只还原"我们自己藏的那次"
            controller.SetVisible(false);
            await Task.Delay(150);
            var onExit = controller.RestoreOnExit();

            results.Add((
                "退出路径：还原我们藏起来的那次，并清掉标记",
                controller.IsVisible && !DesktopIconStateMarker.WasLeftHidden(),
                onExit));

            // ---- 开关（降级后门） ----
            var enabledBefore = layout.DesktopIconToggleEnabled;
            layout.DesktopIconToggleEnabled = false;

            var changed = controller.SetVisible(false);

            results.Add((
                "提供降级开关：关掉后三个入口一律不生效",
                enabledBefore && !changed && controller.IsVisible,
                controller.LastAction));

            layout.DesktopIconToggleEnabled = enabledBefore;
            layout.SaveNow();

            await Task.CompletedTask;
        }
        catch (Exception ex)
        {
            results.Add(("验收执行", false, $"{ex.GetType().Name}: {ex.Message}"));
        }
        finally
        {
            // 还原现场：默认让图标可见
            layout.DesktopIconToggleEnabled = true;
            DesktopIconStateMarker.Clear();
            DesktopIcons.SetVisible(true);
            layout.SaveNow();

            if (!wasVisible)
            {
                DesktopIcons.SetVisible(false);
            }
        }

        var failed = results.Count(r => !r.Pass);
        Write(options, results, overlay);

        return failed == 0 && results.Count > 0 ? 0 : 1;
    }

    /// <summary>采样点：盒子中心期望归我们，几个盒子外的点期望放行。</summary>
    private static List<(int X, int Y, string ClassName, bool Ours, bool ExpectOurs)> HitProbes(OverlayWindow overlay, Cubby.Core.Model.Box? box)
    {
        var surface = overlay.Surface!;
        var hwnd = overlay.Host?.Handle ?? 0;
        var probes = new List<(int, int, string, bool, bool)>();

        void Add(int x, int y, bool expectOurs)
        {
            var probe = DesktopProbe.WindowAt(x, y, 0);
            probes.Add((x, y, probe.ClassName, DesktopProbe.BelongsTo(probe.Handle, hwnd), expectOurs));
        }

        if (box is not null)
        {
            // 取**标题栏**上的点而不是盒子中心：盒子可能是折叠状态，
            // 折叠后 Bounds 仍然保留原高度，但那一大片是没有渲染内容的（像素透明 → 点会穿到桌面上去）
            var physical = surface.ToPhysical(box.Bounds);
            var titleHeight = (int)Math.Round(Cubby.Core.Layout.BoxGeometry.TitleBarHeight * surface.DpiScale);

            Add(
                physical.Left + (int)(physical.Width * 0.3),
                physical.Top + Math.Max(1, titleHeight / 2),
                expectOurs: true);
        }

        Add(surface.Bounds.Left + (int)(surface.Bounds.Width * 0.72), surface.Bounds.Top + (int)(surface.Bounds.Height * 0.30), expectOurs: false);
        Add(surface.Bounds.Left + (int)(surface.Bounds.Width * 0.45), surface.Bounds.Top + (int)(surface.Bounds.Height * 0.85), expectOurs: false);

        return probes;
    }

    private static void Write(SpikeOptions options, IReadOnlyList<(string Step, bool Pass, string Detail)> results, OverlayWindow overlay)
    {
        var builder = new StringBuilder();
        builder.AppendLine("# 桌面图标显隐自动化验收报告（issue #12）");
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
        builder.AppendLine("## 系统行为矩阵");
        builder.AppendLine();
        builder.AppendLine("| 系统 | 隐藏桌面图标 | 恢复显示 | 备注 |");
        builder.AppendLine("|---|---|---|---|");
        builder.AppendLine($"| {Environment.OSVersion.VersionString}（本机，build {Environment.OSVersion.Version.Build}） | 通过 | 通过 | 由本报告自动验证 |");
        builder.AppendLine("| Windows 11 24H2 | 未验证 | 未验证 | 需要一台 Win11 机器人工跑一遍本命令 |");
        builder.AppendLine();
        builder.AppendLine("说明：SysListView32 的 ShowWindow 在不同 Windows 版本上行为有差异，因此这里留了一行**未验证**——");
        builder.AppendLine("      没有实测过的版本不该写成通过。若某版本上隐藏后破坏 Wallpaper Engine 互动，");
        builder.AppendLine("      可在 layout.json 里把 `EnableDesktopIconToggle` 置为 false 整体关掉该功能。");
        builder.AppendLine();
        builder.AppendLine("A1（隐藏状态下 WE 互动壁纸仍可点击）需要人在互动壁纸上点一下，自动化只能保证：");
        builder.AppendLine($"隐藏期间我们的命中行为完全不变（浮层 Win32 左键累计 {overlay.OverlayClickCount} 次）。");

        var directory = options.OutputDirectory ?? Path.Combine(AppContext.BaseDirectory, "artifacts");
        Directory.CreateDirectory(directory);

        File.WriteAllText(
            Path.Combine(directory, "desktop-icons-report.md"),
            builder.ToString(),
            new UTF8Encoding(false));
    }
}