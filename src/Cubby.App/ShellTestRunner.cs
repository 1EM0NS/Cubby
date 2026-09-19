using System.IO;
using System.Text;
using Cubby.Shell.Desktop;
using Cubby.Shell.Diagnostics;

namespace Cubby.App;

/// <summary>
/// 常驻形态的自动化验收（issue #11）：托盘菜单、开机自启、样式即时生效、显示/隐藏、资源基线。
///
/// 托盘与桌面图标都是系统级副作用，因此每条用例都**先记下原始状态、结束再还原**，
/// 中途异常也走 finally，不会把用户的桌面临时状态留在半路上。
/// </summary>
internal static class ShellTestRunner
{
    public static async Task<int> RunAsync(OverlayManager manager, LayoutService layout, SpikeOptions options)
    {
        var results = new List<(string Step, bool Pass, string Detail)>();
        var originalStyle = layout.Style;
        var originalAutoStart = StartupRegistration.ReadValue();
        var iconsWereVisible = DesktopIcons.IsVisible();

        await Task.Delay(200);

        try
        {
            CheckTrayMenu(results);
            CheckAutoStart(results, originalAutoStart);
            await CheckStyle(results, manager, layout);
            CheckBoxesVisibility(results, manager);
            CheckDesktopIcons(results, iconsWereVisible);
        }
        catch (Exception ex)
        {
            results.Add(("验收执行", false, $"{ex.GetType().Name}: {ex.Message}"));
        }
        finally
        {
            // 还原：样式 → 自启 → 盒子可见 → 桌面图标
            manager.ApplyStyle(originalStyle);
            RestoreAutoStart(originalAutoStart);
            manager.SetBoxesVisible(true);
            DesktopIcons.SetVisible(true);

            layout.SaveNow();
            await Task.Delay(100);
        }

        var failed = results.Count(r => !r.Pass);
        Write(options, results);

        return failed == 0 ? 0 : 1;
    }

    private static void CheckTrayMenu(List<(string, bool, string)> results)
    {
        using var tray = new TrayIcon();
        var expected = new[] { "显示盒子", "显示桌面图标", "开机自启", "设置…", "退出" };
        var actual = tray.MenuHeaders.Where(h => !string.IsNullOrEmpty(h)).ToList();

        results.Add((
            "托盘菜单含 显示/隐藏盒子、显示/隐藏桌面图标、开机自启、设置、退出",
            expected.All(actual.Contains),
            $"实际：{string.Join("、", actual)}"));
    }

    private static void CheckAutoStart(List<(string, bool, string)> results, string? original)
    {
        StartupRegistration.Enable();
        var enabledValue = StartupRegistration.ReadValue();

        StartupRegistration.Disable();
        var afterDisable = StartupRegistration.ReadValue();

        results.Add((
            "开机自启可开关（HKCU Run 往返）",
            enabledValue is not null && enabledValue.Contains("Cubby.App.exe") && afterDisable is null,
            $"开启后写入「{enabledValue ?? "(空)"}」，关闭后为 {(afterDisable is null ? "(已清除)" : afterDisable)}"));

        results.Add((
            "关闭开关会把自己那一项删干净（为 M3 卸载清理留的钩子）",
            afterDisable is null,
            $"原值 {(original is null ? "(原本未登记)" : original)}"));

        RestoreAutoStart(original);
    }

    private static void RestoreAutoStart(string? original)
    {
        if (original is null)
        {
            StartupRegistration.Disable();
        }
        else
        {
            StartupRegistration.Enable();
        }
    }

    private static async Task CheckStyle(List<(string, bool, string)> results, OverlayManager manager, LayoutService layout)
    {
        var before = layout.Style;

        // 走设置窗口自己的滑块逻辑，验证的就是用户拖动时那条路径
        var window = new SettingsWindow(before, manager.ApplyStyle);
        window.SetValue("ColumnsSlider", 6);
        window.SetValue("OpacitySlider", 0.5);
        window.SetValue("CornerSlider", 4);
        window.SetValue("FontSlider", 16);

        await Task.Delay(150);

        var applied = layout.Style;
        var windows = manager.Windows.ToList();
        var rendered = windows.Count > 0 && windows.All(w => w.CurrentStyle.Columns == 6 && w.CurrentStyle.FontSize == 16);
        var boxesUpdated = layout.Boxes.Count > 0 && layout.Boxes.All(b => b.Columns == 6 && Math.Abs(b.Opacity - 0.5) < 0.001);

        results.Add((
            "样式改动即时生效（滑块 → 布局 → 每个盒子 → 浮层重绘）",
            applied.Columns == 6 && applied.CornerRadius == 4 && Math.Abs(applied.FontSize - 16) < 0.001 &&
            boxesUpdated && rendered,
            $"样式={applied.Columns} 列 / 圆角 {applied.CornerRadius} / 字号 {applied.FontSize} / 透明度 {applied.Opacity:0.00}；" +
            $"盒子全部同步={boxesUpdated}；浮层全部重绘={rendered}（{windows.Count} 个窗口）"));

        window.Close();

        manager.ApplyStyle(before);
        await Task.Delay(150);

        results.Add((
            "样式可还原（验收不污染用户设置）",
            layout.Style == before.Normalized(),
            $"已还原为 列数 {layout.Style.Columns} / 圆角 {layout.Style.CornerRadius} / 字号 {layout.Style.FontSize}"));
    }

    private static void CheckBoxesVisibility(List<(string, bool, string)> results, OverlayManager manager)
    {
        var handle = manager.PrimaryWindow?.Host?.Handle ?? 0;
        if (handle == 0)
        {
            results.Add(("隐藏 / 显示盒子", false, "没有可用的浮层窗口"));
            return;
        }

        manager.SetBoxesVisible(false);
        var hidden = !(DesktopProbe.Describe(handle)?.IsVisible ?? true);

        manager.SetBoxesVisible(true);
        var shown = DesktopProbe.Describe(handle)?.IsVisible ?? false;

        results.Add((
            "隐藏 / 显示盒子真的改变了窗口可见性",
            hidden && shown,
            $"隐藏窗口可见={!hidden}；恢复后可见={shown}"));
    }

    private static void CheckDesktopIcons(List<(string, bool, string)> results, bool wasVisible)
    {
        if (DesktopIcons.ListViewHandle() == 0)
        {
            results.Add(("桌面图标层定位", false, "没找到 SysListView32（Explorer 可能正在重启）"));
            return;
        }

        var hidden = DesktopIcons.SetVisible(false) && !DesktopIcons.IsVisible();
        var restored = DesktopIcons.SetVisible(true) && DesktopIcons.IsVisible();

        results.Add((
            "显示 / 隐藏真实桌面图标（只做窗口级显隐，不写图标位置）",
            hidden && restored,
            $"隐藏生效={hidden}；恢复生效={restored}；验收开始时可见={wasVisible}"));
    }

    private static void Write(SpikeOptions options, IReadOnlyList<(string Step, bool Pass, string Detail)> results)
    {
        var sample = ResourceProbe.Sample();
        var builder = new StringBuilder();

        builder.AppendLine("# 常驻形态自动化验收报告（issue #11）");
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
        builder.AppendLine("## A7 常驻资源基线");
        builder.AppendLine();
        builder.AppendLine("| 指标 | 本次采样 |");
        builder.AppendLine("|---|---|");
        builder.AppendLine($"| 工作集 | {sample.WorkingSetMb:0.0} MB |");
        builder.AppendLine($"| 私有内存 | {sample.PrivateMb:0.0} MB |");
        builder.AppendLine($"| 句柄数 | {sample.Handles} |");
        builder.AppendLine($"| GDI 对象 | {sample.GdiObjects} |");
        builder.AppendLine($"| USER 对象 | {sample.UserObjects} |");
        builder.AppendLine();
        builder.AppendLine("A7 的「挂机 8 小时无增长」无法在一次会话内自动化，判定方法是：");
        builder.AppendLine("挂机数小时后各跑一次 `Cubby.App.exe --dump-state`，比对 `artifacts/state.txt` 里的这五个数字。");

        var directory = options.OutputDirectory ?? Path.Combine(AppContext.BaseDirectory, "artifacts");
        Directory.CreateDirectory(directory);

        File.WriteAllText(
            Path.Combine(directory, "shell-report.md"),
            builder.ToString(),
            new UTF8Encoding(false));
    }
}