using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Cubby.Core.Model;
using Cubby.Shell.Diagnostics;

namespace ZOrderProbe;

/// <summary>
/// 窗口链 / Z 序快照工具，用于验收「浮层始终在应用窗口之下、桌面层之上」（A4）。
///
/// 用法：
///   ZOrderProbe                                 打印顶层窗口 Z 序与桌面图层级链
///   ZOrderProbe --json                          输出 JSON，便于前后 diff
///   ZOrderProbe --assert-behind --exe Cubby.App --title "Cubby 浮层"
///                                               断言该窗口位于所有应用窗口之下
///
/// 注意：--title 过滤是必要的。同一个进程往往有多个窗口（例如诊断面板也在顶层），
/// 不过滤就会断言到错误的窗口上，得到一个假 PASS。
///
/// 退出码：0 正常；1 断言失败；2 参数或环境问题。
/// </summary>
internal static class Program
{
    private static int Main(string[] args)
    {
        Console.OutputEncoding = Encoding.UTF8;

        var asJson = Has(args, "--json");
        var assertBehind = Has(args, "--assert-behind");
        var targetExe = ValueOf(args, "--exe");
        var targetTitle = ValueOf(args, "--title");
        var max = ValueOf(args, "--max") is { } raw && int.TryParse(raw, out var parsed) ? parsed : 400;

        var snapshot = DesktopProbe.ZOrderSnapshot(max);
        var desktopChain = DesktopProbe.DesktopLayerChain();

        uint? targetPid = null;
        if (targetExe is not null)
        {
            var name = targetExe.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
                ? targetExe[..^4]
                : targetExe;

            targetPid = Process.GetProcessesByName(name).Select(p => (uint)p.Id).FirstOrDefault();
            if (targetPid == 0)
            {
                Console.Error.WriteLine($"未找到进程 {name}，请先启动它。");
                return 2;
            }
        }

        if (asJson)
        {
            // 显式投影一次：WindowInfo 里的句柄是 nint，System.Text.Json 不支持直接序列化它
            Console.WriteLine(JsonSerializer.Serialize(
                new
                {
                    timestamp = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"),
                    topLevelWindows = snapshot.Select(Project),
                    desktopLayerChain = desktopChain.Select(Project),
                    targetPid,
                },
                new JsonSerializerOptions { WriteIndented = true }));
        }
        else
        {
            PrintHuman(snapshot, desktopChain, targetPid);
        }

        if (!assertBehind)
        {
            return 0;
        }

        if (targetPid is null)
        {
            Console.Error.WriteLine("--assert-behind 需要配合 --exe <进程名> 使用。");
            return 2;
        }

        return AssertBehind(snapshot, targetPid.Value, targetTitle);
    }

    private static object Project(WindowInfo info) => new
    {
        Handle = info.Handle.ToInt64(),
        ClassName = info.ClassName,
        Title = info.Title,
        ProcessId = info.ProcessId,
        Bounds = info.Bounds.ToString(),
        IsVisible = info.IsVisible,
    };

    private static void PrintHuman(
        IReadOnlyList<WindowInfo> snapshot,
        IReadOnlyList<WindowInfo> desktopChain,
        uint? targetPid)
    {
        Console.WriteLine($"== 顶层窗口 Z 序（上 → 下，共 {snapshot.Count} 个可见窗口）==");
        for (var i = 0; i < snapshot.Count; i++)
        {
            var info = snapshot[i];
            Console.WriteLine($"  {i,3}  {info.Describe()}{Tag(info, targetPid)}");
        }

        Console.WriteLine();
        Console.WriteLine("== 桌面图层级链（Progman/WorkerW → SHELLDLL_DefView → SysListView32）==");
        if (desktopChain.Count == 0)
        {
            Console.WriteLine("  （未能定位，可能被其他桌面工具改写了层级）");
        }
        else
        {
            foreach (var info in desktopChain)
            {
                Console.WriteLine($"       {info.Describe()}");
            }
        }
    }

    private static string Tag(WindowInfo info, uint? targetPid)
    {
        if (targetPid is not null && info.ProcessId == targetPid)
        {
            return "   ← 被关注进程（Cubby）";
        }

        if (IsTaskbar(info))
        {
            return "   [任务栏]";
        }

        if (IsDesktopLayer(info))
        {
            return "   [桌面层]";
        }

        return "   [应用窗口]";
    }

    /// <summary>
    /// 断言：被关注进程的窗口必须位于所有「应用窗口」之下。
    /// 允许在它之上的只有：任务栏、桌面层窗口、以及它自己进程的其他窗口。
    /// </summary>
    private static int AssertBehind(IReadOnlyList<WindowInfo> snapshot, uint targetPid, string? titleFilter)
    {
        var index = -1;
        for (var i = 0; i < snapshot.Count; i++)
        {
            var info = snapshot[i];
            if (info.ProcessId != targetPid)
            {
                continue;
            }

            if (titleFilter is not null && !info.Title.Equals(titleFilter, StringComparison.Ordinal))
            {
                continue;
            }

            index = i;
            break;
        }

        if (index < 0)
        {
            Console.Error.WriteLine(titleFilter is null
                ? $"断言失败：在可见顶层窗口里没找到 pid={targetPid} 的窗口。"
                : $"断言失败：没找到 pid={targetPid} 且标题严格等于「{titleFilter}」的窗口。");
            return 1;
        }

        Console.WriteLine();
        Console.WriteLine($"== 置底断言（pid={targetPid}，标题过滤={(titleFilter ?? "无")}）==");
        Console.WriteLine($"   被断言的窗口：{snapshot[index].Describe()}");
        Console.WriteLine($"   位置：第 {index} 位（0 为最顶层），可见顶层窗口共 {snapshot.Count} 个");

        var violations = new List<WindowInfo>();
        var below = new List<WindowInfo>();

        // 判据：位于被断言窗口**之下**的，只允许是桌面层、任务栏、同进程窗口或屏幕外的窗口。
        // 因为「置底」的含义是——我们不遮挡任何看得见的东西；反过来（上方有应用窗口）才是正常的。
        for (var i = index + 1; i < snapshot.Count; i++)
        {
            var info = snapshot[i];
            below.Add(info);

            if (info.ProcessId == targetPid || IsTaskbar(info) || IsDesktopLayer(info))
            {
                continue;
            }

            // 停在屏幕外（Windows 会把隐藏窗口挪到 -25600 一带）或与目标不重叠的，谈不上被遮挡
            if (!Intersects(info.Bounds, snapshot[index].Bounds))
            {
                continue;
            }

            violations.Add(info);
        }

        Console.WriteLine($"   它下方共 {below.Count} 个窗口");

        if (violations.Count == 0)
        {
            Console.WriteLine("PASS：它下方只有桌面层 / 任务栏 / 同进程窗口 / 屏幕外窗口，没有压住任何应用窗口。");
            return 0;
        }

        Console.WriteLine($"FAIL：有 {violations.Count} 个应用窗口被它压在下面：");
        foreach (var info in violations)
        {
            Console.WriteLine($"       {info.Describe()}");
        }

        return 1;
    }

    private static bool Intersects(PixelRect a, PixelRect b) =>
        a.Left < b.Right && b.Left < a.Right && a.Top < b.Bottom && b.Top < a.Bottom;

    private static bool IsTaskbar(WindowInfo info) =>
        info.ClassName.Equals("Shell_TrayWnd", StringComparison.OrdinalIgnoreCase) ||
        info.ClassName.Equals("Shell_SecondaryTrayWnd", StringComparison.OrdinalIgnoreCase);

    private static bool IsDesktopLayer(WindowInfo info) =>
        info.ClassName.Equals("Progman", StringComparison.OrdinalIgnoreCase) ||
        info.ClassName.Equals("WorkerW", StringComparison.OrdinalIgnoreCase) ||
        info.ClassName.Equals("SysListView32", StringComparison.OrdinalIgnoreCase) ||
        info.ClassName.Equals("SHELLDLL_DefView", StringComparison.OrdinalIgnoreCase);

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